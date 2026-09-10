import HexBridgeText
import SwiftUI

// MARK: - 2. StatusDot

/// 8 pt dot. Under `waiting` it pulses — transition #9, the only looping
/// animation in the app, and the first thing reduced motion switches off.
struct StatusDot: View {
    let tone: Tone
    var pulsing = false

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion
    @State private var dim = false

    var body: some View {
        Circle()
            .fill(tone.foreground(palette))
            .frame(width: Metrics.statusDot, height: Metrics.statusDot)
            .opacity(dim ? 0.45 : 1)
            .onAppear { startIfNeeded() }
            .onChange(of: pulsing) { _, _ in startIfNeeded() }
            .onChange(of: motion.reduceMotion) { _, _ in startIfNeeded() }
    }

    private func startIfNeeded() {
        let animates = pulsing && !motion.reduceMotion
        withAnimation(
            animates
                ? Motion.standard(Motion.waitingPulsePeriod / 2).repeatForever(autoreverses: true)
                : nil
        ) {
            dim = animates
        }
    }
}

// MARK: - 1. StatusCard

/// One large sentence about the state plus a second line. Transition #4: the
/// background, border and title colours cross-fade over 240 ms; the text is
/// swapped with `contentTransition`, it does not slide.
struct StatusCard<Trailing: View>: View {
    let status: FeatureStatus
    @ViewBuilder var trailing: Trailing

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    var body: some View {
        HStack(alignment: .top, spacing: Space.md) {
            StatusDot(tone: status.tone, pulsing: status.state == .waiting || status.state == .starting)
                .padding(.top, Space.xs + 1)

            VStack(alignment: .leading, spacing: Space.xs) {
                Text(headline)
                    .font(.dsHeading)
                    .foregroundStyle(status.tone.text(palette))
                    .contentTransition(.opacity)
                    .fixedSize(horizontal: false, vertical: true)
                if !detail.isEmpty {
                    Text(detail)
                        .font(.dsCaption)
                        .foregroundStyle(palette.textDim)
                        .contentTransition(.opacity)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Space.sm)
            trailing
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: Radius.lg, style: .continuous)
                .fill(status.tone.background(palette))
        )
        .overlay(
            RoundedRectangle(cornerRadius: Radius.lg, style: .continuous)
                .strokeBorder(status.tone.border(palette), lineWidth: 1)
        )
        .animation(Motion.standard(Motion.base, reduced: motion.reduceMotion), value: status.tone)
        .animation(Motion.standard(Motion.short, reduced: motion.reduceMotion), value: status.headline)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(L.t("statusCard.accessibility", headline, detail))
    }

    private var headline: String { Wording.plain(status.headline) }

    /// One sentence. Whatever the feature added after the full stop explains
    /// the state rather than naming it, and this card exists to name it — the
    /// explanation belongs on the pane, or in the log with the error code that
    /// `plain` has just taken out of here.
    private var detail: String { Wording.firstSentence(Wording.plain(status.detail)) }
}

extension StatusCard where Trailing == EmptyView {
    init(status: FeatureStatus) {
        self.init(status: status) { EmptyView() }
    }
}

// MARK: - 3. MetricTile

/// Caption plus one large number. Transition #6: the digits roll, but only
/// inside an explicit animation, and never faster than twice a second.
struct MetricTile: View {
    let caption: String
    let value: String
    var tone: Tone = .neutral
    var help: String?

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(caption)
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .lineLimit(1)
            Text(value)
                .font(.system(size: 15, weight: .semibold).monospacedDigit())
                .foregroundStyle(tone == .neutral ? palette.text : tone.text(palette))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
                .contentTransition(.numericText())
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.sm)
        .background(
            RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                .fill(palette.surfaceAlt)
        )
        .overlay(
            RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                .strokeBorder(palette.border, lineWidth: 1)
        )
        .help(help ?? "")
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(caption): \(value)")
    }
}

// MARK: - 4. LevelMeter

/// Peak meter with peak-hold, −60…0 dBFS, linear in dB.
///
/// Transition #8: the fill rises instantly and falls over 90 ms. Rising slowly
/// would make the meter lie about the peak, which is the one thing a meter
/// must not do — so the asymmetry is deliberate and is not "missing polish".
struct LevelMeter: View {
    /// Linear sample peak, 0…1.
    let peak: Float
    var muted = false
    /// `peak −12.9 dBFS` under the bar. A number for somebody setting a gain,
    /// which is a settings-window job; in the popover the bar itself is the
    /// whole answer, so the caption is off there.
    var showsCaption = true

    @Environment(\.palette) private var palette
    @State private var displayed: Double = 0
    @State private var hold: Double = 0
    @State private var lastTick = Date()

    private var fraction: Double { Self.fraction(of: peak) }

    static func fraction(of peak: Float) -> Double {
        guard peak > 0 else { return 0 }
        let db = 20 * log10(Double(peak))
        return min(1, max(0, (db + 60) / 60))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(palette.meterTrack)
                    Capsule()
                        .fill(muted ? AnyShapeStyle(palette.off) : AnyShapeStyle(gradient))
                        .frame(width: max(2, geometry.size.width * displayed))
                    if !muted, hold > 0.001 {
                        // Peak-hold tick, 2 px, falling at 20 dB/s.
                        Capsule()
                            .fill(palette.text)
                            .frame(width: 2)
                            .offset(x: max(0, geometry.size.width * hold - 2))
                    }
                }
            }
            .frame(height: Metrics.meterHeight)
            .clipShape(Capsule())

            if showsCaption {
                HStack(spacing: Space.xs) {
                    Text(muted ? L.t("meter.muted") : caption)
                        .font(.dsCaption)
                        .foregroundStyle(palette.textDim)
                        .monospacedDigit()
                    Spacer()
                }
            }
        }
        .onChange(of: peak) { _, _ in step() }
        .accessibilityLabel(L.t("meter.accessibility", muted ? L.t("meter.muted") : caption))
    }

    private var caption: String {
        guard peak > 0 else { return L.t("meter.silence") }
        return L.t("meter.peak", L.number(20 * log10(Double(peak))))
    }

    /// Called from the model's poll rather than from a timer of its own; the
    /// elapsed time is measured so the decay rate does not depend on it.
    private func step() {
        let now = Date()
        let dt = min(0.5, now.timeIntervalSince(lastTick))
        lastTick = now

        let target = fraction
        if target >= displayed {
            displayed = target          // rise: instant
        } else {
            // Fall: reach the target in ~90 ms.
            let k = min(1, dt / 0.09)
            displayed += (target - displayed) * k
        }

        if target >= hold {
            hold = target
        } else {
            // 20 dB/s over a 60 dB scale is 1/3 of the bar per second.
            hold = max(target, hold - dt / 3)
        }
    }

    private var gradient: LinearGradient {
        // −12 dBFS is 0.8 of the scale, −3 dBFS is 0.95.
        LinearGradient(
            stops: [
                .init(color: palette.okFg, location: 0),
                .init(color: palette.okFg, location: 0.78),
                .init(color: palette.warnFg, location: 0.82),
                .init(color: palette.warnFg, location: 0.93),
                .init(color: palette.badFg, location: 0.96),
            ],
            startPoint: .leading,
            endPoint: .trailing
        )
    }
}

// MARK: - 5. Sparkline

/// A minute of history in one stroke. Not animated: it is data, and a new
/// sample every 200 ms animated would be a smear (§5.5 rule 2).
struct Sparkline: View {
    let samples: [Double]
    var tone: Tone = .ok

    @Environment(\.palette) private var palette

    var body: some View {
        Canvas { context, size in
            guard samples.count > 1 else { return }
            let step = size.width / CGFloat(max(1, samples.count - 1))
            var path = Path()
            for (index, value) in samples.enumerated() {
                let x = CGFloat(index) * step
                let y = size.height * (1 - CGFloat(min(1, max(0, value))))
                if index == 0 { path.move(to: CGPoint(x: x, y: y)) } else { path.addLine(to: CGPoint(x: x, y: y)) }
            }
            context.stroke(path, with: .color(tone.foreground(palette)), lineWidth: 1.5)
        }
        .background(
            RoundedRectangle(cornerRadius: Radius.sm, style: .continuous)
                .fill(palette.surfaceAlt)
        )
        .accessibilityHidden(true)
    }
}

// MARK: - 6. FeatureRow

/// Icon + name + state + switch. The switch is the only way to turn a feature
/// on or off — §6.1 forbids Start/Stop buttons next to it.
struct FeatureRow: View {
    let title: String
    let symbolName: String
    let status: FeatureStatus
    @Binding var isEnabled: Bool

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(spacing: Space.sm) {
            Image(systemName: symbolName)
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: 15))
                .foregroundStyle(status.state == .off ? palette.off : status.tone.foreground(palette))
                .frame(width: 20)

            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.dsLabel.weight(.medium))
                    .foregroundStyle(palette.text)
                // The name is already on the line above, so this line never
                // repeats it: «Буфер обмена» over «Буфер обмена общий» was two
                // lines to say one thing. One word from the glossary instead,
                // the same word wherever the same state occurs.
                Text(Wording.stateWord(status))
                    .font(.dsCaption)
                    .foregroundStyle(status.tone.text(palette))
                    .lineLimit(1)
                    .contentTransition(.opacity)
            }
            Spacer(minLength: Space.sm)
            Toggle("", isOn: $isEnabled)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
                .accessibilityLabel(L.t("featureRow.accessibility", title))
        }
    }
}

// MARK: - 8/9. Buttons

/// §6.1: exactly one of these per screen.
///
/// `.buttonStyle(.glass)` is deliberately absent. On macOS 26 it has a broken
/// hover state outside a toolbar, and DESIGN.md §1.1 puts Liquid Glass outside
/// the first revision entirely.
struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.palette) private var palette
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.dsLabel.weight(.medium))
            .foregroundStyle(palette.accentInk)
            .padding(.horizontal, Space.md)
            .padding(.vertical, Space.sm - 1)
            .frame(maxWidth: .infinity)
            .background(
                RoundedRectangle(cornerRadius: Radius.sm, style: .continuous)
                    .fill(palette.accent.opacity(isEnabled ? (configuration.isPressed ? 0.82 : 1) : 0.4))
            )
            .contentShape(Rectangle())
    }
}

struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.palette) private var palette
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.dsLabel)
            .foregroundStyle(isEnabled ? palette.text : palette.textDim)
            .padding(.horizontal, Space.md)
            .padding(.vertical, Space.sm - 1)
            .background(
                RoundedRectangle(cornerRadius: Radius.sm, style: .continuous)
                    .fill(configuration.isPressed ? palette.surfaceAlt : palette.surface)
            )
            .overlay(
                RoundedRectangle(cornerRadius: Radius.sm, style: .continuous)
                    .strokeBorder(palette.borderStrong.opacity(0.55), lineWidth: 1)
            )
            .contentShape(Rectangle())
    }
}

extension ButtonStyle where Self == PrimaryButtonStyle {
    static var dsPrimary: PrimaryButtonStyle { PrimaryButtonStyle() }
}

extension ButtonStyle where Self == SecondaryButtonStyle {
    static var dsSecondary: SecondaryButtonStyle { SecondaryButtonStyle() }
}

// MARK: - 10. InlineAlert

/// Lives inside the card it belongs to. Transition #10: fades in with its
/// height, no shake, no red flash.
struct InlineAlert: View {
    let text: String
    var tone: Tone = .bad
    var action: FeatureAction?

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(alignment: .top, spacing: Space.sm) {
                Image(systemName: tone.symbolName)
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(tone.foreground(palette))
                Text(Wording.plain(text))
                    .font(.dsCaption)
                    .foregroundStyle(tone.text(palette))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            if let action {
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsSecondary)
            }
        }
        .padding(Space.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                .fill(tone.background(palette))
        )
        .overlay(
            RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                .strokeBorder(tone.border(palette), lineWidth: 1)
        )
    }
}

// MARK: - 11. EmptyState

/// §6.1: an empty state without an action is a design bug, so the action is
/// not optional in the initialiser that matters.
struct EmptyState: View {
    let symbolName: String
    let title: String
    let text: String
    var action: FeatureAction?

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(spacing: Space.sm) {
            Image(systemName: symbolName)
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: 32))
                .foregroundStyle(palette.textDim)
            Text(title)
                .font(.dsHeading)
                .foregroundStyle(palette.text)
                .multilineTextAlignment(.center)
            Text(text)
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            if let action {
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsPrimary)
                    .padding(.top, Space.xs)
                    .frame(maxWidth: 220)
            }
        }
        .padding(.vertical, Space.lg)
        .frame(maxWidth: .infinity)
    }
}

// MARK: - 12. KeyField

/// Key behind a mask, with Show, Copy and Generate beside it.
struct KeyField: View {
    @Binding var text: String
    @Binding var revealed: Bool
    var onCopy: () -> Void
    var onGenerate: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Group {
                if revealed {
                    TextField("", text: $text)
                        .font(.system(.body, design: .monospaced))
                } else {
                    SecureField("", text: $text)
                }
            }
            .textFieldStyle(.roundedBorder)

            HStack(spacing: Space.sm) {
                Toggle(L.t("key.reveal"), isOn: $revealed).toggleStyle(.button)
                Button(L.t("key.copy"), action: onCopy).disabled(text.isEmpty)
                if let onGenerate {
                    Button(L.t("key.generate"), action: onGenerate)
                }
                Spacer()
            }
            .controlSize(.small)
        }
    }
}

// MARK: - 13. FingerprintLabel

/// `A1F2 · 9C40 · 77BE · D103`. The point is comparing two of these by eye
/// without ever revealing the key itself.
struct FingerprintLabel: View {
    let fingerprint: String

    @Environment(\.palette) private var palette

    var body: some View {
        Text(fingerprint)
            .font(.dsMono)
            .foregroundStyle(palette.text)
            .textSelection(.enabled)
            .accessibilityLabel(
                L.t("fingerprint.accessibility", fingerprint.replacingOccurrences(of: " · ", with: ", "))
            )
    }
}

// MARK: - 14. CheckRow

/// One line of a checklist: state icon + text + optional detail + action.
struct CheckRow: View {
    enum State: Sendable {
        case pending, running, ok, failed

        var tone: Tone {
            switch self {
            case .pending: return .neutral
            case .running: return .neutral
            case .ok: return .ok
            case .failed: return .bad
            }
        }
    }

    let title: String
    var detail: String = ""
    var state: State = .pending
    var action: FeatureAction?

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
            Group {
                switch state {
                case .running:
                    Spinner()
                case .pending:
                    Image(systemName: "circle.dotted")
                        .foregroundStyle(palette.textDim)
                case .ok:
                    Image(systemName: Tone.ok.symbolName)
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(palette.okFg)
                case .failed:
                    Image(systemName: Tone.bad.symbolName)
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(palette.badFg)
                }
            }
            .frame(width: 16, height: 16)

            Text(title)
                .font(.dsLabel)
                .foregroundStyle(palette.text)
            Spacer(minLength: Space.sm)
            if !detail.isEmpty {
                Text(detail)
                    .font(.dsMono)
                    .foregroundStyle(state == .failed ? palette.badText : palette.textDim)
                    .textSelection(.enabled)
                    .multilineTextAlignment(.trailing)
            }
            if let action {
                Button(action.title, action: action.perform)
                    .controlSize(.small)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

// MARK: - 15. StepDots

struct StepDots: View {
    let count: Int
    let current: Int

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    var body: some View {
        HStack(spacing: Space.sm) {
            ForEach(0..<count, id: \.self) { index in
                Capsule()
                    .fill(index == current ? palette.accent : palette.border)
                    .frame(width: index == current ? 22 : 8, height: 8)
            }
        }
        .animation(Motion.emphasis(Motion.long, reduced: motion.reduceMotion), value: current)
        .accessibilityLabel(L.t("steps.accessibility", L.integer(current + 1), L.integer(count)))
    }
}

// MARK: - 18. Spinner

/// §6.1: never shown before 400 ms of waiting — that is the caller's job, this
/// view only knows how to turn.
struct Spinner: View {
    @Environment(\.motionSettings) private var motion
    @State private var angle: Double = 0

    var body: some View {
        Image(systemName: "arrow.trianglehead.2.clockwise.rotate.90")
            .font(.system(size: 11))
            .rotationEffect(.degrees(angle))
            .onAppear {
                withAnimation(
                    .linear(duration: Motion.spinnerPeriod(reduced: motion.reduceMotion))
                        .repeatForever(autoreverses: false)
                ) {
                    angle = 360
                }
            }
            .accessibilityLabel(L.t("spinner.accessibility"))
    }
}

// MARK: - Section label

/// `section` role from §4.2: caps, semibold caption.
struct SectionLabel: View {
    let text: String

    @Environment(\.palette) private var palette

    var body: some View {
        Text(text)
            .font(.dsSection)
            .textCase(.uppercase)
            .kerning(0.5)
            .foregroundStyle(palette.textDim)
    }
}

// MARK: - Card

/// Plain surface with a border. The popover's own background is provided by
/// the system, so nothing here adds a shadow (§4.3).
struct Card<Content: View>: View {
    var padding: CGFloat = Space.md
    @ViewBuilder var content: Content

    @Environment(\.palette) private var palette

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: Radius.lg, style: .continuous)
                    .fill(palette.surface)
            )
            .overlay(
                RoundedRectangle(cornerRadius: Radius.lg, style: .continuous)
                    .strokeBorder(palette.border, lineWidth: 1)
            )
    }
}

// MARK: - Capped scroll

/// Grows with its content up to `maxHeight`, then scrolls.
///
/// A bare `ScrollView` cannot be used for this: it has no ideal height, so
/// inside a `MenuBarExtra` window — which proposes `nil` — it collapses to
/// nothing. Measuring the content and clamping is the only way to honour both
/// halves of §4.3's "height by content, maximum 520".
struct CappedScroll<Content: View>: View {
    let maxHeight: CGFloat
    @ViewBuilder var content: Content

    @State private var measured: CGFloat = 0

    var body: some View {
        ScrollView {
            content
                .background(
                    GeometryReader { geometry in
                        Color.clear.preference(key: ContentHeightKey.self, value: geometry.size.height)
                    }
                )
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(height: min(max(measured, 1), maxHeight))
        .onPreferenceChange(ContentHeightKey.self) { measured = $0 }
    }
}

private struct ContentHeightKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}
