import AppKit
import Foundation
import Observation

/// What a feature is allowed to ask of the shell.
///
/// This is the whole of the coupling in the other direction. A feature can
/// read the config, ask for it to be saved, and ask for one of the two shared
/// windows — and nothing else. In particular it cannot reach another feature.
@MainActor
protocol FeatureHost: AnyObject {
    var runtime: BridgeRuntime { get }
    var config: Config { get set }

    func saveSoon()
    func note(_ text: String)
    func startPipeline()
    func stopPipeline()
    func openPairing()
    func openLinkCheck()
}

/// Everything the UI is allowed to look at.
///
/// The audio thread runs 50 times a second and must never touch SwiftUI, so it
/// only bumps a float behind a lock inside `BridgeRuntime`. This model polls
/// that value — and the sender counters — from a 20 Hz main-thread timer, which
/// caps view invalidation at a rate a menu bar popover can actually draw.
@Observable
@MainActor
final class AppModel: FeatureHost {
    let runtime: BridgeRuntime

    /// The feature list. The shell walks it and never names a member: adding a
    /// third feature is one line here plus its own folder.
    private(set) var features: [any Feature] = []

    var config: Config {
        didSet { runtime.config = config }
    }

    private(set) var devices: [AudioDevice] = []
    /// Settings that only take effect on a new encoder or a new socket.
    private(set) var needsRestart = false
    private(set) var restarting = false
    /// True between asking for a pipeline and hearing back. Without it the poll
    /// sees `isRunning == false` for a few ticks and flashes "остановлено".
    private(set) var starting = false

    /// Non-transport messages (config write failed, autostart refused). Kept
    /// apart from feature state so the 20 Hz poll cannot wipe them out.
    var noticeText: String?

    var revealPSK = false
    private(set) var probeOutput = ""
    private(set) var probeRunning = false

    /// Which settings pane was open last. §7.1 requires restoring it.
    var selectedPane: String {
        didSet { UserDefaults.standard.set(selectedPane, forKey: Self.paneKey) }
    }

    let linkCheck = LinkCheck()
    let discovery = ReceiverDiscovery()

    private var uiTimer: Timer?
    private var saveTimer: Timer?
    private var muteSignal: DispatchSourceSignal?
    private var deviceRefreshTick = 0
    private var retryTick = 0
    private var loggedSummary: String?

    private static let paneKey = "ru.hexarch.hexbridge.settingsPane"

    init(runtime: BridgeRuntime) {
        self.runtime = runtime
        self.config = runtime.config
        self.selectedPane = UserDefaults.standard.string(forKey: Self.paneKey) ?? "general"
        features = [MicrophoneFeature(host: self), DualSenseFeature(host: self)]
    }

    // MARK: - Shell-level derived state

    var theme: AppTheme {
        get { config.appTheme }
        set {
            config.theme = newValue.rawValue
            saveSoon()
        }
    }

    /// §7.1: the header summary is the worst state among the enabled features.
    var summary: FeatureStatus {
        let enabled = features.filter(\.isEnabled).map(\.status)
        guard let worst = enabled.max(by: { $0.severity < $1.severity }) else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: "Всё выключено",
                detail: "Ни одна фича не включена."
            )
        }
        var summary = worst
        if enabled.allSatisfy({ $0.state == .live && $0.tone == .ok }) {
            summary = FeatureStatus(state: .live, tone: .ok, headline: "Всё работает")
        }
        summary.detail = config.target.isEmpty ? "адрес приёмника не задан" : config.target
        summary.alert = nil
        return summary
    }

    /// Template image only: §7.1 forbids tinting the menu bar icon, so state is
    /// carried by the shape of the symbol and nothing else.
    var menuBarSymbol: String {
        switch summary.state {
        case .error: return "exclamationmark.triangle"
        case .waiting: return "waveform.badge.exclamationmark"
        case .live: return summary.tone == .warn ? "waveform.slash" : "waveform"
        case .starting: return "waveform"
        case .off: return "waveform.slash"
        }
    }

    var fingerprint: String? { Pairing.fingerprint(ofBase64Key: config.psk) }

    // MARK: - Lifecycle

    func onLaunch() {
        refreshDevices()
        // SIGUSR1 mute keeps working with the UI up; the poll timer mirrors the
        // flag back so the icon follows an external toggle.
        muteSignal = CLI.installMuteSignal(runtime)

        uiTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        // The popover runs the main loop in .eventTracking while it is open;
        // without this the meter would freeze exactly when it is being watched.
        if let uiTimer {
            RunLoop.main.add(uiTimer, forMode: .common)
        }

        for feature in features { feature.start() }
    }

    func quit() {
        saveNow()
        runtime.stop()
        NSApplication.shared.terminate(nil)
    }

    // MARK: - Pipeline

    /// Starting touches CoreAudio and may block on the TCC prompt, so it never
    /// happens on the main thread — a frozen menu bar item looks like a crash.
    func startPipeline() {
        guard !starting, !runtime.isRunning else { return }
        starting = true
        runtime.config = config
        let runtime = self.runtime
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            var failure: String?
            do {
                try runtime.start()
            } catch {
                failure = "\(error)"
                // Without this the failure reaches the popover and nowhere else, so a
                // launchd-started agent that cannot capture looks identical in the log
                // to one that simply has no host to talk to.
                print("hexbridge: не удалось запустить конвейер: \(error)")
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    if let failure { self.noticeText = failure }
                    self.needsRestart = false
                    self.restarting = false
                    self.starting = false
                    self.tick()
                }
            }
        }
    }

    func stopPipeline() {
        runtime.stop()
        tick()
    }

    func restartPipeline() {
        guard !restarting else { return }
        restarting = true
        starting = true
        runtime.config = config
        let runtime = self.runtime
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            var failure: String?
            do {
                try runtime.restart()
            } catch {
                failure = "\(error)"
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self.noticeText = failure
                    self.needsRestart = false
                    self.restarting = false
                    self.starting = false
                    self.tick()
                }
            }
        }
    }

    // MARK: - Polling

    /// Retries a start that did not take.
    ///
    /// The case that matters is the microphone permission: an agent launched by
    /// launchd puts the TCC prompt on screen with nobody in front of it, gives up,
    /// and would then stay silent forever — the user grants access, and nothing
    /// happens until they restart the agent by hand. Retrying quietly turns that
    /// into "it starts working a few seconds after you click Allow".
    private func retryPipelineIfStalled() {
        guard !starting, !runtime.isRunning, config.isConfigured else { return }
        guard features.contains(where: { $0.id == "microphone" && $0.isEnabled }) else { return }

        retryTick += 1
        guard retryTick >= 15 else { return }   // tick is 20 Hz-driven but coalesced to 1 s
        retryTick = 0
        startPipeline()
    }

    private func tick() {
        for feature in features { feature.refresh() }
        logSummaryIfChanged()
        retryPipelineIfStalled()

        // CoreAudio enumeration is not free; once a second is plenty for a
        // device list that only changes when someone plugs something in.
        deviceRefreshTick += 1
        if deviceRefreshTick >= 20 {
            deviceRefreshTick = 0
            refreshDevices()
        }
    }

    /// The UI is invisible to logs and to `swift build`, so every state change
    /// is printed. Under launchd this lands in ~/Library/Logs/HexBridge.log and
    /// makes a silent death obvious.
    private func logSummaryIfChanged() {
        // The headline goes in too: a bare `error` sends whoever reads this log
        // hunting through the code for which of the several error branches fired.
        let line = features
            .map { feature -> String in
                let state = feature.status.state.rawValue
                let headline = feature.status.headline
                return headline.isEmpty ? "\(feature.id)=\(state)" : "\(feature.id)=\(state) «\(headline)»"
            }
            .joined(separator: " ")
        guard line != loggedSummary else { return }
        loggedSummary = line
        print("hexbridge: состояние → \(line)")
    }

    func refreshDevices() {
        devices = AudioDevices.inputDevices()
    }

    // MARK: - FeatureHost

    func note(_ text: String) {
        noticeText = text
    }

    func openPairing() {
        WindowRouter.shared.open(.pairing)
    }

    func openLinkCheck() {
        WindowRouter.shared.open(.pairing)
        runLinkCheck()
    }

    func runLinkCheck() {
        linkCheck.run(config: config, live: runtime, gamepad: runtime.gamepadStatus())
    }

    // MARK: - Pairing

    /// Applies a payload that arrived by link or by short code. Everything the
    /// receiver knows about itself arrives at once, which is the whole point of
    /// pairing from the Windows side (§9.1).
    func apply(_ payload: Pairing.Payload) {
        config.target = payload.target
        config.psk = payload.psk
        config.peerName = payload.machineName
        config.paired = true
        saveNow()
        needsRestart = true
        restartPipeline()
    }

    // MARK: - Editable settings

    var target: String {
        get { config.target }
        set { config.target = newValue; markNeedsRestart() }
    }

    var nodeName: String {
        get { config.name }
        set { config.name = newValue; markNeedsRestart() }
    }

    var psk: String {
        get { config.psk }
        set { config.psk = newValue; markNeedsRestart() }
    }

    var bitrate: Int {
        get { config.bitrate }
        set { config.bitrate = newValue; markNeedsRestart() }
    }

    var expectedLossPercent: Int {
        get { config.expectedLossPercent }
        set { config.expectedLossPercent = newValue; markNeedsRestart() }
    }

    var gain: Double {
        get { config.gain }
        set {
            config.gain = newValue
            runtime.setGain(newValue)  // live: read on the next 20 ms frame
            saveSoon()
        }
    }

    /// Empty string means "system default", which is what `inputDevice == nil`
    /// encodes in the config file.
    var inputDeviceSelector: String {
        get {
            guard let selector = config.inputDevice, !selector.isEmpty else { return "" }
            // The config may hold a name fragment (`--device Yeti` writes one),
            // but the picker is keyed by UID. Resolving it here keeps the
            // control from showing an empty row for a perfectly valid setting.
            if devices.contains(where: { $0.uid == selector }) { return selector }
            return devices.first { $0.name.range(of: selector, options: .caseInsensitive) != nil }?.uid ?? ""
        }
        set {
            config.inputDevice = newValue.isEmpty ? nil : newValue
            saveSoon()
            applyDevice()
        }
    }

    private func applyDevice() {
        guard runtime.isRunning else { return }
        let runtime = self.runtime
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            var failure: String?
            do {
                try runtime.restartCapture()
            } catch {
                failure = "\(error)"
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    if let failure { self.noticeText = failure }
                    self.tick()
                }
            }
        }
    }

    private func markNeedsRestart() {
        needsRestart = true
        saveSoon()
    }

    /// Text fields fire on every keystroke; writing JSON that often is pointless.
    func saveSoon() {
        saveTimer?.invalidate()
        saveTimer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.saveNow() }
        }
    }

    func saveNow() {
        saveTimer?.invalidate()
        saveTimer = nil
        do {
            try config.save(to: runtime.configPath)
        } catch {
            noticeText = "не удалось записать конфиг: \(error.localizedDescription)"
        }
    }

    // MARK: - Autostart

    var autostart: Bool {
        get {
            // The truth lives in the filesystem, which Observation cannot watch.
            // Reading the revision counter here is what makes SwiftUI re-run the
            // getter after `enable`/`disable` bump it.
            _ = autostartRevision
            return LaunchAgent.isEnabled
        }
        set {
            do {
                if newValue {
                    try LaunchAgent.enable()
                } else {
                    try LaunchAgent.disable()
                }
            } catch {
                noticeText = "автозапуск: \(error.localizedDescription)"
            }
            autostartRevision &+= 1
        }
    }

    /// Bumped whenever the launchd plist changes; see `autostart`.
    private(set) var autostartRevision: UInt = 0

    var autostartNote: String {
        _ = autostartRevision
        if LaunchAgent.isEnabled {
            return LaunchAgent.managedByLaunchd
                ? "Агент \(LaunchAgent.label) загружен."
                : "Запустится при следующем входе в систему."
        }
        return LaunchAgent.managedByLaunchd
            ? "Автозапуск выключен, текущий процесс продолжит работать."
            : "Автозапуск выключен."
    }

    // MARK: - Diagnostics

    func runProbe() {
        guard !probeRunning else { return }
        probeRunning = true
        probeOutput = "проверяю 3 секунды…\n"
        let selector = config.inputDevice
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let append: (String) -> Void = { line in
                DispatchQueue.main.async {
                    MainActor.assumeIsolated { self?.probeOutput += line + "\n" }
                }
            }
            do {
                try MicProbe.run(deviceSelector: selector, log: append)
            } catch {
                append("ошибка: \(error)")
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.probeRunning = false }
            }
        }
    }

    func revealLog() {
        NSWorkspace.shared.activateFileViewerSelecting([LaunchAgent.logURL])
    }

    func copyPSK() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(config.psk, forType: .string)
    }

    func generatePSK() {
        psk = KeyFactory.newBase64Key()
    }

    /// §7.4 «Диагностика»: versions, the config without the key, recent log.
    func copyReport() {
        var lines = [
            "HexBridge — отчёт диагностики",
            "дата: \(ISO8601DateFormatter().string(from: Date()))",
            "macOS: \(ProcessInfo.processInfo.operatingSystemVersionString)",
            "конфиг: \(runtime.configPath.path)",
            "адрес: \(config.target.isEmpty ? "—" : config.target)",
            "ключ: \(config.psk.isEmpty ? "не задан" : "задан, отпечаток \(fingerprint ?? "—")")",
            "устройство: \(config.inputDevice ?? "системное по умолчанию") (сейчас: \(runtime.deviceName))",
            "битрейт: \(config.bitrate) бит/с, ожидаемые потери \(config.expectedLossPercent) %",
            "фичи:",
        ]
        for feature in features {
            lines.append("  \(feature.id): включена=\(feature.isEnabled) состояние=\(feature.status.state.rawValue) — \(feature.status.headline)")
        }
        if let tail = try? String(contentsOf: LaunchAgent.logURL, encoding: .utf8) {
            lines.append("")
            lines.append("последние строки журнала:")
            lines.append(contentsOf: tail.split(separator: "\n").suffix(200).map(String.init))
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(lines.joined(separator: "\n"), forType: .string)
        noticeText = "Отчёт скопирован в буфер обмена."
    }
}
