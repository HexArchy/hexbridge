import CryptoKit
import Foundation
import Testing

@testable import HexBridgeFiles

/// The fast path's framing, away from any socket.
///
/// The interesting cases here are not "a file crossed" — that is the one case
/// that works by accident — but the four that decide whether this is safe to
/// point at a network at all: a stream from somebody without the key must not
/// produce a byte on disk, a stream that stops halfway must not be mistaken for
/// a file, a nonce must never come round twice under one key, and a name that
/// arrived is not a name until it has been through `SafeFileName`.
@Suite("Быстрый путь: кадры и печати")
struct FileStreamFramingTests {

    static let key = SymmetricKey(data: Data(repeating: 0x2B, count: 32))
    static let room: UInt64 = 0x0123_4567_89AB_CDEF

    /// A file, its opening record and every record after it, as one stream.
    static func stream(
        name: String,
        contents: [UInt8],
        key: SymmetricKey = key,
        room: UInt64 = room,
        recordSize: Int = 64 * 1024,
        endRecord: Bool = true
    ) -> [UInt8] {
        var writer = FileStreamWriter(key: key, room: room)
        var out = writer.prelude()
        out += try! writer.opening(FileStream.Opening(
            name: name,
            size: UInt32(contents.count),
            hash: Array(SHA256.hash(data: contents))
        ))
        var offset = 0
        while offset < contents.count {
            let end = min(offset + recordSize, contents.count)
            out += try! writer.data(contents[offset..<end])
            offset = end
        }
        if endRecord { out += try! writer.end() }
        return out
    }

    /// Everything a reader made of the stream, with the bytes it was fed in
    /// pieces of `pieces` so that the incremental path is the one under test.
    static func read(
        _ stream: [UInt8],
        key: SymmetricKey = key,
        room: UInt64 = room,
        pieces: Int = 1500
    ) throws -> [FileStreamReader.Event] {
        var reader = FileStreamReader(key: key, room: room)
        var events: [FileStreamReader.Event] = []
        var offset = 0
        while offset < stream.count {
            let end = min(offset + pieces, stream.count)
            events += try reader.accept(stream[offset..<end])
            offset = end
        }
        try reader.finish()
        return events
    }

    @Test("Файл уезжает и приезжает тем же")
    func aFileSurvivesTheRoundTrip() throws {
        // Deliberately not a multiple of the record size: the last record is
        // short, and a reader that assumed a fixed length would pass every test
        // but this one.
        let contents = (0..<(200_000 as Int)).map { UInt8($0 % 251) }
        let events = try Self.read(Self.stream(name: "отчёт.pdf", contents: contents))

        guard case .opening(let opening)? = events.first else {
            Issue.record("первым событием должна быть начальная запись")
            return
        }
        #expect(opening.name == "отчёт.pdf")
        #expect(opening.size == UInt32(contents.count))
        #expect(opening.hash == Array(SHA256.hash(data: contents)))

        var received: [UInt8] = []
        for event in events.dropFirst() {
            if case .data(let block) = event { received += block }
        }
        #expect(received == contents)
        #expect(events.last == .end)
        // 200 000 bytes is four records: three full and one of 3 392.
        #expect(events.count == 1 + 4 + 1)
    }

    @Test("Запись, запечатанная чужим ключом, не открывается")
    func aRecordSealedUnderAnotherKeyDoesNotOpen() {
        let stranger = SymmetricKey(data: Data(repeating: 0x5C, count: 32))
        let stream = Self.stream(name: "notes.txt", contents: Array("hello".utf8), key: stranger)

        // The opening record is the first thing read, so this is the moment the
        // whole fast path turns a stranger away: it happens before a temporary
        // file exists, which is what «dropped without a byte being written»
        // means in practice.
        #expect(throws: StreamError.sealBroken(record: 0)) {
            _ = try Self.read(stream)
        }

        // The same bytes under the right key are fine, so it is the key that
        // refused them and not the framing.
        #expect(throws: Never.self) {
            _ = try Self.read(stream, key: stranger)
        }
    }

    @Test("Поток из чужой комнаты не начинается")
    func aPreludeForAnotherPairingIsRefused() {
        let stream = Self.stream(name: "notes.txt", contents: Array("hello".utf8), room: 42)
        #expect(throws: StreamError.wrongRoom) { _ = try Self.read(stream) }

        // And bytes that are not a prelude at all, which is what an unrelated
        // program knocking on the port looks like.
        var reader = FileStreamReader(key: Self.key, room: Self.room)
        #expect(throws: StreamError.badPrelude) {
            _ = try reader.accept([UInt8](repeating: 0x47, count: FileStream.preludeSize))
        }
    }

    @Test("Оборванный поток файлом не считается")
    func aTruncatedStreamIsNotAFile() throws {
        let contents = (0..<(100_000 as Int)).map { UInt8($0 % 97) }

        // Cut off mid-file: every record that did arrive opens, and the reader
        // still refuses to call it a file. That refusal is what makes the
        // difference between a deleted temporary file and a renamed one.
        let whole = Self.stream(name: "big.bin", contents: contents)
        var reader = FileStreamReader(key: Self.key, room: Self.room)
        _ = try reader.accept(whole[0..<70_000])
        #expect(reader.isComplete == false)
        #expect(throws: StreamError.truncated) { try reader.finish() }

        // Every byte but the empty record is the same case and the one a reader
        // that trusts `size` gets wrong: the file is all there and the stream
        // still did not end.
        let noEnd = Self.stream(name: "big.bin", contents: contents, endRecord: false)
        #expect(throws: StreamError.truncated) { _ = try Self.read(noEnd) }

        // Half a record is not a record, however plausible its length prefix.
        var partial = Self.stream(name: "big.bin", contents: contents)
        partial.removeLast(9)
        #expect(throws: StreamError.truncated) { _ = try Self.read(partial) }
    }

    @Test("Длина, которой не бывает, до чтения не доходит")
    func anImpossibleLengthIsRefusedBeforeItIsRead() {
        var stream = FileStream.prelude(room: Self.room)
        stream.appendStreamLE(UInt32(FileStream.maxRecordPlaintext + FileStream.tagSize + 1))

        var reader = FileStreamReader(key: Self.key, room: Self.room)
        #expect(throws: StreamError.oversizedRecord) { _ = try reader.accept(stream) }
    }

    @Test("Номер записи, а с ним и nonce, не повторяется")
    func aRecordNumberIsNeverUsedTwice() throws {
        var writer = FileStreamWriter(key: Self.key, room: Self.room)
        #expect(writer.nextRecord == 0)

        var numbers: [UInt64] = []
        _ = try writer.opening(FileStream.Opening(name: "a", size: 4, hash: [UInt8](repeating: 0, count: 32)))
        numbers.append(writer.nextRecord)
        for _ in 0..<1000 {
            _ = try writer.data([1, 2, 3, 4][...])
            numbers.append(writer.nextRecord)
        }
        _ = try writer.end()
        numbers.append(writer.nextRecord)

        // Strictly increasing, so no number — and therefore no nonce — is ever
        // asked for a second time on one connection.
        #expect(numbers == Array(1...UInt64(numbers.count)))

        // The nonces themselves, because «the number goes up» is only half of
        // it: two numbers must not be able to produce the same twelve bytes.
        let nonces = Set((0..<2048).map { Data(FileStream.nonce(record: UInt64($0))) })
        #expect(nonces.count == 2048)

        // The same plaintext twice is two different records on the wire. If it
        // ever were not, the nonce had come round.
        var again = FileStreamWriter(key: Self.key, room: Self.room)
        let first = try again.data([7, 7, 7][...])
        let second = try again.data([7, 7, 7][...])
        #expect(first != second)
    }

    @Test("Враждебное имя из начальной записи обезврежено")
    func aHostileNameInTheOpeningRecordIsSanitised() throws {
        // The name crossed a network and is about to be handed to the
        // filesystem. docs/PROTOCOL.md: it is not to be trusted, and this is the
        // first place it is looked at.
        let hostile = [
            #"..\..\Windows\System32\drivers\etc\hosts"#: "hosts",
            "../../../etc/passwd": "passwd",
            "/Users/hexarch/.zshrc": ".zshrc",
            "report.txt\u{0}.command": "report.txt.command",
            "..": SafeFileName.fallback,
            "": SafeFileName.fallback,
        ]

        for (sent, expected) in hostile {
            let events = try Self.read(Self.stream(name: sent, contents: [1, 2, 3]))
            guard case .opening(let opening)? = events.first else {
                Issue.record("нет начальной записи для «\(sent)»")
                continue
            }
            #expect(opening.name == expected, "«\(sent)» доехало как «\(opening.name)»")
        }
    }

    @Test("Слишком длинное имя режется с сохранением расширения")
    func anOverlongNameKeepsItsExtension() throws {
        let long = String(repeating: "щ", count: 400) + ".numbers"
        let events = try Self.read(Self.stream(name: long, contents: [1]))
        guard case .opening(let opening)? = events.first else {
            Issue.record("нет начальной записи")
            return
        }
        #expect(opening.name.hasSuffix(".numbers"))
        #expect(opening.name.utf8.count <= SafeFileName.maxNameBytes)
    }

    @Test("Кадры собираются из любых кусков, какими их отдаёт сокет")
    func recordsSurviveAnySplitOfTheStream() throws {
        let contents = (0..<(5000 as Int)).map { UInt8($0 % 13) }
        let stream = Self.stream(name: "x.bin", contents: contents, recordSize: 700)

        // A stream is bytes, not records: the socket may hand over one byte or
        // all of them, and both have to come out the same.
        for pieces in [1, 3, 17, 1024, stream.count] {
            let events = try Self.read(stream, pieces: pieces)
            var received: [UInt8] = []
            for event in events { if case .data(let block) = event { received += block } }
            #expect(received == contents, "по \(pieces) байт за раз")
            #expect(events.last == .end)
        }
    }
    // MARK: - The one record that travels backwards

    @Test("Готовность узнаётся только под своим ключом")
    func readyIsRecognisedOnlyUnderItsOwnKey() throws {
        let frame = try FileStream.readyRecord(key: Self.key)

        #expect(FileStream.isReady(frame: frame[...], key: Self.key))

        let stranger = SymmetricKey(data: Data(repeating: 0x5C, count: 32))
        #expect(!FileStream.isReady(frame: frame[...], key: stranger))
    }

    /// One key seals both directions, and the same nonce over two plaintexts
    /// under one key is the mistake GCM does not forgive. The answer takes
    /// record 0 exactly as the opening record does, so the flag in the twelfth
    /// byte is the only thing keeping them apart.
    @Test("Ответ не запечатан нонсом начальной записи")
    func theAnswerDoesNotShareTheOpeningRecordsNonce() throws {
        let frame = try FileStream.readyRecord(key: Self.key)
        let body = frame[(frame.startIndex + FileStream.lengthSize)...]

        #expect(FileStream.open(record: 0, body: body, key: Self.key) == nil)
        #expect(FileStream.open(record: 0, body: body, key: Self.key, backwards: true) == [0x01])
    }

    @Test("Обрезанный или чужой ответ — это не готовность")
    func anythingShortOfTheAnswerIsNotReady() throws {
        let frame = try FileStream.readyRecord(key: Self.key)

        #expect(!FileStream.isReady(frame: frame[..<(frame.count - 1)], key: Self.key))
        #expect(!FileStream.isReady(frame: [][...], key: Self.key))
        // A whole record, sealed правильно, but not the byte that was agreed.
        let wrong = try FileStream.seal(record: 0, plaintext: [0x02][...], key: Self.key, backwards: true)
        #expect(!FileStream.isReady(frame: wrong[...], key: Self.key))
    }

}
