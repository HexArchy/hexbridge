import CryptoKit
import Foundation
import HexBridgeFiles
import HexBridgeText
import Network

/// The fast path for a file: a TCP stream on the data port plus two.
///
/// docs/PROTOCOL.md, «The fast path for files (TCP, data port + 2)». The
/// reliable UDP channel stays exactly as it is — it is what the clipboard uses,
/// and it is what a file falls back to when no connection can be made — but a
/// file that can take a stream takes one. Measured between this Mac and a
/// Windows VM on one virtual network: 2.8 MB/s in 1024-byte pieces, each sealed
/// and handed to the kernel on its own, against 370 MB/s for a plain stream.
///
/// The framing and the sealing are not here: they are in `HexBridgeFiles`, where
/// a test can reach them. What is here is sockets, disk and the two decisions
/// that need both — when to give up on connecting, and what to do with a file
/// that arrived wrong.
enum FileFastPath {

    /// How long a connection may take before the file goes the slow way instead.
    /// The contract's three seconds: long enough for a machine that is there,
    /// short enough that somebody watching a drop does not think it hung.
    static let connectTimeout: TimeInterval = 3

    /// How long the ready byte may take. The contract's two seconds: the answer
    /// is one sealed byte written the moment the opening record opens, so
    /// anything slower than this is a machine that is not going to answer at
    /// all — and waiting longer is time the slow path could have spent
    /// delivering the file.
    static let readyTimeout: TimeInterval = 2

    /// How long one write may sit unacknowledged by the kernel before the
    /// transfer is called off. Not in the contract, and not a guess at the
    /// network: it is the bound that keeps a peer which stops reading from
    /// pinning a thread and a file handle for the rest of the session.
    static let writeTimeout: TimeInterval = 30

    /// Plaintext per record. The contract's ceiling, and the whole point of the
    /// exercise: one write hands over 64 KiB where the block channel hands over
    /// one kilobyte.
    static let recordSize = FileStream.maxRecordPlaintext

    /// The port a file stream is offered on, or nil when the data port is so
    /// high that there is no room above it.
    static func streamPort(dataPort: UInt16) -> UInt16? {
        dataPort < UInt16.max - 1 ? dataPort + 2 : nil
    }

    /// How quickly a file crossed, in the unit the rest of the app writes speeds
    /// in. The number is the whole reason this path exists, so it is in the log
    /// every time rather than behind a verbose flag.
    static func speedText(bytes: Int, seconds: TimeInterval) -> String {
        L.t("unit.mbs", L.number(Double(bytes) / 1_048_576 / max(seconds, 0.001)))
    }
}

/// Where the other machine is, and what to seal with. Built from the config by
/// everything that wants the fast path, so the arithmetic on the port and the
/// derivation of the room live in one place.
struct FileStreamPlan {
    let host: String
    let port: UInt16
    let key: SymmetricKey
    let room: UInt64

    /// nil when the machines are not paired yet, or when the data port leaves no
    /// room for the stream port above it.
    init?(config: Config) {
        guard let key = try? config.symmetricKey(),
              let parts = try? config.endpointParts(),
              let port = FileFastPath.streamPort(dataPort: parts.port) else { return nil }
        self.host = parts.host
        self.port = port
        self.key = key
        self.room = Wire.roomID(psk: key)
    }
}

/// How an attempt at the fast path ended.
enum FileStreamOutcome {
    /// The whole file was written to the stream and the stream was closed.
    ///
    /// There is no acknowledgement on this path and the contract does not have
    /// one: TCP has already delivered every byte in order, and the other side
    /// checks the hash for itself. What this cannot say is whether that check
    /// passed — a file that arrives damaged is deleted there and said so there.
    case sent(seconds: TimeInterval, bytes: Int)
    /// Nothing was connected within the three seconds. The file has not moved,
    /// and the slow path is expected to take it.
    case noConnection(String)
    /// Connected, and then broke. The file has half-arrived and been deleted on
    /// the other side; falling back now would mean sending gigabytes a second
    /// time over the path that just failed, so the caller says so instead.
    case broke(String)
}

// MARK: - What is moving right now

/// The progress bar's view of the fast path.
///
/// The reliable channel answers this question itself, per transfer; a stream has
/// no bookkeeping of its own and needs somewhere to put the two numbers. One
/// slot each way, because one file can be arriving while another leaves and a
/// bar that shows the wrong one is worse than no bar.
///
/// `@unchecked Sendable`: two optionals behind a lock, written from a socket
/// queue and read by the interface at 20 Hz.
final class FileStreamProgress: @unchecked Sendable {

    struct Moving {
        let direction: BulkDirection
        let name: String
        let size: Int
        var moved: Int
    }

    private let lock = NSLock()
    private var outgoing: Moving?
    private var incoming: Moving?

    func note(_ direction: BulkDirection, name: String, size: Int, moved: Int) {
        let moving = Moving(direction: direction, name: name, size: size, moved: moved)
        lock.lock()
        switch direction {
        case .outgoing: outgoing = moving
        case .incoming: incoming = moving
        }
        lock.unlock()
    }

    func clear(_ direction: BulkDirection) {
        lock.lock()
        switch direction {
        case .outgoing: outgoing = nil
        case .incoming: incoming = nil
        }
        lock.unlock()
    }

    func clearAll() {
        lock.lock()
        outgoing = nil
        incoming = nil
        lock.unlock()
    }

    /// Outgoing first: it is the one somebody is standing over, having just
    /// dropped a file.
    func snapshot() -> [Moving] {
        lock.lock()
        defer { lock.unlock() }
        return [outgoing, incoming].compactMap { $0 }
    }
}

// MARK: - Sending

extension FileFastPath {

    /// Puts one file on a stream, start to finish. Blocks; call it off the main
    /// thread.
    ///
    /// The connection comes before the hash on purpose. Both are needed before
    /// the first byte of the file moves — the contract puts the hash in the
    /// opening record — but connecting is instant when it works at all, and
    /// hashing four gigabytes is not: doing it first would mean a minute of
    /// reading a disk to find out that nothing was listening.
    static func send(
        file url: URL,
        named name: String,
        over plan: FileStreamPlan,
        progress: FileStreamProgress? = nil
    ) -> FileStreamOutcome {
        guard let endpointPort = NWEndpoint.Port(rawValue: plan.port) else {
            return .noConnection("port \(plan.port) cannot be used")
        }

        let handle: FileHandle
        let size: Int
        do {
            handle = try FileHandle(forReadingFrom: url)
            size = Int(try handle.seekToEnd())
        } catch {
            return .broke("\(error)")
        }
        defer { try? handle.close() }

        let queue = DispatchQueue(label: "hexbridge.files.stream.out")
        let connection = NWConnection(
            host: NWEndpoint.Host(plan.host), port: endpointPort, using: streamParameters()
        )

        // The state handler is the only place a failure is heard about, and it
        // has to be able to wake the wait below whichever way it goes.
        let ready = DispatchSemaphore(value: 0)
        let box = OutcomeBox()
        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                ready.signal()
            case .failed(let error):
                box.set("\(error)")
                ready.signal()
            case .cancelled:
                box.set("the connection was closed")
                ready.signal()
            default:
                // `.waiting` is the ordinary shape of "nothing is listening
                // there yet": Network retries by itself, and the deadline below
                // is what decides how long that is worth.
                break
            }
        }
        connection.start(queue: queue)

        if ready.wait(timeout: .now() + connectTimeout) == .timedOut {
            connection.cancel()
            return .noConnection("no answer on \(plan.host):\(plan.port) within three seconds")
        }
        if let failure = box.value {
            connection.cancel()
            return .noConnection(failure)
        }

        let started = Date()
        var writer = FileStreamWriter(key: plan.key, room: plan.room)
        var buffer = [UInt8](repeating: 0, count: recordSize)

        func write(_ bytes: [UInt8]) -> String? {
            let done = DispatchSemaphore(value: 0)
            var failure: String?
            connection.send(content: Data(bytes), completion: .contentProcessed { error in
                if let error { failure = "\(error)" }
                done.signal()
            })
            if done.wait(timeout: .now() + writeTimeout) == .timedOut {
                return "the other machine stopped reading"
            }
            return failure
        }

        /// The one record that travels backwards, and the only proof that an
        /// application — not just a kernel, and not a firewall answering on its
        /// behalf — is holding the other end of this connection.
        func readyAnswer() -> Bool {
            let wanted = FileStream.lengthSize + 1 + FileStream.tagSize
            let done = DispatchSemaphore(value: 0)
            var frame: [UInt8] = []
            connection.receive(minimumIncompleteLength: wanted, maximumLength: wanted) {
                data, _, _, _ in
                if let data { frame = [UInt8](data) }
                done.signal()
            }
            guard done.wait(timeout: .now() + readyTimeout) != .timedOut else { return false }
            return FileStream.isReady(frame: frame[...], key: plan.key)
        }

        do {
            let digest = try hash(handle, size: size, into: &buffer)
            var head = writer.prelude()
            head += try writer.opening(FileStream.Opening(
                name: name, size: UInt32(size), hash: digest
            ))
            if let failure = write(head) {
                connection.cancel()
                return .broke(failure)
            }

            // Before a byte of the file: a connection that nobody is reading
            // looks exactly like a healthy one from here, and the slow path
            // would have delivered the file while this one swallowed it.
            guard readyAnswer() else {
                connection.cancel()
                return .noConnection(L.t("files.fast.noAnswer", "\(plan.host)", L.integer(Int(plan.port))))
            }

            var offset = 0
            while offset < size {
                let wanted = min(recordSize, size - offset)
                let got = try PosixFile.read(
                    handle.fileDescriptor, into: &buffer, count: wanted, at: offset
                )
                // Short of what the file said it held. Sending the rest would
                // deliver something that cannot match the hash already sent, so
                // it is stopped here where the reason is still known.
                guard got == wanted else {
                    connection.cancel()
                    return .broke("the file ended earlier than its length said")
                }
                if let failure = write(try writer.data(buffer[0..<got])) {
                    connection.cancel()
                    return .broke(failure)
                }
                offset += got
                progress?.note(.outgoing, name: name, size: size, moved: offset)
            }

            if let failure = write(try writer.end()) {
                connection.cancel()
                return .broke(failure)
            }
        } catch {
            connection.cancel()
            return .broke("\(error)")
        }

        // The close is part of the message. Everything is on the wire by now, so
        // this only makes sure the kernel has been told there is no more coming
        // before the object it belongs to goes away.
        connection.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .idempotent)
        connection.cancel()
        progress?.clear(.outgoing)
        return .sent(seconds: Date().timeIntervalSince(started), bytes: size)
    }

    /// SHA-256 of the whole file, read into a buffer we already hold.
    ///
    /// `pread` rather than `FileHandle.read(upToCount:)` for the reason spelled
    /// out beside `PosixFile`: Foundation's read maps the file, so hashing four
    /// gigabytes leaves most of them resident.
    private static func hash(_ handle: FileHandle, size: Int, into buffer: inout [UInt8]) throws -> [UInt8] {
        var sha = SHA256()
        var offset = 0
        while offset < size {
            let wanted = min(buffer.count, size - offset)
            let got = try PosixFile.read(handle.fileDescriptor, into: &buffer, count: wanted, at: offset)
            guard got > 0 else { break }
            buffer.withUnsafeBytes { raw in
                sha.update(bufferPointer: UnsafeRawBufferPointer(rebasing: raw[0..<got]))
            }
            offset += got
        }
        return Array(sha.finalize())
    }

    /// TCP, and nothing clever on top of it.
    ///
    /// `noDelay` because the last record of a file is usually a short one, and
    /// waiting 40 ms for company that is never coming is 40 ms added to every
    /// transfer.
    fileprivate static func streamParameters() -> NWParameters {
        let options = NWProtocolTCP.Options()
        options.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: options)
        // So the listener can bind again the moment the feature is switched off
        // and on: without it the port spends a minute in TIME_WAIT after the
        // last transfer and the fast path quietly is not there.
        parameters.allowLocalEndpointReuse = true
        // Deliberately below voice and input. A file has nowhere to be by a
        // particular millisecond and the microphone has, and this path is fast
        // enough to saturate a link that both of them share.
        parameters.serviceClass = .background
        return parameters
    }
}

/// One string set from a socket callback and read once it is over.
private final class OutcomeBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: String?

    /// First failure wins: the second one is usually the cancel that the first
    /// one caused.
    func set(_ value: String) {
        lock.lock()
        if stored == nil { stored = value }
        lock.unlock()
    }

    var value: String? {
        lock.lock()
        defer { lock.unlock() }
        return stored
    }
}

// MARK: - Receiving

/// Listens on the data port plus two and takes files off whatever connects.
///
/// Started and stopped with the files feature and never left bound after it: a
/// port held open by a feature that is switched off is a promise the app is not
/// keeping.
///
/// `@unchecked Sendable`: the listener and the live connections are behind
/// `lock`, and every connection does its own work on its own queue.
final class FileStreamListener: @unchecked Sendable {

    /// How many connections may be in progress at once. Not a throughput
    /// decision — it is one file at a time in practice — but a bound on what
    /// somebody who cannot open a single record can make this process hold: four
    /// handles and four buffers, rather than as many as they care to open.
    private static let maxConnections = 4

    private let port: NWEndpoint.Port
    private let key: SymmetricKey
    private let room: UInt64
    /// Where the part-file is written. The folder the file will finally live in,
    /// so the last step is a rename and not a second copy of the whole thing —
    /// the same reason the block channel writes there.
    private let directory: URL
    private let progress: FileStreamProgress

    /// A file that arrived whole and hashed right, still under its temporary
    /// name. Whoever takes it owns it from that moment, exactly as on the other
    /// path.
    private let onArrival: (URL, String, TimeInterval, Int) -> Void
    private let onNote: (String) -> Void

    private let queue = DispatchQueue(label: "hexbridge.files.stream.in")
    private let lock = NSLock()
    private var listener: NWListener?
    private var live: [ObjectIdentifier: Incoming] = [:]

    init(
        port: UInt16,
        key: SymmetricKey,
        room: UInt64,
        directory: URL,
        progress: FileStreamProgress,
        onArrival: @escaping (URL, String, TimeInterval, Int) -> Void,
        onNote: @escaping (String) -> Void
    ) {
        // The port is checked by whoever built the plan; 0 would mean "any free
        // port", which is precisely what the other machine cannot guess.
        self.port = NWEndpoint.Port(rawValue: port) ?? 47704
        self.key = key
        self.room = room
        self.directory = directory
        self.progress = progress
        self.onArrival = onArrival
        self.onNote = onNote
    }

    func start() {
        lock.lock()
        let already = listener != nil
        lock.unlock()
        guard !already else { return }

        let listener: NWListener
        do {
            listener = try NWListener(using: FileFastPath.streamParameters(), on: port)
        } catch {
            // Not fatal to anything: files still cross on the block channel, and
            // saying so is the only way somebody finds out why they are slow.
            onNote(L.t("files.fast.notListening", L.integer(Int(port.rawValue)), "\(error)"))
            return
        }

        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.onNote(L.t("files.fast.ready", L.integer(Int(self.port.rawValue))))
            case .failed(let error):
                self.onNote(L.t("files.fast.notListening", L.integer(Int(self.port.rawValue)), "\(error)"))
                self.stop()
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            // A listener whose owner is gone must refuse rather than leave the
            // connection open in the backlog: the other machine would otherwise
            // wait out its whole deadline against a socket nobody will ever read.
            guard let self else {
                connection.cancel()
                return
            }
            self.accept(connection)
        }

        lock.lock()
        self.listener = listener
        lock.unlock()
        listener.start(queue: queue)
    }

    func stop() {
        lock.lock()
        let listener = self.listener
        let connections = Array(live.values)
        self.listener = nil
        live.removeAll()
        lock.unlock()

        listener?.cancel()
        for connection in connections { connection.abandon() }
        progress.clear(.incoming)
    }

    private func accept(_ connection: NWConnection) {
        lock.lock()
        let room = live.count < Self.maxConnections
        lock.unlock()

        guard room else {
            connection.cancel()
            return
        }

        let incoming = Incoming(
            connection: connection,
            key: key,
            room: self.room,
            directory: directory,
            progress: progress,
            onArrival: onArrival,
            onNote: onNote
        )
        incoming.onFinished = { [weak self] in
            guard let self else { return }
            self.lock.lock()
            self.live.removeValue(forKey: ObjectIdentifier(incoming))
            self.lock.unlock()
        }

        lock.lock()
        live[ObjectIdentifier(incoming)] = incoming
        lock.unlock()

        incoming.start()
    }
}

/// What can go wrong on this side that the framing has no word for.
private enum ArrivalProblem: Error, CustomStringConvertible {
    /// More bytes than the opening record said the file holds. Not a longer
    /// file — the hash could not match — and a good way to fill somebody's disk
    /// by naming a small file, which is why it is stopped at the record rather
    /// than at the hash.
    case longerThanPromised

    var description: String {
        switch self {
        case .longerThanPromised:
            return L.t("files.fast.overlong")
        }
    }
}

/// One connection carrying one file.
///
/// Everything in here happens on `queue`, which is this connection's alone, so
/// nothing is locked and a slow disk holds up nobody else.
private final class Incoming {
    private let connection: NWConnection
    /// Kept for the one record that travels backwards; the reader holds its own.
    private let key: SymmetricKey
    private let directory: URL
    private let progress: FileStreamProgress
    private let onArrival: (URL, String, TimeInterval, Int) -> Void
    private let onNote: (String) -> Void
    private let queue = DispatchQueue(label: "hexbridge.files.stream.arrival", qos: .userInitiated)

    private var reader: FileStreamReader
    private var opening: FileStream.Opening?
    private var handle: FileHandle?
    private var url: URL?
    private var sha = SHA256()
    private var written = 0
    private var startedAt = Date()
    private var closed = false

    var onFinished: (() -> Void)?

    init(
        connection: NWConnection,
        key: SymmetricKey,
        room: UInt64,
        directory: URL,
        progress: FileStreamProgress,
        onArrival: @escaping (URL, String, TimeInterval, Int) -> Void,
        onNote: @escaping (String) -> Void
    ) {
        self.connection = connection
        self.key = key
        self.directory = directory
        self.progress = progress
        self.onArrival = onArrival
        self.onNote = onNote
        self.reader = FileStreamReader(key: key, room: room)
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed(let error):
                self.queue.async { self.give(up: "\(error)") }
            case .cancelled:
                self.queue.async { self.give(up: nil) }
            default:
                break
            }
        }
        connection.start(queue: queue)
        receive()
    }

    /// Called when the feature is switched off underneath a transfer. The
    /// part-file goes with it: nothing will ever ask for the rest of it.
    func abandon() {
        queue.async { self.give(up: nil) }
        connection.cancel()
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: FileFastPath.recordSize) {
            [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty { self.take(data) }
            if isComplete || error != nil {
                self.finish(error: error.map { "\($0)" })
                return
            }
            guard !self.closed else { return }
            self.receive()
        }
    }

    private func take(_ data: Data) {
        guard !closed else { return }
        do {
            for event in try reader.accept(data) {
                switch event {
                case .opening(let opening):
                    try begin(opening)
                case .data(let block):
                    try store(block)
                case .end:
                    break
                }
            }
        } catch {
            // A stream from somebody who does not hold the key fails at the
            // opening record, before `begin` has made a file: that is the whole
            // of «dropped without a byte being written», and it is why the
            // decision lives in the reader rather than here.
            give(up: "\(error)")
            connection.cancel()
        }
    }

    private func begin(_ opening: FileStream.Opening) throws {
        self.opening = opening
        startedAt = Date()
        // Before the file is made, and the moment the opening record has opened:
        // the sender is holding a gigabyte back until it hears this, and it is
        // the only thing that tells it the difference between a machine that is
        // listening and a firewall that answered the handshake on its behalf.
        answerReady()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let temporary = directory.appendingPathComponent(
            "\(Bulk.partialPrefix)stream-\(UUID().uuidString)\(Bulk.partialSuffix)"
        )
        guard FileManager.default.createFile(atPath: temporary.path, contents: nil) else {
            throw BulkError.noRoom
        }
        url = temporary
        handle = try FileHandle(forWritingTo: temporary)
        progress.note(.incoming, name: opening.name, size: Int(opening.size), moved: 0)
    }

    /// The one record that travels backwards. Nothing waits on it here — it is
    /// sent and forgotten; if it never leaves, the sender's own deadline says so
    /// and the file arrives the slow way instead.
    private func answerReady() {
        guard let frame = try? FileStream.readyRecord(key: key) else { return }
        connection.send(content: Data(frame), completion: .contentProcessed { _ in })
    }

    private func store(_ block: [UInt8]) throws {
        guard let handle, let opening else { return }
        // More than was promised is not a longer file, it is a different one:
        // the hash cannot match and the length is what the other side told us to
        // expect. Stopped here so that nothing can be made to fill a disk by
        // naming a small file and sending a large one.
        guard written + block.count <= Int(opening.size) else {
            throw ArrivalProblem.longerThanPromised
        }
        try handle.write(contentsOf: Data(block))
        block.withUnsafeBytes { sha.update(bufferPointer: $0) }
        written += block.count
        progress.note(.incoming, name: opening.name, size: Int(opening.size), moved: written)
    }

    private func finish(error: String?) {
        guard !closed else { return }
        closed = true
        connection.cancel()
        defer { onFinished?() }

        guard let opening, let url else {
            // Nothing was ever opened. Either a stranger knocked, or the
            // connection went away before the opening record — in both cases
            // there is nothing on disk to clean up and nothing worth a line in
            // the log.
            progress.clear(.incoming)
            return
        }

        try? handle?.close()
        handle = nil
        progress.clear(.incoming)

        func discard(_ note: String) {
            try? FileManager.default.removeItem(at: url)
            onNote(note)
        }

        if let error {
            discard(L.t("files.fast.broke", opening.name, error))
            return
        }
        do {
            try reader.finish()
        } catch {
            // The connection closed cleanly in the middle. The bytes that did
            // arrive are a piece of a file, and a piece of a file is not one.
            discard(L.t("files.fast.broke", opening.name, L.t("files.fast.cutShort")))
            return
        }
        guard written == Int(opening.size), Array(sha.finalize()) == opening.hash else {
            // The same answer as the other path gives a hash that does not
            // match: deleted rather than renamed. There is no such thing as
            // silent corruption on either of them.
            discard(L.t("files.fast.damaged", opening.name))
            return
        }

        onArrival(url, opening.name, Date().timeIntervalSince(startedAt), written)
    }

    /// Drops a half-written file. Safe to call more than once.
    private func give(up reason: String?) {
        guard !closed else { return }
        closed = true
        defer { onFinished?() }

        try? handle?.close()
        handle = nil
        progress.clear(.incoming)
        if let url { try? FileManager.default.removeItem(at: url) }
        if let reason, let opening {
            onNote(L.t("files.fast.broke", opening.name, reason))
        }
    }
}
