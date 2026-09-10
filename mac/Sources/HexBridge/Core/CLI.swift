import CryptoKit
import Foundation

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

/// Key material for `keygen` and for the "сгенерировать" button in settings —
/// one definition so both can never drift apart.
enum KeyFactory {
    static func newBase64Key() -> String {
        SymmetricKey(size: .bits256).withUnsafeBytes { Data($0).base64EncodedString() }
    }
}

enum CLI {
    static let usage = """
    hexbridge — пробрасывает микрофон и USB-устройства с Mac на игровой ПК с Windows.

    Использование:
      hexbridge [флаги]              запустить передачу (меню-бар + UI)
      hexbridge --headless           запустить передачу без интерфейса
      hexbridge list-devices         показать устройства ввода
      hexbridge keygen               сгенерировать общий ключ (PSK)
      hexbridge probe                проверить, что микрофон реально захватывается
      hexbridge devices list         показать подключённые HID-устройства
      hexbridge devices probe        полная диагностика: чтение, запись, дескрипторы
      hexbridge devices monitor      живое состояние устройства, Ctrl-C для выхода
      hexbridge devices haptics      проиграть PCM в актуаторы и проверить их гироскопом
                                     (gamepad — синоним devices, сохранён для старых скриптов)
      hexbridge init --target HOST:PORT --psk KEY
                                     записать конфиг и выйти

    Флаги:
      --config PATH   путь к конфигу (по умолчанию ~/Library/Application Support/HexBridge/config.json)
      --target H:P    адрес приёмника на Windows или релея на VPS
      --psk BASE64    общий ключ, 32 байта в base64
      --device SEL    UID устройства ввода или часть его имени
      --bitrate N     битрейт Opus, бит/с (по умолчанию 32000)
      --gain F        программное усиление, 1.0 — без изменений
      --loss N        ожидаемые потери в процентах для FEC (по умолчанию 10)
      --name S        имя узла в логах приёмника
      --gamepad       включить проброс устройств (по умолчанию выключено)
      --forward LIST  что пробрасывать: VID:PID через запятую, до четырёх.
                      По умолчанию не пробрасывается ничего — устройство
                      остаётся подключённым и к Mac, поэтому клавиатура
                      печатала бы сразу на двух машинах.
      --hid SEL       для devices probe/monitor: VID:PID или часть имени
      --headless      не поднимать интерфейс, только передача и лог в stdout
      --quiet         не печатать строку статистики раз в 5 секунд

    Пока процесс работает, `kill -USR1 <pid>` переключает мьют.
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
                print("устройств ввода не найдено")
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
            fail("укажите действие: \(args.subcommand ?? "devices") list | probe | monitor | haptics")
        case let other?:
            fail("неизвестное действие: \(args.subcommand ?? "devices") \(other)")
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
            print("конфиг записан: \(path.path)")
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

        print("hexbridge: \(runtime.deviceName) → \(hostPort.host):\(hostPort.port), \(runtime.config.bitrate / 1000) кбит/с")

        var lastSent: UInt64 = 0
        var lastBytes: UInt64 = 0
        let statsTimer = DispatchSource.makeTimerSource(queue: .main)

        if !quiet {
            statsTimer.schedule(deadline: .now() + 5, repeating: .seconds(5))
            statsTimer.setEventHandler {
                let snap = runtime.snapshot()
                let peak = runtime.drainPeak()

                let packets = snap.sent - lastSent
                let kbits = Double(snap.bytes - lastBytes) * 8 / 5000
                lastSent = snap.sent
                lastBytes = snap.bytes

                var line = String(
                    format: "отправлено %3d пак/с  %5.1f кбит/с  пик %5.1f dBFS",
                    Int(packets / 5),
                    kbits,
                    peak > 0 ? 20 * log10(Double(peak)) : -99
                )
                if runtime.muted {
                    line += "  [MUTED]"
                }
                if let rtt = snap.rtt, let pong = snap.pong, Date().timeIntervalSince(pong) < 5 {
                    line += String(format: "  rtt %.0f мс  принято %llu, потеряно %llu", rtt, snap.received, snap.lost)
                } else {
                    line += "  хост не отвечает"
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
            print("\nостановлено")
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
            print(runtime.muted ? "микрофон выключен" : "микрофон включён")
            onToggle?()
        }
        source.resume()
        return source
    }
}
