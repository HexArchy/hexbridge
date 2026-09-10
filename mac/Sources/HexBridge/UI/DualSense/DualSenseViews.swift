import SwiftUI

/// The DualSense block inside the popover (§7.1 point 3, §8.7).
///
/// The compact outline: sticks, triggers and button highlights only. The full
/// visualisation lives in the settings window, because a popover that grows a
/// gyroscope has stopped being a popover.
struct DualSenseCard: View {
    @Bindable var feature: DualSenseFeature

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            if feature.bridge?.connected == true {
                ControllerView(
                    snapshot: { feature.liveState() },
                    variant: .compact,
                    mood: feature.mood
                )
                .frame(height: 68)
            }

            if !feature.status.detail.isEmpty {
                Text(feature.status.detail)
                    .font(.dsCaption)
                    .foregroundStyle(feature.status.tone.text(palette))
                    .fixedSize(horizontal: false, vertical: true)
            }

            if feature.bridge?.connected == true {
                Text("\(Plural.reports(Int(feature.reportsPerSecond)))/с · батарея \(feature.bridge?.battery ?? "—")")
                    .font(.dsCaption.monospacedDigit())
                    .foregroundStyle(palette.textDim)
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
}

/// The DualSense pane of the settings window (§7.3 in full).
///
/// The visualisation is the top ~340 pt of the pane, as the document asks: it
/// is not decoration, it is the self-test that proves the pad is being read.
struct DualSenseSettingsPane: View {
    @Bindable var feature: DualSenseFeature

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

                visualisation

                if feature.bridge?.connected == true {
                    telemetry
                    channel
                } else {
                    EmptyState(
                        symbolName: "cable.connector",
                        title: "Контроллер не подключён",
                        text: "Подключите DualSense к Mac кабелем USB. По Bluetooth проброс не работает: нужен доступ к HID-репортам на полной частоте.",
                        action: nil
                    )
                }
            }
            .padding(Space.xl)
        }
        .background(palette.bg)
        .onAppear { feature.beginObservation() }
        .onDisappear { feature.endObservation() }
    }

    private var visualisation: some View {
        Card(padding: Space.md) {
            VStack(alignment: .leading, spacing: Space.sm) {
                ControllerView(
                    snapshot: { feature.liveState() },
                    variant: .full,
                    mood: feature.mood
                )
                .frame(height: 240)
                .frame(maxWidth: .infinity)

                Text(caption)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .frame(maxWidth: .infinity, alignment: .center)
            }
        }
    }

    private var caption: String {
        switch feature.mood {
        case .inactive: return "Контур оживёт, когда контроллер подключат кабелем"
        case .reading: return "Контроллер читается. Нажмите что-нибудь — схема ответит"
        case .forwarding: return "Контроллер читается и передаётся на Windows"
        }
    }

    private var telemetry: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Телеметрия")
            HStack(spacing: Space.sm) {
                MetricTile(caption: "репортов/с", value: String(format: "%.0f", feature.reportsPerSecond))
                MetricTile(caption: "батарея", value: feature.bridge?.battery ?? "—")
                MetricTile(caption: "проброшено", value: "\(feature.bridge?.reportsForwarded ?? 0)")
                MetricTile(caption: "команд назад", value: "\(feature.bridge?.outputsApplied ?? 0)")
            }
        }
    }

    @ViewBuilder
    private var channel: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Канал устройства")
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    CheckRow(
                        title: "Контроллер открыт",
                        detail: feature.bridge?.product ?? "—",
                        state: feature.bridge?.connected == true ? .ok : .failed
                    )
                    CheckRow(
                        title: "Подключение",
                        detail: feature.bridge?.transport ?? "—",
                        state: feature.bridge?.transport == "USB" ? .ok : .failed
                    )
                    CheckRow(
                        title: "Приёмник подтвердил устройство",
                        detail: feature.bridge?.attachAcknowledged == true ? "да" : "нет",
                        state: !feature.isEnabled ? .pending
                            : (feature.bridge?.attachAcknowledged == true ? .ok : .failed)
                    )
                    if let rejected = feature.bridge?.outputsRejected, rejected > 0 {
                        CheckRow(
                            title: "Команды в контроллер",
                            detail: "отклонено \(rejected)",
                            state: .failed
                        )
                    }
                }
            }

            if feature.outputBlocked {
                InlineAlert(
                    text: "macOS не пропускает output-репорты этому приложению, контроллер отвечает 0xE00002C1. Кнопки, стики, гироскоп и тачпад работают, сопротивление триггеров и лайтбар — нет.",
                    tone: .warn
                )
            } else if let error = feature.bridge?.lastError {
                InlineAlert(text: error, tone: .bad)
            }
        }
    }
}
