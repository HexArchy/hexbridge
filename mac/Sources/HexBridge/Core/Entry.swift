import CoreGraphics
import Foundation

/// The single entry point for both faces of the binary.
///
/// UI and capture have to live in one process: TCC ties the microphone grant to
/// the bundle, so a separate UI process would raise a second permission prompt
/// and the user would have to answer it twice.
///
/// `argv` is inspected before anything AppKit-shaped is touched. Merely
/// referencing `NSApplication.shared` connects to the window server, which makes
/// `hexbridge keygen` in an ssh session hang instead of printing a key.
@main
enum HexBridgeMain {
    static func main() {
        // Line-buffer stdout so logs are useful when we run under launchd.
        setvbuf(stdout, nil, _IOLBF, 0)

        let args = Arguments(Array(CommandLine.arguments.dropFirst()))

        CLI.runEarlySubcommand(args)  // exits by itself when it matches

        let (config, path) = CLI.resolveConfig(args)
        CLI.runInitIfRequested(args, config: config, path: path)

        let runtime = BridgeRuntime(config: config, configPath: path)

        // The UI is the default rather than an opt-in flag. The bundle is
        // LSUIElement, so a menu bar item costs no Dock icon and nothing about
        // the launchd agent changes; making it opt-in would mean the installed
        // agent never shows a UI, which is the one place users actually need it.
        // `--headless` stays for ssh sessions and for debugging the pipeline
        // without a window server — the same check is applied automatically when
        // there is no GUI session to attach to.
        if args.has("headless") || !hasWindowServerSession {
            CLI.runHeadless(runtime, quiet: args.has("quiet"))
        }

        HexBridgeApp.launch(runtime: runtime)
    }

    /// nil outside a graphical login session (ssh, a `launchd` system daemon).
    /// NSApplication would abort there, so we degrade to the CLI behaviour.
    private static var hasWindowServerSession: Bool {
        CGSessionCopyCurrentDictionary() != nil
    }
}
