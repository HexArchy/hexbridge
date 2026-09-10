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
/// gamepad accessors are read from the main thread at 20 Hz. Everything they
/// touch is either immutable or behind `peakLock` / `gamepadLock` / a lock
/// inside `Sender` and `GamepadBridge`. The compiler cannot see that, and the
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
    private let gamepadLock = NSLock()
    private var storedGamepad: GamepadBridge?
    private var gamepad: GamepadBridge? {
        get {
            gamepadLock.lock()
            defer { gamepadLock.unlock() }
            return storedGamepad
        }
        set {
            gamepadLock.lock()
            storedGamepad = newValue
            gamepadLock.unlock()
        }
    }

    /// Raised by the views that draw the controller. The bridge is kept alive
    /// while it is non-zero even with the passthrough switched off, because
    /// §7.3 requires the "подключён, но не проброшен" state to show a live
    /// outline — that is the moment the user learns their pad is readable.
    private var gamepadObservers = 0
    private var helloTimer: DispatchSourceTimer?

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

        // Before `start()`: the DEV_OUT handler has to be installed before the
        // receive loop can deliver anything to it.
        reconcileGamepad()

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
        // One timer for both keepalives: the gamepad re-announce has the same
        // period and the same reason to exist as HELLO.
        timer.setEventHandler { [weak sender, weak self] in
            sender?.sendHello()
            self?.gamepad?.tick()
        }
        timer.resume()
        helloTimer = timer
    }

    func stop() {
        helloTimer?.cancel()
        helloTimer = nil
        capture?.stop()
        capture = nil
        sender?.stop()
        sender = nil
        encoder = nil
        // Keeps reading the controller if a view is watching it: closing the
        // pipeline should not blank the visualisation the user is looking at.
        reconcileGamepad()
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

    /// nil when nothing is reading the controller at all, which the UI shows
    /// differently from "reading it and nothing is plugged in".
    func gamepadStatus() -> GamepadBridge.Status? {
        gamepad?.snapshot()
    }

    /// Newest decoded report for the on-screen controller (§8).
    func gamepadLiveState() -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        gamepad?.liveState()
    }

    // MARK: - Gamepad lifecycle

    /// Balanced pair, called by the views that draw the controller.
    func beginGamepadObservation() {
        gamepadObservers += 1
        reconcileGamepad()
        gamepad?.addObserver()
    }

    func endGamepadObservation() {
        guard gamepadObservers > 0 else { return }
        gamepadObservers -= 1
        gamepad?.removeObserver()
        reconcileGamepad()
    }

    /// Applies a change to `config.gamepad` without rebuilding the pipeline.
    /// The switch used to be a restart-required setting; it no longer is,
    /// because the HID reader and the socket have nothing to do with each other.
    func applyGamepadSetting() {
        reconcileGamepad()
    }

    private func reconcileGamepad() {
        let wanted = config.forwardsGamepad || gamepadObservers > 0
        if wanted {
            if let gamepad {
                gamepad.update(sender: sender, forwarding: config.forwardsGamepad)
            } else {
                let bridge = GamepadBridge()
                gamepad = bridge
                bridge.start(sender: sender, forwarding: config.forwardsGamepad)
                for _ in 0..<gamepadObservers { bridge.addObserver() }
            }
        } else if let gamepad {
            gamepad.stop()
            self.gamepad = nil
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
