import CryptoKit
import Foundation
import HexBridgeDiscovery

/// Flags and the optional leading subcommand, parsed once at startup.
struct Arguments {
    let subcommand: String?
    /// The second bare word, for two-level subcommands: `gamepad probe` parses
    /// as `subcommand == "gamepad"`, `object == "probe"`.
    let object: String?
    private let flags: [String]
    private let raw: [String]

    init(_ raw: [String]) {
        self.raw = raw
        var rest = raw
        var words: [String] = []
        // Only leading bare words are subcommands; everything after the first
        // dash belongs to a flag, including its value.
        while let first = rest.first, !first.hasPrefix("-"), words.count < 2 {
            words.append(first)
            rest.removeFirst()
        }
        subcommand = words.first
        object = words.count > 1 ? words[1] : nil
        flags = rest
    }

    func value(_ name: String) -> String? {
        guard let index = flags.firstIndex(of: "--\(name)"), index + 1 < flags.count else { return nil }
        return flags[index + 1]
    }

    func has(_ name: String) -> Bool {
        flags.contains("--\(name)")
    }

    /// Matches an argument exactly, dashes included. For the short forms that
    /// are not `--name value` pairs.
    func contains(literal: String) -> Bool {
        raw.contains(literal)
    }
}

/// Key material for `keygen` and for the Generate button in settings —
/// one definition so both can never drift apart.
enum KeyFactory {
    static func newBase64Key() -> String {
        SymmetricKey(size: .bits256).withUnsafeBytes { Data($0).base64EncodedString() }
    }
}

enum CLI {
    /// The command line is a developer surface and stays in English, whatever
    /// the interface is set to: it is read next to the code, quoted into issues
    /// and piped through `grep`, and none of that survives being translated.
    static let usage = """
    hexbridge — forwards the microphone and USB devices from a Mac to a Windows gaming PC.

    Usage:
      hexbridge [flags]              start streaming (menu bar + interface)
      hexbridge --headless           start streaming with no interface
      hexbridge list-devices         list audio input devices
      hexbridge keygen               generate a pairing key (PSK)
      hexbridge probe                check that the microphone is really captured
      hexbridge discover             list the HexBridge hosts visible on this network
      hexbridge devices list         list connected HID devices
      hexbridge devices probe        full diagnostics: reading, writing, descriptors
      hexbridge devices monitor      live device state, Ctrl-C to leave
      hexbridge devices haptics      play PCM into the actuators and verify it with the gyro
                                     (gamepad is a synonym for devices, kept for old scripts)
      hexbridge init --target HOST:PORT --psk KEY
                                     write the config and exit

    Flags:
      --config PATH   config path (default ~/Library/Application Support/HexBridge/config.json)
      --target H:P    address of the gaming PC, or of a relay on a VPS
      --psk BASE64    pairing key, 32 bytes in base64
      --device SEL    input device UID, or part of its name
      --bitrate N     Opus bitrate in bits per second (default 32000)
      --gain F        software gain, 1.0 leaves the signal alone
      --loss N        expected packet loss in percent, for FEC (default 10)
      --name S        name of this node in the PC's log
      --forward LIST  what to forward: VID:PID separated by commas, up to four.
                      Nothing is forwarded by default — a device stays connected
                      to the Mac as well, so a keyboard would type on both
                      machines at once.
      --gamepad       turn device forwarding on (off by default)
      --hid SEL       for devices probe/monitor: VID:PID or part of a name
      --seconds N     how long `discover` searches for (default 4)
      --headless      no interface, just the stream and a log on stdout
      --quiet         do not print the statistics line every five seconds

    While the process runs, `kill -USR1 <pid>` toggles mute.
    """

    static func fail(_ message: String) -> Never {
        FileHandle.standardError.write(Data("hexbridge: \(message)\n".utf8))
        exit(1)
    }

    /// Subcommands that print something and exit. They must run before any
    /// AppKit type is touched, so `hexbridge keygen` over ssh stays a one-liner.
    static func runEarlySubcommand(_ args: Arguments) {
        if args.subcommand == "help" || args.contains(literal: "--help") || args.contains(literal: "-h") {
            print(usage)
            exit(0)
        }

        switch args.subcommand {
        case "keygen":
            print(KeyFactory.newBase64Key())
            exit(0)

        case "list-devices":
            let devices = AudioDevices.inputDevices()
            let defaultID = AudioDevices.defaultInputDevice()?.id
            if devices.isEmpty {
                print("no input devices found")
            }
            for device in devices {
                let marker = device.id == defaultID ? " *" : "  "
                print("\(marker) \(device.name)  [\(device.inputChannels) ch]  uid=\(device.uid)")
            }
            exit(0)

        case "probe":
            do {
                try MicProbe.run(deviceSelector: args.value("device")) { print($0) }
            } catch {
                fail("\(error)")
            }
            exit(0)

        case "discover":
            runDiscover(args)
            exit(0)

        case "gamepad", "devices":
            runDevices(args)
            exit(0)

        default:
            return
        }
    }

    /// `devices …` is the only two-word subcommand. It stays here with the other
    /// early exits because it must not drag AppKit in: probing over ssh has to
    /// work. `gamepad` is kept as a synonym: it is in scripts and in muscle
    /// memory, and breaking it would buy nothing.
    private static func runDevices(_ args: Arguments) {
        let selector = args.value("hid")
        switch args.object {
        case "probe":
            DeviceProbe.run(selector: selector) { print($0) }
        case "list":
            DeviceProbe.list { print($0) }
        case "monitor":
            DeviceProbe.monitor(selector: selector) { print($0) }
        case "haptics":
            DeviceProbe.haptics(selector: selector) { print($0) }
        case nil:
            fail("say what to do: \(args.subcommand ?? "devices") list | probe | monitor | haptics")
        case let other?:
            fail("unknown action: \(args.subcommand ?? "devices") \(other)")
        }
    }

    /// `hexbridge discover` — what is on the network and which of it is ours.
    ///
    /// This is the only way to see the autodiscovery decision without a window,
    /// and it is deliberately blunt about strangers: a host whose tag is not
    /// ours is listed and marked as somebody else's, because "it is not visible
    /// at all" is indistinguishable from "the search is broken" when you are the
    /// one debugging it.
    private static func runDiscover(_ args: Arguments) {
        let (config, _) = resolveConfig(args)
        let ownTag = DiscoveryTag.tag(forBase64Key: config.psk)
        let seconds = Double(args.value("seconds") ?? "") ?? 4

        print("tag of this Mac: \(ownTag ?? "none — no key is set, autoconnect is off")")
        print("searching for \(Int(seconds)) s…")

        let done = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var hosts: [DiscoveredHost] = []
        Task {
            hosts = await DiscoveryBrowser.scan(seconds: seconds)
            done.signal()
        }
        done.wait()

        if hosts.isEmpty {
            print("no HexBridge is visible on this network")
        }
        for host in hosts {
            let mark: String
            if DiscoveryTag.same(ownTag, host.tag) {
                mark = "  ← ours"
            } else if host.tag == nil {
                mark = "  (no tag, not paired with anyone)"
            } else if ownTag == nil {
                // With no key of our own there is nobody to be a stranger to.
                mark = "  (paired with someone)"
            } else {
                mark = "  (someone else's)"
            }
            let where_ = host.target.isEmpty ? "address not resolved yet" : host.target
            print("  \(host.name)  \(where_)  v\(host.version)  tag=\(host.tag ?? "—")\(mark)")
        }

        let choice = DiscoveryMatch.choose(ownTag: ownTag, hosts: hosts)
        print("")
        print("verdict: \(choice.reason.sentence)")
        if let target = DiscoveryMatch.retarget(current: config.target, choice: choice) {
            print("the config address would move to \(target)"
                + " (currently \(config.target.isEmpty ? "unset" : config.target))")
        } else if choice.shouldConnect {
            print("the config address is already right")
        }
    }

    /// Loads the config file and overlays the command line on top of it.
    static func resolveConfig(_ args: Arguments) -> (config: Config, path: URL) {
        let path = args.value("config").map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
            ?? Config.defaultPath

        var config = (try? Config.load(path)) ?? Config()
        if let value = args.value("target") { config.target = value }
        if let value = args.value("psk") { config.psk = value }
        if let value = args.value("device") { config.inputDevice = value }
        if let value = args.value("bitrate"), let n = Int(value) { config.bitrate = n }
        if let value = args.value("gain"), let f = Double(value) { config.gain = f }
        if let value = args.value("loss"), let n = Int(value) { config.expectedLossPercent = n }
        if let value = args.value("name") { config.name = value }
        if args.has("gamepad") { config.gamepad = true }
        if args.has("no-gamepad") { config.gamepad = false }
        if let list = args.value("forward") {
            // An explicit empty list is a legitimate way to say "forward
            // nothing", and it must be distinguishable from "never chosen".
            config.forwardedDevices = list
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
                .compactMap { DeviceIdentity($0)?.description }
        }
        return (config, path)
    }

    /// `init` writes the merged config and exits; it never starts audio.
    static func runInitIfRequested(_ args: Arguments, config: Config, path: URL) {
        guard args.subcommand == "init" else { return }
        do {
            _ = try config.symmetricKey()
            _ = try config.endpointParts()
            try config.save(to: path)
            print("config written: \(path.path)")
            exit(0)
        } catch {
            fail("\(error)")
        }
    }

    // MARK: - Headless streaming

    /// The pre-UI behaviour, kept intact for launchd setups and ssh sessions:
    /// hard failure on a bad config, a stats line every five seconds, SIGUSR1
    /// for mute. Never returns.
    /// Dispatch sources are only live while something holds them; a local would
    /// be released at the end of the enclosing scope and silently cancelled.
    private nonisolated(unsafe) static var keepAlive: [Any] = []

    static func runHeadless(_ runtime: BridgeRuntime, quiet: Bool) -> Never {
        let hostPort: (host: String, port: UInt16)
        do {
            _ = try runtime.config.symmetricKey()
            hostPort = try runtime.config.endpointParts()
        } catch {
            fail("\(error)\n\n\(usage)")
        }

        do {
            try runtime.start()
        } catch {
            fail("\(error)")
        }

        print("hexbridge: \(runtime.deviceName) → \(hostPort.host):\(hostPort.port),"
            + " \(runtime.config.bitrate / 1000) kbit/s")

        var lastSent: UInt64 = 0
        var lastBytes: UInt64 = 0
        let statsTimer = DispatchSource.makeTimerSource(queue: .main)

        if !quiet {
            statsTimer.schedule(deadline: .now() + 5, repeating: .seconds(5))
            statsTimer.setEventHandler {
                let snap = runtime.snapshot()
                let peak = runtime.drainPeak()

                let packets = snap.sent.growth(since: lastSent)
                let kbits = Double(snap.bytes.growth(since: lastBytes)) * 8 / 5000
                lastSent = snap.sent
                lastBytes = snap.bytes

                var line = String(
                    format: "sent %3d pkt/s  %5.1f kbit/s  peak %5.1f dBFS",
                    Int(packets / 5),
                    kbits,
                    peak > 0 ? 20 * log10(Double(peak)) : -99
                )
                if runtime.muted {
                    line += "  [MUTED]"
                }
                if let rtt = snap.rtt, let pong = snap.pong, Date().timeIntervalSince(pong) < 5 {
                    line += String(format: "  rtt %.0f ms  received %llu, lost %llu", rtt, snap.received, snap.lost)
                } else {
                    line += "  the host is not answering"
                }
                if let error = snap.error {
                    line += "  (\(error))"
                }
                print(line)
            }
            statsTimer.resume()
        }

        let muteSignal = installMuteSignal(runtime)
        let intSignal = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
        signal(SIGINT, SIG_IGN)
        intSignal.setEventHandler {
            runtime.stop()
            print("\nstopped")
            exit(0)
        }
        intSignal.resume()

        keepAlive.append(contentsOf: [statsTimer, muteSignal, intSignal])
        dispatchMain()
    }

    /// `kill -USR1 <pid>` toggles mute. Kept in both modes: existing scripts and
    /// Stream Deck macros rely on it, and the UI just mirrors the flag.
    static func installMuteSignal(_ runtime: BridgeRuntime, onToggle: (() -> Void)? = nil) -> DispatchSourceSignal {
        signal(SIGUSR1, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGUSR1, queue: .main)
        source.setEventHandler {
            runtime.muted.toggle()
            print(runtime.muted ? "microphone muted" : "microphone unmuted")
            onToggle?()
        }
        source.resume()
        return source
    }
}
