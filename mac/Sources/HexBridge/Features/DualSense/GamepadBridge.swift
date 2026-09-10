import Foundation
import IOKit
import IOKit.hid

/// Mirrors one DualSense onto the device channel of the wire protocol.
///
/// It owns the hot-plug watch, the input reader and the throttled writer, and
/// knows nothing about audio: `BridgeRuntime` hands it a `Sender` and it adds a
/// second stream to the same socket. If anything here fails, the voice path is
/// unaffected by construction — nothing in this file can throw into it.
final class GamepadBridge {
    /// The protocol carries a device number 0…3. One controller for now; the
    /// field exists so a second one does not need a protocol change.
    private static let deviceNumber: UInt8 = 0

    /// The controller accepts output at the same 250 Hz as its input. Writing
    /// faster wastes a blocking USB transaction per report and nothing else.
    private static let minimumWriteInterval = 0.004

    struct Status {
        var connected = false
        var product = "—"
        var transport = "—"
        var battery: String?
        /// Measured input reports per second, refreshed once a second.
        var reportRate: Double = 0
        var reportsForwarded: UInt64 = 0
        var outputsApplied: UInt64 = 0
        var outputsRejected: UInt64 = 0
        var attachAcknowledged = false
        var lastError: String?
        /// True while reports are being forwarded to the receiver. False means
        /// the controller is only being read for the on-screen visualisation.
        var forwarding = false
    }

    private let thread = HIDRunLoopThread()
    private let lock = NSLock()
    private let outputQueue = DispatchQueue(label: "hexbridge.gamepad.output", qos: .userInteractive)

    private weak var sender: Sender?
    /// False when the bridge is only feeding the visualisation (§7.3: the
    /// "подключён, не проброшен" state shows a live controller outline).
    private var forwarding = false
    private var device: DualSenseDevice?
    private var attachedRegistryID: UInt64 = 0
    private var descriptors: [Wire.DeviceChannel.Descriptor] = []
    private var status = Status()

    /// Rate accounting, sampled by `tick` once a second.
    private var reportsSinceTick: UInt64 = 0

    // Live state for the on-screen controller. Parsing all 250 reports a second
    // is only worth it while something is actually drawing them, so it is gated
    // on an observer count the UI raises and lowers with the view's lifetime.
    private var observers = 0
    private var liveStateStorage: GamepadState?
    private var liveStateAt = DispatchTime.now()
    private var liveInputAt = DispatchTime.now()
    /// Last lightbar colour the receiver asked for, so the outline can show the
    /// real colour rather than a guess.
    private var lightbar: GamepadOutput.Color?

    // Coalescing state for the writer. A burst of DEV_OUT packets collapses into
    // the newest one: the report is absolute state, not a delta, so replacing a
    // pending write loses nothing.
    private var pendingOutput: [UInt8]?
    private var writeScheduled = false
    private var lastWriteAt = DispatchTime.now()

    private var notifyPort: IONotificationPortRef?
    private var matchedIterator: io_iterator_t = 0
    private var terminatedIterator: io_iterator_t = 0
    private var running = false

    // MARK: - Lifecycle

    /// Starts watching for controllers. Never throws: a missing or unreadable
    /// controller degrades to "not connected", it does not stop the bridge.
    func start(sender: Sender?, forwarding: Bool) {
        guard !running else { return }
        running = true
        self.forwarding = forwarding
        lock.lock()
        status.forwarding = forwarding
        lock.unlock()
        bind(sender)

        thread.start(name: "hexbridge.gamepad")
        thread.sync { [self] in
            installHotPlugWatch()
            rescan()
        }
    }

    func stop() {
        guard running else { return }
        running = false
        sender?.onDeviceOutput = nil

        thread.sync { [self] in
            detachCurrent(notifyHost: true)
            removeHotPlugWatch()
        }
        thread.stop()
        sender = nil
    }

    /// Re-points the bridge at a new socket, or turns forwarding on and off
    /// without tearing down the HID reader. Switching the feature switch must
    /// not make the on-screen controller go dead for a second.
    func update(sender: Sender?, forwarding: Bool) {
        guard running else { return }
        let wasForwarding = self.forwarding
        self.forwarding = forwarding
        bind(sender)

        lock.lock()
        status.forwarding = forwarding
        if !forwarding { status.attachAcknowledged = false }
        let connected = status.connected
        let blocks = descriptors
        lock.unlock()

        if forwarding, !wasForwarding, connected, !blocks.isEmpty {
            sender?.sendDeviceAttach(device: Self.deviceNumber, descriptors: blocks)
        } else if !forwarding, wasForwarding, connected {
            sender?.sendDeviceDetach(device: Self.deviceNumber)
        }
    }

    private func bind(_ sender: Sender?) {
        if let old = self.sender, old !== sender { old.onDeviceOutput = nil }
        self.sender = sender
        sender?.onDeviceOutput = { [weak self] device, report in
            guard device == GamepadBridge.deviceNumber else { return }
            self?.submitOutput(report)
        }
    }

    // MARK: - Live state for the visualisation

    /// Balanced calls from the views that draw the controller. While the count
    /// is zero no report is parsed, which is the difference between 0.3 % and
    /// 3 % of a core with nothing on screen.
    func addObserver() {
        lock.lock()
        observers += 1
        lock.unlock()
    }

    func removeObserver() {
        lock.lock()
        observers = max(0, observers - 1)
        if observers == 0 { liveStateStorage = nil }
        lock.unlock()
    }

    /// The newest decoded report, the lightbar colour the receiver asked for,
    /// and how long ago the controller last moved.
    func liveState() -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        lock.lock()
        defer { lock.unlock() }
        guard let liveStateStorage else { return nil }
        let idle = Double(DispatchTime.now().uptimeNanoseconds &- liveInputAt.uptimeNanoseconds) / 1e9
        return (liveStateStorage, lightbar, idle)
    }

    /// Called once a second from the runtime's keepalive timer.
    func tick() {
        lock.lock()
        let reports = reportsSinceTick
        reportsSinceTick = 0
        status.reportRate = Double(reports)
        let needsAttach = forwarding && status.connected && !status.attachAcknowledged
        let descriptors = self.descriptors
        lock.unlock()

        // Re-announce until the receiver proves it noticed. There is no explicit
        // ack in the protocol, so the first DEV_OUT is taken as one; a receiver
        // that never sends output simply keeps getting a 570-byte packet a
        // second, which is cheaper than the alternative of it never attaching.
        if needsAttach, !descriptors.isEmpty {
            sender?.sendDeviceAttach(device: Self.deviceNumber, descriptors: descriptors)
        }

        // A controller that failed to open stays invisible to the hot-plug
        // notification, which already fired. Retry here.
        if !needsAttach {
            thread.sync { [self] in
                if device == nil { rescan() }
            }
        }
    }

    func snapshot() -> Status {
        lock.lock()
        defer { lock.unlock() }
        return status
    }

    // MARK: - Hot plug

    private func installHotPlugWatch() {
        guard let port = IONotificationPortCreate(kIOMainPortDefault) else { return }
        notifyPort = port
        if let source = IONotificationPortGetRunLoopSource(port)?.takeUnretainedValue() {
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .defaultMode)
        }

        // Matching is by VID/PID only. A broader filter would make the OS ask
        // the user for Input Monitoring on behalf of every keyboard we touched.
        subscribe(port: port, to: kIOMatchedNotification, into: &matchedIterator)
        subscribe(port: port, to: kIOTerminatedNotification, into: &terminatedIterator)
    }

    private func subscribe(port: IONotificationPortRef, to type: String, into iterator: inout io_iterator_t) {
        guard let matching = DualSenseDevice.matchingDictionary() else { return }
        let context = Unmanaged.passUnretained(self).toOpaque()
        let result = IOServiceAddMatchingNotification(port, type, matching, hotPlugCallback, context, &iterator)
        guard result == KERN_SUCCESS else {
            lock.lock()
            status.lastError = "подписка на события USB: \(IOKitError.describe(result))"
            lock.unlock()
            return
        }
        // The iterator has to be drained once or the notification never fires.
        drain(iterator)
    }

    private func removeHotPlugWatch() {
        for iterator in [matchedIterator, terminatedIterator] where iterator != 0 {
            IOObjectRelease(iterator)
        }
        matchedIterator = 0
        terminatedIterator = 0
        if let notifyPort {
            if let source = IONotificationPortGetRunLoopSource(notifyPort)?.takeUnretainedValue() {
                CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .defaultMode)
            }
            IONotificationPortDestroy(notifyPort)
        }
        notifyPort = nil
    }

    private func drain(_ iterator: io_iterator_t) {
        var service = IOIteratorNext(iterator)
        while service != 0 {
            IOObjectRelease(service)
            service = IOIteratorNext(iterator)
        }
    }

    /// Called on the HID thread after any plug or unplug. Rescanning instead of
    /// tracking individual services keeps one code path for "what is attached",
    /// which is the part that has to be right.
    fileprivate func rescanFromNotification(_ iterator: io_iterator_t) {
        drain(iterator)
        rescan()
    }

    // MARK: - Attach / detach

    private func rescan() {
        let candidates = DualSenseDevice.findAll().filter { $0.isUSB }

        if let current = device {
            // Still there? Registry ids are unique, so this also catches an
            // unplug-replug that happened between two notifications.
            if candidates.contains(where: { $0.registryID == attachedRegistryID }) {
                return
            }
            _ = current  // the held device is gone from the registry
            detachCurrent(notifyHost: true)
        }

        guard let candidate = candidates.first else { return }
        attach(candidate)
    }

    private func attach(_ candidate: DualSenseDevice) {
        let openResult = candidate.open()
        guard openResult == kIOReturnSuccess else {
            lock.lock()
            status.lastError = "не удалось открыть контроллер: \(IOKitError.describe(openResult))"
            lock.unlock()
            return
        }

        var blocks: [Wire.DeviceChannel.Descriptor] = []
        let usb = candidate.usbDescriptors()
        if let bytes = usb.device {
            blocks.append(.init(kind: .device, bytes: bytes))
        }
        if let bytes = usb.configuration {
            blocks.append(.init(kind: .configuration, bytes: bytes))
        }
        if let bytes = candidate.reportDescriptor {
            blocks.append(.init(kind: .hidReport, bytes: bytes))
        }

        // Without the device descriptor the receiver cannot build anything, so
        // an attach with missing blocks is worse than no attach at all.
        guard blocks.count == 3 else {
            candidate.close()
            lock.lock()
            status.lastError = "дескрипторы прочитаны не полностью (\(blocks.count) из 3)"
            lock.unlock()
            return
        }

        candidate.startReading(on: thread) { [weak self] report in
            self?.forward(report)
        }

        device = candidate
        attachedRegistryID = candidate.registryID

        lock.lock()
        descriptors = blocks
        status.connected = true
        status.product = candidate.product
        status.transport = candidate.transport
        status.attachAcknowledged = false
        status.lastError = nil
        lock.unlock()

        if forwarding {
            sender?.sendDeviceAttach(device: Self.deviceNumber, descriptors: blocks)
        }
    }

    private func detachCurrent(notifyHost: Bool) {
        guard let current = device else { return }
        current.stopReading()
        current.close()
        device = nil
        attachedRegistryID = 0

        lock.lock()
        descriptors = []
        status.connected = false
        status.battery = nil
        status.reportRate = 0
        status.attachAcknowledged = false
        lock.unlock()

        if notifyHost, forwarding {
            sender?.sendDeviceDetach(device: Self.deviceNumber)
        }
    }

    // MARK: - Input

    /// Runs on the HID thread at up to 250 Hz. Everything here is O(1).
    private func forward(_ report: [UInt8]) {
        if forwarding {
            sender?.sendDeviceInput(device: Self.deviceNumber, report: report)
        }

        lock.lock()
        reportsSinceTick &+= 1
        if forwarding { status.reportsForwarded &+= 1 }
        let watching = observers > 0
        let previous = liveStateStorage
        let tickCount = reportsSinceTick
        lock.unlock()

        // Battery is the only decoded field the plain status needs; parsing the
        // rest 250 times a second for a display that redraws 20 times would be
        // waste. The visualisation is the one thing that does want every report.
        let needsBattery = !watching && tickCount % 50 == 0
        guard watching || needsBattery else { return }
        guard let state = GamepadState.parse(report) else { return }

        lock.lock()
        status.battery = state.batteryDescription
        if watching {
            liveStateStorage = state
            liveStateAt = DispatchTime.now()
            if previous == nil || Self.hasInput(state, since: previous!) {
                liveInputAt = liveStateAt
            }
        }
        lock.unlock()
    }

    /// "Did the user touch anything" — used only to decide whether to draw at
    /// 60 Hz or at 8 Hz, so an approximate answer is fine and a cheap one is
    /// required.
    private static func hasInput(_ new: GamepadState, since old: GamepadState) -> Bool {
        if new.buttons != old.buttons || new.dpad != old.dpad { return true }
        if new.l2 != old.l2 || new.r2 != old.r2 { return true }
        if new.touch[0].active != old.touch[0].active || new.touch[1].active != old.touch[1].active { return true }
        // Sticks rest a couple of counts away from centre and jitter there; a
        // threshold keeps a resting controller from pinning the redraw at 60 Hz.
        let threshold: Int = 3
        func moved(_ a: UInt8, _ b: UInt8) -> Bool { abs(Int(a) - Int(b)) > threshold }
        return moved(new.left.x, old.left.x) || moved(new.left.y, old.left.y)
            || moved(new.right.x, old.right.x) || moved(new.right.y, old.right.y)
    }

    // MARK: - Output

    /// Takes a DEV_OUT report from the network and schedules a write.
    ///
    /// Never writes inline: `IOHIDDeviceSetReport` blocks for the length of the
    /// USB transaction, and the caller is the socket's receive loop.
    private func submitOutput(_ report: [UInt8]) {
        lock.lock()
        pendingOutput = report
        let alreadyScheduled = writeScheduled
        writeScheduled = true
        let elapsed = Double(DispatchTime.now().uptimeNanoseconds - lastWriteAt.uptimeNanoseconds) / 1e9
        lock.unlock()

        guard !alreadyScheduled else { return }
        let delay = max(0, Self.minimumWriteInterval - elapsed)
        outputQueue.asyncAfter(deadline: .now() + delay) { [weak self] in
            self?.drainOutput()
        }
    }

    private func drainOutput() {
        lock.lock()
        let report = pendingOutput
        pendingOutput = nil
        writeScheduled = false
        lastWriteAt = DispatchTime.now()
        let target = device
        lock.unlock()

        guard let report, let target else { return }
        let result = target.setOutputReport(report)
        noteLightbar(report)

        lock.lock()
        if result == kIOReturnSuccess {
            status.outputsApplied &+= 1
            // The receiver only sends output once it has built the virtual
            // device, so this doubles as the attach acknowledgement the protocol
            // never spelled out.
            status.attachAcknowledged = true
        } else {
            status.outputsRejected &+= 1
            status.lastError = "запись в контроллер: \(IOKitError.describe(result))"
        }
        lock.unlock()
    }
}

extension GamepadBridge {
    /// Reads back the colour the receiver just asked for, using exactly the
    /// byte offsets `GamepadOutput.encoded()` writes. Only the two flag bits
    /// that mean "the lightbar fields are meaningful" are trusted, so a report
    /// that only changes rumble does not blank the bar on screen.
    fileprivate func noteLightbar(_ report: [UInt8]) {
        guard report.count >= DualSenseReport.outputReportSize,
              report[0] == DualSenseReport.outputReportID else { return }
        let lightbarValid = report[2] & 0x04 != 0
        let lightOut = report[39] & 0x02 != 0 && report[42] & 0x02 != 0
        lock.lock()
        if lightOut {
            lightbar = .off
        } else if lightbarValid {
            lightbar = GamepadOutput.Color(red: report[45], green: report[46], blue: report[47])
        }
        lock.unlock()
    }
}

/// C callback: the bridge comes back through `refcon`.
private let hotPlugCallback: IOServiceMatchingCallback = { context, iterator in
    guard let context else { return }
    Unmanaged<GamepadBridge>.fromOpaque(context).takeUnretainedValue().rescanFromNotification(iterator)
}
