import SwiftUI

/// The device block inside the popover (§7.1 point 3, §8.7).
///
/// The compact outline is drawn for a model that has a profile, and only when
/// there is exactly one device: four outlines stacked would stop being a
/// popover. Everything else is a one-line row, which is all an unrecognised
/// wheel or HOTAS can honestly be given.
///
/// Either way it is a picture or a name and nothing else. The rate in reports
/// per second, the acknowledgement from the other machine, the USB ids — those
/// answer "почему не работает", and that question is asked in the settings
/// window, by somebody who has already seen here that it does not.
struct DevicesCard: View {
    @Bindable var feature: DevicesFeature

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            if let solo = soloVisualisable {
                ControllerView(
                    snapshot: { feature.liveState(solo.number) },
                    variant: .compact,
                    mood: feature.mood(solo)
                )
                .frame(height: 68)
            } else {
                ForEach(feature.forwarded) { device in
                    DeviceLine(device: device)
                }
            }
        }
        .onAppear { feature.beginObservation() }
        .onDisappear { feature.endObservation() }
    }

    /// The one device worth drawing in full, or nil.
    private var soloVisualisable: DeviceBridge.DeviceStatus? {
        let devices = feature.forwarded
        guard devices.count == 1, let only = devices.first, only.canVisualise else { return nil }
        return only
    }
}

/// One forwarded device on one line: what it is, whether it is talking, and how
/// much charge it has left.
///
/// Battery is the one number here that is about the device rather than about
/// the bridge, and the only one somebody glancing at a menu bar acts on.
struct DeviceLine: View {
    let device: DeviceBridge.DeviceStatus

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(spacing: Space.sm) {
            ActivityDot(idle: device.idle, acknowledged: device.attachAcknowledged)
            Image(systemName: device.category.symbolName)
                .foregroundStyle(palette.textDim)
            Text(device.product)
                .font(.dsCaption)
                .foregroundStyle(palette.text)
                .lineLimit(1)
            Spacer(minLength: Space.sm)
            if let battery = device.battery {
                Text(battery)
                    .font(.dsCaption.monospacedDigit())
                    .foregroundStyle(palette.textDim)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(device.battery.map { "\(device.product), батарея \($0)" } ?? device.product)
    }
}

/// The activity light. Colour alone would break §11.1, so the shape changes
/// too: a filled dot while reports are arriving, a hollow one when they stop.
struct ActivityDot: View {
    let idle: TimeInterval
    var acknowledged = true

    @Environment(\.palette) private var palette

    var body: some View {
        Image(systemName: live ? "circle.fill" : "circle")
            .font(.system(size: 8))
            .foregroundStyle(tone.foreground(palette))
            .accessibilityLabel(live ? "передаёт" : "молчит")
    }

    /// Two seconds is the same "idle" threshold the visualisation uses.
    private var live: Bool { idle < 2 }

    private var tone: Tone {
        if !acknowledged { return .warn }
        return live ? .ok : .neutral
    }
}

/// The devices pane of the settings window (§7.3 in full).
struct DevicesSettingsPane: View {
    @Bindable var feature: DevicesFeature

    @Environment(\.palette) private var palette

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                // §6.1: general errors live in the status card at the top and
                // are not smeared across the pane. Both banners that used to
                // stand here repeated what that card already says.
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction, !Wording.duplicatesSwitch(action.title) {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                picker

                ForEach(feature.forwarded) { device in
                    DeviceCard(feature: feature, device: device)
                }
            }
            .padding(Space.xl)
        }
        .background(palette.bg)
        .onAppear { feature.beginObservation() }
        .onDisappear { feature.endObservation() }
    }

    // MARK: - The picker

    @ViewBuilder
    private var picker: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Что пробрасывать")
            Text("Свободно \(feature.slotsLeft) из \(DeviceBridge.maxDevices).")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)

            if feature.available.isEmpty {
                EmptyState(
                    symbolName: "cable.connector",
                    title: "USB-устройств не видно",
                    text: "Подключите контроллер, руль, педали или HOTAS кабелем USB.",
                    action: nil
                )
            } else {
                Card(padding: Space.md) {
                    VStack(alignment: .leading, spacing: Space.md) {
                        ForEach(feature.available) { row in
                            DevicePickerRow(feature: feature, row: row)
                        }
                    }
                }
            }
        }
    }
}

/// One line of the picker: a switch, what the device is, and — for a keyboard
/// or a mouse — what ticking it will cost.
struct DevicePickerRow: View {
    @Bindable var feature: DevicesFeature
    let row: DeviceBridge.Available

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(alignment: .top, spacing: Space.sm) {
            Image(systemName: row.category.symbolName)
                .foregroundStyle(palette.textDim)
                .frame(width: 20)

            VStack(alignment: .leading, spacing: 2) {
                Text(row.name)
                    .font(.dsBody)
                    .foregroundStyle(enabled ? palette.text : palette.textDim)
                Text(subtitle)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                if let reason = row.eligibility.reason {
                    Text(reason)
                        .font(.dsCaption)
                        .foregroundStyle(palette.warnText)
                } else if row.crowdedOut {
                    Text("Свободных номеров нет — отключите другое устройство")
                        .font(.dsCaption)
                        .foregroundStyle(palette.warnText)
                } else if let warning = row.category.doubleInputWarning {
                    Text(warning)
                        .font(.dsCaption)
                        .foregroundStyle(palette.warnText)
                }
            }

            Spacer(minLength: Space.sm)

            Toggle("", isOn: binding)
                .labelsHidden()
                .toggleStyle(.switch)
                .disabled(!enabled)
                .accessibilityLabel("Пробрасывать \(row.name)")
        }
    }

    private var enabled: Bool { row.eligibility == .eligible }

    /// What the device is, in words. The USB ids used to sit here; they are a
    /// developer's way of telling two identical pads apart, and the person
    /// ticking this box tells them apart by looking at the desk.
    private var subtitle: String {
        var parts = [row.category.label]
        if let number = row.forwardedAs { parts.append("номер \(number)") }
        if row.manufacturer != "—", !row.manufacturer.isEmpty { parts.insert(row.manufacturer, at: 0) }
        return parts.joined(separator: " · ")
    }

    private var binding: Binding<Bool> {
        Binding(
            get: { row.isSelected },
            set: { feature.setSelected(row.identity, $0) }
        )
    }
}

/// One forwarded device in the settings window.
///
/// A model with a profile gets its live outline, because that outline is a
/// self-test: the user pulls a trigger and sees it arrive. Everything else gets
/// the truth and nothing more — name, ids, rate, activity — rather than a
/// drawing of a steering wheel we have no way to make honest.
struct DeviceCard: View {
    @Bindable var feature: DevicesFeature
    let device: DeviceBridge.DeviceStatus

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Устройство \(device.number) · \(device.product)")

            if device.canVisualise {
                Card(padding: Space.md) {
                    VStack(alignment: .leading, spacing: Space.sm) {
                        ControllerView(
                            snapshot: { feature.liveState(device.number) },
                            variant: .full,
                            mood: feature.mood(device)
                        )
                        .frame(height: 240)
                        .frame(maxWidth: .infinity)

                        Text(caption)
                            .font(.dsCaption)
                            .foregroundStyle(palette.textDim)
                            .frame(maxWidth: .infinity, alignment: .center)
                    }
                }
            } else {
                Card(padding: Space.md) {
                    HStack(spacing: Space.md) {
                        Image(systemName: device.category.symbolName)
                            .font(.system(size: 28))
                            .foregroundStyle(palette.textDim)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(device.product)
                                .font(.dsBody)
                                .foregroundStyle(palette.text)
                            Text("\(device.category.label) · \(device.transport)")
                                .font(.dsCaption)
                                .foregroundStyle(palette.textDim)
                        }
                        Spacer(minLength: Space.sm)
                        ActivityDot(idle: device.idle, acknowledged: device.attachAcknowledged)
                    }
                }
            }

            HStack(spacing: Space.sm) {
                MetricTile(caption: "батарея", value: device.battery ?? "—")
                MetricTile(caption: "отчётов/с", value: String(format: "%.0f", device.reportRate))
                MetricTile(caption: "передано", value: "\(device.reportsForwarded)")
                MetricTile(caption: "команд назад", value: "\(device.outputsApplied)")
            }

            // Two lines, both of which can fail and both of which the user can
            // do something about: replug over USB, or check the link. «Устройство
            // открыто» was a row that only ever said yes.
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    CheckRow(
                        title: "Подключено кабелем USB",
                        detail: device.transport,
                        state: device.transport == "USB" ? .ok : .failed
                    )
                    CheckRow(
                        title: "Windows видит устройство",
                        detail: device.attachAcknowledged ? "да" : "нет",
                        state: !feature.isEnabled ? .pending : (device.attachAcknowledged ? .ok : .failed)
                    )
                    if device.outputsRejected > 0 {
                        CheckRow(
                            title: "Вибрация и подсветка",
                            detail: "не применяются",
                            state: .failed
                        )
                    }
                }
            }
        }
    }

    private var caption: String {
        switch feature.mood(device) {
        case .inactive: return "Схема оживёт, когда устройство подключат кабелем"
        case .reading: return "Нажмите что-нибудь — схема ответит"
        case .forwarding: return "Windows видит это устройство"
        }
    }
}
