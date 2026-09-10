import Foundation
import IOKit
import IOKit.hid

/// Mirrors up to four chosen HID devices onto the device channel of the wire
/// protocol.
///
/// It owns the hot-plug watch, one input reader per device and one throttled
/// writer per device, and knows nothing about audio: `BridgeRuntime` hands it a
/// `Sender` and it adds a second stream to the same socket. If anything here
/// fails, the voice path is unaffected by construction — nothing in this file
/// can throw into it.
///
/// Nothing here knows what a DualSense is either. Descriptors and reports go out
/// as the hardware produced them; the only place a model is recognised is
/// `DeviceProfile`, and all it buys is a feature-report snapshot and a decoded
/// copy for the screen.
final class DeviceBridge {
    /// The protocol carries a device number 0…3. Four is not a technical limit
    /// but a chosen one: each device is a separate virtual USB on Windows and a
    /// separate report stream, and games that tell more than four controllers
    /// apart barely exist.
    static let maxDevices = 4

    /// Devices accept output at the same rate they send input. Writing faster
    /// wastes a blocking USB transaction per report and nothing else.
    private static let minimumWriteInterval = 0.004

    /// One forwarded device, as the UI sees it.
    struct DeviceStatus: Identifiable {
        var number: UInt8
        var identity: DeviceIdentity
        var product: String
        var manufacturer: String
        var transport: String
        var category: DeviceCategory
        /// Non-nil when a model was recognised: "DualSense" and nothing else today.
        var profileName: String?
        var canVisualise = false
        /// Measured input reports per second, refreshed once a second.
        var reportRate: Double = 0
        var reportsForwarded: UInt64 = 0
        var outputsApplied: UInt64 = 0
        var outputsRejected: UInt64 = 0
        var attachAcknowledged = false
        var battery: String?
        var lastError: String?
        /// Seconds since the last input report — the activity light.
        var idle: TimeInterval = 0

        // HD haptics. All nil and all zero for a device with no audio function,
        // and for every device until Windows is told to send any.

        /// The CoreAudio device the PCM is played into, once one has been found.
        var hapticDevice: String?
        /// Blocks are arriving and the audio unit is running.
        var hapticsPlaying = false
        var hapticBlocks: UInt64 = 0
        /// Blocks the numbering says were lost on the way here.
        var hapticBlocksLost: UInt64 = 0
        /// Times the buffer ran dry mid-stream: the link is not keeping up.
        var hapticUnderruns: UInt64 = 0
        /// Blocks that arrived on top of a full buffer, so older audio was
        /// dropped to keep latency down.
        var hapticBlocksDropped: UInt64 = 0
        var hapticError: String?

        var id: UInt8 { number }
    }

    /// One row of the picker.
    struct Available: Identifiable {
        var identity: DeviceIdentity
        var name: String
        var manufacturer: String
        var transport: String
        var category: DeviceCategory
        var eligibility: DeviceEligibility
        var isSelected: Bool
        /// Assigned device number, when this one is actually being forwarded.
        var forwardedAs: UInt8?
        /// True when the device is selected and present but every slot is taken.
        var crowdedOut: Bool

        var id: String { identity.description }
    }

    struct Status {
        var devices: [DeviceStatus] = []
        var available: [Available] = []
        /// Something wrong that belongs to no single device.
        var lastError: String?
        /// True while reports are being forwarded. False means devices are only
        /// being read for the on-screen visualisation.
        var forwarding = false
        var slotsUsed: Int { devices.count }
    }

    /// Everything about one attached device. All mutable state is behind the
    /// bridge's single lock; four devices at 250 Hz is a thousand uncontended
    /// lock operations a second, which is not a cost worth optimising.
    private final class Forwarded {
        let number: UInt8
        let device: HIDDevice
        let registryID: UInt64
        /// Shared by every interface of one physical device; the key that says
        /// "this pad is already forwarded" when it has more than one HID node.
        let locationID: Int
        let identity: DeviceIdentity
        let profile: DeviceProfile?
        let descriptors: [Wire.DeviceChannel.Descriptor]
        var status: DeviceStatus

        var reportsSinceTick: UInt64 = 0
        var lastReportAt = DispatchTime.now()

        // Coalescing state for the writer. A burst of DEV_OUT packets collapses
        // into the newest one: the report is absolute state, not a delta, so
        // replacing a pending write loses nothing.
        var pendingOutput: [UInt8]?
        var writeScheduled = false
        var lastWriteAt = DispatchTime.now()

        // Live state for the visualisation, only ever filled for a device whose
        // model has a profile.
        var liveState: GamepadState?
        var liveInputAt = DispatchTime.now()
        var lightbar: GamepadOutput.Color?

        /// Plays HD haptics back into this controller. Built for every device,
        /// because the audio node can appear a moment after the HID one and a
        /// player that found nothing costs a nil check.
        let haptics: HapticPlayer

        init(number: UInt8, device: HIDDevice, identity: DeviceIdentity,
             profile: DeviceProfile?, descriptors: [Wire.DeviceChannel.Descriptor]) {
            haptics = HapticPlayer(
                vendorID: device.vendorID,
                productID: device.productID,
                locationID: device.locationID
            )
            self.number = number
            self.device = device
            self.registryID = device.registryID
            self.locationID = device.locationID
            self.identity = identity
            self.profile = profile
            self.descriptors = descriptors
            status = DeviceStatus(
                number: number,
                identity: identity,
                product: device.displayName,
                manufacturer: device.manufacturer,
                transport: device.transport,
                category: device.category,
                profileName: profile?.name,
                canVisualise: profile?.kind == .dualSense
            )
        }
    }

    private let thread = HIDRunLoopThread()
    private let lock = NSLock()
    private let outputQueue = DispatchQueue(label: "hexbridge.devices.output", qos: .userInteractive)
    /// Haptic blocks, apart from both the socket and the HID writer. See `playHaptics`.
    private let hapticQueue = DispatchQueue(label: "hexbridge.devices.haptics", qos: .userInteractive)

    private weak var sender: Sender?
    /// False when the bridge is only feeding the visualisation and the picker.
    private var forwarding = false
    private var selection: Set<DeviceIdentity> = []

    /// Attached devices by number. Written only on the HID thread, read under
    /// the lock from everywhere else.
    private var forwarded: [UInt8: Forwarded] = [:]
    private var available: [Available] = []
    private var generalError: String?
    /// Registry ids that failed to attach, with the reason. Kept so a device
    /// that cannot be opened is not retried once a second forever; a plug event
    /// clears it.
    private var failures: [UInt64: String] = [:]

    // Live state is parsed only while something is drawing it, which is the
    // difference between 0.3 % and 3 % of a core with nothing on screen.
    private var observers = 0

    /// Counts `tick`s so a device that refused to open is retried occasionally
    /// rather than once a second or never again.
    private var ticksSinceRetry = 0

    private var notifyPort: IONotificationPortRef?
    private var matchedIterator: io_iterator_t = 0
    private var terminatedIterator: io_iterator_t = 0
    private var running = false

    // MARK: - Lifecycle

    /// Starts watching for devices. Never throws: an unreadable device degrades
    /// to a row with a reason in it, it does not stop the bridge.
    func start(sender: Sender?, forwarding: Bool, selection: Set<DeviceIdentity>) {
        guard !running else { return }
        running = true
        lock.lock()
        self.forwarding = forwarding
        self.selection = selection
        lock.unlock()
        _ = bind(sender)

        thread.start(name: "hexbridge.devices")
        thread.sync { [self] in
            installHotPlugWatch()
            rescan()
        }
    }

    func stop() {
        guard running else { return }
        running = false
        sender?.onDeviceOutput = nil
        sender?.onDeviceAck = nil
        sender?.onHaptic = nil

        thread.sync { [self] in
            detachAll(notifyHost: true)
            removeHotPlugWatch()
        }
        thread.stop()
        sender = nil
    }

    /// Re-points the bridge at a new socket, or changes the selection, without
    /// tearing down the readers. Ticking a box must not make an unrelated device
    /// on screen go dead for a second.
    func update(sender: Sender?, forwarding: Bool, selection: Set<DeviceIdentity>) {
        guard running else { return }
        // A new socket means a new session, and the receiver's session was reset
        // with it: whatever it had acknowledged, it has forgotten.
        let reconnected = bind(sender)

        lock.lock()
        let wasForwarding = self.forwarding
        let changed = wasForwarding != forwarding || self.selection != selection
        self.forwarding = forwarding
        self.selection = selection
        if !forwarding || reconnected {
            for entry in forwarded.values { entry.status.attachAcknowledged = false }
        }
        let held = forwarded.values.map(\.number)
        lock.unlock()

        // Switching the passthrough off takes the devices away from Windows but
        // leaves them open here, still feeding the outline on screen.
        if wasForwarding, !forwarding {
            for number in held { sender?.sendDeviceDetach(device: number) }
        }

        guard changed || reconnected else { return }
        thread.async { [self] in
            // A device that was refused while the selection excluded it deserves
            // a fresh try now that it does not.
            failures.removeAll()
            rescan()
            announceAll()
        }
    }

    /// Sends `DEV_DETACH` for everything held and lets the devices go, right now.
    ///
    /// Called while the socket is still open, on the way down. The receiver has a
    /// three-second session timeout as a backstop, but three seconds of a
    /// controller Windows still believes in is three seconds of a game reading a
    /// stick that stopped moving.
    func releaseAll() {
        guard running else { return }
        thread.sync { [self] in detachAll(notifyHost: true) }
    }

    /// Points the bridge at a socket. Returns true when that is a different
    /// socket from the one it had.
    @discardableResult
    private func bind(_ sender: Sender?) -> Bool {
        let changed = self.sender !== sender
        if let old = self.sender, old !== sender {
            old.onDeviceOutput = nil
            old.onDeviceAck = nil
            old.onHaptic = nil
        }
        self.sender = sender
        sender?.onDeviceOutput = { [weak self] device, report in
            self?.submitOutput(device: device, report: report)
        }
        sender?.onDeviceAck = { [weak self] device in
            self?.noteAck(device)
        }
        sender?.onHaptic = { [weak self] block in
            self?.playHaptics(block)
        }
        return changed
    }

    // MARK: - Live state for the visualisation

    /// Balanced calls from the views that draw a device. While the count is zero
    /// no report is parsed at all.
    func addObserver() {
        lock.lock()
        observers += 1
        lock.unlock()
    }

    func removeObserver() {
        lock.lock()
        observers = max(0, observers - 1)
        if observers == 0 {
            for entry in forwarded.values { entry.liveState = nil }
        }
        lock.unlock()
    }

    /// The newest decoded report for one device, the lightbar colour the
    /// receiver asked for, and how long ago it last moved. Nil for a device with
    /// no profile — there is nothing honest to draw.
    func liveState(device number: UInt8) -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        lock.lock()
        defer { lock.unlock() }
        guard let entry = forwarded[number], let state = entry.liveState else { return nil }
        let idle = Double(DispatchTime.now().uptimeNanoseconds &- entry.liveInputAt.uptimeNanoseconds) / 1e9
        return (state, entry.lightbar, idle)
    }

    /// Called once a second from the runtime's keepalive timer.
    func tick() {
        let now = DispatchTime.now()
        lock.lock()
        let forwarding = self.forwarding
        var needsAttach: [(UInt8, [Wire.DeviceChannel.Descriptor])] = []
        var players: [(UInt8, HapticPlayer, Bool)] = []
        for entry in forwarded.values {
            entry.status.reportRate = Double(entry.reportsSinceTick)
            entry.reportsSinceTick = 0
            entry.status.idle = Double(now.uptimeNanoseconds &- entry.lastReportAt.uptimeNanoseconds) / 1e9
            if forwarding, !entry.status.attachAcknowledged, !entry.descriptors.isEmpty {
                needsAttach.append((entry.number, entry.descriptors))
            }
            players.append((entry.number, entry.haptics, entry.profile?.kind == .dualSense))
        }
        lock.unlock()

        // Re-announce until the receiver proves it noticed. DEV_ACK is what
        // stops this; without it a receiver that started late would never learn
        // the device exists.
        for (number, descriptors) in needsAttach {
            sender?.sendDeviceAttach(device: number, descriptors: descriptors)
        }

        // Outside the lock: `tick` can tear an audio unit down, and CoreAudio
        // takes its own time about that.
        for (number, player, isDualSense) in players {
            let streaming = player.tick()
            // The controller boots with its haptics muted and a game's own output
            // report can mute them again by selecting classic rumble, so the
            // unmute is re-asserted for as long as PCM keeps arriving. One HID
            // write a second against a device already taking 250 reports a second
            // is not a cost worth measuring.
            if streaming, isDualSense { assertHapticsEnabled(device: number) }
            publish(player.status(), for: number)
        }

        // A device that failed to open stays invisible to the hot-plug
        // notification, which already fired. Rescanning also refreshes the
        // picker, which is the only thing that populates it while nothing is
        // selected.
        ticksSinceRetry += 1
        let retry = ticksSinceRetry >= 10
        if retry { ticksSinceRetry = 0 }
        thread.async { [self] in
            // Ten seconds between retries of a device that would not open. Once a
            // second would mean a permission prompt or a busy device produced a
            // stream of identical failures; never would mean a controller Steam
            // held for a moment stayed dead until it was unplugged.
            if retry { failures.removeAll() }
            rescan()
        }
    }

    func snapshot() -> Status {
        lock.lock()
        defer { lock.unlock() }
        return Status(
            devices: forwarded.values.map(\.status).sorted { $0.number < $1.number },
            available: available,
            lastError: generalError,
            forwarding: forwarding
        )
    }

    // MARK: - Hot plug

    private func installHotPlugWatch() {
        guard let port = IONotificationPortCreate(kIOMainPortDefault) else { return }
        notifyPort = port
        if let source = IONotificationPortGetRunLoopSource(port)?.takeUnretainedValue() {
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .defaultMode)
        }

        subscribe(port: port, to: kIOMatchedNotification, into: &matchedIterator)
        subscribe(port: port, to: kIOTerminatedNotification, into: &terminatedIterator)
    }

    private func subscribe(port: IONotificationPortRef, to type: String, into iterator: inout io_iterator_t) {
        guard let matching = HIDDevice.matchingDictionary() else { return }
        let context = Unmanaged.passUnretained(self).toOpaque()
        let result = IOServiceAddMatchingNotification(port, type, matching, hotPlugCallback, context, &iterator)
        guard result == KERN_SUCCESS else {
            lock.lock()
            generalError = "подписка на события USB: \(IOKitError.describe(result))"
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
        // Something changed on the bus, so a device that refused to open before
        // may well be a different device now.
        failures.removeAll()
        rescan()
    }

    // MARK: - Attach / detach

    /// The whole of "what should be forwarded right now", recomputed from the
    /// registry. Runs on the HID thread.
    private func rescan() {
        // A block queued on the run loop can still arrive after `stop()`; taking
        // a device back at that point would leave it open with nobody to close it.
        guard running else { return }

        let present = HIDDevice.findAll().filter { !$0.isBuiltIn }
        let physical = Self.onePerPhysicalDevice(present)

        lock.lock()
        let forwarding = self.forwarding
        let selection = self.selection
        lock.unlock()

        // 1. Drop everything that is gone or no longer chosen.
        //
        //    Deliberately not "or forwarding is off". Reading a device is not
        //    forwarding it: §7.3 wants the outline of a chosen pad to stay live
        //    with the switch off, because that is where the user finds out their
        //    controller is read correctly before Windows is involved at all.
        let liveIDs = Set(present.map(\.registryID))
        for entry in Array(forwarded.values) {
            if !liveIDs.contains(entry.registryID) || !selection.contains(entry.identity) {
                detach(entry, notifyHost: true)
            }
        }

        // 2. Fill free slots, lowest number first, in registry order so the same
        //    two pads come back with the same numbers after a replug.
        var attachedIDs = Set(forwarded.values.map(\.registryID))
        var attachedLocations = Set(forwarded.values.map(\.locationID))
        var crowdedOut = Set<UInt64>()
        for candidate in physical
        where selection.contains(candidate.identity)
            && !attachedIDs.contains(candidate.registryID)
            && !attachedLocations.contains(candidate.locationID)
            && DeviceEligibility.of(candidate) == .eligible
            && failures[candidate.registryID] == nil {

            guard let number = freeSlot() else {
                crowdedOut.insert(candidate.registryID)
                continue
            }
            if attach(candidate, number: number, forwarding: forwarding) {
                attachedIDs.insert(candidate.registryID)
                attachedLocations.insert(candidate.locationID)
            }
        }

        // 3. Publish the picker.
        publishAvailable(physical, selection: selection, crowdedOut: crowdedOut)
    }

    /// One HID node per physical device.
    ///
    /// A composite device shows up once per HID interface, and each of those has
    /// its own registry id. Forwarding two of them would burn two of the four
    /// slots on one keyboard and hand Windows the same configuration descriptor
    /// twice. `locationID` is shared by every interface of one device and
    /// differs between two identical devices in two ports, which is exactly the
    /// distinction needed; ties are broken by the lowest `bInterfaceNumber`,
    /// because that is the interface the receiver rebuilds around.
    private static func onePerPhysicalDevice(_ devices: [HIDDevice]) -> [HIDDevice] {
        var best: [Int: HIDDevice] = [:]
        var order: [Int] = []
        for device in devices {
            let key = device.locationID
            if let existing = best[key] {
                if device.interfaceNumber < existing.interfaceNumber { best[key] = device }
            } else {
                best[key] = device
                order.append(key)
            }
        }
        return order.compactMap { best[$0] }
    }

    private func freeSlot() -> UInt8? {
        (0..<UInt8(Self.maxDevices)).first { forwarded[$0] == nil }
    }

    private func publishAvailable(_ present: [HIDDevice], selection: Set<DeviceIdentity>, crowdedOut: Set<UInt64>) {
        let byRegistry = Dictionary(uniqueKeysWithValues: forwarded.values.map { ($0.registryID, $0.number) })
        var rows: [Available] = []
        var seen = Set<DeviceIdentity>()

        for device in present {
            let identity = device.identity
            // Two interfaces of one composite device share an identity. Showing
            // it twice would let the user tick a box that then does nothing.
            guard seen.insert(identity).inserted else { continue }
            let eligibility = DeviceEligibility.of(device)
            rows.append(Available(
                identity: identity,
                name: device.displayName,
                manufacturer: device.manufacturer,
                transport: device.transport,
                category: device.category,
                eligibility: eligibility,
                isSelected: selection.contains(identity),
                forwardedAs: byRegistry[device.registryID],
                crowdedOut: crowdedOut.contains(device.registryID)
            ))
        }

        // Usable things first, then by kind, then by name: a list the user reads
        // top to bottom should start with what they can actually pick.
        rows.sort {
            if ($0.eligibility == .eligible) != ($1.eligibility == .eligible) {
                return $0.eligibility == .eligible
            }
            if $0.category != $1.category { return order($0.category) < order($1.category) }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }

        lock.lock()
        available = rows
        lock.unlock()
    }

    private func order(_ category: DeviceCategory) -> Int {
        switch category {
        case .gamepad: return 0
        case .other: return 1
        case .pointer: return 2
        case .keyboard: return 3
        }
    }

    /// Opens one device, reads everything the receiver needs, and announces it.
    /// Returns false when the device could not be taken; the reason is recorded
    /// so the next tick does not try again.
    @discardableResult
    private func attach(_ candidate: HIDDevice, number: UInt8, forwarding: Bool) -> Bool {
        let openResult = candidate.open()
        guard openResult == kIOReturnSuccess else {
            note(failure: "не удалось открыть «\(candidate.displayName)»: \(IOKitError.describe(openResult))",
                 for: candidate.registryID)
            return false
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

        // Without all three the receiver cannot build anything, so an attach
        // with missing blocks is worse than no attach at all.
        guard blocks.count == 3 else {
            candidate.close()
            note(failure: "«\(candidate.displayName)»: дескрипторы прочитаны не полностью (\(blocks.count) из 3)"
                    + (usb.error.map { ", \($0)" } ?? ""),
                 for: candidate.registryID)
            return false
        }

        // Feature-report snapshots, when the model is one we know. Windows has
        // to answer GET_REPORT from games and from Steam without a network round
        // trip, and the values are static for the session.
        let profile = DeviceProfile.of(vendorID: candidate.vendorID, productID: candidate.productID)
        var snapshotError: String?
        for wanted in profile?.featureReports ?? [] {
            let result = candidate.featureReport(id: wanted.id, length: wanted.length)
            guard result.status == kIOReturnSuccess, result.data.count >= 2 else {
                snapshotError = "feature-репорт 0x\(String(format: "%02X", wanted.id)) не прочитан: "
                    + IOKitError.describe(result.status)
                continue
            }
            blocks.append(.init(kind: .featureReport, bytes: result.data))
        }

        let entry = Forwarded(
            number: number,
            device: candidate,
            identity: candidate.identity,
            profile: profile,
            descriptors: blocks
        )
        entry.status.lastError = snapshotError

        candidate.startReading(on: thread) { [weak self] report in
            self?.forward(report, from: number)
        }

        lock.lock()
        forwarded[number] = entry
        generalError = nil
        lock.unlock()

        if forwarding {
            sender?.sendDeviceAttach(device: number, descriptors: blocks)
        }
        return true
    }

    private func note(failure: String, for registryID: UInt64) {
        failures[registryID] = failure
        lock.lock()
        generalError = failure
        lock.unlock()
    }

    private func detach(_ entry: Forwarded, notifyHost: Bool) {
        // Before the HID handle goes: the audio unit holds the same physical
        // device, and letting it run against hardware that has been unplugged is
        // how a render callback finds itself writing into nothing.
        entry.haptics.stop()
        entry.device.stopReading()
        entry.device.close()

        lock.lock()
        forwarded[entry.number] = nil
        let wasForwarding = forwarding
        lock.unlock()

        // Nothing to take back if the receiver was never told about it.
        if notifyHost, wasForwarding {
            sender?.sendDeviceDetach(device: entry.number)
        }
    }

    private func detachAll(notifyHost: Bool) {
        for entry in Array(forwarded.values) { detach(entry, notifyHost: notifyHost) }
    }

    /// Re-sends `DEV_ATTACH` for everything currently held. Used when the socket
    /// is replaced or forwarding is switched back on.
    private func announceAll() {
        lock.lock()
        let entries = forwarded.values.map { ($0.number, $0.descriptors) }
        let forwarding = self.forwarding
        lock.unlock()
        guard forwarding else { return }
        for (number, descriptors) in entries where !descriptors.isEmpty {
            sender?.sendDeviceAttach(device: number, descriptors: descriptors)
        }
    }

    // MARK: - Input

    /// Runs on the HID thread at up to 250 Hz per device. Everything here is O(1).
    private func forward(_ report: [UInt8], from number: UInt8) {
        lock.lock()
        guard let entry = forwarded[number] else {
            lock.unlock()
            return
        }
        let forwarding = self.forwarding
        entry.reportsSinceTick &+= 1
        entry.lastReportAt = DispatchTime.now()
        if forwarding { entry.status.reportsForwarded &+= 1 }
        let watching = observers > 0
        let tickCount = entry.reportsSinceTick
        let parse = entry.profile?.parse
        let previous = entry.liveState
        lock.unlock()

        if forwarding {
            sender?.sendDeviceInput(device: number, report: report)
        }

        // Battery is the only decoded field the plain status needs; parsing the
        // rest 250 times a second for a display that redraws 20 times would be
        // waste. The visualisation is the one thing that does want every report.
        guard let parse else { return }
        let needsBattery = !watching && tickCount % 50 == 0
        guard watching || needsBattery else { return }
        guard let state = parse(report) else { return }

        lock.lock()
        if let entry = forwarded[number] {
            entry.status.battery = state.batteryDescription
            if watching {
                entry.liveState = state
                let now = DispatchTime.now()
                if previous == nil || Self.hasInput(state, since: previous!) {
                    entry.liveInputAt = now
                }
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

    /// Marks a device acknowledged. `DEV_ACK` is the receiver saying it built
    /// the virtual device; without it the sender would repeat a 700-byte
    /// `DEV_ATTACH` once a second forever at a game that never rumbles.
    private func noteAck(_ number: UInt8) {
        lock.lock()
        forwarded[number]?.status.attachAcknowledged = true
        lock.unlock()
    }

    // MARK: - HD haptics

    /// One block of PCM off the wire, on its way into the controller's own
    /// actuators. Arrives on the connection queue at up to 200 blocks a second.
    ///
    /// Handed to a queue of its own rather than played inline. The first block of
    /// a stream is the one that opens the CoreAudio device, and opening a USB
    /// audio device takes tens of milliseconds — or, if the controller is being
    /// unplugged at that moment, considerably longer. The socket's receive loop
    /// is not the place to find that out: it is also carrying the rumble commands
    /// for three other devices.
    ///
    /// Serial, so blocks keep their order, and separate from the output queue,
    /// which blocks for the length of a USB transaction on every HID write.
    private func playHaptics(_ block: Wire.Haptics.Block) {
        hapticQueue.async { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let entry = self.forwarded[block.device]
            let isDualSense = entry?.profile?.kind == .dualSense
            self.lock.unlock()

            // A block for a device we are not holding is not worth counting as an
            // error: the receiver can still be streaming into a controller that
            // was unplugged half a second ago.
            guard let entry else { return }

            if entry.haptics.play(block), isDualSense {
                // First block of a stream. Nothing has cleared the controller's
                // haptic mute yet, and until something does, the PCM is carried
                // the whole way and thrown away at the last step.
                self.assertHapticsEnabled(device: block.device)
            }
        }
    }

    /// Sends the unmute report on the writer queue, so it serialises with the
    /// forwarded output reports instead of racing one mid-transaction.
    private func assertHapticsEnabled(device number: UInt8) {
        outputQueue.async { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let target = self.forwarded[number]?.device
            self.lock.unlock()
            _ = target?.setOutputReport(DualSenseReport.audioHapticsEnable)
        }
    }

    private func publish(_ status: HapticPlayer.Status, for number: UInt8) {
        lock.lock()
        if let entry = forwarded[number] {
            entry.status.hapticDevice = status.deviceName
            entry.status.hapticsPlaying = status.playing
            entry.status.hapticBlocks = status.blocksPlayed
            entry.status.hapticBlocksLost = status.blocksLost
            entry.status.hapticUnderruns = status.underruns
            entry.status.hapticBlocksDropped = status.blocksDropped
            entry.status.hapticError = status.lastError
        }
        lock.unlock()
    }

    /// Takes a DEV_OUT report from the network and schedules a write.
    ///
    /// Never writes inline: `IOHIDDeviceSetReport` blocks for the length of the
    /// USB transaction, and the caller is the socket's receive loop.
    private func submitOutput(device number: UInt8, report: [UInt8]) {
        lock.lock()
        guard let entry = forwarded[number] else {
            lock.unlock()
            return
        }
        entry.pendingOutput = report
        let alreadyScheduled = entry.writeScheduled
        entry.writeScheduled = true
        let elapsed = Double(DispatchTime.now().uptimeNanoseconds &- entry.lastWriteAt.uptimeNanoseconds) / 1e9
        lock.unlock()

        guard !alreadyScheduled else { return }
        let delay = max(0, Self.minimumWriteInterval - elapsed)
        outputQueue.asyncAfter(deadline: .now() + delay) { [weak self] in
            self?.drainOutput(device: number)
        }
    }

    private func drainOutput(device number: UInt8) {
        lock.lock()
        guard let entry = forwarded[number] else {
            lock.unlock()
            return
        }
        let report = entry.pendingOutput
        entry.pendingOutput = nil
        entry.writeScheduled = false
        entry.lastWriteAt = DispatchTime.now()
        let target = entry.device
        lock.unlock()

        guard let report else { return }
        let result = target.setOutputReport(report)
        noteLightbar(report, device: number)

        lock.lock()
        if let entry = forwarded[number] {
            if result == kIOReturnSuccess {
                entry.status.outputsApplied &+= 1
                // The receiver only sends output once it has built the virtual
                // device, so this doubles as an acknowledgement for a receiver
                // too old to send DEV_ACK.
                entry.status.attachAcknowledged = true
            } else {
                entry.status.outputsRejected &+= 1
                entry.status.lastError = "запись в устройство: \(IOKitError.describe(result))"
            }
        }
        lock.unlock()
    }
}

extension DeviceBridge {
    /// Reads back the colour the receiver just asked for, using exactly the byte
    /// offsets `GamepadOutput.encoded()` writes. DualSense-shaped, and applied
    /// only to a device whose profile says so: on anything else those offsets
    /// mean something entirely different.
    fileprivate func noteLightbar(_ report: [UInt8], device number: UInt8) {
        lock.lock()
        let isDualSense = forwarded[number]?.profile?.kind == .dualSense
        lock.unlock()

        guard isDualSense,
              report.count >= DualSenseReport.outputReportSize,
              report[0] == DualSenseReport.outputReportID else { return }
        let lightbarValid = report[2] & 0x04 != 0
        let lightOut = report[39] & 0x02 != 0 && report[42] & 0x02 != 0

        lock.lock()
        if lightOut {
            forwarded[number]?.lightbar = .off
        } else if lightbarValid {
            forwarded[number]?.lightbar = GamepadOutput.Color(
                red: report[45], green: report[46], blue: report[47]
            )
        }
        lock.unlock()
    }
}

/// C callback: the bridge comes back through `refcon`.
private let hotPlugCallback: IOServiceMatchingCallback = { context, iterator in
    guard let context else { return }
    Unmanaged<DeviceBridge>.fromOpaque(context).takeUnretainedValue().rescanFromNotification(iterator)
}
