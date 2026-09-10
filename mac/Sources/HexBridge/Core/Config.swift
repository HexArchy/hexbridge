import CryptoKit
import Foundation

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
    /// Gamepad passthrough. Optional rather than `Bool = false` on purpose:
    /// synthesised `Codable` treats a missing key as an error, not as the
    /// default, so a non-optional field would make every config file written
    /// before this feature fail to load — and a failed load silently falls back
    /// to an empty config, taking the target and the key with it.
    var gamepad: Bool?

    /// Microphone feature switch. Optional for the same reason as `gamepad`,
    /// and defaulting to **on**: every config written before the feature list
    /// existed described a machine that was streaming audio.
    var microphone: Bool?

    /// Appearance preference: `system` / `light` / `dark` (§7.4). Optional so
    /// an older config still decodes.
    var theme: String?

    /// Name of the paired Windows machine, learned during pairing (§9). Used
    /// in status texts — «Звук идёт на GAMING-PC» — and nowhere else.
    var peerName: String?

    /// True once the machines have been paired, so the first-run empty state
    /// stops being shown (§10.2).
    var paired: Bool?

    var streamsMicrophone: Bool { microphone ?? true }

    var appTheme: AppTheme { theme.flatMap(AppTheme.init(rawValue:)) ?? .system }

    /// The one question «настроено или нет» (§7.2, state «Не настроен»).
    var isConfigured: Bool {
        !target.isEmpty && (try? symmetricKey()) != nil
    }

    /// Off unless asked for: the receiver ignores the device channel until it
    /// learns to build a virtual controller, and sending 250 packets a second
    /// that nothing reads is not a good default.
    var forwardsGamepad: Bool { gamepad ?? false }

    static let defaultPath = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/HexBridge/config.json")

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
            return "не задан target (host:port приёмника или релея)"
        case .badKey:
            return "psk должен быть 32 байта в base64 — сгенерируйте через `hexbridge keygen`"
        }
    }
}
