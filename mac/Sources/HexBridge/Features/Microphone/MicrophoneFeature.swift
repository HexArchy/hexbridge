import Foundation
import Observation
import SwiftUI

/// The microphone feature: capture → Opus → UDP, and the five states from
/// §7.2 that describe it.
///
/// It owns no pipeline of its own — `BridgeRuntime` does — but it owns the
/// question "what should the user be told right now", which is the part the
/// shell must not have an opinion about.
@Observable
@MainActor
final class MicrophoneFeature: Feature {
    let id = "microphone"
    let title = "Микрофон"
    let symbolName = "waveform"
    let summary = "Звук с этого Mac уходит на игровой ПК и подставляется играм как обычный микрофон."

    private unowned let host: FeatureHost

    // Telemetry, refreshed from the shell's poll.
    private(set) var peak: Float = 0
    private(set) var packetsSent: UInt64 = 0
    private(set) var packetsPerSecond: Int = 0
    private(set) var rttMs: Double?
    private(set) var remoteReceived: UInt64 = 0
    private(set) var remoteLost: UInt64 = 0
    private(set) var hostAlive = false
    private(set) var isMuted = false
    private(set) var deviceName = "—"
    private(set) var uptime: TimeInterval = 0
    /// One minute of level history for the sparkline, one sample per 0.5 s.
    private(set) var history: [Double] = []

    private(set) var status = FeatureStatus()

    /// Transport or start-up failure, already worded for a human.
    private(set) var failure: String?
    /// True when the failure is specifically the TCC microphone denial, which
    /// has its own button (§10.3).
    private(set) var deniedAccess = false

    private var startedAt: Date?
    private var lastPacketCount: UInt64 = 0
    private var lastRateSample = Date()
    private var historyTick = 0
    private var startingSince: Date?

    init(host: FeatureHost) {
        self.host = host
        status = FeatureStatus(state: .off, tone: .off, headline: "Микрофон выключен")
    }

    // MARK: - Feature

    var isEnabled: Bool {
        get { host.config.streamsMicrophone }
        set {
            host.config.microphone = newValue
            host.saveSoon()
            if newValue { start() } else { stop() }
        }
    }

    func start() {
        guard isEnabled else { return }
        startingSince = Date()
        failure = nil
        deniedAccess = false
        host.startPipeline()
    }

    func stop() {
        startingSince = nil
        host.stopPipeline()
        refresh()
    }

    func refresh() {
        let runtime = host.runtime
        peak = runtime.drainPeak()

        let snapshot = runtime.snapshot()
        packetsSent = snapshot.sent
        rttMs = snapshot.rtt
        remoteReceived = snapshot.received
        remoteLost = snapshot.lost
        hostAlive = snapshot.pong.map { Date().timeIntervalSince($0) < 5 } ?? false
        isMuted = runtime.muted
        deviceName = runtime.deviceName

        if runtime.isRunning {
            if startedAt == nil { startedAt = Date() }
            startingSince = nil
        } else {
            startedAt = nil
        }
        uptime = startedAt.map { Date().timeIntervalSince($0) } ?? 0

        let elapsed = Date().timeIntervalSince(lastRateSample)
        if elapsed >= 1 {
            packetsPerSecond = Int(Double(snapshot.sent - lastPacketCount) / elapsed)
            lastPacketCount = snapshot.sent
            lastRateSample = Date()
        }

        if let transport = snapshot.error {
            failure = transport
        } else if runtime.isRunning {
            failure = nil
        }
        if let failure {
            // The TCC denial arrives as a CoreAudio error; recognising it is
            // what turns a dead end into a button (§10.3).
            deniedAccess = failure.localizedCaseInsensitiveContains("доступ")
                || failure.localizedCaseInsensitiveContains("privacy")
                || failure.contains("560557673")
        }

        historyTick += 1
        if historyTick >= 10 {
            historyTick = 0
            history.append(LevelMeter.fraction(of: peak))
            if history.count > 120 { history.removeFirst(history.count - 120) }
        }

        status = derive()
    }

    // MARK: - State machine (§7.2)

    private func derive() -> FeatureStatus {
        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: "Микрофон выключен",
                detail: "Звук на игровой ПК не отправляется. Включите фичу, когда она понадобится.",
                primaryAction: FeatureAction(title: "Включить микрофон") { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        if !host.config.isConfigured {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Микрофон не настроен",
                detail: "Укажите адрес приёмника и общий ключ — это делается один раз.",
                primaryAction: FeatureAction(title: "Настроить") { [weak self] in
                    self?.host.openPairing()
                }
            )
        }

        if deniedAccess {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Нет доступа к микрофону",
                detail: "macOS не разрешает HexBridge читать вход. Разрешение выдаётся один раз в «Системных настройках».",
                alert: failure,
                primaryAction: FeatureAction(title: "Открыть настройки конфиденциальности") {
                    NSWorkspace.shared.open(
                        URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
                    )
                }
            )
        }

        if let failure {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Передача не запустилась",
                detail: failure,
                alert: failure,
                primaryAction: FeatureAction(title: "Проверить связь") { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if !host.runtime.isRunning {
            // §7.0: `starting` lasts up to 10 s and then becomes an error.
            if let since = startingSince, Date().timeIntervalSince(since) < 10 {
                return FeatureStatus(state: .starting, tone: .neutral, headline: "Запускается")
            }
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Передача остановлена",
                detail: "Захват звука не запущен.",
                primaryAction: FeatureAction(title: "Запустить") { [weak self] in self?.start() }
            )
        }

        if isMuted {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: "Микрофон заглушен",
                detail: "Приёмник знает про мьют и держит соединение.",
                primaryAction: FeatureAction(title: "Включить микрофон") { [weak self] in
                    self?.toggleMute()
                }
            )
        }

        guard hostAlive else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Ждёт Windows",
                detail: "Звук отправляется на \(host.config.target), но приёмник не отвечает.",
                primaryAction: FeatureAction(title: "Проверить связь") { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        let total = remoteReceived + remoteLost
        if total > 200, Double(remoteLost) / Double(total) > 0.01 {
            let percent = Double(remoteLost) * 100 / Double(total)
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: String(format: "Потери в сети — %.1f %%", percent).replacingOccurrences(of: ".", with: ","),
                detail: "Звук восстанавливается, но местами слышны артефакты. Помогает проводное подключение или увеличение буфера в дополнительных настройках.",
                primaryAction: FeatureAction(title: "Заглушить") { [weak self] in self?.toggleMute() }
            )
        }

        let peer = host.config.peerName.map { " на \($0)" } ?? ""
        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: "Звук идёт",
            detail: "Игры\(peer.isEmpty ? "" : peer) видят его как «Steam Streaming Microphone».",
            primaryAction: FeatureAction(title: "Заглушить") { [weak self] in self?.toggleMute() }
        )
    }

    func toggleMute() {
        host.runtime.muted.toggle()
        isMuted = host.runtime.muted
        status = derive()
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(MicrophoneCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(MicrophoneSettingsPane(feature: self, host: host))
    }

    // MARK: - Formatting helpers used by both views

    var rttText: String {
        guard hostAlive, let rttMs else { return "—" }
        return String(format: "%.0f мс", rttMs)
    }

    var uptimeText: String {
        guard uptime > 0 else { return "—" }
        let seconds = Int(uptime)
        if seconds < 60 { return "\(seconds) с" }
        if seconds < 3600 { return "\(seconds / 60) мин" }
        return String(format: "%d ч %02d мин", seconds / 3600, (seconds % 3600) / 60)
    }

    var lossText: String {
        let total = remoteReceived + remoteLost
        guard total > 0 else { return "—" }
        let percent = Double(remoteLost) * 100 / Double(total)
        return String(format: "%.1f %%", percent).replacingOccurrences(of: ".", with: ",")
    }
}
