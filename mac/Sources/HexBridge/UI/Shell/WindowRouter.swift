import AppKit
import SwiftUI

/// Opens the app's two windows from anywhere, and works around the one SwiftUI
/// problem this app cannot avoid.
///
/// 🔴 Measured on macOS 26.6.2 (25G83) with the scratch harness in
/// `mac/docs/STAGE-MINUS-1.md`: `SettingsLink` and `openSettings()` **do**
/// open the Settings scene from a `MenuBarExtra`, but the app is not
/// activated — `NSApp.isActive` stays false and the window is not key, so it
/// appears behind whatever the user was looking at. Apple has never answered
/// forum thread 731628 about this.
///
/// The workaround is the one from DESIGN.md §2.2: hold `.regular` activation
/// policy while a window of ours is open, activate explicitly, and drop back
/// to `.accessory` when the last one closes. Dropping back immediately is what
/// most implementations get wrong — it re-hides the window that was just asked
/// for.
@MainActor
final class WindowRouter {
    static let shared = WindowRouter()

    enum Destination {
        case settings
        case pairing
    }

    static let pairingWindowID = "pairing"

    /// Filled in from a live SwiftUI view: `openSettings` refuses to work
    /// unless it is invoked from inside an existing render tree, so the action
    /// has to be captured rather than reconstructed.
    var openSettingsAction: (() -> Void)?
    var openWindowAction: ((String) -> Void)?
    /// Closes the menu bar popover. Supplied by `MenuBarExtraAccess`.
    var dismissPopover: (() -> Void)?

    private var observers: [NSObjectProtocol] = []
    private var watching = false

    /// Scripting surface: `hexbridge` has always been drivable from a Stream
    /// Deck macro (`kill -USR1` toggles mute), and this is the same idea for
    /// the windows. Posting the notification with an object of `popover`,
    /// `settings` or `pairing` opens that surface.
    ///
    ///     osascript -l JavaScript -e 'ObjC.import("Foundation"); \
    ///       $.NSDistributedNotificationCenter.defaultCenter \
    ///        .postNotificationNameObjectUserInfoDeliverImmediately(
    ///           "ru.hexarch.hexbridge.open", "settings", $(), true)'
    static let openNotification = Notification.Name("ru.hexarch.hexbridge.open")

    private var scriptingInstalled = false

    func installScriptingHook(showPopover: @escaping () -> Void) {
        guard !scriptingInstalled else { return }
        scriptingInstalled = true
        DistributedNotificationCenter.default().addObserver(
            forName: Self.openNotification, object: nil, queue: .main
        ) { note in
            MainActor.assumeIsolated {
                switch note.object as? String {
                case "settings": WindowRouter.shared.open(.settings)
                case "pairing": WindowRouter.shared.open(.pairing)
                default: showPopover()
                }
            }
        }
    }

    func open(_ destination: Destination) {
        dismissPopover?()
        beginWatchingWindows()

        // The policy has to change *before* the window is asked for, or the
        // first activation lands while the process is still an accessory.
        NSApp.setActivationPolicy(.regular)

        switch destination {
        case .settings:
            openSettingsAction?()
        case .pairing:
            openWindowAction?(Self.pairingWindowID)
        }

        // One turn of the run loop: the window does not exist yet at this point.
        DispatchQueue.main.async { [weak self] in
            NSApp.activate(ignoringOtherApps: true)
            self?.frontmostAppWindow()?.makeKeyAndOrderFront(nil)
        }
    }

    private func frontmostAppWindow() -> NSWindow? {
        NSApp.windows.first {
            $0.isVisible
                && !$0.className.contains("StatusBar")
                && !$0.className.contains("MenuBarExtra")
                && !$0.className.contains("Popover")
        }
    }

    /// Returns to `.accessory` only once nothing of ours is on screen. An
    /// LSUIElement app that stays `.regular` grows a Dock icon it should not
    /// have; one that flips back too early loses the window.
    private func beginWatchingWindows() {
        guard !watching else { return }
        watching = true
        let token = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: nil, queue: .main
        ) { [weak self] _ in
            // `willClose` fires before the window leaves the list.
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    if self.frontmostAppWindow() == nil {
                        NSApp.setActivationPolicy(.accessory)
                    }
                }
            }
        }
        observers.append(token)
    }
}
