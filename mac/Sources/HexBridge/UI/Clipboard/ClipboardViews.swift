import SwiftUI

/// The clipboard block inside the menu bar popover.
///
/// A feature whose whole job is to be invisible has nothing to show while it is
/// working: the row above says so in one word, and counters of objects sent and
/// received are not why anybody opened the menu bar. The only thing that earns
/// a pixel here is a transfer in progress — a 12 MB screenshot takes long
/// enough that the silence would read as a failure.
struct ClipboardCard: View {
    @Bindable var feature: ClipboardFeature

    @Environment(\.palette) private var palette

    var body: some View {
        if let flight = feature.flight {
            ProgressView(value: flight.fraction)
                .progressViewStyle(.linear)
                .tint(palette.okFg)
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
                // §6.1: one action per screen. «Выключить общий буфер» is what
                // the switch in «Основное» does, so it is not offered again
                // here — and when the feature is off, the empty state below is
                // the one place that offers to turn it on.
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction, !Wording.duplicatesSwitch(action.title) {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "doc.on.clipboard",
                        title: "Общий буфер выключен",
                        text: "Скопированное на этом Mac никуда не уходит.",
                        action: FeatureAction(title: "Включить общий буфер") { feature.isEnabled = true }
                    )
                } else {
                    privacy
                    transfer
                    telemetry
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
            text: "Скопированное на любой из двух машин уходит на другую — включая пароли. Передаются текст и картинки PNG.",
            tone: .warn
        )
    }

    /// The bar, and only the bar. What is moving and which way is already the
    /// status card's headline right above it — a card that said it again under
    /// a heading of its own was the same sentence twice on one screen.
    @ViewBuilder
    private var transfer: some View {
        if let flight = feature.flight {
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
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

    /// What crossed last, which is the only proof an invisible feature can
    /// offer. Three tiles and one line: what it was and which way it went.
    private var telemetry: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Последняя передача")
            HStack(spacing: Space.sm) {
                MetricTile(caption: "отправлено", value: "\(feature.sent)")
                MetricTile(caption: "принято", value: "\(feature.received)")
                MetricTile(caption: "когда", value: feature.lastWhenText)
            }
            Text(lastLine)
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var lastLine: String {
        guard let description = feature.lastDescription else {
            return "Пока ничего не передавалось. Записывается вид и размер объекта, не содержимое."
        }
        return "\(description) — \(feature.lastDirectionText). Записывается вид и размер объекта, не содержимое."
    }
}
