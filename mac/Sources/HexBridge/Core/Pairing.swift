import CryptoKit
import Foundation
import HexBridgePairing
import HexBridgeText
import Network

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
    case exchangePlaintext

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
        case .exchangePlaintext:
            return L.t("pairing.error.plaintext")
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
enum PairingExchange {
    /// How long to wait for the whole exchange. A PC that accepts the connection and then
    /// says nothing must not leave the wizard spinning.
    private static let timeout: TimeInterval = 8

    /// Ceiling on the answer. The real one is a few hundred bytes; this is here so a
    /// stranger on the port cannot stream until memory runs out.
    private static let maximumAnswer = 64 * 1024

    static func fetch(host: String, port: UInt16, code: String) async -> Result<Pairing.Payload, PairingError> {
        let request = """
        GET /pair?code=\(Pairing.normalizedCode(code))&enc=1 HTTP/1.1\r
        Host: \(host)\r
        User-Agent: HexBridge/1.0 (macOS)\r
        Connection: close\r
        \r

        """

        let raw: Data
        do {
            raw = try await send(request, host: host, port: port)
        } catch {
            return .failure(.exchangeUnreachable(error.localizedDescription))
        }

        guard let answer = Answer(raw) else { return .failure(.exchangeBadAnswer) }
        // 403 and 404 are the two the PC uses for «that is not the code I am showing».
        if answer.status == 403 || answer.status == 404 { return .failure(.exchangeRefused) }
        guard answer.status == 200 else { return .failure(.exchangeBadAnswer) }

        // The key must not arrive in the clear, however reachable the PC was: on a network
        // worth worrying about, whoever is on the path would have it too.
        guard let opened = PairingSeal.open(answer.body, code: code) else {
            return .failure(answer.body.contains("hexbridge://") ? .exchangePlaintext : .exchangeBadAnswer)
        }

        switch Pairing.parse(text: opened) {
        case .success(let payload):
            return .success(payload)
        case .failure:
            return .failure(.exchangeBadAnswer)
        }
    }

    // MARK: - Transport

    /// One request, one answer, over a plain TCP socket.
    ///
    /// Deliberately not `URLSession`. The peer is the PC on the other side of the desk
    /// speaking four lines of HTTP, not a website, and it has no certificate — so App
    /// Transport Security refuses the request and explains itself with «the resource could
    /// not be loaded because the App Transport Security policy requires the use of a
    /// secure connection», which is both unactionable and, once the answer is sealed under
    /// the code, beside the point. The alternative was to switch ATS off for the whole
    /// application; a socket for this one request is far narrower, and it is the same
    /// `NWConnection` the rest of the protocol already runs on.
    private static func send(_ text: String, host: String, port: UInt16) async throws -> Data {
        guard let endpointPort = NWEndpoint.Port(rawValue: port) else { throw ExchangeFailure.unusablePort }

        let connection = NWConnection(host: NWEndpoint.Host(host), port: endpointPort, using: .tcp)
        let queue = DispatchQueue(label: "ru.hexarch.hexbridge.pairing-exchange")

        return try await withCheckedThrowingContinuation { continuation in
            let once = Once()
            let received = Box()

            func finish(_ result: Result<Data, Error>) {
                guard once.claim() else { return }
                connection.cancel()
                continuation.resume(with: result)
            }

            queue.asyncAfter(deadline: .now() + timeout) { finish(.failure(ExchangeFailure.timedOut)) }

            func readMore() {
                connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { chunk, _, isComplete, error in
                    if let chunk, !chunk.isEmpty { received.append(chunk) }
                    if let error { finish(.failure(error)); return }
                    if isComplete { finish(.success(received.data)); return }
                    if received.count > maximumAnswer { finish(.failure(ExchangeFailure.tooLong)); return }
                    readMore()
                }
            }

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    connection.send(content: Data(text.utf8), completion: .contentProcessed { error in
                        if let error { finish(.failure(error)); return }
                        readMore()
                    })
                case .failed(let error):
                    finish(.failure(error))
                case .cancelled:
                    finish(.failure(ExchangeFailure.cancelled))
                default:
                    break
                }
            }

            connection.start(queue: queue)
        }
    }

    /// Status line and body of an HTTP/1.1 answer. Everything in between is ignored: the
    /// PC sends four headers and we care about none of them.
    struct Answer {
        let status: Int
        let body: String

        init?(_ raw: Data) {
            guard let text = String(data: raw, encoding: .utf8),
                  let split = text.range(of: "\r\n\r\n"),
                  let line = text[..<split.lowerBound].split(separator: "\r\n", omittingEmptySubsequences: false).first
            else { return nil }

            let fields = line.split(separator: " ", omittingEmptySubsequences: true)
            guard fields.count >= 2, let code = Int(fields[1]) else { return nil }

            status = code
            body = String(text[split.upperBound...])
        }
    }

    enum ExchangeFailure: Error, LocalizedError {
        case timedOut
        case cancelled
        case tooLong
        case unusablePort

        var errorDescription: String? {
            switch self {
            case .timedOut: return L.t("pairing.exchange.timedOut")
            case .cancelled: return L.t("pairing.exchange.cancelled")
            case .tooLong: return L.t("pairing.exchange.tooLong")
            case .unusablePort: return L.t("pairing.exchange.badPort")
            }
        }
    }

    /// Guarantees the continuation is resumed exactly once, whichever of the timeout, the
    /// socket and the reader gets there first.
    private final class Once: @unchecked Sendable {
        private let lock = NSLock()
        private var taken = false

        func claim() -> Bool {
            lock.lock()
            defer { lock.unlock() }
            if taken { return false }
            taken = true
            return true
        }
    }

    /// The answer accumulates across receive callbacks on one queue.
    private final class Box: @unchecked Sendable {
        private(set) var data = Data()
        var count: Int { data.count }
        func append(_ chunk: Data) { data.append(chunk) }
    }
}
