import Foundation
import Testing

@testable import HexBridgeFiles

/// Asking the macOS firewall whether it is holding incoming connections back.
///
/// <para>
/// The question exists because of how the firewall refuses: it completes the TCP
/// handshake itself and then never hands the connection to an application it has
/// not been told about. Nothing is logged and nothing is refused, so a file from
/// the PC quietly takes the slow channel — and this is the only way the app can
/// say why.
/// </para>
@Suite("Фаервол")
struct FirewallTests {

    /// What `--listapps` prints, down to the leading count and the indented
    /// second line, because that shape is what the match has to survive.
    private static let apps = """
    Total number of apps = 3\u{0020}
    1 : /usr/bin/nc\u{0020}
                 (Allow incoming connections)
    2 : /Applications/Google Chrome.app\u{0020}
                 (Allow incoming connections)
    3 : /Applications/HexBridge.app\u{0020}
                 (Allow incoming connections)
    """

    private static let on = "Firewall is enabled. (State = 1)"
    private static let off = "Firewall is disabled. (State = 0)"

    @Test("Фаервол включён, а приложения в списке нет — предупреждаем")
    func aFirewallThatHasNeverBeenToldAboutUsWithholds() {
        #expect(Firewall.withholds(state: Self.on, apps: Self.apps, bundlePath: "/Applications/Меню.app"))
    }

    @Test("Приложение в списке — молчим")
    func anApplicationInTheListIsNotWithheld() {
        #expect(!Firewall.withholds(state: Self.on, apps: Self.apps, bundlePath: "/Applications/HexBridge.app"))
    }

    @Test("Выключенный фаервол никого не держит")
    func aFirewallThatIsOffWithholdsNobody() {
        #expect(!Firewall.withholds(state: Self.off, apps: Self.apps, bundlePath: "/Applications/Меню.app"))
    }

    /// A warning nobody can act on is worse than no warning, and the fast path
    /// may well be working: everything unanswerable answers «no».
    @Test("Чего не удалось выяснить — не повод пугать")
    func anythingUnknownIsNotAWarning() {
        #expect(!Firewall.withholds(state: nil, apps: Self.apps, bundlePath: "/Applications/HexBridge.app"))
        #expect(!Firewall.withholds(state: Self.on, apps: nil, bundlePath: "/Applications/HexBridge.app"))
    }

    /// Two builds of one app in two folders are two applications as far as the
    /// list is concerned — which is exactly what an update leaves behind.
    @Test("Совпадение по пути, а не по имени")
    func theMatchIsOnThePathAndNotTheName() {
        #expect(Firewall.withholds(state: Self.on, apps: Self.apps, bundlePath: "/Users/кто-то/Build/HexBridge.app"))
    }
}
