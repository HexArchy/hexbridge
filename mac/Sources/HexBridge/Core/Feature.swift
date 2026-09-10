import SwiftUI

/// The five states every feature shares (DESIGN.md §7.0).
///
/// One automaton for the microphone and for the controller is a product
/// decision, not a coding convenience: the user learns the rules once.
///
/// ```
/// off ──(вкл)──▶ starting ──▶ waiting ──▶ live
///  ▲                │            │          │
///  └──(выкл)────────┴────────────┴──────────┘
///                   │
///                   ▼
///                 error ──(исправить)──▶ starting
/// ```
enum FeatureState: String, Sendable {
    /// Switched off by the user. Not a problem, and never shown as one.
    case off
    /// Up to 10 s, then it becomes an error.
    case starting
    /// Our side is ready, the other side is not.
    case waiting
    case live
    case error
}

/// The colour family a state is drawn in. Separate from `FeatureState` because
/// mute is a `live` substate painted `warn` (§4.1), and because a feature may
/// want `warn` while technically working (packet loss).
enum Tone: Sendable {
    case ok, warn, bad, off, neutral

    func foreground(_ palette: Palette) -> Color {
        switch self {
        case .ok: return palette.okFg
        case .warn: return palette.warnFg
        case .bad: return palette.badFg
        case .off: return palette.off
        case .neutral: return palette.textDim
        }
    }

    func text(_ palette: Palette) -> Color {
        switch self {
        case .ok: return palette.okText
        case .warn: return palette.warnText
        case .bad: return palette.badText
        case .off, .neutral: return palette.textDim
        }
    }

    func background(_ palette: Palette) -> Color {
        switch self {
        case .ok: return palette.okBg
        case .warn: return palette.warnBg
        case .bad: return palette.badBg
        case .off, .neutral: return palette.offBg
        }
    }

    func border(_ palette: Palette) -> Color {
        switch self {
        case .ok: return palette.okBorder
        case .warn: return palette.warnBorder
        case .bad: return palette.badBorder
        case .off, .neutral: return palette.border
        }
    }

    /// Shape, not just colour: §11.1 forbids colour as the only carrier of
    /// meaning, so each tone owns a symbol with a distinct silhouette.
    var symbolName: String {
        switch self {
        case .ok: return "checkmark.circle.fill"
        case .warn: return "exclamationmark.triangle.fill"
        case .bad: return "xmark.octagon.fill"
        case .off: return "circle.slash"
        case .neutral: return "ellipsis.circle"
        }
    }
}

/// The one action a state offers. §0 rule 2: one primary button per screen,
/// and §10.1 rule 4: an error always carries exactly one verb.
struct FeatureAction: Identifiable {
    let id = UUID()
    let title: String
    let perform: () -> Void
}

/// Everything the shell is allowed to know about a feature.
///
/// The shell renders a list of these and never names one. There is no
/// `if feature is MicrophoneFeature` anywhere, which is what makes adding a
/// third feature a matter of appending to `AppModel.features`.
struct FeatureStatus {
    var state: FeatureState = .off
    var tone: Tone = .off
    /// The single sentence that answers "работает или нет" (§7.0).
    var headline: String = ""
    /// The second line: где, чем, почему. May be empty.
    var detail: String = ""
    /// Shown instead of telemetry when the feature cannot work at all.
    var alert: String?
    var primaryAction: FeatureAction?

    /// §7.1: the summary in the header is the worst state among enabled
    /// features. This is the ordering that makes "worst" well defined.
    var severity: Int {
        switch state {
        case .error: return 4
        case .waiting: return 3
        case .starting: return 2
        case .live: return tone == .warn ? 1 : 0
        case .off: return -1
        }
    }
}

/// A feature: one switchable capability with its own state, settings pane and
/// popover card.
///
/// Views are returned type-erased on purpose. A protocol with associated view
/// types cannot be held in a heterogeneous array, and a heterogeneous array is
/// exactly what "the shell does not know the features by name" requires.
@MainActor
protocol Feature: AnyObject, Identifiable where ID == String {
    var id: String { get }
    var title: String { get }
    /// SF Symbol for the settings toolbar and the popover card.
    var symbolName: String { get }
    /// The one-line explanation shown under the switch in settings.
    var summary: String { get }

    var isEnabled: Bool { get set }
    var status: FeatureStatus { get }

    /// Called when the pipeline comes up or the switch is turned on. Must not
    /// block: features start their own work asynchronously.
    func start()
    func stop()
    /// Once a second, from the shell's poll. Cheap by contract.
    func refresh()

    /// The block inside the menu bar popover. Collapses to a single row with a
    /// switch when the feature is off (§7.1).
    @ViewBuilder func popoverCard() -> AnyView
    /// One pane of the settings window.
    @ViewBuilder func settingsPane() -> AnyView
}
