import SwiftUI

/// The five states every feature shares (DESIGN.md §7.0).
///
/// One automaton for the microphone and for the controller is a product
/// decision, not a coding convenience: the user learns the rules once.
///
/// ```
/// off ──(on)───▶ starting ──▶ waiting ──▶ live
///  ▲                │            │          │
///  └──(off)─────────┴────────────┴──────────┘
///                   │
///                   ▼
///                 error ──(fixed)──▶ starting
/// ```
enum FeatureState: String, Sendable, Equatable {
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
enum Tone: Sendable, Equatable {
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
    /// True when pressing this button would do exactly what the feature's own
    /// switch does. §6.1 says the switch is the only way to turn a feature on
    /// and off, so a button that duplicates it is dropped rather than drawn two
    /// centimetres away from it.
    ///
    /// A flag rather than a comparison against a list of button titles, which is
    /// what this was: a list of titles stops working the moment there are two
    /// sets of them.
    var togglesFeature = false
    let perform: () -> Void

    init(title: String, togglesFeature: Bool = false, perform: @escaping () -> Void) {
        self.title = title
        self.togglesFeature = togglesFeature
        self.perform = perform
    }
}

/// The word a feature's state is announced with in the popover.
///
/// Seven words, the same seven for every feature and the same seven on Windows;
/// docs/GLOSSARY.md is the authority and holds both languages. `muted` and
/// `notAvailable` are the two that do not follow from `FeatureState` on their
/// own — the first is a `live` substate, the second is a thing this platform
/// cannot do at all and must never be shown as a switch somebody could flip.
enum StateWord: String, Sendable, Equatable {
    case off, starting, waiting, working, muted, notWorking, unavailable
}

/// Everything the shell is allowed to know about a feature.
///
/// The shell renders a list of these and never names one. There is no
/// `if feature is MicrophoneFeature` anywhere, which is what makes adding a
/// third feature a matter of appending to `AppModel.features`.
struct FeatureStatus: Equatable {

    /// Two statuses are the same when they would draw the same.
    ///
    /// The shell refreshes up to twenty times a second while the popover is
    /// open, and Observation fires on the assignment rather than on the
    /// difference — so rewriting an identical status rebuilt the popover fifty
    /// times a second, which is what «the window opens late and then freezes»
    /// was. The action is compared by what it says and what it does, never by
    /// its `id`: that is a fresh UUID on every `derive()`, so comparing it would
    /// make every status different from every other one and this pointless.
    static func == (a: Self, b: Self) -> Bool {
        a.state == b.state
            && a.tone == b.tone
            && a.headline == b.headline
            && a.detail == b.detail
            && a.alert == b.alert
            && a.wordOverride == b.wordOverride
            && a.primaryAction?.title == b.primaryAction?.title
            && a.primaryAction?.togglesFeature == b.primaryAction?.togglesFeature
            && (a.primaryAction == nil) == (b.primaryAction == nil)
    }

    var state: FeatureState = .off
    var tone: Tone = .off
    /// The single sentence that answers "does it work or not" (§7.0).
    var headline: String = ""
    /// The second line: where, with what, why. May be empty.
    var detail: String = ""
    /// Shown instead of telemetry when the feature cannot work at all.
    var alert: String?
    /// Overrides the word the popover announces this state with. Set only where
    /// `state` and `tone` cannot say it — "muted" is the one case on macOS.
    var wordOverride: StateWord?
    var primaryAction: FeatureAction?

    /// The word this state is announced with in the popover.
    var word: StateWord {
        if let wordOverride { return wordOverride }
        switch state {
        case .off: return .off
        case .starting: return .starting
        case .waiting: return .waiting
        case .live: return .working
        case .error: return .notWorking
        }
    }

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
    /// From the shell's poll: once a second with the popover closed, twenty
    /// times a second while it is open. Cheap by contract — and, just as
    /// importantly, it must write only what has actually changed, or every call
    /// invalidates every view that reads it.
    func refresh()

    /// The block inside the menu bar popover. Collapses to a single row with a
    /// switch when the feature is off (§7.1).
    @ViewBuilder func popoverCard() -> AnyView
    /// One pane of the settings window.
    @ViewBuilder func settingsPane() -> AnyView
}
