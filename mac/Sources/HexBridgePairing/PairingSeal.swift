import CommonCrypto
import CryptoKit
import Foundation

/// Unwraps the pairing URI the PC hands over in exchange for the short code.
///
/// The exchange used to answer in plain text over plain HTTP. Across a home network that
/// was a considered trade; across the internet it hands the 32-byte key to anyone on the
/// path, and macOS refused the request outright rather than let that happen. The two
/// screens already share a secret — the twelve characters being copied across — so that is
/// what the answer is encrypted with, and no exception to anybody's transport policy is
/// needed.
///
/// **Why the derivation is slow.** The code is twelve characters from a 32-symbol
/// alphabet: exactly 60 bits. Against somebody who captured the blob and can then guess
/// offline, 60 bits behind a fast KDF is worth about a weekend of rented GPUs.
/// PBKDF2-SHA256 at ``iterations`` multiplies that by roughly 2²⁰, which puts it well past
/// the value of one microphone link. It costs each side a single derivation during a step
/// the person is already waiting on.
///
/// This lives in its own target rather than in the app for the same reason
/// `HexBridgeDiscovery` does: it is a security decision, so it has to be testable, and the
/// app target cannot be linked into a test bundle.
///
/// The other half is `PairingSeal` in `win/src/HexBridge.Core/PairingSeal.cs`. Both are
/// pinned to the same vector in `docs/PROTOCOL.md`.
public enum PairingSeal {
    /// Leading byte of the blob, and the only associated data the tag covers.
    public static let version: UInt8 = 1

    public static let saltBytes = 16
    public static let nonceBytes = 12
    public static let tagBytes = 16

    /// PBKDF2-SHA256 rounds. See the note above about why this is not HKDF.
    public static let iterations = 600_000

    /// The 32 symbols a code is written in. I, O, 0 and 1 are absent: this is a string
    /// somebody reads off one screen and types into another.
    public static let alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

    /// Upper case, alphabet only. Matches `ShortCode.Normalise` on the PC exactly — the
    /// key is derived from the result, so a difference of one character is a silent
    /// failure to decrypt rather than a mismatch anybody could read.
    public static func normalise(_ code: String) -> String {
        String(code.uppercased().filter { alphabet.contains($0) })
    }

    /// The key both sides arrive at from the same code and salt.
    public static func deriveKey(code: String, salt: Data) -> SymmetricKey {
        let password = Array(normalise(code).utf8)
        var derived = [UInt8](repeating: 0, count: 32)

        let status = salt.withUnsafeBytes { saltBytes -> Int32 in
            CCKeyDerivationPBKDF(
                CCPBKDFAlgorithm(kCCPBKDF2),
                // The password is ASCII by construction: it came through `normalise`.
                password.withUnsafeBytes { $0.baseAddress!.assumingMemoryBound(to: CChar.self) },
                password.count,
                saltBytes.baseAddress!.assumingMemoryBound(to: UInt8.self),
                salt.count,
                CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                UInt32(iterations),
                &derived,
                derived.count
            )
        }

        // The only documented failure is a bad parameter, and every parameter here is a
        // constant or a length. A zero key would be worse than a crash.
        precondition(status == kCCSuccess, "PBKDF2 refused fixed parameters")
        return SymmetricKey(data: Data(derived))
    }

    /// Seals a payload. The PC does this in production; the Mac needs it to check itself
    /// against the shared vector.
    public static func seal(_ plaintext: String, code: String, salt: Data, nonce: Data) -> String? {
        seal(plaintext, key: deriveKey(code: code, salt: salt), salt: salt, nonce: nonce)
    }

    public static func seal(_ plaintext: String, key: SymmetricKey, salt: Data, nonce: Data) -> String? {
        guard salt.count == saltBytes, nonce.count == nonceBytes,
              let boxNonce = try? AES.GCM.Nonce(data: nonce),
              let box = try? AES.GCM.seal(
                  Data(plaintext.utf8), using: key, nonce: boxNonce, authenticating: Data([version])
              )
        else { return nil }

        var blob = Data([version])
        blob.append(salt)
        blob.append(nonce)
        blob.append(box.ciphertext)
        blob.append(box.tag)
        return blob.base64EncodedString()
    }

    /// The payload inside the blob, or nil if it is malformed, of a version this build
    /// does not know, or sealed under a different code.
    public static func open(_ blob: String, code: String) -> String? {
        guard let raw = Data(base64Encoded: blob.trimmingCharacters(in: .whitespacesAndNewlines)),
              raw.count >= 1 + saltBytes + nonceBytes + tagBytes,
              raw[raw.startIndex] == version
        else { return nil }

        // Data slices keep the parent's indices, so every bound is written from the start
        // rather than from zero.
        let base = raw.startIndex
        let saltEnd = base + 1 + saltBytes
        let nonceEnd = saltEnd + nonceBytes
        let cipherEnd = raw.endIndex - tagBytes

        let salt = Data(raw[(base + 1)..<saltEnd])
        let nonce = Data(raw[saltEnd..<nonceEnd])
        let cipher = Data(raw[nonceEnd..<cipherEnd])
        let tag = Data(raw[cipherEnd..<raw.endIndex])

        guard let boxNonce = try? AES.GCM.Nonce(data: nonce),
              let box = try? AES.GCM.SealedBox(nonce: boxNonce, ciphertext: cipher, tag: tag),
              let plain = try? AES.GCM.open(
                  box, using: deriveKey(code: code, salt: salt), authenticating: Data([version])
              )
        else { return nil }

        return String(data: plain, encoding: .utf8)
    }
}
