import Foundation
import HexBridgeText
import Observation
import SwiftUI

/// The HID passthrough feature and its five states from §7.3.
///
/// The important asymmetry with the microphone: a device is *read* even when the
/// passthrough is off, as long as something is drawing it. That is what makes
/// the "plugged in but not forwarded" state show a live outline, and it is the
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
    var title: String { L.t("dev.title") }
    let symbolName = "cable.connector"
    var summary: String { L.t("dev.summary") }

    private unowned let host: FeatureHost

    private(set) var status = FeatureStatus()
    private(set) var bridge: DeviceBridge.Status?
    /// Set when a device refuses output reports — the adaptive-trigger case from
    /// §10.3, which is a `warn`, not a failure.
    private(set) var outputBlocked = false

    init(host: FeatureHost) {
        self.host = host
        status = FeatureStatus(state: .off, tone: .off, headline: L.t("dev.off.headline"))
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
        // `kIOReturnNotPrivileged` is exactly what the trigger effects come
        // back with when macOS blocks writes to the device.
        outputBlocked = forwarded.contains { device in
            device.outputsForbidden || (device.outputsRejected > 0 && device.outputsApplied == 0)
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

        // Reading is not forwarding: a chosen device is opened and drawn as soon
        // as something is looking at it, switch or no switch. §7.3 calls that the
        // moment the user learns their pad is read correctly, and it is worth
        // more than a tidier state machine.
        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: L.t(live.isEmpty ? "dev.off.headline" : "dev.off.reading.headline"),
                detail: live.isEmpty
                    ? (chosen.isEmpty
                        ? L.t("dev.off.detail.nothing")
                        : L.t("dev.off.detail.chosen", L.plural("devices", chosen.count)))
                    : L.t("dev.off.detail.reading", names(live)),
                primaryAction: FeatureAction(title: L.t("dev.action.turnOn"), togglesFeature: true) { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        guard !chosen.isEmpty else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: L.t("dev.nothingChosen.headline"),
                detail: L.t("dev.nothingChosen.detail"),
                primaryAction: nil
            )
        }

        guard !live.isEmpty else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: L.t("dev.absent.headline"),
                detail: L.t("dev.absent.detail"),
                primaryAction: nil
            )
        }

        if let error = bridge?.lastError, !outputBlocked {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("dev.error.headline"),
                detail: error,
                alert: error,
                primaryAction: FeatureAction(title: L.t("action.retry")) { [weak self] in
                    self?.host.runtime.applyDeviceSetting()
                }
            )
        }

        let silent = live.filter { !$0.attachAcknowledged }
        if !silent.isEmpty {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: L.t(silent.count == live.count ? "dev.waiting.headline" : "dev.partial.headline"),
                detail: L.t("dev.waiting.detail", names(silent)),
                primaryAction: FeatureAction(title: L.t("action.checkLink")) { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if outputBlocked {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: L.t("dev.blocked.headline"),
                detail: L.t("dev.blocked.detail"),
                primaryAction: FeatureAction(title: L.t("dev.action.turnOff"), togglesFeature: true) { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        let crowded = available.filter(\.crowdedOut)
        if !crowded.isEmpty {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: L.t(
                    "dev.crowded.headline",
                    L.plural("devices", live.count),
                    L.integer(DeviceBridge.maxDevices)
                ),
                detail: L.t("dev.crowded.detail", crowded.map(\.name).joined(separator: ", ")),
                primaryAction: FeatureAction(title: L.t("dev.action.turnOff"), togglesFeature: true) { [weak self] in
                    self?.isEnabled = false
                }
            )
        }

        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: live.count == 1
                ? L.t("dev.live.oneDevice")
                : L.t("dev.live.headline", L.plural("devices", live.count)),
            detail: L.t("dev.live.detail", names(live)),
            primaryAction: FeatureAction(title: L.t("dev.action.turnOff"), togglesFeature: true) { [weak self] in
                self?.isEnabled = false
            }
        )
    }

    /// The devices, named the way their owner names them.
    ///
    /// The USB ids used to be appended here and scrubbed back off by the
    /// presentation layer. The glossary settles it: an id is an internal detail,
    /// it belongs in the log, and the person reading this sentence tells two
    /// identical pads apart by looking at the desk.
    private func names(_ devices: [DeviceBridge.DeviceStatus]) -> String {
        devices.map { L.t("quoted", $0.product) }.joined(separator: ", ")
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(DevicesCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(DevicesSettingsPane(feature: self))
    }
}
