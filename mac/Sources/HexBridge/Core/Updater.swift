import Foundation
import Observation
import Sparkle

/// Updates for the Mac side (docs/UPDATES.md).
///
/// Sparkle 2.9.6 — there is no alternative outside the App Store and has not
/// been one for a decade. Everything below is a thin cover over it, because
/// Sparkle already answers the three things this has to get right:
///
/// * **once a day** — `updateCheckInterval` is 86 400 s, the same number the
///   Windows side keeps in `UpdatePolicy.CheckInterval`;
/// * **only with consent** — `automaticallyDownloadsUpdates` stays off, so
///   Sparkle shows what it found and waits for a button;
/// * **switchable off** — `automaticallyChecksForUpdates` is bound to the
///   config, and off means no request is made at all.
///
/// ## The signature, and why this works on an ad-hoc signed build
///
/// HexBridge is signed ad-hoc, not with a Developer ID: there is no paid
/// certificate behind it. Sparkle does not need one. It verifies an **EdDSA
/// (ed25519) signature** over the downloaded archive against `SUPublicEDKey`
/// baked into `Info.plist`, and that check is independent of the code
/// signature — it is what makes an unsigned or ad-hoc build updatable at all.
/// The private half never leaves the release machine and CI; see
/// docs/UPDATES.md.
///
/// ## The consequence nobody expects
///
/// An ad-hoc signature has no stable identity: the code directory hash changes
/// on every build, so after an update macOS considers this a **different
/// application** and TCC asks for microphone access again. Nothing is broken
/// and nothing was reset — but a user who is not told this will read the prompt
/// as the update having damaged something. So it is said out loud, before the
/// update rather than after: see `Wording.updateWillReaskForMicrophone`.
@Observable
@MainActor
final class Updater {
    /// Where the appcast lives. `releases/latest/download/…` always redirects
    /// to the newest release's asset, so the feed URL never has to change and
    /// a release only has to publish its own `appcast.xml`.
    static let feedURL = "https://github.com/HexArchy/hexbridge/releases/latest/download/appcast.xml"

    /// The same day the Windows side uses. Two platforms, one promise.
    static let checkInterval: TimeInterval = 86400

    private(set) var lastError: String?

    /// Nil when this build cannot update itself — a `swift run` binary outside
    /// a bundle, or a bundle built without a feed URL. Sparkle refuses to start
    /// without those, loudly, and starting it anyway would put an error dialog
    /// in front of a user who has done nothing wrong.
    private let controller: SPUStandardUpdaterController?

    /// True when the update machinery is actually live.
    var isAvailable: Bool { controller != nil }

    /// The version on screen in the settings.
    var currentVersion: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        return short ?? "не из бандла"
    }

    init(automaticallyChecks: Bool) {
        if Self.feedIsConfigured {
            // `startingUpdater: true` schedules the first check itself, honouring
            // the interval and the last-check date Sparkle keeps in defaults —
            // which is what makes «раз в сутки» survive a restart.
            controller = SPUStandardUpdaterController(
                startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil
            )
        } else {
            controller = nil
        }

        configure(automaticallyChecks: automaticallyChecks)
    }

    /// Applies the setting. Called from the settings switch, and once at init.
    func configure(automaticallyChecks: Bool) {
        guard let updater = controller?.updater else { return }
        updater.automaticallyChecksForUpdates = automaticallyChecks
        // Never download behind the user's back. The whole point of the switch
        // above is defeated if something is already on disk when they look.
        updater.automaticallyDownloadsUpdates = false
        updater.updateCheckInterval = Self.checkInterval
    }

    /// «Проверить сейчас» — the manual check, with Sparkle's own UI.
    func checkNow() {
        guard let controller else {
            lastError = "Эта сборка запущена не из HexBridge.app, обновляться ей нечем."
            return
        }
        lastError = nil
        controller.updater.checkForUpdates()
    }

    /// When Sparkle last looked, for the line under the switch.
    var lastCheck: Date? { controller?.updater.lastUpdateCheckDate }

    /// True only when the bundle carries everything Sparkle insists on. Both
    /// keys are written by `mac/scripts/build-app.sh`.
    private static var feedIsConfigured: Bool {
        guard Bundle.main.bundleIdentifier != nil else { return false }
        let feed = Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String
        let key = Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String
        return !(feed ?? "").isEmpty && !(key ?? "").isEmpty
    }
}
