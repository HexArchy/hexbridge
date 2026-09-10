import Foundation
import Observation
import SwiftUI

/// The shared clipboard: copy on this Mac, paste on the gaming PC, and the other
/// way round.
///
/// It owns no transport of its own. Everything that has to arrive intact goes
/// through `BridgeRuntime.bulk`, which is the contract's reliable layer and knows
/// nothing about clipboards; this class only decides what to hand it and what to
/// do with what comes back.
///
/// Off unless asked for, and it stays that way. The clipboard is where passwords
/// live for the few seconds between a manager and a login form, and a feature
/// that shipped enabled would send them to another machine before anybody read a
/// settings page. Both the switch and the pane say so in as many words.
@Observable
@MainActor
final class ClipboardFeature: Feature {
    let id = "clipboard"
    let title = "Буфер обмена"
    let symbolName = "doc.on.clipboard"
    let summary = "Скопированное на Mac появляется на игровом ПК и наоборот. Содержимое буфера — включая пароли — уходит на другую машину."

    /// The pasteboard is read four times a second. macOS has no change
    /// notification, so this is the whole mechanism; `changeCount` is cheap and
    /// nothing is read until it moves.
    private static let pollTicks = 5

    private unowned let host: FeatureHost
    private let sync: ClipboardSync

    private(set) var status = FeatureStatus()
    private(set) var sent = 0
    private(set) var received = 0
    private(set) var lastDescription: String?
    private(set) var lastDirection: BulkDirection?
    private(set) var lastAt: Date?
    /// Non-nil while something is on the wire, which is what the progress bar draws.
    private(set) var flight: BulkProgress?
    private(set) var failure: String?

    private var running = false
    private var pollTick = 0
    private var pipelineWasUp = false

    init(host: FeatureHost, surface: ClipboardSurface = SystemPasteboard()) {
        self.host = host
        self.sync = ClipboardSync(surface: surface)
        status = FeatureStatus(state: .off, tone: .off, headline: "Общий буфер выключен")
    }

    // MARK: - Feature

    var isEnabled: Bool {
        get { host.config.sharesClipboard }
        set {
            host.config.clipboard = newValue
            host.saveSoon()
            host.runtime.config = host.config
            if newValue { start() } else { stop() }
        }
    }

    func start() {
        guard isEnabled, !running else { return }
        running = true
        failure = nil

        let bulk = host.runtime.bulk
        // The channel calls these from the socket queue, so everything they touch
        // is either `ClipboardSync` (locked) or a hop back to the main actor.
        bulk.owns = { [sync] hash in sync.owns(hash) }
        bulk.onDelivered = { [weak self] delivery in
            guard delivery.kind == .clipboard else { return }
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.accept(delivery) }
            }
        }
        bulk.onFinished = { [weak self] result in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(result) }
            }
        }
        bulk.onNote = { note in print("hexbridge: \(note)") }

        refresh()
    }

    func stop() {
        guard running else {
            status = derive()
            return
        }
        running = false

        let bulk = host.runtime.bulk
        // «принято: 0 — не нужно». With the feature off we must not pull somebody's
        // clipboard across the wire, and a refusal is the only thing the ack can say.
        bulk.owns = { _ in true }
        bulk.onDelivered = nil
        bulk.onFinished = nil
        bulk.onNote = nil
        bulk.reset()
        flight = nil
        status = derive()
    }

    func refresh() {
        guard isEnabled else {
            if running { stop() }
            status = derive()
            return
        }
        if !running { start() }

        // A rebuilt socket means a new session, and what the other side holds is
        // anybody's guess again. Our own clipboard is still ours, so only the
        // peer's half is forgotten.
        let up = host.runtime.isRunning
        if up, !pipelineWasUp { sync.forgetPeer() }
        pipelineWasUp = up

        flight = host.runtime.bulk.progress().first

        pollTick += 1
        if pollTick >= Self.pollTicks {
            pollTick = 0
            pollClipboard()
        }

        status = derive()
    }

    // MARK: - The two directions

    private func pollClipboard() {
        guard let item = sync.poll() else { return }

        // Something copied while the pipeline is down stays local: offering it
        // into a dead socket would only burn ten retries and report a failure the
        // user cannot act on.
        guard host.runtime.isRunning else { return }

        do {
            try host.runtime.bulk.offer(
                kind: .clipboard,
                format: item.format,
                bytes: item.bytes,
                description: item.describe
            )
        } catch {
            failure = "\(error)"
        }
    }

    private func accept(_ delivery: BulkDelivery) {
        let item = ClipboardItem(format: delivery.format, bytes: delivery.bytes)
        sync.apply(item)

        received += 1
        lastDescription = item.describe
        lastDirection = .incoming
        lastAt = Date()
        status = derive()
    }

    private func finish(_ result: BulkResult) {
        switch result.outcome {
        case .delivered, .alreadyThere:
            sync.notePeerHas(result.hash)
        case .noAnswer, .stalled:
            break
        }

        if result.outcome == .delivered {
            sent += 1
            lastDescription = result.description
            lastDirection = .outgoing
            lastAt = Date()
        }
        status = derive()
    }

    /// The peer restarted. Half-transferred objects belong to a session that is
    /// gone, and what it holds is anybody's guess again — but our own clipboard
    /// is still ours.
    func forgetPeer() {
        sync.forgetPeer()
    }

    // MARK: - State machine

    private func derive() -> FeatureStatus {
        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: "Общий буфер выключен",
                detail: "Ничего скопированного этот Mac никуда не отправляет.",
                primaryAction: FeatureAction(title: "Включить общий буфер") { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        if !host.config.isConfigured {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Общий буфер не настроен",
                detail: "Укажите адрес приёмника и общий ключ — это делается один раз.",
                primaryAction: FeatureAction(title: "Настроить") { [weak self] in
                    self?.host.openPairing()
                }
            )
        }

        if let failure {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Буфер обмена не передаётся",
                detail: failure,
                alert: failure,
                primaryAction: FeatureAction(title: "Выключить общий буфер") { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        guard host.runtime.isRunning else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Ждёт запуска передачи",
                detail: "Буфер обмена поедет по тому же каналу, что и звук. Пока канал не поднят, скопированное остаётся здесь.",
                primaryAction: FeatureAction(title: "Проверить связь") { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if let flight {
            let verb = flight.direction == .outgoing ? "Отправляем" : "Принимаем"
            return FeatureStatus(
                state: .live,
                tone: .ok,
                headline: "\(verb): \(flight.description)",
                detail: String(format: "%.0f %%", flight.fraction * 100),
                primaryAction: FeatureAction(title: "Выключить общий буфер") { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: "Буфер обмена общий",
            detail: lastText,
            primaryAction: FeatureAction(title: "Выключить общий буфер") { [weak self] in
                self?.isEnabled = false
            }
        )
    }

    // MARK: - Formatting used by both views

    var lastText: String {
        guard let lastDescription, let lastAt, let lastDirection else {
            return "Скопируйте что-нибудь — оно появится на другой машине."
        }
        let where_ = lastDirection == .outgoing ? "ушло на ПК" : "пришло с ПК"
        return "Последнее: \(lastDescription), \(where_) \(Self.when(since: lastAt))."
    }

    var lastDirectionText: String {
        switch lastDirection {
        case .outgoing: return "с этого Mac на ПК"
        case .incoming: return "с ПК на этот Mac"
        case nil: return "—"
        }
    }

    var lastWhenText: String {
        guard let lastAt else { return "—" }
        return Self.when(since: lastAt)
    }

    private static func when(since date: Date) -> String {
        let ago = Date().timeIntervalSince(date)
        if ago < 10 { return "только что" }
        if ago < 60 { return "\(Int(ago)) с назад" }
        if ago < 3600 { return "\(Int(ago / 60)) мин назад" }
        return "\(Int(ago / 3600)) ч назад"
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(ClipboardCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(ClipboardSettingsPane(feature: self))
    }
}
