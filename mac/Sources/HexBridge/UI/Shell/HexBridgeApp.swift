import AppKit
import MenuBarExtraAccess
import SwiftUI

/// SwiftUI constructs the `App` value itself and AppKit constructs the delegate,
/// so neither can be handed the model through an initialiser. One holder, filled
/// in before `main()` runs, is cheaper than routing it through notifications.
enum AppBootstrap {
    nonisolated(unsafe) static var model: AppModel?
}

struct HexBridgeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model = AppBootstrap.model ?? AppModel(
        runtime: BridgeRuntime(config: Config(), configPath: Config.defaultPath)
    )
    @State private var popoverPresented = false
    @Environment(\.openSettings) private var openSettings
    @Environment(\.openWindow) private var openWindow

    var body: some Scene {
        MenuBarExtra {
            Themed(theme: model.theme) {
                PopoverView(model: model)
            }
            .environment(\.motionSettings, MotionSettings.shared)
        } label: {
            // §7.1: template image, never tinted. The menu bar in Tahoe is
            // fully transparent and the icon sits over arbitrary wallpaper, so
            // a green or red glyph would be unreadable — state is carried by
            // the shape of the symbol instead.
            //
            // The size is deliberately not hard-coded: Apple publishes no
            // recommended menu bar icon size, only that the bar is 24 pt tall.
            Image(systemName: model.menuBarSymbol)
                .symbolRenderingMode(.hierarchical)
        }
        // Must be the first modifier on `MenuBarExtra` — it is an extension on
        // that type, not on `Scene`. Verified working on macOS 26.6.2 with
        // MenuBarExtraAccess 1.3.1; see mac/docs/STAGE-MINUS-1.md.
        .menuBarExtraAccess(isPresented: $popoverPresented) { statusItem in
            // Nothing here may write back to the status item. This closure runs
            // from MenuBarExtraAccess's own KVO observer, so assigning
            // `isVisible` re-triggers the observer, which re-runs this closure —
            // a render loop that cost ~22% CPU permanently and made the popover
            // visibly laggy to open.
            captureActions()
        }
        .menuBarExtraStyle(.window)
        .onChange(of: popoverPresented) { _, open in
            AppBootstrap.model?.popoverOpen = open
        }

        Settings {
            Themed(theme: model.theme) {
                SettingsWindow(model: model)
            }
            .environment(\.motionSettings, MotionSettings.shared)
            .onAppear { captureActions() }
        }

        // The pairing wizard is a window of its own, not a popover step: a
        // popover closes on the first click outside it, and pairing involves
        // reading a code off another screen (§9.3).
        Window("Подключиться к ПК", id: WindowRouter.pairingWindowID) {
            Themed(theme: model.theme) {
                PairingWindow(model: model)
            }
            .environment(\.motionSettings, MotionSettings.shared)
            .onAppear { captureActions() }
        }
        .windowResizability(.contentSize)
        .defaultSize(width: Metrics.wizardWidth, height: Metrics.wizardHeight)
        .handlesExternalEvents(matching: [Pairing.scheme])
    }

    /// `openSettings` only works from inside a live render tree, so the action
    /// is captured rather than reconstructed at the call site (§2.2).
    private func captureActions() {
        WindowRouter.shared.openSettingsAction = { openSettings() }
        WindowRouter.shared.openWindowAction = { id in openWindow(id: id) }
        WindowRouter.shared.dismissPopover = { popoverPresented = false }
        WindowRouter.shared.installScriptingHook { popoverPresented = true }
    }

    /// Ровно один экземпляр: побеждает свежезапущенный.
    ///
    /// Без этого приложение открывается сколько угодно раз, и каждая копия
    /// держит свой захват звука и свой значок в строке меню. Копия, поднятая
    /// launchd, будет перезапущена им и снимет ручную — процесс сходится к
    /// одному экземпляру, а не зацикливается.
    private static func terminateOtherInstances() {
        guard let me = Bundle.main.bundleIdentifier else { return }
        let others = NSRunningApplication
            .runningApplications(withBundleIdentifier: me)
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        guard !others.isEmpty else { return }

        for app in others {
            app.terminate()
        }

        // Даём им уйти по-хорошему; звуковой захват освобождается не мгновенно.
        let deadline = Date().addingTimeInterval(2)
        while Date() < deadline, others.contains(where: { !$0.isTerminated }) {
            Thread.sleep(forTimeInterval: 0.05)
        }
        for app in others where !app.isTerminated {
            app.forceTerminate()
        }
    }

    /// Deliberately not `@main`: the real entry point inspects argv first so
    /// `keygen` and friends never reach AppKit. `App` still supplies `main()`,
    /// we just call it at the moment of our choosing.
    static func launch(runtime: BridgeRuntime) -> Never {
        terminateOtherInstances()
        AppBootstrap.model = AppModel(runtime: runtime)
        HexBridgeApp.main()
        // NSApplication.run() does not return; the compiler cannot know that.
        exit(0)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // The app bundle is LSUIElement, but the bare binary from
        // `.build/release` is not: without this it grabs a Dock icon and focus.
        NSApp.setActivationPolicy(.accessory)
        print("hexbridge: интерфейс запущен, pid \(getpid())")
        MainActor.assumeIsolated {
            AppBootstrap.model?.onLaunch()
        }
    }

    /// `hexbridge://pair?…` arriving from a browser, from Messages, or from
    /// `open`. This is the single-URI intake path from §9.
    func application(_ application: NSApplication, open urls: [URL]) {
        MainActor.assumeIsolated {
            guard let model = AppBootstrap.model else { return }
            for url in urls {
                if case .success(let payload) = Pairing.parse(url) {
                    model.apply(payload)
                    model.note("Связано с «\(payload.machineName)» — \(payload.target).")
                    WindowRouter.shared.open(.pairing)
                    return
                }
            }
            model.note("Ссылка не распознана как код связывания HexBridge.")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated {
            AppBootstrap.model?.saveNow()
            AppBootstrap.model?.runtime.stop()
        }
        print("hexbridge: интерфейс завершён")
    }
}
