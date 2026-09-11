import CryptoKit
import Foundation
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

    /// 64 MiB. The channel paces at about 1.5 MB/s, so that is three quarters of a
    /// minute at the top end — long, but a length somebody watching a progress bar
    /// can live with. Both ends hold the whole object in memory while it travels,
    /// which is the real reason there is a ceiling at all.
    static let maxObjectSize = 64 * 1024 * 1024

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

/// An object that arrived whole.
struct BulkDelivery {
    var transferID: UInt32
    var kind: BulkKind
    var format: BulkFormat
    var bytes: [UInt8]
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

/// The reliable-delivery layer from docs/PROTOCOL.md, both roles in one object:
/// it offers objects, it accepts them, and it retransmits what the other side
/// says it is missing.
///
/// It knows nothing about clipboards. A feature hands it bytes, a kind and a
/// format and gets told when they landed; the day file transfer arrives it adds
/// a kind and changes nothing here. Deliberately primitive, as the contract
/// says: a fixed send budget, repeat on the receiver's ack, no congestion
/// control and no reordering.
///
/// Time is a parameter rather than a field read from the clock, so every timeout
/// in here is reachable from a test without waiting for it.
///
/// `@unchecked Sendable`: everything mutable is behind `lock`, and `send` is
/// called from the socket queue and from the feature's timer alike.
final class BulkChannel: @unchecked Sendable {
    private let lock = NSLock()
    private var send: (Wire.PacketType, [UInt8]) -> Void

    private var outgoing: [UInt32: Outgoing] = [:]
    private var incoming: [UInt32: Incoming] = [:]
    /// Ids of transfers already assembled, kept so a lost BULK_DONE can be re-sent.
    private var finished: [UInt32: Date] = [:]

    private var nextID = UInt32.random(in: 1...UInt32.max)
    private var credit: Double = 0
    private var lastTick: Date?

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
    private var deliveryObservers: [Int: (kind: BulkKind, handle: (BulkDelivery) -> Void)] = [:]
    private var nextObserver = 1

    /// Registers an observer for one kind, and returns the token to remove it with.
    ///
    /// The kind is declared rather than filtered for inside the handler, because the
    /// channel needs to know it too: an object of a kind nobody is waiting for must be
    /// turned away at the offer instead of being pulled across and dropped.
    @discardableResult
    func observeDeliveries(of kind: BulkKind, _ handler: @escaping (BulkDelivery) -> Void) -> Int {
        lock.lock()
        defer { lock.unlock() }
        let token = nextObserver
        nextObserver += 1
        deliveryObservers[token] = (kind, handler)
        return token
    }

    /// Whether anything is waiting for this kind. Caller must not hold `lock`.
    private func listening(for kind: BulkKind) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return deliveryObservers.values.contains { $0.kind == kind }
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

    /// Chunks per second the sender may put on the wire. The relay caps a source
    /// at 2000 packets/s and voice plus a gamepad already use 300 of them, so the
    /// default leaves headroom rather than filling the pipe.
    var chunksPerSecond: Double = 1500

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
        credit = 0
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
        finished.removeAll()
        for transfer in outgoing.values { transfer.renegotiate() }
        lock.unlock()
    }

    /// Drops everything in flight. Used when the channel itself is going away.
    func reset() {
        lock.lock()
        outgoing.removeAll()
        incoming.removeAll()
        finished.removeAll()
        credit = 0
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
        lock.unlock()
    }

    // MARK: - Sending

    /// Starts offering an object. Returns its transfer id, or throws with a
    /// reason a person can read. The bytes are held until the receiver confirms
    /// them, as the contract requires — there is nowhere else to retransmit from.
    @discardableResult
    func offer(
        kind: BulkKind,
        format: BulkFormat,
        bytes: [UInt8],
        description: String,
        now: Date = Date()
    ) throws -> UInt32 {
        guard !bytes.isEmpty else { throw BulkError.empty }
        guard bytes.count <= Bulk.maxObjectSize else { throw BulkError.tooLarge(bytes.count) }

        let hash = Array(SHA256.hash(data: bytes))

        lock.lock()
        let id = nextID
        nextID = nextID == UInt32.max ? 1 : nextID + 1
        let transfer = Outgoing(id: id, kind: kind, format: format, bytes: bytes, hash: hash, description: description)
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

        guard offer.size > 0, offer.size <= UInt32(Bulk.maxObjectSize) else { return }
        guard offer.chunkCount == Bulk.chunkCount(forSize: Int(offer.size)) else { return }

        // The whole loop-breaker, and the reason the contract puts a hash in the
        // offer at all: if we already hold these exact bytes, nothing has to cross
        // the wire.
        // Nobody is waiting for this kind — the feature that would take it is switched
        // off. Refusing at the offer costs one packet; accepting costs the whole object,
        // which is then discarded, while the other end watches a transfer it thinks
        // succeeded.
        if !listening(for: offer.kind) {
            deliver(.bulkAck, BulkCodec.encode(ack: BulkAck(
                transferID: offer.transferID, accepted: false, firstMissing: 0, missing: []
            )))
            onNote?("bulk: nothing here is waiting for that, not pulling it")
            return
        }

        if owns?(offer.kind, offer.hash) == true {
            deliver(.bulkAck, BulkCodec.encode(ack: BulkAck(
                transferID: offer.transferID, accepted: false, firstMissing: 0, missing: []
            )))
            onNote?("bulk: the object is already here, not pulling it")
            return
        }

        lock.lock()
        let state: Incoming
        if let existing = incoming[offer.transferID], existing.hash == offer.hash {
            state = existing
        } else {
            state = Incoming(offer: offer)
            incoming[offer.transferID] = state
        }
        state.lastActivity = now
        state.lastAckAt = now
        let ack = BulkCodec.encode(ack: state.buildAck(accepted: true))
        lock.unlock()

        deliver(.bulkAck, ack)
    }

    private func handle(chunkFor transferID: UInt32, index: UInt32, data: [UInt8], now: Date) {
        var packets: [(Wire.PacketType, [UInt8])] = []
        var delivery: BulkDelivery?
        var note: String?

        lock.lock()
        let deliver = send
        if finished[transferID] != nil {
            packets.append((.bulkDone, BulkCodec.encodeDone(transferID: transferID)))
        } else if let state = incoming[transferID] {
            state.lastActivity = now
            state.store(index: index, data: data)

            if state.isComplete {
                if state.verify() {
                    delivery = state.delivery()
                    incoming.removeValue(forKey: transferID)
                    finished[transferID] = now
                    packets.append((.bulkDone, BulkCodec.encodeDone(transferID: transferID)))
                } else {
                    // «Молчаливой порчи не бывает»: every chunk is asked for again.
                    state.hashFailures += 1
                    if state.hashFailures >= maxHashFailures {
                        incoming.removeValue(forKey: transferID)
                        note = "bulk: the object arrived corrupt three times, giving up"
                    } else {
                        note = "bulk: hash mismatch, asking for the object again (attempt \(state.hashFailures))"
                        state.forget()
                        state.lastAckAt = now
                        packets.append((.bulkAck, BulkCodec.encode(ack: state.buildAck(accepted: true))))
                    }
                }
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
            transfer.rebuild(from: ack)
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

        lock.lock()
        let deliver = send
        let elapsed = max(0, lastTick.map { now.timeIntervalSince($0) } ?? 0.02)
        lastTick = now
        // A token bucket rather than «n chunks per tick», so the rate on the wire
        // does not change when the caller's timer does.
        credit = min(credit + elapsed * chunksPerSecond, chunksPerSecond)

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
                while credit >= 1, let index = transfer.dequeue() {
                    credit -= 1
                    packets.append((.bulkChunk, BulkCodec.encodeChunk(
                        transferID: transfer.id, index: index, data: transfer.chunk(index)
                    )))
                    transfer.lastSendAt = now
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

            if now.timeIntervalSince(transfer.lastSendAt) > 0.5, credit >= 1 {
                credit -= 1
                let last = transfer.offer.chunkCount - 1
                packets.append((.bulkChunk, BulkCodec.encodeChunk(
                    transferID: transfer.id, index: last, data: transfer.chunk(last)
                )))
                transfer.lastSendAt = now
            }
        }
    }

    /// Caller holds `lock`.
    private func tickIncoming(now: Date, packets: inout [(Wire.PacketType, [UInt8])]) {
        for state in Array(incoming.values) {
            if now.timeIntervalSince(state.lastActivity) > Bulk.incomingIdleTimeout {
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
        let incomingProgress = incoming.values.map {
            BulkProgress(
                transferID: $0.transferID, direction: .incoming, kind: $0.kind, format: $0.format,
                size: $0.size, chunksDone: $0.haveCount, chunkCount: $0.chunkCount,
                description: $0.description
            )
        }
        return out + incomingProgress
    }

    // MARK: - Roles

    /// One object we are pushing.
    private final class Outgoing {
        let id: UInt32
        let offer: BulkOffer
        private let bytes: [UInt8]
        private var queue: [UInt32] = []
        private var queueHead = 0
        private var queued: Set<UInt32> = []

        var accepted = false
        var offerAttempts = 0
        var lastOfferAt = Date.distantPast
        var lastAckAt = Date.distantPast
        var lastSendAt = Date.distantPast
        /// Chunks handed to the socket so far, for the progress bar only.
        var chunksSent: UInt32 = 0

        init(id: UInt32, kind: BulkKind, format: BulkFormat, bytes: [UInt8], hash: [UInt8], description: String) {
            self.id = id
            self.bytes = bytes
            self.offer = BulkOffer(
                transferID: id,
                kind: kind,
                format: format,
                size: UInt32(bytes.count),
                chunkCount: Bulk.chunkCount(forSize: bytes.count),
                hash: hash,
                description: description
            )
        }

        var hasQueued: Bool { queueHead < queue.count }

        /// Back to square one: offer it again and wait to be told what to send.
        func renegotiate() {
            accepted = false
            offerAttempts = 0
            lastOfferAt = .distantPast
            chunksSent = 0
            queue.removeAll(keepingCapacity: true)
            queueHead = 0
            queued.removeAll(keepingCapacity: true)
        }

        /// Turns an ack into a send list. A full list means the receiver had more
        /// holes than it could name, and the contract's answer to that is to start
        /// over from the first one rather than to guess at the rest.
        func rebuild(from ack: BulkAck) {
            queue.removeAll(keepingCapacity: true)
            queueHead = 0
            queued.removeAll(keepingCapacity: true)
            guard ack.firstMissing < offer.chunkCount else { return }

            if ack.isTruncated {
                for index in ack.firstMissing..<offer.chunkCount { enqueue(index) }
                return
            }

            enqueue(ack.firstMissing)
            for index in ack.missing where index < offer.chunkCount { enqueue(index) }
        }

        private func enqueue(_ index: UInt32) {
            if queued.insert(index).inserted { queue.append(index) }
        }

        func dequeue() -> UInt32? {
            guard queueHead < queue.count else { return nil }
            let index = queue[queueHead]
            queueHead += 1
            queued.remove(index)
            if chunksSent < offer.chunkCount { chunksSent += 1 }
            return index
        }

        func chunk(_ index: UInt32) -> ArraySlice<UInt8> {
            let start = Int(index) * Bulk.chunkSize
            return bytes[start..<(start + Bulk.chunkLength(size: bytes.count, index: index))]
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

        private var buffer: [UInt8]
        private var have: [Bool]

        private(set) var haveCount: UInt32 = 0
        var hashFailures = 0
        var lastActivity = Date.distantPast
        var lastAckAt = Date.distantPast

        init(offer: BulkOffer) {
            transferID = offer.transferID
            kind = offer.kind
            format = offer.format
            size = offer.size
            chunkCount = offer.chunkCount
            hash = offer.hash
            description = offer.description
            buffer = [UInt8](repeating: 0, count: Int(offer.size))
            have = [Bool](repeating: false, count: Int(offer.chunkCount))
        }

        var isComplete: Bool { haveCount == chunkCount }

        func store(index: UInt32, data: [UInt8]) {
            guard index < chunkCount else { return }
            // A chunk of the wrong length cannot be part of this object, whatever
            // else it is. Taking it would corrupt the buffer in a way only the
            // hash would catch.
            guard data.count == Bulk.chunkLength(size: Int(size), index: index) else { return }
            guard !have[Int(index)] else { return }

            let start = Int(index) * Bulk.chunkSize
            buffer.replaceSubrange(start..<(start + data.count), with: data)
            have[Int(index)] = true
            haveCount += 1
        }

        func verify() -> Bool { Array(SHA256.hash(data: buffer)) == hash }

        func forget() {
            for i in have.indices { have[i] = false }
            haveCount = 0
        }

        /// The first hole, then up to 256 more. When nothing is missing the
        /// first-missing field points one past the last chunk — the contract has
        /// no other way to say «ничего не нужно», and the sender reads anything at
        /// or past the count as «жду BULK_DONE».
        func buildAck(accepted: Bool) -> BulkAck {
            var first = chunkCount
            var listed = [UInt32]()
            listed.reserveCapacity(Bulk.maxMissingListed)

            for i in 0..<chunkCount where !have[Int(i)] {
                if first == chunkCount {
                    first = i
                    continue
                }
                if listed.count == Bulk.maxMissingListed { break }
                listed.append(i)
            }

            return BulkAck(transferID: transferID, accepted: accepted, firstMissing: first, missing: listed)
        }

        func delivery() -> BulkDelivery {
            BulkDelivery(
                transferID: transferID, kind: kind, format: format,
                bytes: buffer, hash: hash, description: description
            )
        }
    }
}

enum BulkError: Error, CustomStringConvertible {
    case empty
    case tooLarge(Int)

    var description: String {
        switch self {
        case .empty:
            return L.t("bulk.error.empty")
        case .tooLarge:
            return L.t("bulk.error.tooLarge")
        }
    }
}
