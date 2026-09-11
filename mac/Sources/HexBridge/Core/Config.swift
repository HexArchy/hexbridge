import CryptoKit
import Foundation
import HexBridgeText

struct Config: Codable {
    /// `host:port` of the Windows receiver, or of the VPS relay.
    var target: String = ""
    /// 32-byte pre-shared key, base64. Must match the receiver.
    var psk: String = ""
    /// Input device UID or a substring of its name; nil means system default.
    var inputDevice: String?
    var bitrate: Int = 32000
    var gain: Double = 1.0
    var expectedLossPercent: Int = 10
    var name: String = Host.current().localizedName ?? "mac"
    /// Device passthrough master switch. Optional rather than `Bool = false` on
    /// purpose: synthesised `Codable` treats a missing key as an error, not as
    /// the default, so a non-optional field would make every config file written
    /// before this feature fail to load — and a failed load silently falls back
    /// to an empty config, taking the target and the key with it.
    ///
    /// The JSON key keeps its old name: the on-disk shape is deployed.
    var gamepad: Bool?

    /// Which devices the user chose to forward, as `VID:PID` or
    /// `VID:PID:SERIAL`. Up to four are honoured at a time.
    ///
    /// Empty by default and never populated automatically. Devices are opened
    /// without seizing them, so the Mac goes on receiving everything it
    /// forwards — a keyboard nobody asked for would type on two machines at
    /// once. Nil means "never chosen", which is the only state the DualSense
    /// migration below is allowed to fill in.
    var forwardedDevices: [String]?

    /// Shared clipboard. Optional for the same reason as `gamepad`, and
    /// defaulting to **off**: the clipboard holds passwords, and a feature that
    /// shipped enabled would send them to the other machine before anybody read
    /// a settings page.
    var clipboard: Bool?

    /// Microphone feature switch. Optional for the same reason as `gamepad`,
    /// and defaulting to **on**: every config written before the feature list
    /// existed described a machine that was streaming audio.
    var microphone: Bool?

    /// Appearance preference: `system` / `light` / `dark` (§7.4). Optional so
    /// an older config still decodes.
    var theme: String?

    /// Interface language: `system` / `en` / `ru`. Optional for the same reason
    /// as everything else here — a config written before the app was bilingual
    /// must still decode — and `system` when absent, which is what the first
    /// launch on any machine gets.
    var language: String?

    /// Name of the paired Windows machine, learned during pairing (§9). Used
    /// in status texts — «Звук идёт на GAMING-PC» — and nowhere else.
    var peerName: String?

    /// True once the machines have been paired, so the first-run empty state
    /// stops being shown (§10.2).
    var paired: Bool?

    /// Look for a new version once a day (docs/UPDATES.md). Optional for the
    /// same reason as `gamepad`: a config written before this existed must not
    /// fail to decode, and a failed decode silently loses the key and the
    /// address with it.
    ///
    /// On by default. Nothing is ever downloaded or installed without the user
    /// saying so — this switch governs only whether HexBridge looks.
    var autoUpdate: Bool?

    var streamsMicrophone: Bool { microphone ?? true }

    /// On unless asked otherwise. Off means no request leaves the machine.
    var checksForUpdates: Bool { autoUpdate ?? true }

    /// Off unless asked for. See `clipboard`.
    var sharesClipboard: Bool { clipboard ?? false }

    var appTheme: AppTheme { theme.flatMap(AppTheme.init(rawValue:)) ?? .system }

    var appLanguage: AppLanguage { language.flatMap(AppLanguage.init(rawValue:)) ?? .system }

    /// The one question "set up or not" (§7.2, the «не настроен» state).
    var isConfigured: Bool {
        !target.isEmpty && (try? symmetricKey()) != nil
    }

    /// Off unless asked for: sending 250 packets a second per device that
    /// nothing reads is not a good default.
    var forwardsDevices: Bool { gamepad ?? false }

    /// The devices chosen for forwarding, decoded.
    ///
    /// A config written before the picker existed carries `gamepad: true` and no
    /// list, and it described a machine that forwarded its DualSense. Reading
    /// that as "forward nothing" would silently break a working setup, so it is
    /// read as "forward the DualSense" until the user touches the list.
    var selectedDevices: Set<DeviceIdentity> {
        guard let forwardedDevices else {
            guard gamepad == true else { return [] }
            return [DeviceIdentity(
                vendorID: DeviceProfile.dualSenseVendorID,
                productID: DeviceProfile.dualSenseProductIDs[0]
            )]
        }
        return Set(forwardedDevices.compactMap(DeviceIdentity.init))
    }

    static let defaultPath = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/HexBridge/config.json")

    /// Decoded field by field, with every absence falling back to the default.
    ///
    /// The synthesised decoder does not do this: it treats a missing key as an error
    /// even when the property has a default, and one missing key fails the whole
    /// object. That is how a config with no `expectedLossPercent` in it — a field
    /// nobody would think to write by hand — threw away the address and the key along
    /// with it, and the app then complained that the key was not 32 bytes, which sent
    /// the reader to look at the one line that was fine.
    ///
    /// Two fields were already `Optional` for exactly this reason, one of them with a
    /// comment explaining the trap. This closes it for all of them, which also means a
    /// config written by an older version keeps working when a field is added.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let fresh = Config()

        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) throws -> T {
            try values.decodeIfPresent(T.self, forKey: key) ?? fallback
        }

        target = try value(.target, fresh.target)
        psk = try value(.psk, fresh.psk)
        bitrate = try value(.bitrate, fresh.bitrate)
        gain = try value(.gain, fresh.gain)
        expectedLossPercent = try value(.expectedLossPercent, fresh.expectedLossPercent)
        name = try value(.name, fresh.name)

        inputDevice = try values.decodeIfPresent(String.self, forKey: .inputDevice)
        gamepad = try values.decodeIfPresent(Bool.self, forKey: .gamepad)
        forwardedDevices = try values.decodeIfPresent([String].self, forKey: .forwardedDevices)
        clipboard = try values.decodeIfPresent(Bool.self, forKey: .clipboard)
        microphone = try values.decodeIfPresent(Bool.self, forKey: .microphone)
        theme = try values.decodeIfPresent(String.self, forKey: .theme)
        language = try values.decodeIfPresent(String.self, forKey: .language)
        peerName = try values.decodeIfPresent(String.self, forKey: .peerName)
        paired = try values.decodeIfPresent(Bool.self, forKey: .paired)
        autoUpdate = try values.decodeIfPresent(Bool.self, forKey: .autoUpdate)
    }

    init() {}

    static func load(_ url: URL) throws -> Config {
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode(Config.self, from: data)
    }

    func save(to url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: url)
    }

    func symmetricKey() throws -> SymmetricKey {
        guard let raw = Data(base64Encoded: psk), raw.count == 32 else {
            throw ConfigError.badKey
        }
        return SymmetricKey(data: raw)
    }

    /// Splits `target` into host and port, defaulting to the standard port.
    func endpointParts() throws -> (host: String, port: UInt16) {
        guard !target.isEmpty else { throw ConfigError.missingTarget }

        // IPv6 literals are written as [::1]:47702.
        if target.hasPrefix("["), let close = target.firstIndex(of: "]") {
            let host = String(target[target.index(after: target.startIndex)..<close])
            let rest = target[target.index(after: close)...]
            if rest.hasPrefix(":"), let port = UInt16(rest.dropFirst()) {
                return (host, port)
            }
            return (host, 47702)
        }

        let parts = target.split(separator: ":")
        if parts.count == 2, let port = UInt16(parts[1]) {
            return (String(parts[0]), port)
        }
        return (target, 47702)
    }
}

enum ConfigError: Error, CustomStringConvertible {
    case missingTarget
    case badKey

    var description: String {
        switch self {
        case .missingTarget:
            return L.t("config.error.noAddress")
        case .badKey:
            return L.t("config.error.badKey")
        }
    }
}
