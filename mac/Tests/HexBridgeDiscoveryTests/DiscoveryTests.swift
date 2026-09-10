import Foundation
import Testing

@testable import HexBridgeDiscovery

/// Autodiscovery, and mostly the one rule it exists for: this Mac dials a host
/// it found by itself **only** when that host's tag equals the tag of its own
/// key (PROTOCOL.md, «Автопоиск хоста»).
///
/// The rule lives on the Mac, so it is tested on the Mac. The Windows suite
/// (`win/src/HexBridge.Tests/DiscoveryTests.cs`) states the same thing about the
/// half that publishes, against the same two vectors — two implementations that
/// agree with a constant agree with each other.
///
/// swift-testing rather than XCTest: this machine builds with the Command Line
/// Tools toolchain, which ships `Testing.framework` and no `XCTest`.
enum Vectors {
    /// Bytes 0…31. Nothing about it is secret; that is what a vector is for.
    static let ourPsk = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
    static let ourTag = "QftJm4C_xaQadblqBrCUpA"

    /// Somebody else's key, and the tag their PC therefore advertises.
    static let theirPsk = "AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw="
    static let theirTag = "wVDaMu3kwZo6E2yawqf5WQ"

    static func randomKey() -> String {
        Data((0..<32).map { _ in UInt8.random(in: 0...255) }).base64EncodedString()
    }
}

@Suite("Метка")
struct DiscoveryTagTests {
    @Test("Метка — это документированный хеш ключа")
    func theTagIsTheDocumentedHashOfTheKey() {
        // tag = base64url(SHA256("hexbridge-discovery-v1" || PSK)[0..16]).
        // Drift here is not an error anywhere: it is a Mac that quietly stops
        // recognising its own PC.
        #expect(DiscoveryTag.tag(forBase64Key: Vectors.ourPsk) == Vectors.ourTag)
        #expect(DiscoveryTag.tag(forBase64Key: Vectors.theirPsk) == Vectors.theirTag)
        #expect(DiscoveryTag.domain == "hexbridge-discovery-v1")
    }

    @Test("Метка — 22 символа base64url")
    func aTagIsTwentyTwoCharactersOfBase64Url() throws {
        let tag = try #require(DiscoveryTag.tag(forBase64Key: Vectors.ourPsk))

        #expect(tag.count == DiscoveryTag.length)
        #expect(!tag.contains("="))
        #expect(!tag.contains("+"))
        #expect(!tag.contains("/"))
        #expect(DiscoveryTag.isWellFormed(tag))
    }

    @Test("Ключа в метке нет")
    func theTagIsNotTheKeyInDisguise() throws {
        // Sixteen bytes of SHA-256 over 32 random ones — «нельзя обратить» is
        // not something a test can show. What it can show is that the key never
        // appears in the tag in any form we hand out.
        let tag = try #require(DiscoveryTag.tag(forBase64Key: Vectors.ourPsk))

        #expect(!tag.contains(Vectors.ourPsk.prefix(8)))
        #expect(tag != Vectors.ourPsk)
    }

    @Test("У каждого ключа своя метка")
    func everyKeyGetsItsOwnTag() {
        var seen = Set<String>()
        for _ in 0..<200 {
            #expect(seen.insert(DiscoveryTag.tag(forBase64Key: Vectors.randomKey())!).inserted)
        }
    }

    @Test("Один бит ключа — совсем другая метка")
    func oneBitOfKeyIsAWholeDifferentTag() throws {
        var key = try #require(Data(base64Encoded: Vectors.ourPsk))
        let before = DiscoveryTag.tag(forKey: key)
        key[31] ^= 0x01

        #expect(DiscoveryTag.tag(forKey: key) != before)
    }

    @Test(
        "Ключ, который не ключ, метки не имеет",
        arguments: [nil, "", "   ", "не base64", "YWJj", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gIQ=="]
            as [String?]
    )
    func aKeyThatIsNotAKeyHasNoTag(psk: String?) {
        // Nil is the honest answer, and it is what makes an unconfigured Mac
        // refuse to match anything at all.
        #expect(DiscoveryTag.tag(forBase64Key: psk) == nil)
    }

    @Test("Своя метка совпадает")
    func ourOwnTagMatches() {
        #expect(DiscoveryTag.same(Vectors.ourTag, DiscoveryTag.tag(forBase64Key: Vectors.ourPsk)))
    }

    @Test("Чужая метка не совпадает")
    func somebodyElsesTagDoesNotMatch() {
        #expect(!DiscoveryTag.same(Vectors.ourTag, Vectors.theirTag))
        #expect(!DiscoveryTag.same(Vectors.theirTag, Vectors.ourTag))
    }

    @Test(
        "Отсутствие метки у обоих — не совпадение",
        arguments: [
            (nil, nil),
            (nil, Vectors.ourTag),
            (Vectors.ourTag, nil),
            ("", ""),
            ("QftJm4C_xaQadblqBrCUp", "QftJm4C_xaQadblqBrCUp"),      // twenty-one characters
            ("QftJm4C_xaQadblqBrCUpA=", "QftJm4C_xaQadblqBrCUpA="),  // padded, so not our shape
        ] as [(String?, String?)]
    )
    func twoMachinesThatCannotStateATagAreNotPaired(pair: (String?, String?)) {
        // «Ни у кого нет метки» must never read as «метки совпали»: that
        // equality is precisely how an unconfigured Mac would come to trust an
        // unconfigured stranger.
        #expect(!DiscoveryTag.same(pair.0, pair.1))
    }

    @Test("Регистр метки значим")
    func tagComparisonIsCaseSensitive() {
        // base64url distinguishes case; folding it would collide keys silently.
        #expect(!DiscoveryTag.same(Vectors.ourTag, Vectors.ourTag.lowercased()))
        #expect(!DiscoveryTag.same(Vectors.ourTag, Vectors.ourTag.uppercased()))
    }
}

@Suite("Разбор TXT")
struct DiscoveryTxtTests {
    @Test("Запись, какую пишет Windows, читается")
    func aTxtRecordFromWindowsIsRead() throws {
        // Byte for byte what `DiscoveryTxt.Build` emits on the other side.
        let host = try #require(DiscoveryTxt.parse(
            entries: ["v=1", "port=47702", "name=GAMING-PC", "tag=\(Vectors.ourTag)"],
            id: "GAMING-PC._hexbridge._udp.local."
        ))

        #expect(host.version == 1)
        #expect(host.port == 47702)
        #expect(host.name == "GAMING-PC")
        #expect(host.tag == Vectors.ourTag)
        #expect(host.target.isEmpty)   // no address until it resolves
    }

    @Test("Запись переживает круг")
    func aTxtRecordSurvivesTheRoundTrip() throws {
        let entries = DiscoveryTxt.render(version: 1, port: 47702, name: "Никита-ПК", tag: Vectors.ourTag)
        let host = try #require(DiscoveryTxt.parse(entries: entries, id: "id"))

        #expect(host.name == "Никита-ПК")
        #expect(host.tag == Vectors.ourTag)
        #expect(host.port == 47702)
    }

    @Test("Незнакомые ключи не ломают разбор")
    func keysWeDoNotKnowAreIgnoredRatherThanFatal() throws {
        // A later version of the host may add entries. Refusing the whole record
        // over one unknown key would empty the list on every Mac in the field.
        let host = try #require(DiscoveryTxt.parse(
            entries: ["v=1", "port=47702", "name=PC", "tag=\(Vectors.ourTag)", "future=whatever", "bare", "=novalue"],
            id: "id"
        ))

        #expect(host.tag == Vectors.ourTag)
    }

    @Test("У повторяющегося ключа побеждает первое значение")
    func theFirstValueOfADuplicateKeyWins() throws {
        // RFC 6763 §6.4. Otherwise a chosen tag could be appended to somebody
        // else's advertisement and would win.
        let host = try #require(DiscoveryTxt.parse(
            entries: ["v=1", "port=47702", "tag=\(Vectors.ourTag)", "tag=\(Vectors.theirTag)"],
            id: "id"
        ))

        #expect(host.tag == Vectors.ourTag)
    }

    @Test(
        "Запись, по которой некуда звонить, отвергается",
        arguments: [
            [],
            ["port=47702"],
            ["v=1"],
            ["v=x", "port=47702"],
            ["v=1", "port=0"],
            ["v=1", "port=70000"],
            ["v=1", "port=-5"],
            ["v=1", "port=+47702"],
            ["v=1", "port=477 02"],
        ]
    )
    func aTxtRecordWeCannotDialIsRefused(entries: [String]) {
        #expect(DiscoveryTxt.parse(entries: entries, id: "id") == nil)
    }

    @Test(
        "Кривая метка выбрасывается, и хост остаётся чужим",
        arguments: [
            "tag=",
            "tag=QftJm4C_xaQadblqBrCUp",       // one character short
            "tag=QftJm4C_xaQadblqBrCUpAA",     // one character long
            "tag=QftJm4C/xaQadblqBrCUpA",      // base64, not base64url
            "tag=Qft Jm4C_xaQadblqBrCUp",
        ]
    )
    func aMalformedTagIsDroppedAndTheHostStaysUnmatchable(entry: String) throws {
        // Keeping it would only give a later comparison a chance to accept it.
        let host = try #require(DiscoveryTxt.parse(entries: ["v=1", "port=47702", "name=PC", entry], id: "id"))

        #expect(host.tag == nil)
        #expect(DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: [host]).verdict == .noMatch)
    }

    @Test("Без имени в TXT берётся имя экземпляра")
    func aHostWithNoNameFallsBackToItsInstanceName() throws {
        // The list has to show something, and Bonjour guarantees the instance
        // name. It is display text either way — never trust.
        let host = try #require(
            DiscoveryTxt.parse(entries: ["v=1", "port=47702"], id: "id", fallbackName: "GAMING-PC"))

        #expect(host.name == "GAMING-PC")
    }

    @Test("Слишком длинное имя режется по границе символа")
    func anOverlongNameIsCutToWhatATxtStringHolds() throws {
        let entries = DiscoveryTxt.render(
            version: 1, port: 47702, name: String(repeating: "Я", count: 400), tag: Vectors.ourTag)
        let name = try #require(entries.first { $0.hasPrefix("name=") })

        #expect(name.utf8.count <= 255)
        #expect(!name.contains("\u{FFFD}"))
    }
}

@Suite("Правило подключения")
struct DiscoveryMatchTests {
    private func host(
        _ name: String, _ address: String, _ tag: String?, port: UInt16 = 47702
    ) -> DiscoveredHost {
        DiscoveredHost(id: "\(name).\(address)", name: name, address: address, port: port, tag: tag)
    }

    @Test("К хосту со своей меткой подключаемся")
    func aHostWithOurTagIsTheOneWeDial() {
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.10", Vectors.ourTag)])

        #expect(choice.shouldConnect)
        #expect(choice.host?.target == "192.168.1.10:47702")
    }

    @Test("Хоста с чужой меткой для нас не существует")
    func aHostWithSomebodyElsesTagDoesNotExistForUs() {
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.77", Vectors.theirTag)])

        #expect(!choice.shouldConnect)
        #expect(choice.verdict == .noMatch)
        #expect(choice.host == nil)
    }

    @Test("Имя не значит ничего")
    func theNameCountsForNothing() {
        // The stranger's PC is called exactly what ours is — «GAMING-PC» is a
        // default, not a coincidence — and it is listed first. The tag decides.
        let choice = DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: [
            host("GAMING-PC", "192.168.1.77", Vectors.theirTag),
            host("GAMING-PC", "192.168.1.10", Vectors.ourTag),
        ])

        #expect(choice.host?.target == "192.168.1.10:47702")
    }

    @Test("Хост без метки не выбирается никогда")
    func aHostThatPublishesNoTagIsNeverDialled() {
        let choice = DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: [host("PC", "192.168.1.50", nil)])

        #expect(choice.verdict == .noMatch)
    }

    @Test("Полная сеть чужих — это по-прежнему никто")
    func aRoomFullOfStrangersIsStillNobody() {
        let crowd = (1...20).map { index in
            host("PC-\(index)", "192.168.1.\(index)", DiscoveryTag.tag(forBase64Key: Vectors.randomKey()))
        }

        #expect(DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: crowd).verdict == .noMatch)
    }

    @Test("Неспаренный Mac не подключается ни к кому", arguments: [nil, "", "   ", "не метка"] as [String?])
    func anUnpairedMacConnectsToNothingAtAll(ownTag: String?) {
        // The whole point. No key means no tag means no comparison — not «the
        // only host wins», not «the first host wins», nothing at all. The short
        // code off the host's screen stays required.
        let hosts = [
            host("GAMING-PC", "192.168.1.10", Vectors.ourTag),
            host("PC", "192.168.1.77", Vectors.theirTag),
        ]
        let choice = DiscoveryMatch.choose(ownTag: ownTag, hosts: hosts)

        #expect(choice.verdict == .unpaired)
        #expect(!choice.shouldConnect)
        #expect(choice.host == nil)
        #expect(DiscoveryMatch.retarget(current: "", choice: choice) == nil)
    }

    @Test("Неспаренному Mac объясняют, чего не хватает")
    func anUnpairedMacStillGetsAReasonItCanShow() {
        // §9.3: the list is shown, the connection is not made, and the user is
        // told which of those two is happening.
        let choice = DiscoveryMatch.choose(
            ownTag: nil, hosts: [host("GAMING-PC", "192.168.1.10", Vectors.ourTag)])

        #expect(choice.reason == .noKeyOfOurOwn)
    }

    @Test("Совпадение без адреса — ещё не совпадение")
    func aMatchWithNoAddressYetIsNotAMatchYet() {
        // Browse results arrive before they resolve; taking one then would blank
        // out a target that is working perfectly well.
        let choice = DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "", Vectors.ourTag)])

        #expect(choice.verdict == .noMatch)
    }

    @Test("Пустая сеть так и говорит")
    func anEmptyNetworkSaysSoRatherThanBlaming() {
        let choice = DiscoveryMatch.choose(ownTag: Vectors.ourTag, hosts: [])

        #expect(choice.verdict == .noMatch)
        #expect(choice.reason == .networkEmpty)
    }

    // MARK: - Хост переехал

    @Test("За хостом на новом адресе идём сами")
    func aHostThatCameBackOnANewAddressIsFollowed() {
        // The router rebooted and DHCP handed out .23 instead of .10. This is
        // the most common «вчера работало, сегодня нет», and the tag is not tied
        // to an address precisely so that it fixes itself.
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.23", Vectors.ourTag)])

        #expect(DiscoveryMatch.retarget(current: "192.168.1.10:47702", choice: choice) == "192.168.1.23:47702")
    }

    @Test("Переезд не трогает ключ")
    func followingTheHostNeverTouchesTheKey() {
        // Re-pairing would mean a new key. A DHCP lease is not a reason for one.
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.23", Vectors.ourTag)])

        #expect(choice.host?.tag == Vectors.ourTag)
        #expect(DiscoveryTag.tag(forBase64Key: Vectors.ourPsk) == Vectors.ourTag)
    }

    @Test("Сменившийся порт тоже подхватывается")
    func aHostThatMovedPortIsFollowedToo() {
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.10", Vectors.ourTag, port: 47800)])

        #expect(DiscoveryMatch.retarget(current: "192.168.1.10:47702", choice: choice) == "192.168.1.10:47800")
    }

    @Test("Хост, который не переезжал, не трогаем")
    func aHostThatHasNotMovedIsLeftAlone() {
        // Retargeting every browse cycle would restart the pipeline every browse
        // cycle, and each restart costs a word of speech.
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.10", Vectors.ourTag)])

        #expect(DiscoveryMatch.retarget(current: "192.168.1.10:47702", choice: choice) == nil)
        #expect(DiscoveryMatch.retarget(current: " 192.168.1.10:47702 ", choice: choice) == nil)
    }

    @Test("За переехавшим чужаком не идём")
    func aStrangerThatMovedIsStillNotFollowed() {
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.23", Vectors.theirTag)])

        #expect(DiscoveryMatch.retarget(current: "192.168.1.10:47702", choice: choice) == nil)
    }

    @Test("Mac без адреса получает совпавший")
    func aMacWithNoTargetYetIsGivenTheOneThatMatches() {
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "10.0.0.5", Vectors.ourTag)])

        #expect(DiscoveryMatch.retarget(current: "", choice: choice) == "10.0.0.5:47702")
        #expect(DiscoveryMatch.retarget(current: nil, choice: choice) == "10.0.0.5:47702")
    }

    @Test("Перепривязанный хост перестаёт быть нашим")
    func repairingTheHostStopsTheOldMacFindingIt() {
        // After the host is paired with a different Mac its tag changes, and
        // this Mac stops matching it — by itself, because the tag follows the
        // key.
        let choice = DiscoveryMatch.choose(
            ownTag: Vectors.ourTag, hosts: [host("GAMING-PC", "192.168.1.10", Vectors.theirTag)])

        #expect(choice.verdict == .noMatch)
    }
}
