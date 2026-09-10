import SwiftUI

/// The device block inside the popover (§7.1 point 3, §8.7).
///
/// The compact outline is drawn for a model that has a profile, and only when
/// there is exactly one device: four outlines stacked would stop being a
/// popover. Everything else is a one-line row, which is all an unrecognised
/// wheel or HOTAS can honestly be given.
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
            }

            if !feature.status.detail.isEmpty {
                Text(feature.status.detail)
                    .font(.dsCaption)
                    .foregroundStyle(feature.status.tone.text(palette))
                    .fixedSize(horizontal: false, vertical: true)
            }

            ForEach(feature.forwarded) { device in
                DeviceLine(device: device, showsName: soloVisualisable == nil)
            }

            if let action = feature.status.primaryAction {
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsSecondary)
                    .frame(maxWidth: .infinity)
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

/// One forwarded device on one line: what it is, how fast it is talking, and
/// whether it is talking at all.
struct DeviceLine: View {
    let device: DeviceBridge.DeviceStatus
    var showsName = true

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(spacing: Space.sm) {
            ActivityDot(idle: device.idle, acknowledged: device.attachAcknowledged)
            if showsName {
                Image(systemName: device.category.symbolName)
                    .foregroundStyle(palette.textDim)
                Text(device.product)
                    .font(.dsCaption)
                    .foregroundStyle(palette.text)
                    .lineLimit(1)
            }
            Spacer(minLength: Space.sm)
            Text(rate)
                .font(.dsCaption.monospacedDigit())
                .foregroundStyle(palette.textDim)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(device.product), \(rate)")
    }

    private var rate: String {
        var text = "\(Plural.reports(Int(device.reportRate)))/с"
        if let battery = device.battery { text += " · батарея \(battery)" }
        return text
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
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                picker

                ForEach(feature.forwarded) { device in
                    DeviceCard(feature: feature, device: device)
                }

                if let error = feature.bridge?.lastError, !feature.outputBlocked {
                    InlineAlert(text: error, tone: .bad)
                }
                if feature.outputBlocked {
                    InlineAlert(
                        text: "macOS не пропускает output-репорты этому приложению, устройство отвечает 0xE00002C1. Кнопки, оси и сенсоры работают, вибрация и подсветка — нет.",
                        tone: .warn
                    )
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
            Text("Одновременно можно пробросить до четырёх устройств. Свободно \(feature.slotsLeft) из \(DeviceBridge.maxDevices).")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)

            if feature.available.isEmpty {
                EmptyState(
                    symbolName: "cable.connector",
                    title: "USB-устройств не видно",
                    text: "Подключите контроллер, руль, педали или HOTAS кабелем USB. Встроенные клавиатура и трекпад Mac не предлагаются: их проброс означал бы, что всё набранное печатается сразу на двух машинах.",
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

            ForEach(warned, id: \.id) { row in
                InlineAlert(text: row.category.doubleInputWarning ?? "", tone: .warn)
            }
        }
    }

    /// Selected devices that will also go on working on this Mac. The warning is
    /// repeated outside the row because a row is easy to tick without reading.
    private var warned: [DeviceBridge.Available] {
        feature.available.filter { $0.isSelected && $0.category.warnsAboutDoubleInput }
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

    private var subtitle: String {
        var parts = [row.category.label, row.identity.modelDescription]
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
                            Text("\(device.category.label) · \(device.identity.modelDescription) · \(device.transport)")
                                .font(.dsCaption)
                                .foregroundStyle(palette.textDim)
                        }
                        Spacer(minLength: Space.sm)
                        ActivityDot(idle: device.idle, acknowledged: device.attachAcknowledged)
                    }
                }
            }

            HStack(spacing: Space.sm) {
                MetricTile(caption: "репортов/с", value: String(format: "%.0f", device.reportRate))
                MetricTile(caption: "проброшено", value: "\(device.reportsForwarded)")
                MetricTile(caption: "команд назад", value: "\(device.outputsApplied)")
                MetricTile(caption: "батарея", value: device.battery ?? "—")
            }

            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    CheckRow(
                        title: "Устройство открыто",
                        detail: device.manufacturer,
                        state: .ok
                    )
                    CheckRow(
                        title: "Подключение",
                        detail: device.transport,
                        state: device.transport == "USB" ? .ok : .failed
                    )
                    CheckRow(
                        title: "Приёмник подтвердил устройство",
                        detail: device.attachAcknowledged ? "да" : "нет",
                        state: !feature.isEnabled ? .pending : (device.attachAcknowledged ? .ok : .failed)
                    )
                    if device.outputsRejected > 0 {
                        CheckRow(
                            title: "Команды в устройство",
                            detail: "отклонено \(device.outputsRejected)",
                            state: .failed
                        )
                    }
                }
            }

            if let error = device.lastError {
                InlineAlert(text: error, tone: .warn)
            }
        }
    }

    private var caption: String {
        switch feature.mood(device) {
        case .inactive: return "Контур оживёт, когда устройство подключат кабелем"
        case .reading: return "Устройство читается. Нажмите что-нибудь — схема ответит"
        case .forwarding: return "Устройство читается и передаётся на Windows"
        }
    }
}
