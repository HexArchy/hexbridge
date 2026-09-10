import CryptoKit
import Foundation
import Testing

@testable import HexBridgePairing

/// The encryption around the short-code exchange.
///
/// The point of these is interoperability, not arithmetic: the blob is produced by the
/// PC and opened here, so an accidental change to the salt length, the iteration count,
/// the associated data or the order of the fields must fail loudly rather than quietly
/// stop two machines from ever pairing again. The vector below came out of the Windows
/// implementation and is written down in `docs/PROTOCOL.md`; the mirror of this file is
/// `win/src/HexBridge.Tests/PairingSealTests.cs`.
struct PairingSealTests {
    /// Salt 01…10, nonce A0…AB, so the whole answer is reproducible.
    static let salt = Data((1...16).map { UInt8($0) })
    static let nonce = Data((0..<12).map { UInt8(0xA0 + $0) })

    static let code = "TUJJC8XU3LJ4"
    static let uri = "hexbridge://pair?v=1&h=10.0.0.7&p=47702&k=3q2-796tvu_erb7v3q2-796tvu_erb7v3q2-796tvu8&n=PC"

    static let blob = """
    AQECAwQFBgcICQoLDA0ODxCgoaKjpKWmp6ipqquYsSxi+zUzZvJq1//po186Z/Mjzmj52LsWiAN6XTnFUHQm\
    oJ2ReIRAhxGw77vd7slbDBE7r5lxvBMh0XPAfjdwIa8S86pAsZ3skt5HC/MCC6Rw0CT9aQd5D0b1ZyCUuovF\
    ba2HYAgIqgXf
    """

    @Test("Ключ выводится так же, как на ПК")
    func keyMatchesTheOtherImplementation() {
        let key = PairingSeal.deriveKey(code: Self.code, salt: Self.salt)
        let hex = key.withUnsafeBytes { Data($0) }.map { String(format: "%02x", $0) }.joined()

        #expect(hex == "3b5d88c7628acaec690b06bce40aca7174eed292d27a8ded9d3b801d3beb513c")
    }

    @Test("Ответ с ПК открывается")
    func opensWhatTheOtherSideSealed() {
        #expect(PairingSeal.open(Self.blob, code: Self.code) == Self.uri)
    }

    @Test("Код можно ввести как угодно — с дефисами и в нижнем регистре")
    func theCodeIsNormalisedBeforeItBecomesAKey() {
        #expect(PairingSeal.open(Self.blob, code: "tujj-c8xu-3lj4") == Self.uri)
        #expect(PairingSeal.open(Self.blob, code: "  TUJJ C8XU 3LJ4 ") == Self.uri)
    }

    @Test("Та же соль и тот же одноразовый номер дают те же байты")
    func sealingReproducesTheVector() {
        let made = PairingSeal.seal(Self.uri, code: Self.code, salt: Self.salt, nonce: Self.nonce)

        #expect(made == Self.blob)
    }

    @Test("Чужой код не открывает ничего")
    func aWrongCodeIsRefused() {
        #expect(PairingSeal.open(Self.blob, code: "TUJJC8XU3LJ5") == nil)
    }

    /// The tag covers the version byte, so a blob relabelled as some future format cannot
    /// be replayed as this one.
    @Test("Подделанная версия не проходит")
    func aChangedVersionIsRefused() {
        var raw = Data(base64Encoded: Self.blob)!
        raw[raw.startIndex] = 2

        #expect(PairingSeal.open(raw.base64EncodedString(), code: Self.code) == nil)
    }

    @Test("Испорченный шифротекст не проходит")
    func atamperedBodyIsRefused() {
        var raw = Data(base64Encoded: Self.blob)!
        let victim = raw.startIndex + 1 + PairingSeal.saltBytes + PairingSeal.nonceBytes
        raw[victim] ^= 0x01

        #expect(PairingSeal.open(raw.base64EncodedString(), code: Self.code) == nil)
    }

    @Test("Мусор вместо ответа не роняет разбор")
    func rubbishIsJustNil() {
        #expect(PairingSeal.open("", code: Self.code) == nil)
        #expect(PairingSeal.open("not base64 at all!", code: Self.code) == nil)
        #expect(PairingSeal.open(Data([1, 2, 3]).base64EncodedString(), code: Self.code) == nil)
    }
}
