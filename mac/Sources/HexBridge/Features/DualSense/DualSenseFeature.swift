import Foundation
import Observation
import SwiftUI

/// The DualSense passthrough feature and its five states from §7.3.
///
/// The important asymmetry with the microphone: the controller is *read* even
/// when the passthrough is off, as long as something is drawing it. That is
/// what makes the "подключён, но не проброшен" state show a live outline, and
/// it is the moment the user learns their pad is being read correctly — the
/// first wow moment, arrived at by accident rather than by ceremony.
@Observable
@MainActor
final class DualSenseFeature: Feature {
    let id = "dualsense"
    let title = "DualSense"
    let symbolName = "gamecontroller"
    let summary = "Контроллер остаётся подключённым к Mac: HexBridge читает репорты, не забирая устройство у системы и у Steam."

    private unowned let host: FeatureHost

    private(set) var status = FeatureStatus()
    private(set) var bridge: GamepadBridge.Status?
    private(set) var reportsPerSecond: Double = 0
    /// Set when the controller refuses output reports — the adaptive-trigger
    /// case from §10.3, which is a `warn`, not a failure.
    private(set) var outputBlocked = false

    init(host: FeatureHost) {
        self.host = host
        status = FeatureStatus(state: .off, tone: .off, headline: "Проброс выключен")
    }

    // MARK: - Feature

    var isEnabled: Bool {
        get { host.config.forwardsGamepad }
        set {
            host.config.gamepad = newValue
            host.saveSoon()
            // Not a pipeline change any more: the HID reader and the socket
            // are independent, so the switch takes effect immediately.
            host.runtime.config = host.config
            host.runtime.applyGamepadSetting()
            refresh()
        }
    }

    func start() {
        host.runtime.applyGamepadSetting()
    }

    func stop() {
        host.runtime.applyGamepadSetting()
    }

    func refresh() {
        bridge = host.runtime.gamepadStatus()
        reportsPerSecond = bridge?.reportRate ?? 0
        // 0xE00002C1 kIOReturnNotPrivileged is exactly the code the trigger
        // effects come back with when macOS blocks output reports.
        outputBlocked = (bridge?.lastError?.contains("0xE00002C1") ?? false)
            || (bridge?.outputsRejected ?? 0) > 0 && (bridge?.outputsApplied ?? 0) == 0
        status = derive()
    }

    /// Balanced with `endObservation`. Called by the views that draw the pad.
    func beginObservation() {
        host.runtime.beginGamepadObservation()
        refresh()
    }

    func endObservation() {
        host.runtime.endGamepadObservation()
    }

    func liveState() -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        host.runtime.gamepadLiveState()
    }

    // MARK: - State machine (§7.3)

    private func derive() -> FeatureStatus {
        let connected = bridge?.connected ?? false

        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: connected ? "Контроллер найден, проброс выключен" : "Проброс выключен",
                detail: connected
                    ? "«\(productName)»\(batterySuffix). Windows его пока не видит."
                    : "Контроллер на Windows не пробрасывается.",
                primaryAction: FeatureAction(title: "Включить проброс") { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        guard connected else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Контроллер не подключён",
                detail: "Подключите DualSense к Mac кабелем USB. По Bluetooth проброс не работает: нужен доступ к HID-репортам на полной частоте.",
                primaryAction: nil
            )
        }

        if let error = bridge?.lastError, !outputBlocked {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Контроллер не читается",
                detail: error,
                alert: error,
                primaryAction: FeatureAction(title: "Повторить") { [weak self] in
                    self?.host.runtime.applyGamepadSetting()
                }
            )
        }

        guard bridge?.attachAcknowledged == true else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Ждёт Windows",
                detail: "«\(productName)» прочитан\(batterySuffix). Приёмник ещё не подтвердил, что собрал виртуальное устройство.",
                primaryAction: FeatureAction(title: "Проверить связь") { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if outputBlocked {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: "Адаптивные триггеры не применяются",
                detail: "macOS не пропускает output-репорты этому приложению, контроллер отвечает 0xE00002C1. Кнопки, стики, гироскоп и тачпад работают, сопротивление триггеров и лайтбар — нет.",
                primaryAction: FeatureAction(title: "Выключить проброс") { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: "Контроллер проброшен",
            detail: "Windows видит его как 054C:0CE6\(batterySuffix). Триггеры и гироскоп работают.",
            primaryAction: FeatureAction(title: "Выключить проброс") { [weak self] in
                self?.isEnabled = false
            }
        )
    }

    private var productName: String { bridge?.product ?? "DualSense" }

    private var batterySuffix: String {
        guard let battery = bridge?.battery else { return "" }
        return ", батарея \(battery)"
    }

    /// How the outline should be coloured for the current state (§7.3).
    var mood: ControllerMood {
        guard bridge?.connected == true else { return .inactive }
        return isEnabled && bridge?.attachAcknowledged == true ? .forwarding : .reading
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(DualSenseCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(DualSenseSettingsPane(feature: self))
    }
}
