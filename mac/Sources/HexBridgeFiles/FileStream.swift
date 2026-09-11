import CryptoKit
import Foundation

/// The fast path for a file: one TCP connection, one file, sealed a record at a
/// time. docs/PROTOCOL.md, «The fast path for files (TCP, data port + 2)», is
/// the contract; this is the whole of it that is not sockets.
///
/// The arithmetic behind it is the reason it exists. Every byte of a file on the
/// reliable UDP channel travels in a 1024-byte piece inside its own datagram,
/// each one sealed, framed and handed to the kernel separately: 2.8 MB/s
/// measured, against 370 MB/s for a plain stream between the same two machines.
/// Nothing here replaces that channel's guarantees — TCP already delivers every
/// byte, in order, once — it sidesteps the need for them.
///
/// It lives in this target rather than in the app for the same reason as
/// ``SafeFileName`` beside it: this is where a connection from somebody without
/// the key is turned away and where a name that crossed the network is made safe
/// before anything is written with it, and neither decision may rest on a test
/// nobody can run. The app target drags in AppKit, SwiftUI, CoreAudio and a
/// static libopus, none of which links into a test bundle. This is Foundation
/// and CryptoKit.
public enum FileStream {

    /// "MBG1", the four bytes the UDP header opens with, for the same reason it
    /// opens with them: something has to be readable before there is a key to
    /// read with.
    public static let magic: [UInt8] = [0x4D, 0x42, 0x47, 0x31]

    public static let version: UInt8 = 1

    /// magic, version, room. The only bytes on the connection that are not
    /// sealed, and they carry nothing worth sealing: the room is derived from
    /// the key by a one-way hash and travels in the clear on every datagram
    /// already.
    public static let preludeSize = 13

    /// The contract's ceiling on one record's plaintext. Nothing forces a record
    /// to be this long — a short one is legal and the last one usually is — so
    /// it is a ceiling for the reader rather than a shape for the writer.
    public static let maxRecordPlaintext = 65536

    public static let tagSize = 16

    /// How many bytes of sealed record follow, LE u32, in front of every record.
    ///
    /// The contract does not spell this out, and a stream cannot do without it:
    /// records are «1…65536 bytes of plaintext each», so a reader has no way of
    /// knowing where one ends unless it is told. Four bytes rather than two
    /// because a full record sealed is 65552 bytes, which does not fit in two —
    /// the only width the contract's own numbers leave available.
    public static let lengthSize = 4

    /// The largest a single framed record can be, which is what a reader may
    /// allocate for and what it refuses to go past.
    public static let maxFrame = lengthSize + maxRecordPlaintext + tagSize

    public static let hashSize = 32

    /// What the opening record carries: which file this is, how long it is, and
    /// what it should hash to.
    public struct Opening: Equatable, Sendable {
        /// Already through ``SafeFileName/sanitised(_:)`` when it came off the
        /// wire — see ``FileStream/decodeOpening(_:)``.
        public let name: String
        public let size: UInt32
        public let hash: [UInt8]

        public init(name: String, size: UInt32, hash: [UInt8]) {
            self.name = name
            self.size = size
            self.hash = hash
        }
    }

    // MARK: - The prelude

    public static func prelude(room: UInt64) -> [UInt8] {
        var out = [UInt8]()
        out.reserveCapacity(preludeSize)
        out.append(contentsOf: magic)
        out.append(version)
        out.appendStreamLE(room)
        return out
    }

    /// The room named by a prelude, or nil when those bytes are not one.
    public static func room(inPrelude bytes: ArraySlice<UInt8>) -> UInt64? {
        guard bytes.count >= preludeSize else { return nil }
        let head = Array(bytes.prefix(preludeSize))
        guard Array(head[0..<4]) == magic, head[4] == version else { return nil }
        return head.readStreamLE(at: 5)
    }

    // MARK: - Records

    /// The nonce for one record: the record number, little-endian, in all twelve
    /// bytes.
    ///
    /// A record number is never reused on a connection — one connection carries
    /// one file, the opening record is 0 and every record after it is one more —
    /// so the pair (key, nonce) that GCM may not repeat cannot repeat here
    /// either. Little-endian in the whole twelve bytes rather than in a u32 or a
    /// u64 padded with zeros on purpose: for any number that fits any of those
    /// three, all three produce the same bytes, so the other implementation
    /// cannot get this wrong by picking a different width.
    ///
    /// One key seals both directions, so the single record that travels back —
    /// the ready byte — takes its nonce from the same counter with the twelfth
    /// byte set to `0x80`. A nonce repeated over two different plaintexts under
    /// one key is the one mistake GCM does not forgive, and neither side has to
    /// know the other's numbering to stay clear of it.
    public static func nonce(record: UInt64, backwards: Bool = false) -> AES.GCM.Nonce {
        var raw = [UInt8](repeating: 0, count: 12)
        for byte in 0..<8 { raw[byte] = UInt8(truncatingIfNeeded: record >> (8 * byte)) }
        if backwards { raw[11] = 0x80 }
        // Twelve bytes is always a valid GCM nonce.
        return try! AES.GCM.Nonce(data: raw)
    }

    /// The one byte that says «somebody is really here».
    public static let readyByte: UInt8 = 0x01

    /// One framed record: `length LE u32 || ciphertext || tag`.
    ///
    /// No associated data. The contract seals the record and says nothing about
    /// authenticating the prelude with it, and there is nothing to be gained by
    /// inventing that here: the room in the prelude is checked against our own
    /// before a record is opened, and a room somebody else wrote does not make
    /// a record open.
    public static func seal(
        record number: UInt64, plaintext: ArraySlice<UInt8>, key: SymmetricKey,
        backwards: Bool = false
    ) throws -> [UInt8] {
        guard plaintext.count <= maxRecordPlaintext else { throw StreamError.oversizedRecord }
        let sealed = try AES.GCM.seal(
            plaintext, using: key, nonce: nonce(record: number, backwards: backwards))
        let cipher = Array(sealed.ciphertext)
        let tag = Array(sealed.tag)

        var out = [UInt8]()
        out.reserveCapacity(lengthSize + cipher.count + tag.count)
        out.appendStreamLE(UInt32(cipher.count + tag.count))
        out.append(contentsOf: cipher)
        out.append(contentsOf: tag)
        return out
    }

    /// The plaintext of one sealed record, or nil when it does not open under
    /// this key at this number. `body` is the record without its length prefix.
    public static func open(
        record number: UInt64, body: ArraySlice<UInt8>, key: SymmetricKey,
        backwards: Bool = false
    ) -> [UInt8]? {
        guard body.count >= tagSize, body.count - tagSize <= maxRecordPlaintext else { return nil }
        let split = body.endIndex - tagSize
        guard let box = try? AES.GCM.SealedBox(
            nonce: nonce(record: number, backwards: backwards),
            ciphertext: body[body.startIndex..<split],
            tag: body[split...]
        ),
        let plain = try? AES.GCM.open(box, using: key) else { return nil }
        return Array(plain)
    }

    // MARK: - The ready byte

    /// The framed answer to an opening record: record 0 of the backwards
    /// direction, sealing one byte.
    public static func readyRecord(key: SymmetricKey) throws -> [UInt8] {
        try seal(record: 0, plaintext: [readyByte][...], key: key, backwards: true)
    }

    /// Whether a framed answer is the ready byte, sealed under this key.
    ///
    /// Everything else — a short frame, a record that will not open, any other
    /// plaintext — is the same answer: nobody who holds the key is there.
    public static func isReady(frame: ArraySlice<UInt8>, key: SymmetricKey) -> Bool {
        guard frame.count > lengthSize else { return false }
        var length = 0
        for byte in stride(from: lengthSize - 1, through: 0, by: -1) {
            length = (length << 8) | Int(frame[frame.startIndex + byte])
        }
        guard length == frame.count - lengthSize else { return false }
        let body = frame[(frame.startIndex + lengthSize)...]
        guard let plain = open(record: 0, body: body, key: key, backwards: true) else { return false }
        return plain == [readyByte]
    }

    // MARK: - The opening record

    /// `name length LE u16 || name UTF-8 || size LE u32 || SHA-256`.
    ///
    /// The name is cut to what a path component can be on this filesystem before
    /// it is sent, extension kept, exactly as the reliable channel cuts a
    /// description: a name the other end would have to shorten anyway is better
    /// shortened here, where the extension is still known to be an extension.
    public static func encode(opening: Opening) -> [UInt8] {
        let name = Array(SafeFileName.shortened(opening.name, toBytes: SafeFileName.maxNameBytes).utf8)
        var out = [UInt8]()
        out.reserveCapacity(2 + name.count + 4 + hashSize)
        out.appendStreamLE(UInt16(name.count))
        out.append(contentsOf: name)
        out.appendStreamLE(opening.size)
        out.append(contentsOf: opening.hash.prefix(hashSize))
        return out
    }

    /// The opening record's fields, with the name already made safe.
    ///
    /// Sanitised here rather than at the write itself, and then again at the
    /// write: the name was written by the other machine and this is the first
    /// place it is looked at, so it is also the first place it stops being a
    /// path. See ``SafeFileName``.
    public static func decodeOpening(_ plaintext: [UInt8]) -> Opening? {
        guard plaintext.count >= 2 else { return nil }
        let nameLength = Int(plaintext.readStreamLE(at: 0) as UInt16)
        guard plaintext.count >= 2 + nameLength + 4 + hashSize else { return nil }

        let raw = String(decoding: plaintext[2..<(2 + nameLength)], as: UTF8.self)
        return Opening(
            name: SafeFileName.sanitised(raw),
            size: plaintext.readStreamLE(at: 2 + nameLength),
            hash: Array(plaintext[(2 + nameLength + 4)..<(2 + nameLength + 4 + hashSize)])
        )
    }
}

/// What can go wrong with a stream, in the words the code uses. Nothing here is
/// shown to anybody: the app turns an outcome into a sentence of its own, in the
/// language the interface is in.
public enum StreamError: Error, Equatable, Sendable {
    /// The first bytes are not a prelude: wrong magic, or a version this build
    /// does not know.
    case badPrelude
    /// A prelude for somebody else's pairing. Nothing is written for it.
    case wrongRoom
    /// A record that does not open under our key, the opening record included.
    case sealBroken(record: UInt64)
    /// A length prefix naming more than a record may hold. Refused before the
    /// bytes are read, so a hostile length cannot make us allocate for it.
    case oversizedRecord
    /// The opening record opened but is not one.
    case malformedOpening
    /// Records after the empty record that ends the stream.
    case recordAfterEnd
    /// The connection ended before the empty record did. The file is incomplete,
    /// whatever its length says.
    case truncated
}

// MARK: - Writing

/// One file's worth of records, in order.
///
/// A value type holding the record number and nothing else. The number is what
/// must never repeat under one key, so there is exactly one place it is
/// incremented and no way to seal a record without moving it on.
public struct FileStreamWriter {
    private let key: SymmetricKey
    private let room: UInt64

    /// The number the next record will be sealed under. Public so a test can
    /// watch it rather than infer it.
    public private(set) var nextRecord: UInt64 = 0

    public init(key: SymmetricKey, room: UInt64) {
        self.key = key
        self.room = room
    }

    /// The bytes that go first, before anything is sealed.
    public func prelude() -> [UInt8] {
        FileStream.prelude(room: room)
    }

    /// The opening record. Must be the first record on the connection: the
    /// contract fixes its number at 0, and the number is the nonce.
    public mutating func opening(_ opening: FileStream.Opening) throws -> [UInt8] {
        try seal(FileStream.encode(opening: opening)[...])
    }

    /// One record of file data, at most ``FileStream/maxRecordPlaintext`` bytes.
    public mutating func data(_ plaintext: ArraySlice<UInt8>) throws -> [UInt8] {
        try seal(plaintext)
    }

    /// The empty record that ends the stream. Anything after it is a protocol
    /// error on the other side, which is what makes a connection that simply
    /// stops distinguishable from a file that finished.
    public mutating func end() throws -> [UInt8] {
        let nothing: [UInt8] = []
        return try seal(nothing[...])
    }

    private mutating func seal(_ plaintext: ArraySlice<UInt8>) throws -> [UInt8] {
        let number = nextRecord
        nextRecord += 1
        return try FileStream.seal(record: number, plaintext: plaintext, key: key)
    }
}

// MARK: - Reading

/// The other end of the same thing: bytes in as they arrive off a socket, whole
/// records out.
///
/// Incremental because a stream is: TCP hands over whatever it has, which is
/// never the shape the records were written in. Everything a connection may be
/// refused for is decided in here, so that «a connection whose opening record
/// does not open is dropped without a byte being written» is a property of one
/// tested type rather than of the order of statements in a socket callback.
public struct FileStreamReader {

    public enum Event: Equatable, Sendable {
        case opening(FileStream.Opening)
        case data([UInt8])
        /// The empty record. Nothing follows it.
        case end
    }

    private let key: SymmetricKey
    private let room: UInt64

    /// Bytes taken in and not yet turned into records, and how far into them the
    /// records already handed out reach. A cursor rather than removing from the
    /// front: at 64 KiB a record, shuffling the remainder down on every one of
    /// them is a copy of the whole file for nothing.
    private var buffer: [UInt8] = []
    private var cursor = 0

    private var sawPrelude = false
    private var sawOpening = false
    private var nextRecord: UInt64 = 0
    private var ended = false

    public init(key: SymmetricKey, room: UInt64) {
        self.key = key
        self.room = room
    }

    /// True once the empty record has arrived, which is the only honest end.
    public var isComplete: Bool { ended }

    /// Takes whatever came off the socket and returns the records it completed.
    public mutating func accept(_ bytes: some Sequence<UInt8>) throws -> [Event] {
        guard !ended else {
            // A peer that goes on talking past the end is not one we understand,
            // and the file is already whole — refusing is cheaper than deciding
            // what the extra bytes meant.
            var empty = bytes.makeIterator()
            if empty.next() != nil { throw StreamError.recordAfterEnd }
            return []
        }

        buffer.append(contentsOf: bytes)
        var events: [Event] = []

        if !sawPrelude {
            guard buffer.count - cursor >= FileStream.preludeSize else { return events }
            guard let named = FileStream.room(inPrelude: buffer[cursor...]) else {
                throw StreamError.badPrelude
            }
            guard named == room else { throw StreamError.wrongRoom }
            cursor += FileStream.preludeSize
            sawPrelude = true
        }

        while let event = try nextEvent() {
            events.append(event)
            if case .end = event { break }
        }

        compact()
        return events
    }

    /// The connection closed. Anything short of the empty record means the file
    /// is not all there, and a file that is not all there is deleted rather than
    /// renamed — the same answer the reliable channel gives a hash that does not
    /// match.
    public func finish() throws {
        guard ended else { throw StreamError.truncated }
    }

    private mutating func nextEvent() throws -> Event? {
        guard !ended else { throw StreamError.recordAfterEnd }
        guard buffer.count - cursor >= FileStream.lengthSize else { return nil }

        let length = Int(buffer.readStreamLE(at: cursor) as UInt32)
        // Checked before the bytes are waited for, not after they arrive: a
        // length prefix is the one field an unauthenticated peer writes, and
        // waiting for what it names would be somebody else deciding how much
        // memory this process uses.
        guard length >= FileStream.tagSize,
              length <= FileStream.maxRecordPlaintext + FileStream.tagSize else {
            throw StreamError.oversizedRecord
        }
        guard buffer.count - cursor >= FileStream.lengthSize + length else { return nil }

        let start = cursor + FileStream.lengthSize
        let number = nextRecord
        guard let plaintext = FileStream.open(
            record: number, body: buffer[start..<(start + length)], key: key
        ) else {
            throw StreamError.sealBroken(record: number)
        }
        cursor = start + length
        nextRecord += 1

        if !sawOpening {
            guard let opening = FileStream.decodeOpening(plaintext) else {
                throw StreamError.malformedOpening
            }
            sawOpening = true
            return .opening(opening)
        }
        if plaintext.isEmpty {
            ended = true
            return .end
        }
        return .data(plaintext)
    }

    /// Drops what has been handed out. Left until the end of a batch so that a
    /// socket handing over one byte at a time cannot turn into one copy a byte.
    private mutating func compact() {
        guard cursor > 0 else { return }
        if cursor == buffer.count {
            buffer.removeAll(keepingCapacity: true)
        } else {
            buffer.removeFirst(cursor)
        }
        cursor = 0
    }
}

// MARK: - Little-endian helpers

/// The same two operations the app has on its own arrays, written again here
/// because this target deliberately depends on nothing. Named apart so that a
/// file importing both cannot end up with an ambiguous call.
extension Array where Element == UInt8 {
    mutating func appendStreamLE<T: FixedWidthInteger>(_ value: T) {
        for byte in 0..<MemoryLayout<T>.size {
            append(UInt8(truncatingIfNeeded: value >> (8 * byte)))
        }
    }

    func readStreamLE<T: FixedWidthInteger>(at offset: Int) -> T {
        var value: T = 0
        for byte in stride(from: MemoryLayout<T>.size - 1, through: 0, by: -1) {
            value = (value << 8) | T(truncatingIfNeeded: self[offset + byte])
        }
        return value
    }
}
