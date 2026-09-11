import CryptoKit
import Foundation
import HexBridgeBulk
import HexBridgeText

/// What a bulk object is for. The kind is what lets one reliable channel carry
/// several features: the clipboard today, file transfer later, with no new
/// packet types.
enum BulkKind: UInt8 {
    case clipboard = 1
    /// A file somebody dropped on the other machine's window.
    case file = 2
}

/// How to interpret the bytes. Anything else travels as `opaque`.
enum BulkFormat: UInt8 {
    case utf8Text = 1
    case png = 2
    case opaque = 3
}

/// The numbers docs/PROTOCOL.md fixes for «Надёжная передача крупных объектов».
///
/// Constants rather than settings on purpose: both ends have to agree on every
/// one of them, and a knob is a way for them to disagree.
enum Bulk {
    /// 1024 bytes, so a chunk packet stays far below a typical MTU even with the
    /// 24-byte header, the 16-byte tag and the 8 bytes of chunk framing.
    static let chunkSize = 1024

    /// 64 MiB, and only for the clipboard. It is held in memory at both ends and
    /// there is no avoiding that — the pasteboard has to be handed the bytes — so
    /// the memory is the reason for the ceiling. A clipboard that large is a
    /// mistake rather than a use.
    static let maxClipboardSize = 64 * 1024 * 1024

    /// 4 GiB less one byte: the ceiling the wire format itself imposes, because
    /// `size` in the offer is a `u32`. Nothing smaller is needed — a file is
    /// never held whole at either end, so what limits it is the format and not
    /// the machine.
    static let maxFileSize = Int(UInt32.max)

    /// The two kinds are not alike and neither is their ceiling. Asked with the
    /// kind everywhere, including in the sentence shown to somebody whose file
    /// was refused: naming the clipboard's 64 MB at a file is a limit they
    /// cannot find and cannot act on.
    static func maxObjectSize(of kind: BulkKind) -> Int {
        switch kind {
        case .clipboard: return maxClipboardSize
        case .file: return maxFileSize
        }
    }

    /// How many missing chunk numbers a single ack may list after the first one.
    static let maxMissingListed = 256

    /// An offer is repeated at most this many times before the transfer is given up.
    static let maxOfferAttempts = 10

    /// The description is only ever shown in a UI, and the whole offer packet has
    /// to fit the MTU. 256 bytes is well inside both.
    static let maxDescriptionBytes = 256

    static let offerInterval: TimeInterval = 1
    static let ackInterval: TimeInterval = 0.2

    /// Not in the contract: how long a half-finished incoming transfer is kept
    /// before the receiver forgets it. Without it a sender that dies mid-transfer
    /// would pin its bytes on the other machine forever.
    static let incomingIdleTimeout: TimeInterval = 30

    /// Also not in the contract: the sender gives up if the acks stop while it
    /// still has chunks outstanding. Retransmission is driven entirely by the
    /// receiver's 200 ms ack, so silence means the transfer can never finish.
    static let sendingSilenceTimeout: TimeInterval = 15

    /// What a file is called while it is being assembled, and how to recognise
    /// one left behind by a run that did not finish. A leading dot so that the
    /// Downloads folder does not show somebody a file that is not there yet.
    static let partialPrefix = ".hexbridge-"
    static let partialSuffix = ".part"

    static let offerHeaderSize = 48
    static let chunkHeaderSize = 8
    static let ackHeaderSize = 11

    static func chunkCount(forSize size: Int) -> UInt32 {
        UInt32((size + chunkSize - 1) / chunkSize)
    }

    /// Length of chunk `index` of an object of `size` bytes.
    static func chunkLength(size: Int, index: UInt32) -> Int {
        let start = Int(index) * chunkSize
        guard start < size else { return 0 }
        return min(chunkSize, size - start)
    }
}

/// The payload of one BULK_OFFER.
struct BulkOffer {
    var transferID: UInt32
    var kind: BulkKind
    var format: BulkFormat
    var size: UInt32
    var chunkCount: UInt32
    var hash: [UInt8]
    var description: String
}

/// The payload of one BULK_ACK.
struct BulkAck {
    var transferID: UInt32
    var accepted: Bool
    var firstMissing: UInt32
    var missing: [UInt32]

    /// The receiver had more holes than one packet can name. The contract's answer
    /// to that is «начать заново с первого недостающего», and a full list is the
    /// only way the sender can tell the two cases apart.
    var isTruncated: Bool { missing.count >= Bulk.maxMissingListed }
}

/// Byte-for-byte codecs for the four bulk payloads in docs/PROTOCOL.md, with no
/// I/O anywhere near them so every branch is reachable from a test.
enum BulkCodec {

    // MARK: - BULK_OFFER

    static func encode(offer: BulkOffer) -> [UInt8] {
        var payload = [UInt8]()
        payload.reserveCapacity(Bulk.offerHeaderSize + 64)
        payload.appendLE(offer.transferID)
        payload.append(offer.kind.rawValue)
        payload.append(offer.format.rawValue)
        payload.appendLE(offer.size)
        payload.appendLE(offer.chunkCount)
        payload.append(contentsOf: offer.hash)
        let description = truncate(offer.description)
        payload.appendLE(UInt16(description.count))
        payload.append(contentsOf: description)
        return payload
    }

    static func decodeOffer(_ payload: [UInt8]) -> BulkOffer? {
        guard payload.count >= Bulk.offerHeaderSize else { return nil }
        let descriptionLength = Int(payload.readLE(at: 46) as UInt16)
        guard payload.count >= Bulk.offerHeaderSize + descriptionLength else { return nil }
        guard let kind = BulkKind(rawValue: payload[4]),
              let format = BulkFormat(rawValue: payload[5]) else { return nil }

        let descriptionBytes = payload[Bulk.offerHeaderSize..<(Bulk.offerHeaderSize + descriptionLength)]
        return BulkOffer(
            transferID: payload.readLE(at: 0),
            kind: kind,
            format: format,
            size: payload.readLE(at: 6),
            chunkCount: payload.readLE(at: 10),
            hash: Array(payload[14..<46]),
            description: String(decoding: descriptionBytes, as: UTF8.self)
        )
    }

    // MARK: - BULK_CHUNK

    static func encodeChunk(transferID: UInt32, index: UInt32, data: ArraySlice<UInt8>) -> [UInt8] {
        var payload = [UInt8]()
        payload.reserveCapacity(Bulk.chunkHeaderSize + data.count)
        payload.appendLE(transferID)
        payload.appendLE(index)
        payload.append(contentsOf: data)
        return payload
    }

    static func decodeChunk(_ payload: [UInt8]) -> (transferID: UInt32, index: UInt32, data: [UInt8])? {
        guard payload.count >= Bulk.chunkHeaderSize else { return nil }
        return (
            payload.readLE(at: 0),
            payload.readLE(at: 4),
            Array(payload[Bulk.chunkHeaderSize...])
        )
    }

    // MARK: - BULK_ACK

    static func encode(ack: BulkAck) -> [UInt8] {
        let listed = min(ack.missing.count, Bulk.maxMissingListed)
        var payload = [UInt8]()
        payload.reserveCapacity(Bulk.ackHeaderSize + 4 * listed)
        payload.appendLE(ack.transferID)
        payload.append(ack.accepted ? 1 : 0)
        payload.appendLE(ack.firstMissing)
        payload.appendLE(UInt16(listed))
        for index in ack.missing.prefix(listed) { payload.appendLE(index) }
        return payload
    }

    static func decodeAck(_ payload: [UInt8]) -> BulkAck? {
        guard payload.count >= Bulk.ackHeaderSize else { return nil }
        let listed = Int(payload.readLE(at: 9) as UInt16)
        guard listed <= Bulk.maxMissingListed,
              payload.count >= Bulk.ackHeaderSize + 4 * listed else { return nil }

        var missing = [UInt32]()
        missing.reserveCapacity(listed)
        for i in 0..<listed { missing.append(payload.readLE(at: Bulk.ackHeaderSize + 4 * i)) }

        return BulkAck(
            transferID: payload.readLE(at: 0),
            accepted: payload[4] == 1,
            firstMissing: payload.readLE(at: 5),
            missing: missing
        )
    }

    // MARK: - BULK_DONE

    static func encodeDone(transferID: UInt32) -> [UInt8] {
        var payload = [UInt8]()
        payload.appendLE(transferID)
        return payload
    }

    static func decodeDone(_ payload: [UInt8]) -> UInt32? {
        guard payload.count >= 4 else { return nil }
        return payload.readLE(at: 0)
    }

    /// UTF-8, cut to `Bulk.maxDescriptionBytes` without splitting a character —
    /// half a code point in a status line is worse than a shorter status line.
    private static func truncate(_ description: String) -> [UInt8] {
        var bytes = Array(description.utf8)
        guard bytes.count > Bulk.maxDescriptionBytes else { return bytes }
        var cut = Bulk.maxDescriptionBytes
        while cut > 0, bytes[cut] & 0xC0 == 0x80 { cut -= 1 }
        bytes.removeSubrange(cut...)
        return bytes
    }
}

// MARK: - Channel

enum BulkDirection {
    case outgoing
    case incoming
}

/// How an outgoing transfer ended.
enum BulkOutcome {
    /// The receiver assembled the object and the hash matched.
    case delivered
    /// The receiver already had this exact object and asked us not to send it.
    case alreadyThere
    /// Ten offers, no answer.
    case noAnswer
    /// The acks stopped while chunks were still outstanding.
    case stalled
}

/// Where an object being assembled is kept until it is whole.
///
/// Declared by whoever is waiting for the kind, because only that feature knows
/// what it is going to do with the object. The clipboard has to put the bytes on
/// the pasteboard and so has no use for anything but memory; a file is written
/// to disk in the end anyway, and holding four gigabytes of it in RAM on the way
/// there buys nothing at all.
enum BulkStorage {
    case memory
    /// Straight into a temporary file in this directory, each chunk at its own
    /// offset. The directory is the one the file will finally live in, so that
    /// putting it in place is a rename rather than a second copy of four
    /// gigabytes.
    case file(directory: URL)
}

/// The object itself, in whichever shape it was assembled in.
enum BulkPayload {
    case bytes([UInt8])
    /// A file that arrived whole and whose hash matched, still under its
    /// temporary name. Whoever takes the delivery owns it from that moment:
    /// nothing else will move it, and nothing else will remove it.
    case file(URL)
}

/// An object that arrived whole.
struct BulkDelivery {
    var transferID: UInt32
    var kind: BulkKind
    var format: BulkFormat
    var payload: BulkPayload
    var hash: [UInt8]
    var description: String
}

/// The end of an outgoing transfer, whatever the reason.
struct BulkResult {
    var transferID: UInt32
    var kind: BulkKind
    var format: BulkFormat
    var size: UInt32
    var hash: [UInt8]
    var description: String
    var outcome: BulkOutcome
}

/// One line of «что сейчас едет», for a progress bar and nothing else.
struct BulkProgress: Identifiable {
    var id: UInt32 { transferID }
    var transferID: UInt32
    var direction: BulkDirection
    var kind: BulkKind
    var format: BulkFormat
    var size: UInt32
    var chunksDone: UInt32
    var chunkCount: UInt32
    var description: String

    var fraction: Double { chunkCount == 0 ? 0 : Double(chunksDone) / Double(chunkCount) }
}

// MARK: - Where the bytes come from and go

/// One object's worth of bytes on the way out.
///
/// An abstraction with exactly two implementations, and it exists for the
/// asymmetry between them: the clipboard has the bytes in hand already, while a
/// file must never be pulled into memory to be sent. Retransmission is what
/// makes this more than a read loop — the contract has the other end name what
/// it is missing, in any order, at any point, so whatever is behind this has to
/// be able to produce any chunk at any time.
private protocol BulkBody: AnyObject {
    var size: Int { get }
    /// Chunk `index`, or nil when it cannot be produced any more — a file that
    /// was replaced or unmounted under a transfer that is still running.
    func chunk(_ index: UInt32) -> ArraySlice<UInt8>?
}

/// An object that was already in memory when it was offered.
private final class MemoryBody: BulkBody {
    private let bytes: [UInt8]

    init(_ bytes: [UInt8]) { self.bytes = bytes }

    var size: Int { bytes.count }

    func chunk(_ index: UInt32) -> ArraySlice<UInt8>? {
        let start = Int(index) * Bulk.chunkSize
        let length = Bulk.chunkLength(size: bytes.count, index: index)
        guard length > 0 else { return nil }
        return bytes[start..<(start + length)]
    }
}

/// A file read a window at a time, and never held whole.
///
/// The handle stays open for the life of the transfer, as the contract's
/// «отправитель читает каждый блок с диска» requires it to: a retransmission has
/// to read a chunk that went out minutes ago, and reopening the file for each
/// one would both cost an `open` a packet and quietly follow whatever has taken
/// that name in the meantime.
///
/// The window is what keeps the cost sane. Four gigabytes is four million
/// chunks, and a seek and a read for each would be eight million system calls
/// for a transfer that is otherwise strictly sequential. A quarter of a megabyte
/// at a time makes that one read per 256 chunks; a retransmission that lands
/// outside the window simply moves it, which is the rare case and is allowed to
/// be the slow one.
private final class FileBody: BulkBody {
    private static let windowBytes = 256 * 1024
    /// How much of the file is hashed at a time when the offer is being built.
    private static let digestBytes = 1024 * 1024

    private let handle: FileHandle
    let size: Int

    /// The window, always `windowBytes` long once allocated; `windowCount` says
    /// how much of it is the file. One allocation for the whole transfer, and
    /// never one per chunk.
    private var window: [UInt8] = []
    private var windowCount = 0
    private var windowStart = 0

    init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
        size = Int(try handle.seekToEnd())
    }

    deinit {
        try? handle.close()
    }

    /// SHA-256 of the whole file, read a window at a time.
    ///
    /// The contract puts the hash in the offer, so it has to be known before the
    /// first chunk goes out — which for a large file means one full pass over it
    /// before anything moves. There is no way around that and no reason to want
    /// one: it is the same pass the other end will make to check the result.
    func digest() throws -> [UInt8] {
        var sha = SHA256()
        var buffer = [UInt8](repeating: 0, count: Self.digestBytes)
        var offset = 0
        while offset < size {
            let wanted = min(Self.digestBytes, size - offset)
            let got = try PosixFile.read(handle.fileDescriptor, into: &buffer, count: wanted, at: offset)
            guard got > 0 else { break }
            buffer.withUnsafeBytes { raw in
                sha.update(bufferPointer: UnsafeRawBufferPointer(rebasing: raw[0..<got]))
            }
            offset += got
        }
        // The window describes a position the digest pass has just walked away
        // from, so it is not to be trusted afterwards.
        windowCount = 0
        windowStart = 0
        return Array(sha.finalize())
    }

    func chunk(_ index: UInt32) -> ArraySlice<UInt8>? {
        let start = Int(index) * Bulk.chunkSize
        let length = Bulk.chunkLength(size: size, index: index)
        guard length > 0 else { return nil }

        if start < windowStart || start + length > windowStart + windowCount {
            refill(from: start)
        }
        guard start >= windowStart, start + length <= windowStart + windowCount else { return nil }

        let offset = start - windowStart
        return window[offset..<(offset + length)]
    }

    private func refill(from start: Int) {
        if window.count != Self.windowBytes {
            window = [UInt8](repeating: 0, count: Self.windowBytes)
        }
        windowStart = start
        // Left empty when the read fails: `chunk` then sees a window that cannot
        // hold what was asked for and says so, and the transfer is given up
        // rather than finished with a hole in it.
        windowCount = (try? PosixFile.read(
            handle.fileDescriptor, into: &window,
            count: min(Self.windowBytes, max(0, size - start)), at: start
        )) ?? 0
    }
}

/// Reading and writing a file at an offset, into memory we already hold.
///
/// Not `FileHandle.read(upToCount:)`, and this is not a matter of taste:
/// Foundation's read maps the file, so hashing a four-gigabyte object leaves
/// most of it resident — 2.8 GB, measured, for the object this whole change
/// exists to make possible. A `pread` into a buffer that is reused costs the
/// buffer and nothing else.
///
/// Both loops carry on through a short answer. A `read` or a `write` on a
/// regular file is allowed to move less than it was asked for, and treating
/// that as the end of the file — or as a completed write — is the kind of bug
/// that only shows up on somebody else's disk.
///
/// Visible to the rest of the target rather than to this file alone, because the
/// fast path reads a file the same way and for the same reason. Nothing about
/// the block channel changes with it.
enum PosixFile {

    static func read(_ descriptor: Int32, into buffer: inout [UInt8], count: Int, at offset: Int) throws -> Int {
        guard count > 0 else { return 0 }
        var done = 0
        while done < count {
            let moved = buffer.withUnsafeMutableBytes { raw -> Int in
                pread(descriptor, raw.baseAddress! + done, count - done, off_t(offset + done))
            }
            if moved < 0 {
                if errno == EINTR { continue }
                throw failure()
            }
            if moved == 0 { break }
            done += moved
        }
        return done
    }

    static func write(_ descriptor: Int32, _ bytes: [UInt8], at offset: Int) throws {
        var done = 0
        while done < bytes.count {
            let moved = bytes.withUnsafeBytes { raw -> Int in
                pwrite(descriptor, raw.baseAddress! + done, bytes.count - done, off_t(offset + done))
            }
            if moved < 0 {
                if errno == EINTR { continue }
                throw failure()
            }
            // A write that moves nothing, having been asked for something, is
            // a disk with no room on it: `errno` is not set in that case, so
            // the reading has to be supplied rather than read back.
            if moved == 0 { throw NSError(domain: NSPOSIXErrorDomain, code: Int(ENOSPC)) }
            done += moved
        }
    }

    private static func failure() -> NSError {
        NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
}

/// One object's worth of bytes on the way in.
///
/// The mirror of `BulkBody`, and the same asymmetry: chunks arrive in whatever
/// order the network gives them, so whatever is behind this has to take a write
/// at an arbitrary offset, and then hash everything it took.
private protocol BulkStore: AnyObject {
    func write(_ data: [UInt8], at offset: Int) throws
    /// SHA-256 of everything written, computed without holding the object.
    func digest() throws -> [UInt8]
    /// What was written is wrong and all of it will arrive again.
    func restart()
    /// Hands the object over. The store owns nothing afterwards.
    func take() throws -> BulkPayload
}

/// An object assembled in memory, which is what the clipboard needs.
private final class MemoryStore: BulkStore {
    private var buffer: [UInt8]

    init(size: Int) {
        buffer = [UInt8](repeating: 0, count: size)
    }

    func write(_ data: [UInt8], at offset: Int) throws {
        buffer.replaceSubrange(offset..<(offset + data.count), with: data)
    }

    func digest() throws -> [UInt8] {
        Array(SHA256.hash(data: buffer))
    }

    /// Nothing to undo: every chunk will be written again over the same bytes,
    /// and the map of which ones have arrived is cleared by the caller.
    func restart() {}

    func take() throws -> BulkPayload { .bytes(buffer) }
}

/// An object assembled straight on disk.
///
/// Hidden, because a half-written file in the Downloads folder is not something
/// anybody asked to see, and in that folder rather than in a temporary one
/// because the last step is then a rename on the same volume instead of a second
/// copy of the whole object.
///
/// It cleans up after itself in `deinit`. That is deliberate rather than tidy:
/// the transfer can be abandoned in half a dozen places — an idle timeout, the
/// feature being switched off, the peer restarting, the channel being reset —
/// and a temporary file left behind by any one of them is a gigabyte of somebody
/// else's disk. Hanging the cleanup off the object's own lifetime is the only
/// version of this that cannot be forgotten at a new call site.
private final class FileStore: BulkStore {
    /// How much is held before it is written. In the ordinary case chunks arrive
    /// in order, so this turns 256 writes into one; out of order it flushes at
    /// the discontinuity and is no worse than writing each chunk as it lands.
    private static let flushBytes = 256 * 1024

    private let url: URL
    private let handle: FileHandle
    private let size: Int
    private var pending: [UInt8] = []
    private var pendingAt = 0
    /// Set once the file has been handed to whoever took the delivery, which is
    /// the one case where `deinit` must leave it alone.
    private var givenAway = false

    init(directory: URL, transferID: UInt32, size: Int) throws {
        self.size = size
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        url = directory.appendingPathComponent(
            "\(Bulk.partialPrefix)\(transferID)-\(UUID().uuidString)\(Bulk.partialSuffix)"
        )
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw BulkError.noRoom
        }
        // For updating rather than for writing: the hash at the end is read back
        // out of this same descriptor, and a write-only one answers that with
        // «bad file descriptor» only once the object is already complete.
        handle = try FileHandle(forUpdating: url)
        // Given its final length up front so that a chunk landing near the end
        // before the ones in front of it is an ordinary write rather than a hole
        // the filesystem has to invent.
        try handle.truncate(atOffset: UInt64(size))
        pending.reserveCapacity(Self.flushBytes + Bulk.chunkSize)
    }

    deinit {
        try? handle.close()
        if !givenAway { try? FileManager.default.removeItem(at: url) }
    }

    func write(_ data: [UInt8], at offset: Int) throws {
        if !pending.isEmpty, offset != pendingAt + pending.count { try flush() }
        if pending.isEmpty { pendingAt = offset }
        pending.append(contentsOf: data)
        if pending.count >= Self.flushBytes { try flush() }
    }

    func digest() throws -> [UInt8] {
        try flush()
        var sha = SHA256()
        var buffer = [UInt8](repeating: 0, count: Self.flushBytes)
        var offset = 0
        while offset < size {
            let wanted = min(Self.flushBytes, size - offset)
            let got = try PosixFile.read(handle.fileDescriptor, into: &buffer, count: wanted, at: offset)
            guard got > 0 else { break }
            buffer.withUnsafeBytes { raw in
                sha.update(bufferPointer: UnsafeRawBufferPointer(rebasing: raw[0..<got]))
            }
            offset += got
        }
        return Array(sha.finalize())
    }

    func restart() {
        pending.removeAll(keepingCapacity: true)
        pendingAt = 0
    }

    func take() throws -> BulkPayload {
        try flush()
        try handle.close()
        givenAway = true
        return .file(url)
    }

    private func flush() throws {
        guard !pending.isEmpty else { return }
        try PosixFile.write(handle.fileDescriptor, pending, at: pendingAt)
        pending.removeAll(keepingCapacity: true)
    }
}

// MARK: - The channel itself

/// The reliable-delivery layer from docs/PROTOCOL.md, both roles in one object:
/// it offers objects, it accepts them, and it retransmits what the other side
/// says it is missing.
///
/// It knows nothing about clipboards and nothing about files. A feature hands it
/// an object, a kind and a format, says where an arriving object of that kind
/// should be put, and gets told when it landed.
///
/// Nothing that crosses this channel is ever held whole unless the feature asked
/// for it in memory. On the way out chunks are read off disk as they go,
/// including on retransmission; on the way in they are written straight into a
/// temporary file at their own offset and what has arrived is remembered in a
/// bitmap. At the format's ceiling that is 512 KB of bookkeeping against four
/// gigabytes for the obvious implementation, and it is the difference between a
/// 4 GiB transfer and a crash.
///
/// Time is a parameter rather than a field read from the clock, so every timeout
/// in here is reachable from a test without waiting for it.
///
/// `@unchecked Sendable`: everything mutable is behind `lock`, and `send` is
/// called from the socket queue and from the feature's timer alike. The one
/// thing that happens outside the lock is verification — see `finishing`.
final class BulkChannel: @unchecked Sendable {
    private let lock = NSLock()
    private var send: (Wire.PacketType, [UInt8]) -> Void

    private var outgoing: [UInt32: Outgoing] = [:]
    private var incoming: [UInt32: Incoming] = [:]
    /// Objects that are complete and are being hashed.
    ///
    /// A separate dictionary and not a flag, because while a transfer is in here
    /// nothing but the verification itself may touch it. Hashing four gigabytes
    /// takes seconds, and doing it under `lock` would stop the socket, the
    /// timer and the 20 Hz redraw of the popover for all of them — a beachball
    /// on a menu bar app. Moving the object out of reach is what lets the work
    /// happen on another queue without a second lock inside every transfer.
    private var finishing: [UInt32: Incoming] = [:]
    /// Ids of transfers already assembled, kept so a lost BULK_DONE can be re-sent.
    private var finished: [UInt32: Date] = [:]

    private var nextID = UInt32.random(in: 1...UInt32.max)

    /// Where the hashing happens. `.userInitiated` because somebody is watching
    /// a progress bar that has reached the end and is waiting for this.
    private let verifyQueue = DispatchQueue(label: "hexbridge.bulk.verify", qos: .userInitiated)

    /// «Этот объект у меня уже есть». The contract makes this the answer to a
    /// duplicate offer *and* the thing that stops two machines syncing each
    /// other in a circle.
    /// "We already hold these exact bytes, do not send them."
    ///
    /// Asked with the kind, because the answer differs by feature: a clipboard that
    /// already holds the text should refuse it, and a file always has to arrive. A
    /// feature that is switched off answers for its own kind and for nothing else.
    var owns: ((BulkKind, [UInt8]) -> Bool)?

    /// An object arrived whole and verified. Called outside the channel's lock.
    /// Everything that arrived whole, to whoever asked to hear about it.
    ///
    /// A list rather than one closure: the clipboard and file transfer both ride this
    /// channel, and with a single slot whichever feature started last would silently
    /// take delivery of the other's objects — and clear it again on the way out.
    /// Each observer filters by kind.
    private var deliveryObservers:
        [Int: (kind: BulkKind, storage: BulkStorage, handle: (BulkDelivery) -> Void)] = [:]
    private var nextObserver = 1

    /// Registers an observer for one kind, and returns the token to remove it with.
    ///
    /// The kind is declared rather than filtered for inside the handler, because the
    /// channel needs to know it too: an object of a kind nobody is waiting for must be
    /// turned away at the offer instead of being pulled across and dropped.
    ///
    /// The storage is declared here for the same reason. It has to be decided when the
    /// offer arrives, which is long before the handler is ever called, and the feature
    /// waiting for the kind is the only thing that knows the answer.
    @discardableResult
    func observeDeliveries(
        of kind: BulkKind,
        storage: BulkStorage = .memory,
        _ handler: @escaping (BulkDelivery) -> Void
    ) -> Int {
        lock.lock()
        defer { lock.unlock() }
        let token = nextObserver
        nextObserver += 1
        deliveryObservers[token] = (kind, storage, handler)
        return token
    }

    /// Where an arriving object of this kind goes, or nil when nothing is waiting
    /// for the kind at all. Caller must not hold `lock`.
    ///
    /// One observer a kind is what the app registers, so "the first" is not a
    /// choice between several — it is the only one.
    private func storage(for kind: BulkKind) -> BulkStorage? {
        lock.lock()
        defer { lock.unlock() }
        return deliveryObservers.values.first { $0.kind == kind }?.storage
    }

    func removeDeliveryObserver(_ token: Int) {
        lock.lock()
        deliveryObservers.removeValue(forKey: token)
        lock.unlock()
    }

    private func announce(_ delivery: BulkDelivery) {
        lock.lock()
        let observers = deliveryObservers.values.filter { $0.kind == delivery.kind }
        lock.unlock()
        for observer in observers { observer.handle(delivery) }
    }

    /// An outgoing transfer ended. Called outside the channel's lock.
    /// Outgoing transfers that ended, however they ended. A list for the same reason
    /// as the delivery observers: two features share this channel.
    private var finishObservers: [Int: (BulkResult) -> Void] = [:]

    @discardableResult
    func observeFinished(_ handler: @escaping (BulkResult) -> Void) -> Int {
        lock.lock()
        defer { lock.unlock() }
        let token = nextObserver
        nextObserver += 1
        finishObservers[token] = handler
        return token
    }

    func removeFinishObserver(_ token: Int) {
        lock.lock()
        finishObservers.removeValue(forKey: token)
        lock.unlock()
    }

    private func announce(_ result: BulkResult) {
        lock.lock()
        let observers = Array(finishObservers.values)
        lock.unlock()
        for observer in observers { observer(result) }
    }

    /// Diagnostics, one line per interesting event. Called outside the lock.
    var onNote: ((String) -> Void)?

    /// How fast chunks go out, and the loop that decides it. See `BulkPacer`.
    private var pacer = BulkPacer()
    private var lastTick: Date?

    /// A ceiling the user asked for, in chunks a second, or nil for "as fast as
    /// this path actually goes". Read on the next tick, so moving the control in
    /// settings takes effect without restarting anything.
    var configuredCeiling: Double? {
        get { lock.lock(); defer { lock.unlock() }; return storedCeiling }
        set { lock.lock(); storedCeiling = newValue; lock.unlock() }
    }
    private var storedCeiling: Double?

    /// Whether the other machine is currently being reached through a relay.
    ///
    /// A relay has its own per-endpoint limit and drops what exceeds it, and a
    /// dropped chunk comes back as a hole and is sent twice — so aiming above it
    /// is a way of going slower. Asked on every tick rather than remembered,
    /// because the direct path can come up and go away mid-transfer.
    var relayInPath: (() -> Bool)?

    /// How many times an object may fail its hash before the receiver gives up.
    var maxHashFailures = 3

    init(send: @escaping (Wire.PacketType, [UInt8]) -> Void) {
        self.send = send
    }

    /// Re-points the channel at a freshly built socket. A new socket means a new
    /// session, so whatever was half-received is gone — but an object we were
    /// pushing is still worth delivering and is simply offered again.
    func rebind(send: @escaping (Wire.PacketType, [UInt8]) -> Void) {
        lock.lock()
        self.send = send
        // Credit earned against a link that no longer exists is not credit, and
        // a speed found on one path says nothing about the next one.
        pacer.restart()
        lastTick = nil
        lock.unlock()
        peerRestarted()
    }

    /// The peer is not the one we were talking to a moment ago — it restarted, or
    /// it has only just appeared.
    ///
    /// Anything half-received belongs to a session that is gone, so it goes.
    /// Anything we were pushing does *not*: the object is still worth delivering,
    /// and the new peer has simply never heard of it.
    func peerRestarted() {
        lock.lock()
        incoming.removeAll()
        // Dropped from the books but not touched: whatever is hashing it owns it
        // until it comes back, and it cleans up when it finds itself unwanted.
        finishing.removeAll()
        finished.removeAll()
        for transfer in outgoing.values { transfer.renegotiate() }
        lock.unlock()
    }

    /// Drops everything in flight. Used when the channel itself is going away.
    func reset() {
        lock.lock()
        outgoing.removeAll()
        incoming.removeAll()
        finishing.removeAll()
        finished.removeAll()
        pacer.restart()
        lastTick = nil
        lock.unlock()
    }

    /// Drops only what belongs to one feature.
    ///
    /// A feature being switched off must not cancel the other one's transfer, and
    /// switching the clipboard off in the middle of a file arriving is an ordinary
    /// thing for somebody to do.
    func reset(kind: BulkKind) {
        lock.lock()
        outgoing = outgoing.filter { $0.value.offer.kind != kind }
        incoming = incoming.filter { $0.value.kind != kind }
        finishing = finishing.filter { $0.value.kind != kind }
        lock.unlock()
    }

    // MARK: - Sending

    /// Starts offering an object held in memory. Returns its transfer id, or
    /// throws with a reason a person can read.
    @discardableResult
    func offer(
        kind: BulkKind,
        format: BulkFormat,
        bytes: [UInt8],
        description: String,
        now: Date = Date()
    ) throws -> UInt32 {
        guard !bytes.isEmpty else { throw BulkError.empty }
        guard bytes.count <= Bulk.maxObjectSize(of: kind) else {
            throw BulkError.tooLarge(limit: Bulk.maxObjectSize(of: kind))
        }
        return push(
            kind: kind, format: format, body: MemoryBody(bytes),
            hash: Array(SHA256.hash(data: bytes)), description: description, now: now
        )
    }

    /// Starts offering a file. The file is read as it goes and is never held
    /// whole, which is the only reason the ceiling for a file can be the
    /// format's own rather than the machine's.
    ///
    /// Slow, and has to be: the contract puts the hash in the offer, so the file
    /// is read once through before the first chunk moves. Call it off the main
    /// thread.
    @discardableResult
    func offer(
        kind: BulkKind,
        format: BulkFormat,
        file url: URL,
        description: String,
        now: Date = Date()
    ) throws -> UInt32 {
        let body = try FileBody(url: url)
        guard body.size > 0 else { throw BulkError.empty }
        guard body.size <= Bulk.maxObjectSize(of: kind) else {
            throw BulkError.tooLarge(limit: Bulk.maxObjectSize(of: kind))
        }
        return push(
            kind: kind, format: format, body: body,
            hash: try body.digest(), description: description, now: now
        )
    }

    private func push(
        kind: BulkKind,
        format: BulkFormat,
        body: BulkBody,
        hash: [UInt8],
        description: String,
        now: Date
    ) -> UInt32 {
        lock.lock()
        let id = nextID
        nextID = nextID == UInt32.max ? 1 : nextID + 1
        let transfer = Outgoing(id: id, kind: kind, format: format, body: body, hash: hash, description: description)
        transfer.offerAttempts = 1
        transfer.lastOfferAt = now
        transfer.lastAckAt = now
        transfer.lastSendAt = now
        outgoing[id] = transfer
        let deliver = send
        lock.unlock()

        deliver(.bulkOffer, BulkCodec.encode(offer: transfer.offer))
        return id
    }

    // MARK: - Packets

    /// One decrypted bulk packet. Types outside 9…12 are not this layer's
    /// business and are ignored rather than treated as an error.
    func handle(type: Wire.PacketType, payload: [UInt8], now: Date = Date()) {
        switch type {
        case .bulkOffer:
            if let offer = BulkCodec.decodeOffer(payload) { handle(offer: offer, now: now) }
        case .bulkChunk:
            if let chunk = BulkCodec.decodeChunk(payload) {
                handle(chunkFor: chunk.transferID, index: chunk.index, data: chunk.data, now: now)
            }
        case .bulkAck:
            if let ack = BulkCodec.decodeAck(payload) { handle(ack: ack, now: now) }
        case .bulkDone:
            if let id = BulkCodec.decodeDone(payload) { handleDone(id) }
        default:
            break
        }
    }

    private func handle(offer: BulkOffer, now: Date) {
        // A repeat of an offer we have already satisfied: the BULK_DONE was lost,
        // so say it again instead of letting the sender sit on the bytes.
        lock.lock()
        let alreadyDone = finished[offer.transferID] != nil
        let deliver = send
        lock.unlock()

        if alreadyDone {
            deliver(.bulkDone, BulkCodec.encodeDone(transferID: offer.transferID))
            return
        }

        func refuse(_ note: String) {
            deliver(.bulkAck, BulkCodec.encode(ack: BulkAck(
                transferID: offer.transferID, accepted: false, firstMissing: 0, missing: []
            )))
            onNote?(note)
        }

        guard offer.size > 0, Int(offer.size) <= Bulk.maxObjectSize(of: offer.kind) else { return }
        guard offer.chunkCount == Bulk.chunkCount(forSize: Int(offer.size)) else { return }

        // Nobody is waiting for this kind — the feature that would take it is switched
        // off. Refusing at the offer costs one packet; accepting costs the whole object,
        // which is then discarded, while the other end watches a transfer it thinks
        // succeeded.
        guard let storage = storage(for: offer.kind) else {
            refuse("bulk: nothing here is waiting for that, not pulling it")
            return
        }

        // The per-kind ceiling is about the wire; this one is about the memory. A
        // feature that asked for its objects in memory gets the memory limit
        // whatever kind it registered for, or an offer could name four gigabytes
        // and have us allocate them on the strength of it.
        if case .memory = storage, Int(offer.size) > Bulk.maxClipboardSize {
            refuse("bulk: the object is too large to hold in memory, not pulling it")
            return
        }

        // The whole loop-breaker, and the reason the contract puts a hash in the
        // offer at all: if we already hold these exact bytes, nothing has to cross
        // the wire.
        if owns?(offer.kind, offer.hash) == true {
            refuse("bulk: the object is already here, not pulling it")
            return
        }

        lock.lock()
        let known = incoming[offer.transferID]
        lock.unlock()

        let state: Incoming
        if let known, known.hash == offer.hash {
            state = known
        } else {
            // Built before the lock is taken: opening a file and giving it its
            // length is a trip to the disk, and the socket queue is not the place
            // to hold a lock across one.
            do {
                state = try Incoming(offer: offer, storage: storage)
            } catch {
                refuse("bulk: nowhere to put the object — \(error)")
                return
            }
        }

        lock.lock()
        incoming[offer.transferID] = state
        state.lastActivity = now
        state.lastAckAt = now
        let ack = BulkCodec.encode(ack: state.buildAck(accepted: true))
        lock.unlock()

        deliver(.bulkAck, ack)
    }

    private func handle(chunkFor transferID: UInt32, index: UInt32, data: [UInt8], now: Date) {
        var packets: [(Wire.PacketType, [UInt8])] = []
        var note: String?
        var verify: Incoming?

        lock.lock()
        let deliver = send
        if finished[transferID] != nil {
            packets.append((.bulkDone, BulkCodec.encodeDone(transferID: transferID)))
        } else if let state = incoming[transferID] {
            state.lastActivity = now
            do {
                try state.store(index: index, data: data)
                if state.isComplete {
                    // Out of reach of everything else until the hash is known.
                    incoming.removeValue(forKey: transferID)
                    finishing[transferID] = state
                    verify = state
                }
            } catch {
                // There is no packet for «мне некуда это писать», and inventing
                // one would be a change to the format. The transfer is dropped;
                // the other end finds out when its acknowledgements stop, which
                // is the honest outcome, and the reason is in the log.
                incoming.removeValue(forKey: transferID)
                note = "bulk: the object could not be written to disk — \(error)"
            }
        }
        lock.unlock()

        for packet in packets { deliver(packet.0, packet.1) }
        if let note { onNote?(note) }
        if let verify { beginVerification(transferID, state: verify) }
    }

    /// Hashes a complete object away from the lock, and then puts the answer back.
    private func beginVerification(_ transferID: UInt32, state: Incoming) {
        verifyQueue.async { [weak self] in
            // A payload only if the hash matched. A temporary file that cannot be
            // read back is treated exactly like one whose hash is wrong: the
            // object is not what it claims to be, whichever of the two it is.
            let payload: BulkPayload? = {
                do { return try state.digest() == state.hash ? state.take() : nil } catch { return nil }
            }()

            guard let self else {
                if case .file(let url)? = payload { try? FileManager.default.removeItem(at: url) }
                return
            }
            self.settle(transferID, state: state, payload: payload, now: Date())
        }
    }

    private func settle(_ transferID: UInt32, state: Incoming, payload: BulkPayload?, now: Date) {
        var packets: [(Wire.PacketType, [UInt8])] = []
        var delivery: BulkDelivery?
        var note: String?

        lock.lock()
        guard finishing.removeValue(forKey: transferID) != nil else {
            // The channel was reset, or the feature switched off, while this was
            // being hashed. Nobody is waiting for it and nobody else will clean
            // up the file it was handed.
            lock.unlock()
            if case .file(let url)? = payload { try? FileManager.default.removeItem(at: url) }
            return
        }
        let deliver = send
        let allowedFailures = maxHashFailures

        if let payload {
            finished[transferID] = now
            delivery = state.delivery(payload: payload)
            packets.append((.bulkDone, BulkCodec.encodeDone(transferID: transferID)))
        } else {
            // «Молчаливой порчи не бывает»: every chunk is asked for again.
            state.hashFailures += 1
            if state.hashFailures >= allowedFailures {
                note = "bulk: the object arrived corrupt three times, giving up"
            } else {
                note = "bulk: hash mismatch, asking for the object again (attempt \(state.hashFailures))"
                state.forget()
                state.lastActivity = now
                state.lastAckAt = now
                incoming[transferID] = state
                packets.append((.bulkAck, BulkCodec.encode(ack: state.buildAck(accepted: true))))
            }
        }
        lock.unlock()

        for packet in packets { deliver(packet.0, packet.1) }
        if let note { onNote?(note) }
        if let delivery { announce(delivery) }
    }

    private func handle(ack: BulkAck, now: Date) {
        lock.lock()
        guard let transfer = outgoing[ack.transferID] else {
            lock.unlock()
            return
        }
        transfer.lastAckAt = now
        var result: BulkResult?
        if ack.accepted {
            transfer.accepted = true
            let round = transfer.rebuild(from: ack)
            // The one measurement the contract offers: chunks the other end has
            // had a full round to receive and is still asking for.
            pacer.note(sent: round.sent, lost: round.lost)
        } else {
            outgoing.removeValue(forKey: ack.transferID)
            result = transfer.result(.alreadyThere)
        }
        lock.unlock()

        if let result { announce(result) }
    }

    private func handleDone(_ transferID: UInt32) {
        lock.lock()
        let transfer = outgoing.removeValue(forKey: transferID)
        lock.unlock()

        if let transfer { announce(transfer.result(.delivered)) }
    }

    // MARK: - Clock

    /// Everything that happens on a timer: repeating an offer, putting chunks on
    /// the wire, the receiver's 200 ms ack, and the timeouts. Call it about every
    /// 20 ms.
    func tick(now: Date = Date()) {
        var packets: [(Wire.PacketType, [UInt8])] = []
        var results: [BulkResult] = []
        var notes: [String] = []
        var staleFinished: [UInt32] = []

        // Asked outside the lock: it reaches into the socket, which has a lock of
        // its own, and one lock taken inside another is a rule that has to be
        // kept rather than remembered.
        let throughRelay = relayInPath?() ?? false

        lock.lock()
        let deliver = send
        let elapsed = max(0, lastTick.map { now.timeIntervalSince($0) } ?? 0.02)
        lastTick = now

        // A relay's limit is a fact about the path, and the setting is what the
        // user asked for; where both apply the lower one wins.
        var ceiling = storedCeiling
        if throughRelay { ceiling = min(ceiling ?? BulkPacer.relayCeiling, BulkPacer.relayCeiling) }
        pacer.ceiling = ceiling
        // A token bucket rather than «n chunks per tick», so the rate on the wire
        // does not change when the caller's timer does.
        pacer.advance(by: elapsed)

        tickOutgoing(now: now, packets: &packets, results: &results, notes: &notes)
        tickIncoming(now: now, packets: &packets)

        for (id, at) in finished where now.timeIntervalSince(at) > Bulk.incomingIdleTimeout {
            staleFinished.append(id)
        }
        for id in staleFinished { finished.removeValue(forKey: id) }
        lock.unlock()

        for packet in packets { deliver(packet.0, packet.1) }
        for note in notes { onNote?(note) }
        for result in results { announce(result) }
    }

    /// Caller holds `lock`.
    private func tickOutgoing(
        now: Date,
        packets: inout [(Wire.PacketType, [UInt8])],
        results: inout [BulkResult],
        notes: inout [String]
    ) {
        for transfer in Array(outgoing.values) {
            guard transfer.accepted else {
                guard now.timeIntervalSince(transfer.lastOfferAt) >= Bulk.offerInterval else { continue }

                if transfer.offerAttempts >= Bulk.maxOfferAttempts {
                    outgoing.removeValue(forKey: transfer.id)
                    results.append(transfer.result(.noAnswer))
                    notes.append("bulk: ten offers went unanswered")
                    continue
                }

                transfer.offerAttempts += 1
                transfer.lastOfferAt = now
                packets.append((.bulkOffer, BulkCodec.encode(offer: transfer.offer)))
                continue
            }

            if transfer.hasQueued {
                var unreadable = false
                while transfer.hasQueued, pacer.take() {
                    guard let index = transfer.dequeue() else { break }
                    guard let data = transfer.chunk(index) else {
                        unreadable = true
                        break
                    }
                    packets.append((.bulkChunk, BulkCodec.encodeChunk(
                        transferID: transfer.id, index: index, data: data
                    )))
                    transfer.lastSendAt = now
                }
                if unreadable {
                    // The file moved, was replaced, or the volume went away. There
                    // is nothing to retransmit from any more, and going on would
                    // deliver an object that is not the one that was offered.
                    outgoing.removeValue(forKey: transfer.id)
                    results.append(transfer.result(.stalled))
                    notes.append("bulk: the file being sent can no longer be read")
                }
                continue
            }

            // Everything asked for is on the wire. Retransmission is driven by the
            // receiver's ack, so if that ack never comes the transfer can only
            // wait — poke it with the last chunk rather than sit until the timeout.
            if now.timeIntervalSince(transfer.lastAckAt) > Bulk.sendingSilenceTimeout {
                outgoing.removeValue(forKey: transfer.id)
                results.append(transfer.result(.stalled))
                notes.append("bulk: acknowledgements stopped")
                continue
            }

            if now.timeIntervalSince(transfer.lastSendAt) > 0.5, pacer.take() {
                let last = transfer.offer.chunkCount - 1
                if let data = transfer.chunk(last) {
                    packets.append((.bulkChunk, BulkCodec.encodeChunk(
                        transferID: transfer.id, index: last, data: data
                    )))
                }
                transfer.lastSendAt = now
            }
        }
    }

    /// Caller holds `lock`.
    private func tickIncoming(now: Date, packets: inout [(Wire.PacketType, [UInt8])]) {
        for state in Array(incoming.values) {
            if now.timeIntervalSince(state.lastActivity) > Bulk.incomingIdleTimeout {
                // Dropping the last reference is what removes the temporary file:
                // the store cleans up in its own `deinit`, so no path out of a
                // transfer can forget to.
                incoming.removeValue(forKey: state.transferID)
                continue
            }
            guard now.timeIntervalSince(state.lastAckAt) >= Bulk.ackInterval else { continue }
            state.lastAckAt = now
            packets.append((.bulkAck, BulkCodec.encode(ack: state.buildAck(accepted: true))))
        }
    }

    // MARK: - Telemetry

    /// Everything in flight, for a progress bar. Cheap; safe on a UI tick.
    func progress() -> [BulkProgress] {
        lock.lock()
        defer { lock.unlock() }

        let out = outgoing.values.map {
            BulkProgress(
                transferID: $0.id, direction: .outgoing, kind: $0.offer.kind, format: $0.offer.format,
                size: $0.offer.size, chunksDone: $0.chunksSent, chunkCount: $0.offer.chunkCount,
                description: $0.offer.description
            )
        }
        // Objects being hashed are included and sit at the end of the bar. They
        // are the last seconds of a large transfer, and a progress bar that
        // vanishes and comes back reads as a transfer that failed and restarted.
        let inbound = (Array(incoming.values) + Array(finishing.values)).map {
            BulkProgress(
                transferID: $0.transferID, direction: .incoming, kind: $0.kind, format: $0.format,
                size: $0.size, chunksDone: $0.haveCount, chunkCount: $0.chunkCount,
                description: $0.description
            )
        }
        return out + inbound
    }

    // MARK: - Roles

    /// One object we are pushing.
    private final class Outgoing {
        let id: UInt32
        let offer: BulkOffer
        private let body: BulkBody

        /// The chunks the other end named. At most 257 of them, which is what
        /// one acknowledgement can carry.
        private var listed: [UInt32] = []
        private var listedHead = 0

        /// The rest of the object, as a pair of cursors, for when the
        /// acknowledgement ran out of room to name holes.
        ///
        /// Cursors and not a list: at four million chunks, writing out
        /// «everything from here on» as an array would be sixteen megabytes to
        /// say one sentence.
        private var sweepNext: UInt32 = 0
        private var sweepEnd: UInt32 = 0

        /// Chunks put on the wire at least once. What tells a retransmission from
        /// a first attempt, which is the only loss signal this layer has — and a
        /// bitmap rather than a set, because at the format's ceiling a set of
        /// four million numbers is a hundred megabytes to answer a yes-or-no
        /// question.
        private var sent: ChunkMap

        /// Chunks at or below this index have had a full round in which to
        /// arrive, so the other end still asking for one of them means it was
        /// lost rather than that it is still on its way.
        ///
        /// This one line is the difference between a fast link and a link that
        /// talks itself down. An acknowledgement is a snapshot, and everything
        /// put on the wire since it was taken is naturally missing from it — the
        /// faster the link goes the more of it there is. Without the mark that
        /// shows up as loss, and the pacing cuts the speed for it; and the same
        /// chunks get sent a second time for good measure, so the cut is paid
        /// for twice.
        private var settled: Int = -1
        /// What `settled` becomes at the next acknowledgement. Deliberately a
        /// round behind: the mark has to describe the round that has finished,
        /// not the one that is starting.
        private var settledNext: Int = -1
        private var highestSent: Int = -1
        /// Chunks handed to the socket since the previous acknowledgement, which
        /// is what the loss count is a fraction of.
        private var sentSinceAck = 0

        var accepted = false
        var offerAttempts = 0
        var lastOfferAt = Date.distantPast
        var lastAckAt = Date.distantPast
        var lastSendAt = Date.distantPast

        init(id: UInt32, kind: BulkKind, format: BulkFormat, body: BulkBody, hash: [UInt8], description: String) {
            self.id = id
            self.body = body
            self.sent = ChunkMap(count: Int(Bulk.chunkCount(forSize: body.size)))
            self.offer = BulkOffer(
                transferID: id,
                kind: kind,
                format: format,
                size: UInt32(body.size),
                chunkCount: Bulk.chunkCount(forSize: body.size),
                hash: hash,
                description: description
            )
        }

        /// For the progress bar: chunks that have been on the wire at least once.
        /// Counted this way rather than as "packets handed over" so that a round
        /// of retransmission does not make the bar claim more than exists.
        var chunksSent: UInt32 { UInt32(sent.present) }

        var hasQueued: Bool { listedHead < listed.count || sweepNext < sweepEnd }

        /// Back to square one: offer it again and wait to be told what to send.
        func renegotiate() {
            accepted = false
            offerAttempts = 0
            lastOfferAt = .distantPast
            sweepNext = 0
            sweepEnd = 0
            listed.removeAll(keepingCapacity: true)
            listedHead = 0
            sent.removeAll()
            settled = -1
            settledNext = -1
            highestSent = -1
            sentSinceAck = 0
        }

        /// Turns an ack into a send list, and reports what the ack said about the
        /// round that has just gone by.
        ///
        /// A full list means the other end had more holes than it could name.
        /// What it named is still the whole truth about the stretch it covers,
        /// though — from the first hole to the last number in the list there is
        /// nothing else missing — so that stretch is answered exactly, and only
        /// past the last number named is anything guessed at.
        ///
        /// Guessing there means sending what has never been sent, and nothing
        /// else. The obvious reading of «start over from the first missing
        /// chunk» is to re-send the entire tail of the object, and on a large
        /// file that is a disaster: one chunk lost early holds the first-missing
        /// number down, and every 200 ms the whole remainder goes out again. The
        /// chunk that was really lost is named, gets sent, and the transfer
        /// moves on; the rest of the tail is the other end's business to ask
        /// about when its list reaches that far.
        func rebuild(from ack: BulkAck) -> (sent: Int, lost: Int) {
            // The mark moves up by one acknowledgement, and only now: what went
            // out during the round that has just ended is what this
            // acknowledgement cannot be expected to know about yet.
            settled = settledNext
            settledNext = highestSent

            let round = (sent: sentSinceAck, lost: losses(named: ack))
            sentSinceAck = 0

            listed.removeAll(keepingCapacity: true)
            listedHead = 0
            sweepNext = 0
            sweepEnd = 0
            guard ack.firstMissing < offer.chunkCount else { return round }

            listed.append(ack.firstMissing)
            var lastNamed = ack.firstMissing
            for index in ack.missing where index < offer.chunkCount {
                // The list arrives in order, so a repeat is next to its twin.
                // Sending one twice costs a packet and nothing else, but the
                // check is one comparison.
                if index != listed[listed.count - 1] { listed.append(index) }
                lastNamed = max(lastNamed, index)
            }

            if ack.isTruncated, lastNamed + 1 < offer.chunkCount {
                sweepNext = lastNamed + 1
                sweepEnd = offer.chunkCount
            }
            return round
        }

        /// How many of the chunks this ack names were out a full round ago and
        /// are therefore lost rather than in flight. See `settled`.
        private func losses(named ack: BulkAck) -> Int {
            guard ack.firstMissing < offer.chunkCount else { return 0 }
            var count = wasLost(ack.firstMissing) ? 1 : 0
            for index in ack.missing where index != ack.firstMissing {
                if wasLost(index) { count += 1 }
            }
            return count
        }

        private func wasLost(_ index: UInt32) -> Bool {
            index < offer.chunkCount && Int(index) <= settled && sent.contains(Int(index))
        }

        func dequeue() -> UInt32? {
            while true {
                let index: UInt32
                let named: Bool
                if listedHead < listed.count {
                    index = listed[listedHead]
                    listedHead += 1
                    named = true
                } else if sweepNext < sweepEnd {
                    index = sweepNext
                    sweepNext += 1
                    named = false
                } else {
                    return nil
                }

                if sent.contains(Int(index)) {
                    // Past what the acknowledgement could name, nothing is known
                    // about a chunk that has already gone out, so it is left
                    // alone until the other end's list reaches it and says.
                    if !named { continue }
                    // Named, but sent too recently for this acknowledgement to
                    // have seen it: it is in flight, not lost. Skipped for one
                    // round only — the mark moves up at the next acknowledgement,
                    // and if it really was lost it is asked for again and goes
                    // out then. Without this, a link with any latency at all
                    // sends its own in-flight chunks a second time, every round.
                    if Int(index) > settled { continue }
                }

                sent.insert(Int(index))
                highestSent = max(highestSent, Int(index))
                sentSinceAck += 1
                return index
            }
        }

        func chunk(_ index: UInt32) -> ArraySlice<UInt8>? {
            body.chunk(index)
        }

        func result(_ outcome: BulkOutcome) -> BulkResult {
            BulkResult(
                transferID: id, kind: offer.kind, format: offer.format, size: offer.size,
                hash: offer.hash, description: offer.description, outcome: outcome
            )
        }
    }

    /// One object being assembled.
    private final class Incoming {
        let transferID: UInt32
        let kind: BulkKind
        let format: BulkFormat
        let size: UInt32
        let chunkCount: UInt32
        let hash: [UInt8]
        let description: String

        private let sink: BulkStore
        private var have: ChunkMap

        var hashFailures = 0
        var lastActivity = Date.distantPast
        var lastAckAt = Date.distantPast

        init(offer: BulkOffer, storage: BulkStorage) throws {
            transferID = offer.transferID
            kind = offer.kind
            format = offer.format
            size = offer.size
            chunkCount = offer.chunkCount
            hash = offer.hash
            description = offer.description
            have = ChunkMap(count: Int(offer.chunkCount))
            switch storage {
            case .memory:
                sink = MemoryStore(size: Int(offer.size))
            case .file(let directory):
                sink = try FileStore(
                    directory: directory, transferID: offer.transferID, size: Int(offer.size)
                )
            }
        }

        var haveCount: UInt32 { UInt32(have.present) }
        var isComplete: Bool { have.isComplete }

        /// Takes one chunk. A throw means the object cannot be assembled here at
        /// all — the disk said no — and the transfer is given up rather than
        /// retried: the map is left claiming a chunk that is not on disk,
        /// because nothing is going to ask it again.
        func store(index: UInt32, data: [UInt8]) throws {
            guard index < chunkCount else { return }
            // A chunk of the wrong length cannot be part of this object, whatever
            // else it is. Taking it would corrupt the object in a way only the
            // hash would catch.
            guard data.count == Bulk.chunkLength(size: Int(size), index: index) else { return }
            // The bitmap answers «is this new» and marks it in one step, which is
            // what keeps the cost of a duplicate down to nothing — and a
            // duplicate must not be written twice, because on disk the second
            // write would land in the middle of somebody else's flush.
            guard have.insert(Int(index)) else { return }

            try sink.write(data, at: Int(index) * Bulk.chunkSize)
        }

        func digest() throws -> [UInt8] { try sink.digest() }

        func take() throws -> BulkPayload { try sink.take() }

        func forget() {
            have.removeAll()
            sink.restart()
        }

        /// The first hole, then up to 256 more. When nothing is missing the
        /// first-missing field points one past the last chunk — the contract has
        /// no other way to say «ничего не нужно», and the sender reads anything at
        /// or past the count as «жду BULK_DONE».
        func buildAck(accepted: Bool) -> BulkAck {
            let holes = have.missing(limit: Bulk.maxMissingListed + 1)
            return BulkAck(
                transferID: transferID,
                accepted: accepted,
                firstMissing: holes.first.map(UInt32.init) ?? chunkCount,
                missing: holes.dropFirst().map(UInt32.init)
            )
        }

        func delivery(payload: BulkPayload) -> BulkDelivery {
            BulkDelivery(
                transferID: transferID, kind: kind, format: format,
                payload: payload, hash: hash, description: description
            )
        }
    }
}

enum BulkError: Error, CustomStringConvertible {
    case empty
    case tooLarge(limit: Int)
    case noRoom

    var description: String {
        switch self {
        case .empty:
            return L.t("bulk.error.empty")
        case .tooLarge(let limit):
            return L.t("bulk.error.tooLarge", L.sizeLimit(limit))
        case .noRoom:
            return L.t("files.error.noRoom")
        }
    }
}
