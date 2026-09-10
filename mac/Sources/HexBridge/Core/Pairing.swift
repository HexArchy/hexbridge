import CryptoKit
import Foundation
import HexBridgeText

/// Machine pairing (DESIGN.md §9).
///
/// The key is generated on Windows, not here. Windows is the side that listens:
/// it owns the address and the port, and those are the things that have to
/// travel. The Mac only ever *receives* a pairing payload.
enum Pairing {
    /// Custom URL scheme registered by the app bundle.
    static let scheme = "hexbridge"
    static let host = "pair"
    static let version = 1

    /// Where the Windows side answers a short-code exchange.
    ///
    /// ⚠️ Convention, not yet a shared contract: the receiver listens for audio
    /// on UDP `port` and is expected to serve the pairing handshake on TCP
    /// `port + 1` while the wizard is open. The Windows side of this is being
    /// written separately; until both halves are tested together this path is
    /// unverified.
    static func exchangePort(forDataPort port: UInt16) -> UInt16 { port &+ 1 }

    /// The payload behind one QR code / one link.
    struct Payload: Equatable {
        var host: String
        var port: UInt16
        /// 32 bytes, standard base64 (the URI carries base64url, converted on
        /// the way in and out).
        var psk: String
        var machineName: String

        var target: String { "\(host):\(port)" }
    }

    // MARK: - URI

    /// `hexbridge://pair?v=1&h=192.168.1.10&p=47702&k=<base64url>&n=<PC name>`
    static func parse(_ url: URL) -> Result<Payload, PairingError> {
        guard url.scheme?.lowercased() == scheme else { return .failure(.notAPairingLink) }
        guard url.host?.lowercased() == host else { return .failure(.notAPairingLink) }
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return .failure(.malformed)
        }

        var values: [String: String] = [:]
        for item in components.queryItems ?? [] {
            values[item.name] = item.value
        }

        if let raw = values["v"], Int(raw) != version {
            return .failure(.wrongVersion(raw))
        }
        guard let host = values["h"], !host.isEmpty else { return .failure(.malformed) }
        guard let portText = values["p"], let port = UInt16(portText) else { return .failure(.malformed) }
        guard let keyText = values["k"], let psk = base64(fromURLSafe: keyText) else {
            return .failure(.badKey)
        }
        guard let raw = Data(base64Encoded: psk), raw.count == 32 else { return .failure(.badKey) }

        return .success(Payload(
            host: host,
            port: port,
            psk: psk,
            machineName: values["n"] ?? L.t("pair.pcFallbackName")
        ))
    }

    /// Accepts a full `hexbridge://` link or a bare pasted URI with whitespace.
    static func parse(text: String) -> Result<Payload, PairingError> {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .failure(.malformed) }
        guard let url = URL(string: trimmed) else { return .failure(.malformed) }
        return parse(url)
    }

    static func link(for payload: Payload) -> String {
        var components = URLComponents()
        components.scheme = scheme
        components.host = host
        components.queryItems = [
            URLQueryItem(name: "v", value: String(version)),
            URLQueryItem(name: "h", value: payload.host),
            URLQueryItem(name: "p", value: String(payload.port)),
            URLQueryItem(name: "k", value: urlSafe(base64: payload.psk)),
            URLQueryItem(name: "n", value: payload.machineName),
        ]
        return components.string ?? ""
    }

    private static func urlSafe(base64: String) -> String {
        base64
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private static func base64(fromURLSafe text: String) -> String? {
        var value = text
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        // base64url drops padding; base64 decoding in Foundation demands it.
        while value.count % 4 != 0 { value += "=" }
        return value
    }

    // MARK: - Fingerprint

    /// `A1F2 · 9C40 · 77BE · D103` — eight bytes of a domain-separated hash of
    /// the key, shown so two machines can be compared by eye without either of
    /// them revealing the key itself (§10.3).
    ///
    /// The domain string is part of the contract with the Windows side: change
    /// it and the two fingerprints stop matching even for identical keys.
    static func fingerprint(ofBase64Key key: String) -> String? {
        guard let raw = Data(base64Encoded: key), raw.count == 32 else { return nil }
        var hasher = SHA256()
        hasher.update(data: Data("hexbridge-fingerprint-v1".utf8))
        hasher.update(data: raw)
        let digest = Array(hasher.finalize()).prefix(8)
        let hex = digest.map { String(format: "%02X", $0) }.joined()
        return stride(from: 0, to: hex.count, by: 4)
            .map { offset -> String in
                let start = hex.index(hex.startIndex, offsetBy: offset)
                let end = hex.index(start, offsetBy: 4)
                return String(hex[start..<end])
            }
            .joined(separator: " · ")
    }

    // MARK: - Short code

    /// Alphabet without `I`, `O`, `0` and `1`: those are the four characters
    /// people mistype when reading a code off a screen (§9.3).
    static let codeAlphabet = Array("ABCDEFGHJKLMNPQRSTUVWXYZ23456789")
    static let codeLength = 12

    /// Uppercases, drops everything outside the alphabet, regroups as
    /// `ABCD-EFGH-JKLM`. Safe to call on every keystroke.
    static func formatCode(_ input: String) -> String {
        let kept = input.uppercased().filter { codeAlphabet.contains($0) }.prefix(codeLength)
        var out = ""
        for (index, character) in kept.enumerated() {
            if index > 0, index % 4 == 0 { out.append("-") }
            out.append(character)
        }
        return out
    }

    static func isCompleteCode(_ input: String) -> Bool {
        input.filter { codeAlphabet.contains($0) }.count == codeLength
    }

    static func normalizedCode(_ input: String) -> String {
        String(input.uppercased().filter { codeAlphabet.contains($0) })
    }
}

enum PairingError: Error, CustomStringConvertible, Equatable {
    case notAPairingLink
    case malformed
    case wrongVersion(String)
    case badKey
    case exchangeUnreachable(String)
    case exchangeRefused
    case exchangeBadAnswer

    var description: String {
        switch self {
        case .notAPairingLink:
            return L.t("pairing.error.notALink")
        case .malformed:
            return L.t("pairing.error.malformed")
        case .wrongVersion(let value):
            return L.t("pairing.error.version", value, L.integer(Pairing.version))
        case .badKey:
            return L.t("pairing.error.badKey")
        case .exchangeUnreachable(let reason):
            return L.t("pairing.error.unreachable", reason)
        case .exchangeRefused:
            return L.t("pairing.error.refused")
        case .exchangeBadAnswer:
            return L.t("pairing.error.badAnswer")
        }
    }
}

// MARK: - Short-code exchange client

/// Asks the Windows side for the full pairing URI in exchange for the short
/// code the user typed.
///
/// The code alone cannot carry a 32-byte key, so it is a one-time ticket: the
/// receiver holds a temporary listener for three minutes and hands the URI to
/// whoever presents the right code (§9.1).
///
/// ⚠️ Untested end to end — the Windows half of this handshake is written by a
/// different agent and did not exist when this was implemented. Failures are
/// surfaced verbatim rather than swallowed, so a mismatch will be obvious.
enum PairingExchange {
    static func fetch(host: String, port: UInt16, code: String) async -> Result<Pairing.Payload, PairingError> {
        var components = URLComponents()
        components.scheme = "http"
        components.host = host
        components.port = Int(port)
        components.path = "/pair"
        components.queryItems = [URLQueryItem(name: "code", value: Pairing.normalizedCode(code))]

        guard let url = components.url else { return .failure(.malformed) }

        var request = URLRequest(url: url)
        request.timeoutInterval = 5
        request.setValue("HexBridge/1.0 (macOS)", forHTTPHeaderField: "User-Agent")

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            if let http = response as? HTTPURLResponse, http.statusCode == 403 || http.statusCode == 404 {
                return .failure(.exchangeRefused)
            }
            guard let text = String(data: data, encoding: .utf8) else { return .failure(.exchangeBadAnswer) }
            switch Pairing.parse(text: text) {
            case .success(let payload):
                return .success(payload)
            case .failure:
                return .failure(.exchangeBadAnswer)
            }
        } catch {
            return .failure(.exchangeUnreachable(error.localizedDescription))
        }
    }
}
