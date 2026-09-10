import CryptoKit
import Foundation

/// The label a host publishes so this Mac can tell «мой ПК» from «чей-то ПК»
/// (PROTOCOL.md, «Автопоиск хоста»).
///
/// ```
/// tag = base64url( SHA256("hexbridge-discovery-v1" || PSK)[0..16] )
/// ```
///
/// Neither the name nor the key travels. A name proves nothing — the owner of
/// the other HexBridge chooses it, and «GAMING-PC» is a default — so a Mac that
/// trusted names would walk into a stranger's session in any dormitory. The tag
/// is a one-way hash of 32 random bytes: it cannot be reversed, and it reveals
/// exactly one fact, «эти двое уже связаны», which anyone watching the link can
/// already see from the traffic.
///
/// The domain string is part of the contract with Windows —
/// `win/src/HexBridge.Core/Discovery.cs` hashes the same bytes in the same
/// order. Change it on one side only and every pair quietly stops finding
/// itself, with no error anywhere to say why.
public enum DiscoveryTag {
    public static let domain = "hexbridge-discovery-v1"

    /// How much of the digest travels. Sixteen bytes is 128 bits of collision room.
    public static let bytes = 16

    /// Sixteen bytes as unpadded base64url — always exactly this many characters.
    public static let length = 22

    /// The tag for a raw 32-byte key.
    public static func tag(forKey key: Data) -> String {
        var hasher = SHA256()
        hasher.update(data: Data(domain.utf8))
        hasher.update(data: key)
        return base64url(Data(hasher.finalize().prefix(bytes)))
    }

    /// The tag for a key the way `config.json` stores it, or nil when there is
    /// no usable key.
    ///
    /// Nil is the honest answer for an unpaired Mac, and it is what stops such a
    /// Mac from matching anything at all: see ``DiscoveryMatch/choose(ownTag:hosts:)``.
    public static func tag(forBase64Key key: String?) -> String? {
        guard let key, let raw = Data(base64Encoded: key.trimmingCharacters(in: .whitespacesAndNewlines)),
              raw.count == 32
        else { return nil }
        return tag(forKey: raw)
    }

    /// Compares two tags.
    ///
    /// Case-sensitive, because base64url is: folding case here would make two
    /// different keys look like one. A nil or malformed tag matches nothing,
    /// including another nil — «мы оба не знаем свою метку» is not evidence of
    /// being paired, and treating it as such is exactly how an unconfigured Mac
    /// would end up trusting an unconfigured stranger.
    public static func same(_ mine: String?, _ theirs: String?) -> Bool {
        guard isWellFormed(mine), isWellFormed(theirs) else { return false }
        return mine == theirs
    }

    /// The shape a tag must have before it is worth comparing at all.
    public static func isWellFormed(_ tag: String?) -> Bool {
        guard let tag, tag.count == length else { return false }
        return tag.allSatisfy { character in
            character.isASCII && (character.isLetter || character.isNumber || character == "-" || character == "_")
        }
    }

    private static func base64url(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
