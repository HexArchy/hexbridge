import AppKit
import Observation
import SwiftUI

/// Motion spec from DESIGN.md §5: four curves, six durations, and one rule for
/// what happens when the user asked for less movement.
enum Motion {
    // §5.3. Nothing longer than `slow`, nothing shorter than `micro` except
    // `instant`, which is the absence of an animation rather than a duration.
    static let micro = 0.12
    static let short = 0.18
    static let base = 0.24
    static let long = 0.32
    static let slow = 0.48

    /// §5.2 `ease.standard` — appearing, disappearing, opacity.
    static func standard(_ duration: Double) -> Animation { .smooth(duration: duration) }

    /// §5.2 `ease.emphasis` — a selection moving, a section changing.
    static func emphasis(_ duration: Double) -> Animation { .snappy(duration: duration) }

    /// §5.2 `ease.spring` — something coming into existence, the success tick.
    /// `.bouncy` is deliberately never used: 0.3 of bounce is wrong in a utility.
    static func spring(_ duration: Double) -> Animation { .spring(duration: duration, bounce: 0.15) }

    /// §5.6: reduced motion replaces movement with a 120 ms crossfade rather
    /// than removing the transition, so state changes are still noticeable.
    static func standard(_ duration: Double, reduced: Bool) -> Animation {
        reduced ? .smooth(duration: micro) : standard(duration)
    }

    static func emphasis(_ duration: Double, reduced: Bool) -> Animation {
        reduced ? .smooth(duration: micro) : emphasis(duration)
    }

    /// Transitions 6, 7, 14, 18 have no animation at all under reduced motion:
    /// the value simply changes.
    static func spring(_ duration: Double, reduced: Bool) -> Animation? {
        reduced ? nil : spring(duration)
    }

    /// §5.4 #9 — the only looping animation in the app, and it is switched off
    /// entirely under reduced motion.
    static let waitingPulsePeriod = 1.4
    /// §5.4 #15 — a spinner is an indicator, not decoration, so it survives;
    /// it just turns slower.
    static func spinnerPeriod(reduced: Bool) -> Double { reduced ? 1.4 : 0.9 }
}

/// Live accessibility display settings.
///
/// 🔴 The notification only arrives on `NSWorkspace.shared.notificationCenter`;
/// registering on `NotificationCenter.default` silently never fires. Apple
/// calls this out in an Important block, and it is the single most common way
/// to get "reduce motion" wrong.
@Observable
@MainActor
final class MotionSettings {
    static let shared = MotionSettings()

    private(set) var reduceMotion: Bool
    private(set) var reduceTransparency: Bool

    /// Held for the lifetime of the process. There is deliberately no `deinit`
    /// unregistration: the observer is only ever created for the shared
    /// instance, and a `deinit` cannot touch main-actor state anyway.
    private var token: NSObjectProtocol?

    init() {
        let workspace = NSWorkspace.shared
        reduceMotion = workspace.accessibilityDisplayShouldReduceMotion
        reduceTransparency = workspace.accessibilityDisplayShouldReduceTransparency

        token = workspace.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            // The notification carries no userInfo; the current values have to
            // be read back from the workspace.
            MainActor.assumeIsolated {
                guard let self else { return }
                self.reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
                self.reduceTransparency = NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency
            }
        }
    }

}

private struct MotionSettingsKey: @preconcurrency EnvironmentKey {
    @MainActor static var defaultValue: MotionSettings { .shared }
}

extension EnvironmentValues {
    var motionSettings: MotionSettings {
        get { self[MotionSettingsKey.self] }
        set { self[MotionSettingsKey.self] = newValue }
    }
}
