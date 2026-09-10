import Foundation
import Observation
import SwiftUI

/// The HID passthrough feature and its five states from §7.3.
///
/// The important asymmetry with the microphone: a device is *read* even when the
/// passthrough is off, as long as something is drawing it. That is what makes
/// the "подключён, но не проброшен" state show a live outline, and it is the
/// moment the user learns their pad is being read correctly.
///
/// The second asymmetry is the one this feature exists to manage. Devices are
/// opened without seizing them, so the Mac keeps using whatever it forwards.
/// For a pad that is the point; for a keyboard it means double input. Hence:
/// nothing is forwarded by default, every device is chosen by hand, the built-in
/// keyboard and trackpad are never offered, and the ones that type or click say
/// so out loud.
@Observable
@MainActor
final class DevicesFeature: Feature {
    let id = "devices"
    let title = "Устройства"
    let symbolName = "cable.connector"
    let summary = "Выбранные USB-устройства остаются подключёнными к Mac: HexBridge читает репорты, не забирая устройство у системы и у Steam."

    private unowned let host: FeatureHost

    private(set) var status = FeatureStatus()
    private(set) var bridge: DeviceBridge.Status?
    /// Set when a device refuses output reports — the adaptive-trigger case from
    /// §10.3, which is a `warn`, not a failure.
    private(set) var outputBlocked = false

    init(host: FeatureHost) {
        self.host = host
        status = FeatureStatus(state: .off, tone: .off, headline: "Проброс выключен")
    }

    // MARK: - Feature

    var isEnabled: Bool {
        get { host.config.forwardsDevices }
        set {
            host.config.gamepad = newValue
            host.saveSoon()
            // Not a pipeline change: the HID readers and the socket are
            // independent, so the switch takes effect immediately.
            host.runtime.config = host.config
            host.runtime.applyDeviceSetting()
            refresh()
        }
    }

    func start() {
        host.runtime.applyDeviceSetting()
    }

    func stop() {
        host.runtime.applyDeviceSetting()
    }

    func refresh() {
        bridge = host.runtime.deviceStatus()
        // 0xE00002C1 kIOReturnNotPrivileged is exactly the code the trigger
        // effects come back with when macOS blocks output reports.
        outputBlocked = forwarded.contains { device in
            device.lastError?.contains("0xE00002C1") == true
                || (device.outputsRejected > 0 && device.outputsApplied == 0)
        }
        status = derive()
    }

    /// Balanced with `endObservation`. Called by the views that draw a device or
    /// list the ones on offer — the list needs a running scan just as much as
    /// the outline does.
    func beginObservation() {
        host.runtime.beginDeviceObservation()
        refresh()
    }

    func endObservation() {
        host.runtime.endDeviceObservation()
    }

    // MARK: - What the views read

    /// The devices actually being forwarded, by device number.
    var forwarded: [DeviceBridge.DeviceStatus] { bridge?.devices ?? [] }

    /// Everything on offer, ineligible rows included so the reason is visible.
    var available: [DeviceBridge.Available] { bridge?.available ?? [] }

    var selection: Set<DeviceIdentity> { host.config.selectedDevices }

    var slotsLeft: Int { max(0, DeviceBridge.maxDevices - forwarded.count) }

    /// Ticking a box. Writes the config through and applies it at once: a switch
    /// that needs a restart to mean anything is a switch nobody trusts.
    func setSelected(_ identity: DeviceIdentity, _ wanted: Bool) {
        var chosen = selection
        if wanted { chosen.insert(identity) } else { chosen.remove(identity) }
        // Sorted so the file does not churn on every toggle.
        host.config.forwardedDevices = chosen.map(\.description).sorted()
        host.saveSoon()
        host.runtime.config = host.config
        host.runtime.applyDeviceSetting()
        refresh()
    }

    func liveState(_ number: UInt8) -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)? {
        host.runtime.deviceLiveState(number)
    }

    /// How a device's outline should be coloured for the current state (§7.3).
    func mood(_ device: DeviceBridge.DeviceStatus) -> ControllerMood {
        guard isEnabled else { return .reading }
        return device.attachAcknowledged ? .forwarding : .reading
    }

    // MARK: - State machine (§7.3)

    private func derive() -> FeatureStatus {
        let chosen = selection
        let live = forwarded

        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: "Проброс выключен",
                detail: chosen.isEmpty
                    ? "Ни одно устройство на Windows не пробрасывается."
                    : "Выбрано \(count(chosen.count)), но проброс выключен.",
                primaryAction: FeatureAction(title: "Включить проброс") { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        guard !chosen.isEmpty else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Ничего не выбрано",
                detail: "По умолчанию не пробрасывается ничего. Отметьте нужные устройства в списке ниже — по одному.",
                primaryAction: nil
            )
        }

        guard !live.isEmpty else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: "Выбранные устройства не подключены",
                detail: "Подключите их к Mac кабелем USB. По Bluetooth проброс не работает: приёмнику нужны USB-дескрипторы, а их отдаёт только USB-стек.",
                primaryAction: nil
            )
        }

        if let error = bridge?.lastError, !outputBlocked {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: "Устройство не читается",
                detail: error,
                alert: error,
                primaryAction: FeatureAction(title: "Повторить") { [weak self] in
                    self?.host.runtime.applyDeviceSetting()
                }
            )
        }

        let silent = live.filter { !$0.attachAcknowledged }
        if !silent.isEmpty {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: silent.count == live.count ? "Ждёт Windows" : "Приёмник подтвердил не всё",
                detail: "\(names(silent)) — приёмник ещё не подтвердил, что собрал виртуальное устройство.",
                primaryAction: FeatureAction(title: "Проверить связь") { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if outputBlocked {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: "Обратные команды не применяются",
                detail: "macOS не пропускает output-репорты этому приложению, устройство отвечает 0xE00002C1. Кнопки, оси и сенсоры работают, вибрация и подсветка — нет.",
                primaryAction: FeatureAction(title: "Выключить проброс") { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        let crowded = available.filter(\.crowdedOut)
        if !crowded.isEmpty {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: "Проброшено \(count(live.count)) из \(DeviceBridge.maxDevices)",
                detail: "Больше четырёх одновременно протокол не несёт. Не поместились: \(crowded.map(\.name).joined(separator: ", ")).",
                primaryAction: FeatureAction(title: "Выключить проброс") { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: live.count == 1 ? "Устройство проброшено" : "Проброшено \(count(live.count))",
            detail: "Windows видит \(names(live)).",
            primaryAction: FeatureAction(title: "Выключить проброс") { [weak self] in
                self?.isEnabled = false
            }
        )
    }

    private func count(_ n: Int) -> String {
        "\(n) \(Plural.of(n, "устройство", "устройства", "устройств"))"
    }

    private func names(_ devices: [DeviceBridge.DeviceStatus]) -> String {
        devices.map { "«\($0.product)» \($0.identity.modelDescription)" }.joined(separator: ", ")
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(DevicesCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(DevicesSettingsPane(feature: self))
    }
}
