import Foundation

/// The launchd agent that `scripts/install-agent.sh` writes.
///
/// The toggle deliberately only writes or removes the plist and never
/// bootstraps a running copy: launchd would start a *second* instance next to
/// the one the user is clicking in, and two instances mean two menu bar items
/// and two CoreAudio clients on the same microphone.
enum LaunchAgent {
    static let label = "ru.hexarch.hexbridge"

    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/HexBridge.log")
    }

    static var isEnabled: Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }

    /// True when launchd started us. `bootout` would then kill the process the
    /// user is talking to, so the UI says "current session keeps running".
    static var managedByLaunchd: Bool {
        getppid() == 1
    }

    static func enable() throws {
        let executable = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>\(label)</string>
            <key>ProgramArguments</key>
            <array>
                <string>\(executable)</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
            <key>StandardOutPath</key>
            <string>\(logURL.path)</string>
            <key>StandardErrorPath</key>
            <string>\(logURL.path)</string>
        </dict>
        </plist>
        """
        try FileManager.default.createDirectory(
            at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        try Data(plist.utf8).write(to: plistURL)
    }

    static func disable() throws {
        if FileManager.default.fileExists(atPath: plistURL.path) {
            try FileManager.default.removeItem(at: plistURL)
        }
        // Only tear down the live job when it is not us; otherwise this call
        // would terminate the process mid-click.
        if !managedByLaunchd {
            _ = try? runLaunchctl(["bootout", "gui/\(getuid())/\(label)"])
        }
    }

    @discardableResult
    private static func runLaunchctl(_ arguments: [String]) throws -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        return process.terminationStatus
    }
}
