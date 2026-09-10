import SwiftUI

/// The menu bar popover (§7.1, macOS).
///
/// HIG says "display a menu — not a popover" and then makes an exception for
/// functionality that is too complex for a menu. This is that exception: a
/// live level meter and a live controller outline are the two things that
/// answer "работает или нет" in under two seconds, and `NSMenu` cannot draw
/// either. The reasoning is recorded in DESIGN.md §1.1 so it is not relitigated.
///
/// The shell walks `model.features` and asks each one for its card. It does not
/// know what a microphone is.
///
/// One rule governs everything below the summary: **a card carries one state
/// and no more than one action, and the action appears only when there is
/// something to do.** The popover used to end up with a button in every card —
/// the same «Проверить связь» twice over, and a «Выключить общий буфер» two
/// centimetres from the switch that does exactly that. So the cards no longer
/// carry buttons at all: the switch is the on/off, and the one remedy worth
/// offering is offered once, underneath, by `AppModel.popoverAction`.
struct PopoverView: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            summary

            // §4.3 caps the popover at 520 pt. A fixed frame would clip the
            // tallest state instead of capping it, so the feature stack — the
            // only part whose height depends on state — scrolls inside the cap
            // while the summary and the footer stay put.
            CappedScroll(maxHeight: Metrics.popoverMaxHeight - 120) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    ForEach(model.features, id: \.id) { feature in
                        // The summary above is one of these cards' own
                        // headlines; the card it came from says it in fewer
                        // words rather than saying it again.
                        FeatureCard(feature: feature, summary: model.summary.headline)
                    }
                }
            }

            if let action = model.popoverAction {
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsPrimary)
            }

            if let notice = model.noticeText {
                InlineAlert(
                    text: Wording.plain(notice),
                    tone: .warn,
                    action: FeatureAction(title: "Понятно") { model.noticeText = nil }
                )
            }

            Divider()
            footer
        }
        .padding(Space.md)
        .frame(width: Metrics.popoverWidth)
        .background(palette.bg)
        .onAppear { model.refreshDevices() }
    }

    /// One sentence and who it is about. §0 rule 1.
    private var summary: some View {
        HStack(spacing: Space.sm) {
            Image(systemName: model.summary.tone.symbolName)
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: 17))
                .foregroundStyle(model.summary.tone.foreground(palette))
                .contentTransition(.symbolEffect(.replace.downUp))
            VStack(alignment: .leading, spacing: 1) {
                Text(Wording.plain(model.summary.headline))
                    .font(.dsHeading)
                    .foregroundStyle(palette.text)
                    .contentTransition(.opacity)
                Text(model.summary.detail)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }

    private var footer: some View {
        HStack(spacing: Space.sm) {
            Button {
                WindowRouter.shared.open(.settings)
            } label: {
                // §1.1: macOS 27 hides menu item images by default. Asking for
                // both parts explicitly keeps the icon wherever it is allowed.
                Label("Настройки…", systemImage: "gearshape")
                    .labelStyle(.titleAndIcon)
            }
            .buttonStyle(.link)
            .keyboardShortcut(",", modifiers: .command)

            Spacer()

            Button("Выйти") { model.quit() }
                .buttonStyle(.link)
        }
        .font(.dsCaption)
        .foregroundStyle(palette.textDim)
    }
}

/// One feature's block inside the popover.
///
/// §7.1: a switched-off feature collapses to a single row with its switch, so
/// the popover height does not jump by more than 40 pt between neighbouring
/// states (§11.4 check 4). A feature that is on adds only what cannot be said
/// in words — the meter, the outline, the progress bar — and never a paragraph
/// explaining itself.
private struct FeatureCard: View {
    let feature: any Feature
    let summary: String

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    var body: some View {
        Card(padding: Space.sm + 2) {
            VStack(alignment: .leading, spacing: Space.sm) {
                FeatureRow(
                    title: feature.title,
                    symbolName: feature.symbolName,
                    status: feature.status,
                    echoing: summary,
                    isEnabled: Binding(
                        get: { feature.isEnabled },
                        set: { feature.isEnabled = $0 }
                    )
                )
                if feature.isEnabled {
                    feature.popoverCard()
                }
            }
        }
        .animation(Motion.standard(Motion.base, reduced: motion.reduceMotion), value: feature.isEnabled)
    }
}
