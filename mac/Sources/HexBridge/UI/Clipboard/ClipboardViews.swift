import SwiftUI

/// The clipboard block inside the menu bar popover.
///
/// One line of what happened last, a progress bar only while something is
/// actually moving, and the switch's counterpart action. A feature whose whole
/// job is invisible needs exactly one thing on screen: proof it is working.
struct ClipboardCard: View {
    @Bindable var feature: ClipboardFeature

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            if let flight = feature.flight {
                ProgressView(value: flight.fraction)
                    .progressViewStyle(.linear)
                    .tint(palette.okFg)
            }

            if !feature.status.detail.isEmpty {
                Text(feature.status.detail)
                    .font(.dsCaption)
                    .foregroundStyle(feature.status.tone.text(palette))
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let alert = feature.status.alert, feature.status.state == .error {
                InlineAlert(text: alert, tone: .bad)
            }

            Text("отправлено \(feature.sent) · принято \(feature.received)")
                .font(.dsCaption.monospacedDigit())
                .foregroundStyle(palette.textDim)

            if let action = feature.status.primaryAction {
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsSecondary)
                    .frame(maxWidth: .infinity)
            }
        }
    }
}

/// The clipboard pane of the settings window.
struct ClipboardSettingsPane: View {
    @Bindable var feature: ClipboardFeature

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

                if feature.isEnabled {
                    privacy
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "doc.on.clipboard",
                        title: "Общий буфер выключен",
                        text: "Пока фича выключена, скопированное на этом Mac никуда не уходит. Включите её, если хотите переносить текст и картинки между машинами одним Cmd-C.",
                        action: FeatureAction(title: "Включить общий буфер") { feature.isEnabled = true }
                    )
                } else {
                    transfer
                    telemetry
                    formats
                }
            }
            .padding(Space.xl)
        }
        .background(palette.bg)
    }

    /// The honest sentence, not a reassuring one. It lives on the pane and not
    /// only next to the switch: somebody who opens this a month later should not
    /// have to remember what they agreed to.
    private var privacy: some View {
        InlineAlert(
            text: "Всё, что вы копируете на любой из двух машин, отправляется на другую — включая пароли, если они окажутся в буфере. Канал шифруется тем же ключом, что и звук, но содержимое буфера покидает этот Mac.",
            tone: .warn
        )
    }

    @ViewBuilder
    private var transfer: some View {
        if let flight = feature.flight {
            VStack(alignment: .leading, spacing: Space.sm) {
                SectionLabel(text: "Сейчас передаётся")
                Card(padding: Space.md) {
                    VStack(alignment: .leading, spacing: Space.sm) {
                        Text(flight.direction == .outgoing
                             ? "Отправляем: \(flight.description)"
                             : "Принимаем: \(flight.description)")
                            .font(.dsLabel)
                            .foregroundStyle(palette.text)
                        ProgressView(value: flight.fraction)
                            .progressViewStyle(.linear)
                            .tint(palette.okFg)
                        Text("\(flight.chunksDone) из \(flight.chunkCount) блоков")
                            .font(.dsCaption.monospacedDigit())
                            .foregroundStyle(palette.textDim)
                    }
                }
            }
        }
    }

    private var telemetry: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Последняя передача")
            HStack(spacing: Space.sm) {
                MetricTile(caption: "отправлено", value: "\(feature.sent)")
                MetricTile(caption: "принято", value: "\(feature.received)")
                MetricTile(caption: "когда", value: feature.lastWhenText)
            }
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    LabeledContent("Что") {
                        Text(feature.lastDescription ?? "—")
                            .font(.dsLabel)
                            .foregroundStyle(palette.text)
                    }
                    LabeledContent("Куда") {
                        Text(feature.lastDirectionText)
                            .font(.dsLabel)
                            .foregroundStyle(palette.text)
                    }
                }
                .font(.dsLabel)
                .foregroundStyle(palette.textDim)
            }
            Text("Здесь показывается только вид и размер объекта — само содержимое буфера нигде не записывается.")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var formats: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Что передаётся")
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    CheckRow(title: "Текст", detail: "UTF-8, целиком", state: .ok)
                    CheckRow(title: "Картинки", detail: "PNG, до 16 МиБ", state: .ok)
                    CheckRow(title: "Файлы и всё остальное", detail: "не передаётся", state: .pending)
                }
            }
        }
    }
}
