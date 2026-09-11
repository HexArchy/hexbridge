import Foundation

/// Whether the macOS application firewall is holding incoming connections back
/// from this app.
///
/// <para>
/// It is worth asking because of how the firewall refuses: it completes the TCP
/// handshake itself and then simply does not hand the connection to an
/// application it has not been told to allow. Nothing is logged, nothing is
/// refused, and the other machine sees a healthy socket. The file then goes the
/// slow way — which is the right outcome, and still leaves somebody wondering
/// why a gigabyte takes six minutes here and four seconds at a friend's.
/// </para>
///
/// <para>
/// Two questions answer it, and neither needs a privilege: is the firewall on at
/// all, and is this bundle in its list. An app that has never been answered for
/// is in no list — `--getappblocked` says «permitted» for it, which is why that
/// question is not the one asked here.
/// </para>
public enum Firewall {

    private static let tool = "/usr/libexec/ApplicationFirewall/socketfilterfw"

    /// True only when both things are true: the firewall is on, and this bundle
    /// is not among the applications it has been told about. Anything that
    /// cannot be determined answers false — a warning nobody can act on is worse
    /// than no warning, and the fast path may well be working.
    public static func withholdsIncoming(from bundle: URL = Bundle.main.bundleURL) -> Bool {
        guard FileManager.default.isExecutableFile(atPath: tool) else { return false }
        return withholds(
            state: run([tool, "--getglobalstate"]),
            apps: run([tool, "--listapps"]),
            bundlePath: bundle.path
        )
    }

    /// The decision itself, away from the tool that answers the two questions, so
    /// that it can be tested on the strings the tool actually prints.
    ///
    /// Path match rather than name match: two builds of the same app in two
    /// folders are two different applications as far as this list is concerned,
    /// and that is exactly what an update makes.
    public static func withholds(state: String?, apps: String?, bundlePath: String) -> Bool {
        guard let state, state.contains("State = 1") else { return false }
        guard let apps else { return false }
        return !apps.contains(bundlePath)
    }

    /// Where somebody is sent to fix it. The pane moved in Sequoia; the old URL
    /// still opens the right place, and opening the wrong pane is a smaller
    /// failure than opening none.
    public static let settings = URL(string: "x-apple.systempreferences:com.apple.preference.security?Firewall")!

    private static func run(_ argv: [String]) -> String? {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: argv[0])
        task.arguments = Array(argv.dropFirst())
        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = FileHandle.nullDevice
        do {
            try task.run()
        } catch {
            return nil
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        return String(data: data, encoding: .utf8)
    }
}
