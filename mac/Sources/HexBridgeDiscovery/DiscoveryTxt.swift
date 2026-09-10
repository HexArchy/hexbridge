import Foundation

/// One host seen on the local network.
public struct DiscoveredHost: Identifiable, Equatable, Sendable {
    /// The Bonjour instance name. Identity for the list, never for trust.
    public var id: String

    /// Display name out of TXT, falling back to the instance name. Proves
    /// nothing — see ``DiscoveryTag``.
    public var name: String

    /// Filled in once the browse result has been resolved; empty until then.
    public var address: String

    public var port: UInt16

    /// The label, or nil when the host published none.
    public var tag: String?

    public var version: Int

    public init(
        id: String,
        name: String,
        address: String = "",
        port: UInt16,
        tag: String? = nil,
        version: Int = 1
    ) {
        self.id = id
        self.name = name
        self.address = address
        self.port = port
        self.tag = tag
        self.version = version
    }

    /// What goes into `config.target`. Empty until the address is known.
    public var target: String { address.isEmpty ? "" : "\(address):\(port)" }
}

/// The four TXT keys and nothing else (PROTOCOL.md, «Что ещё лежит в TXT»).
///
/// * `v` — protocol version, integer;
/// * `port` — the data port, integer;
/// * `name` — display name, UTF-8, shown only in the list;
/// * `tag` — the label, see ``DiscoveryTag``.
///
/// A record with no `tag` is legal and means «этот хост ещё не спарен ни с кем».
/// It belongs in the list an unpaired Mac reads, and can never be connected to
/// automatically.
public enum DiscoveryTxt {
    public static let versionKey = "v"
    public static let portKey = "port"
    public static let nameKey = "name"
    public static let tagKey = "tag"

    /// Reads an already-split TXT record — the shape `NWTXTRecord` hands over.
    ///
    /// Returns nil rather than throwing, and skipping one row is the whole cost
    /// of a neighbour's malformed advertisement. This data comes from whatever
    /// else is on the local network.
    public static func parse(
        _ values: [String: String],
        id: String,
        fallbackName: String = ""
    ) -> DiscoveredHost? {
        guard let rawVersion = values[versionKey], let version = strictInt(rawVersion) else { return nil }
        guard let rawPort = values[portKey], let port = strictInt(rawPort), (1...65535).contains(port) else {
            return nil
        }

        // A tag of the wrong shape is dropped rather than carried: keeping it
        // would only give some later comparison a chance to accept it.
        let raw = values[tagKey]
        let tag = DiscoveryTag.isWellFormed(raw) ? raw : nil

        let name = values[nameKey].map(trim) ?? ""
        return DiscoveredHost(
            id: id,
            name: name.isEmpty ? fallbackName : name,
            port: UInt16(port),
            tag: tag,
            version: version
        )
    }

    /// Reads the raw `key=value` strings a TXT record actually carries.
    public static func parse(
        entries: [String],
        id: String,
        fallbackName: String = ""
    ) -> DiscoveredHost? {
        var values: [String: String] = [:]
        for entry in entries {
            guard let separator = entry.firstIndex(of: "="), separator != entry.startIndex else {
                // A TXT string with no `=` is a legal boolean attribute in
                // DNS-SD and carries nothing we need; a leading `=` is a key
                // with no name.
                continue
            }
            let key = String(entry[entry.startIndex..<separator])
            // First value wins, per RFC 6763 §6.4: a repeated key must not let
            // a later entry overwrite an earlier one, or a chosen tag could be
            // appended to somebody else's advertisement.
            if values[key] == nil {
                values[key] = String(entry[entry.index(after: separator)...])
            }
        }
        return parse(values, id: id, fallbackName: fallbackName)
    }

    /// The entries this Mac would publish, were it the host. Only the tests use
    /// it — but they use it to check the Windows record round-trips.
    public static func render(version: Int, port: UInt16, name: String, tag: String?) -> [String] {
        var entries = ["\(versionKey)=\(version)", "\(portKey)=\(port)", "\(nameKey)=\(trim(name))"]
        if DiscoveryTag.isWellFormed(tag), let tag { entries.append("\(tagKey)=\(tag)") }
        return entries
    }

    /// `Int(_:)` accepts a leading `+` and `-`; a port is neither.
    private static func strictInt(_ text: String) -> Int? {
        guard !text.isEmpty, text.allSatisfy(\.isASCII), text.allSatisfy({ $0.isNumber }) else { return nil }
        return Int(text)
    }

    /// A TXT string holds at most 255 bytes including its `name=` prefix.
    static func trim(_ value: String) -> String {
        var name = value.trimmingCharacters(in: .whitespacesAndNewlines)
        while name.utf8.count > 255 - 5 { name.removeLast() }
        return name
    }
}
