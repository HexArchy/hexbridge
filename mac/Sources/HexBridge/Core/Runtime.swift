import CryptoKit
import Foundation
import Network

/// Owns the capture → encode → send pipeline.
///
/// This used to live at the top level of `main.swift`. It is a type now because
/// the menu bar UI needs the same wiring and has to be able to swap a device or
/// a bitrate at runtime — something a pile of top-level `let`s cannot do.
/// `@unchecked Sendable` states an invariant this type has always had rather
/// than adding one: `start`, `restart` and `restartCapture` are called from a
/// background queue precisely because they can block on CoreAudio and on the
/// TCC prompt, while `snapshot`, `drainPeak`, `muted`, `isRunning` and the
/// device accessors are read from the main thread at 20 Hz. Everything they
/// touch is either immutable or behind `peakLock` / `deviceLock` / a lock
/// inside `Sender` and `DeviceBridge`. The compiler cannot see that, and the
/// alternative — an actor — would make the audio path `await` its own state.
final class BridgeRuntime: @unchecked Sendable {
    /// Where `config` is persisted. The UI writes through to this path so the
    /// CLI and the UI never disagree about what the current settings are.
    let configPath: URL

    /// Mutating this does nothing on its own: the caller decides whether the
    /// change is live (`setGain`) or needs a pipeline rebuild (`restart`).
    var config: Config

    private var sender: Sender?
    private var encoder: VoiceEncoder?
    private var capture: AudioCapture?
    /// Read from the keepalive timer's own queue as well as from the main
    /// thread, so the reference itself is behind a lock. The bridge's own state
    /// is already internally synchronised.
    private let deviceLock = NSLock()
    private var storedDevices: DeviceBridge?
    private var deviceBridge: DeviceBridge? {
        get {
            deviceLock.lock()
            defer { deviceLock.unlock() }
            return storedDevices
        }
        set {
            deviceLock.lock()
            storedDevices = newValue
            deviceLock.unlock()
        }
    }

    /// Raised by the views that draw a device or show the picker. The bridge is
    /// kept alive while it is non-zero even with the passthrough switched off:
    /// §7.3 requires the "подключён, но не проброшен" state to show a live
    /// outline, and the picker cannot list devices without a running scan.
    private var deviceObservers = 0
    private var helloTimer: DispatchSourceTimer?
    private var bulkTimer: DispatchSourceTimer?

    /// The reliable-delivery layer (PROTOCOL.md, «Надёжная передача крупных
    /// объектов»). It lives on the runtime rather than inside a feature because
    /// it is transport, not clipboard: the day file transfer arrives it asks the
    /// same channel to deliver an object and nothing here changes.
    let bulk = BulkChannel(send: { _, _ in })

    // The hello timer used to run on the main queue. It has its own queue now so
    // that a busy UI run loop cannot delay the keepalive the host uses to decide
    // whether we are still there.
    private let timerQueue = DispatchQueue(label: "hexbridge.runtime")

    private let peakLock = NSLock()
    private var peakSinceLastRead: Float = 0

    init(config: Config, configPath: URL) {
        self.config = config
        self.configPath = configPath
    }

    // MARK: - Lifecycle

    var isRunning: Bool { capture != nil }
    var deviceName: String { capture?.deviceName ?? "—" }
    var inputFormatDescription: String { capture?.inputFormatDescription ?? "—" }

    var muted: Bool {
        get { sender?.muted ?? false }
        set { sender?.muted = newValue }
    }

    /// Validates the config and brings the whole pipeline up. Throws with a
    /// user-readable `description` for every failure mode.
    func start() throws {
        let key = try config.symmetricKey()
        let hostPort = try config.endpointParts()

        let endpoint = NWEndpoint.hostPort(
            host: NWEndpoint.Host(hostPort.host),
            port: NWEndpoint.Port(rawValue: hostPort.port)!
        )

        let sender = Sender(target: endpoint, key: key, nodeName: config.name)
        let encoder = try VoiceEncoder(
            bitrate: Int32(config.bitrate),
            complexity: 10,
            fec: true,
            expectedLossPercent: Int32(config.expectedLossPercent)
        )

        self.sender = sender
        self.encoder = encoder

        // Before `start()`: the DEV_OUT and DEV_ACK handlers have to be
        // installed before the receive loop can deliver anything to them.
        reconcileDevices()

        // The channel outlives individual sockets, so it is re-pointed rather
        // than rebuilt: a `restart` must not leave a feature holding a dead one.
        bulk.rebind { [weak sender] type, payload in
            sender?.sendBulk(type: type, payload: payload)
        }
        sender.onBulkPacket = { [weak self] type, payload in
            self?.bulk.handle(type: type, payload: payload)
        }

        sender.start()

        do {
            try startCapture()
        } catch {
            // Leave nothing half-started: the UI retries by calling `start` again.
            stop()
            throw error
        }

        let timer = DispatchSource.makeTimerSource(queue: timerQueue)
        timer.schedule(deadline: .now(), repeating: .seconds(1))
        // One timer for both keepalives: the device re-announce has the same
        // period and the same reason to exist as HELLO.
        timer.setEventHandler { [weak sender, weak self] in
            sender?.sendHello()
            self?.deviceBridge?.tick()
        }
        timer.resume()
        helloTimer = timer

        // Bulk needs a much finer clock than the keepalive: the contract's ack is
        // every 200 ms and chunks are paced by a token bucket, both of which turn
        // into «once a second» on the hello timer.
        let bulkTimer = DispatchSource.makeTimerSource(queue: timerQueue)
        bulkTimer.schedule(deadline: .now(), repeating: .milliseconds(20))
        bulkTimer.setEventHandler { [weak self] in self?.bulk.tick() }
        bulkTimer.resume()
        self.bulkTimer = bulkTimer
    }

    func stop() {
        helloTimer?.cancel()
        helloTimer = nil
        bulkTimer?.cancel()
        bulkTimer = nil
        bulk.reset()
        capture?.stop()
        capture = nil
        // Goodbye before the socket goes: a DEV_DETACH that misses the send
        // window leaves Windows holding a virtual device until the session
        // times out three seconds later.
        deviceBridge?.releaseAll()
        sender?.stop()
        sender = nil
        encoder = nil
        // Keeps reading devices if a view is watching them: closing the
        // pipeline should not blank the visualisation the user is looking at.
        reconcileDevices()
    }

    func restart() throws {
        stop()
        try start()
    }

    /// Rebinds CoreAudio without touching the transport, so switching a
    /// microphone does not reset the packet counters or the room session.
    func restartCapture() throws {
        capture?.stop()
        capture = nil
        try startCapture()
    }

    /// Live: the audio thread reads the field on the next 20 ms frame.
    func setGain(_ gain: Double) {
        capture?.gain = Float(gain)
    }

    // MARK: - Telemetry

    /// Peak sample seen since the previous call, then reset. Exactly one poller
    /// is expected — the CLI stats line in headless mode, the UI timer otherwise.
    func drainPeak() -> Float {
        peakLock.lock()
        defer { peakLock.unlock() }
        let value = peakSinceLastRead
        peakSinceLastRead = 0
        return value
    }

    func snapshot() -> (sent: UInt64, bytes: UInt64, pong: Date?, rtt: Double?, received: UInt64, lost: UInt64, error: String?) {
        sender?.snapshot() ?? (0, 0, nil, nil, 0, 0, nil)
    }

    /// nil when nothing is reading devices at all, which the UI shows
    /// differently from "reading and nothing is plugged in".
    func deviceStatus() -> DeviceBridge.Status? {
        deviceBridge?.snapshot()
    }

    /// Newest decoded report for one on-screen device (§8). Nil unless the
    /// model has a profile that can decode it.
    func deviceLiveState(_ number: UInt8) -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        deviceBridge?.liveState(device: number)
    }

    // MARK: - Device lifecycle

    /// Balanced pair, called by the views that draw a device or list devices.
    func beginDeviceObservation() {
        deviceObservers += 1
        reconcileDevices()
        deviceBridge?.addObserver()
    }

    func endDeviceObservation() {
        guard deviceObservers > 0 else { return }
        deviceObservers -= 1
        deviceBridge?.removeObserver()
        reconcileDevices()
    }

    /// Applies a change to the switch or to the chosen list without rebuilding
    /// the pipeline. The switch used to be a restart-required setting; it no
    /// longer is, because the HID readers and the socket have nothing to do
    /// with each other.
    func applyDeviceSetting() {
        reconcileDevices()
    }

    private func reconcileDevices() {
        let selection = config.selectedDevices
        // Nothing chosen is not the same as switched off, but it forwards just
        // as little — and it must not make the bridge open anything. Neither
        // must a closed socket: holding somebody's wheel open with nowhere to
        // send its reports is all cost and no benefit.
        let forwarding = config.forwardsDevices && !selection.isEmpty && sender != nil
        let wanted = forwarding || deviceObservers > 0
        if wanted {
            if let deviceBridge {
                deviceBridge.update(sender: sender, forwarding: forwarding, selection: selection)
            } else {
                let bridge = DeviceBridge()
                deviceBridge = bridge
                bridge.start(sender: sender, forwarding: forwarding, selection: selection)
                for _ in 0..<deviceObservers { bridge.addObserver() }
            }
        } else if let deviceBridge {
            deviceBridge.stop()
            self.deviceBridge = nil
        }
    }

    // MARK: - Private

    private func startCapture() throws {
        guard let sender, let encoder else { throw RuntimeError.notStarted }

        // The closure keeps `sender` and `encoder` alive on its own: the audio
        // thread must never reach through `self` for them, or a `stop()` racing
        // a frame would hand it a nil.
        let capture = AudioCapture(gain: Float(config.gain)) { [weak self] pcm, peak in
            self?.notePeak(peak)
            guard !sender.muted else { return }
            if let packet = try? encoder.encode(pcm) {
                sender.sendAudio(packet)
            }
        }
        try capture.start(deviceSelector: config.inputDevice)
        self.capture = capture
    }

    private func notePeak(_ peak: Float) {
        peakLock.lock()
        peakSinceLastRead = max(peakSinceLastRead, peak)
        peakLock.unlock()
    }
}

enum RuntimeError: Error, CustomStringConvertible {
    case notStarted

    var description: String {
        switch self {
        case .notStarted:
            return "передача не запущена"
        }
    }
}
