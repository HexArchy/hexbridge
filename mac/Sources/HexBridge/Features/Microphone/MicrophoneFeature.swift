import Foundation
import HexBridgeText
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
    var title: String { L.t("mic.title") }
    let symbolName = "waveform"
    var summary: String { L.t("mic.summary") }

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
    private(set) var deviceName = L.t("unit.none")
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
        status = FeatureStatus(state: .off, tone: .off, headline: L.t("mic.off.headline"))
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
            packetsPerSecond = Int(Double(snapshot.sent.growth(since: lastPacketCount)) / elapsed)
            lastPacketCount = snapshot.sent
            lastRateSample = Date()
        }

        if let transport = snapshot.error {
            failure = transport
        } else if runtime.isRunning {
            failure = nil
        }
        // The TCC denial is what turns a dead end into a button (§10.3), and it
        // is asked for as a fact rather than recognised in a sentence: the
        // sentence is now translated, and matching a translation is a bug
        // waiting for the next language.
        deniedAccess = runtime.microphoneDenied
            || (failure?.contains("560557673") ?? false)

        historyTick += 1
        if historyTick >= 10 {
            historyTick = 0
            history.append(LevelMeter.fraction(of: peak))
            if history.count > 120 { history.removeFirst(history.count - 120) }
        }

        status = derive()
    }

    // MARK: - State machine (§7.2)

    /// Network-layer unreachability, as opposed to a real local failure. Matched on
    /// the POSIX codes Network.framework reports rather than on message text, which
    /// is localised: 50 network down, 51 network unreachable, 64 host down,
    /// 65 no route to host.
    private static func isHostUnreachable(_ failure: String) -> Bool {
        ["error 50", "error 51", "error 64", "error 65"].contains { failure.contains($0) }
    }

    private func derive() -> FeatureStatus {
        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: L.t("mic.off.headline"),
                detail: L.t("mic.off.detail"),
                primaryAction: FeatureAction(title: L.t("mic.action.turnOn"), togglesFeature: true) { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        if !host.config.isConfigured {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("mic.unconfigured.headline"),
                detail: L.t("mic.unconfigured.detail"),
                primaryAction: FeatureAction(title: L.t("action.setUp")) { [weak self] in
                    self?.host.openPairing()
                }
            )
        }

        if deniedAccess {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("mic.denied.headline"),
                detail: L.t("mic.denied.detail"),
                alert: failure,
                primaryAction: FeatureAction(title: L.t("mic.action.privacy")) {
                    NSWorkspace.shared.open(
                        URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
                    )
                }
            )
        }

        if let failure {
            // An unreachable host is not a fault of ours: the gaming PC is simply
            // off, asleep or off the network, which is the normal overnight state.
            // Painting that red trains people to ignore red.
            if Self.isHostUnreachable(failure) {
                return FeatureStatus(
                    state: .waiting,
                    tone: .warn,
                    headline: L.t("mic.unreachable.headline"),
                    detail: L.t("mic.unreachable.detail"),
                    primaryAction: FeatureAction(title: L.t("action.checkLink")) { [weak self] in
                        self?.host.openLinkCheck()
                    }
                )
            }

            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("mic.failed.headline"),
                detail: failure,
                alert: failure,
                primaryAction: FeatureAction(title: L.t("action.checkLink")) { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if !host.runtime.isRunning {
            // §7.0: `starting` lasts up to 10 s and then becomes an error.
            if let since = startingSince, Date().timeIntervalSince(since) < 10 {
                return FeatureStatus(state: .starting, tone: .neutral, headline: L.t("mic.starting.headline"))
            }
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("mic.stopped.headline"),
                detail: L.t("mic.stopped.detail"),
                primaryAction: FeatureAction(title: L.t("mic.action.start")) { [weak self] in self?.start() }
            )
        }

        if isMuted {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: L.t("mic.muted.headline"),
                detail: L.t("mic.muted.detail"),
                wordOverride: .muted,
                primaryAction: FeatureAction(title: L.t("mic.action.unmute")) { [weak self] in
                    self?.toggleMute()
                }
            )
        }

        guard hostAlive else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: L.t("mic.waiting.headline"),
                detail: L.t("mic.waiting.detail", host.config.target),
                primaryAction: FeatureAction(title: L.t("action.checkLink")) { [weak self] in
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
                headline: L.t("mic.loss.headline", L.percent(percent)),
                detail: L.t("mic.loss.detail"),
                primaryAction: FeatureAction(title: L.t("mic.action.mute")) { [weak self] in self?.toggleMute() }
            )
        }

        let peer = host.config.peerName.flatMap { $0.isEmpty ? nil : $0 }
        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: L.t("mic.live.headline"),
            detail: peer.map { L.t("mic.live.detail.peer", $0) } ?? L.t("mic.live.detail"),
            primaryAction: FeatureAction(title: L.t("mic.action.mute")) { [weak self] in self?.toggleMute() }
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
        guard hostAlive, let rttMs else { return L.t("unit.none") }
        return L.milliseconds(rttMs)
    }

    var uptimeText: String {
        guard uptime > 0 else { return L.t("unit.none") }
        return L.duration(uptime)
    }

    var lossText: String {
        let total = remoteReceived + remoteLost
        guard total > 0 else { return L.t("unit.none") }
        return L.percent(Double(remoteLost) * 100 / Double(total))
    }
}
