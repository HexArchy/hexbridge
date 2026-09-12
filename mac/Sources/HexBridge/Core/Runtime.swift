import CryptoKit
import Foundation
import HexBridgeText
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
    /// §7.3 requires the "plugged in but not forwarded" state to show a live
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

    /// Whether the bridge is up: a socket, a channel, and the timers that keep
    /// them alive.
    ///
    /// This used to be `capture != nil` — the microphone, standing in for the
    /// whole bridge — and everything that needed the transport asked the
    /// microphone whether it was allowed to work. A permission macOS would not
    /// grant, or the switch turned off, then took the clipboard, the files and
    /// the forwarded controller with it.
    var isRunning: Bool { sender != nil }

    /// Whether the microphone in particular is being captured. Only the
    /// microphone's own feature has any business asking.
    var isCapturing: Bool { capture != nil }
    var deviceName: String { capture?.deviceName ?? L.t("unit.none") }

    /// True when the last attempt to start capture was refused by TCC.
    ///
    /// A fact rather than a sentence on purpose. The microphone feature has to
    /// tell "macOS said no" apart from every other reason capture did not
    /// start, because only the first one has a button to offer (§10.3), and it
    /// used to do that by looking for the word «доступ» inside the error text —
    /// which stopped being a stable thing to look for the moment that text
    /// acquired a second language.
    private(set) var microphoneDenied = false

    /// Why the microphone is not being captured while everything else runs.
    /// Nil both when it is running and when nobody asked for it.
    private(set) var captureFailure: String?

    /// The backoff on retrying a capture, in seconds of wall clock.
    ///
    /// Every attempt at a denied microphone puts another TCC prompt on screen,
    /// which is what doubling this is for. Seconds and not ticks: the shell calls
    /// in twenty times a second, and counting ticks turned «fifteen seconds»
    /// into three quarters of one.
    private static let firstCaptureRetry: TimeInterval = 15
    private var captureRetryAt: Date?
    private var captureRetryEvery = firstCaptureRetry
    /// True from the moment an attempt is handed to a background queue until it
    /// comes back, so that twenty ticks a second cannot pile them up.
    private var captureAttemptRunning = false
    var inputFormatDescription: String { capture?.inputFormatDescription ?? "—" }

    var muted: Bool {
        get { sender?.muted ?? false }
        set { sender?.muted = newValue }
    }

    /// True while what leaves here is going through a relay rather than straight
    /// at the other machine.
    ///
    /// Asked by file transfer before it tries the fast path: a relay forwards
    /// datagrams and that path is a stream, so the three seconds spent finding
    /// out would buy a fact that is already known. The reliable channel asks the
    /// same question for its own reasons — see `Sender.throughRelay`.
    var throughRelay: Bool { sender?.throughRelay ?? false }

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
        // Both of these are read by the channel and not pushed to it, so a
        // direct path coming up mid-transfer, or the user moving the control in
        // settings, takes effect on the next tick rather than on the next
        // restart.
        bulk.relayInPath = { [weak sender] in sender?.throughRelay ?? false }
        applySendRate()
        sender.onBulkPacket = { [weak self] type, payload in
            self?.bulk.handle(type: type, payload: payload)
        }

        sender.start()

        // The microphone is one passenger on this bridge, and it used to be the
        // bridge itself: a capture that would not start took the socket down with
        // it, so a denied permission — or a switch turned off — left the
        // clipboard, the files and the forwarded controller with nothing to
        // travel on. It is started here when it is wanted, and its failure is
        // recorded rather than thrown.
        captureFailure = nil
        captureRetryAt = nil
        captureRetryEvery = Self.firstCaptureRetry
        if config.streamsMicrophone { attemptCapture() }

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
        captureFailure = nil
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

    /// Live: the reliable channel reads the ceiling on its next tick. Nothing
    /// here has to be rebuilt for it, so this is not a restart-required setting.
    func applySendRate() {
        bulk.configuredCeiling = config.bulkRateCeiling
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

    /// Tries the microphone and remembers how it went.
    ///
    /// Never on the main thread. Starting a capture touches CoreAudio and can
    /// block on the TCC prompt for as long as nobody answers it, and a menu bar
    /// that stops drawing for that long looks exactly like a crash — which is
    /// what it looked like, because this used to be called straight from the
    /// twenty-times-a-second UI tick.
    private func attemptCapture() {
        guard !captureAttemptRunning else { return }
        captureAttemptRunning = true

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            var failure: String?
            do {
                try self.startCapture()
            } catch {
                failure = "\(error)"
            }
            DispatchQueue.main.async {
                self.captureFailure = failure
                self.captureAttemptRunning = false
            }
        }
    }

    /// Called about once a second by the shell.
    ///
    /// The case this exists for: an agent launched by launchd puts the TCC prompt
    /// on screen with nobody in front of it and gives up. Someone clicks Allow
    /// minutes later, and without this nothing happens until the agent is
    /// restarted by hand.
    func retryCaptureIfStalled() {
        guard sender != nil, config.streamsMicrophone, capture == nil else { return }
        guard !captureAttemptRunning else { return }

        let now = Date()
        guard let due = captureRetryAt else {
            // First call after the bridge came up: start the clock rather than
            // trying again straight away, since the attempt that failed was a
            // moment ago.
            captureRetryAt = now.addingTimeInterval(captureRetryEvery)
            return
        }
        guard now >= due else { return }

        captureRetryEvery = min(captureRetryEvery * 2, 900)
        captureRetryAt = now.addingTimeInterval(captureRetryEvery)
        attemptCapture()
    }

    /// The microphone switch, applied to a bridge that is already up. Off stops
    /// the capture and leaves everything else crossing.
    func wantsMicrophone(_ wanted: Bool) {
        guard sender != nil else { return }
        if wanted {
            captureRetryAt = nil
            captureRetryEvery = Self.firstCaptureRetry
            if capture == nil { attemptCapture() }
        } else {
            capture?.stop()
            capture = nil
            captureFailure = nil
            microphoneDenied = false
        }
    }

    private func startCapture() throws {
        guard let sender, let encoder else { throw RuntimeError.notStarted }
        microphoneDenied = false

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
        do {
            try capture.start(deviceSelector: config.inputDevice)
        } catch CaptureError.permissionDenied {
            microphoneDenied = true
            throw CaptureError.permissionDenied
        }
        self.capture = capture
    }

    private func notePeak(_ peak: Float) {
        // A misbehaving input device can hand us infinities or NaN, and this one
        // number reaches both the console and the width of the level meter's bar.
        // NaN geometry is not a cosmetic problem in SwiftUI, so it is stopped at
        // the single point every reader goes through rather than at each of them.
        guard peak.isFinite else { return }

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
            return L.t("runtime.error.notStarted")
        }
    }
}

extension UInt64 {
    /// Growth since an earlier reading of the same counter, for a counter that
    /// can start over from zero.
    ///
    /// The transfer counters live in the sender, and switching the microphone
    /// off releases the sender: the next reading is zero while the one held
    /// from a moment ago is not. On an unsigned type that subtraction is not a
    /// negative number, it is a runtime trap, and it fired on the main thread
    /// the instant the switch was flipped. A counter that has gone backwards
    /// has restarted, and the honest rate for that one interval is nothing.
    func growth(since previous: UInt64) -> UInt64 {
        self >= previous ? self - previous : 0
    }
}
