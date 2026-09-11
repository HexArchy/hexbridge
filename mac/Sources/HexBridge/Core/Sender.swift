import CryptoKit
import Foundation
import HexBridgeText
import Network

/// Owns the UDP connection to the host (or to the VPS relay) and the packet counters.
final class Sender {
    /// Rebuilt on failure, so not a constant. NWConnection has no way back from
    /// `.failed` — the object is spent and a new one has to take its place.
    private var connection: NWConnection
    private let target: NWEndpoint
    private let queue = DispatchQueue(label: "hexbridge.sender")
    /// Grows to a minute so a host that is switched off overnight costs almost nothing.
    private var retryDelay: TimeInterval = 2
    private var stopped = false
    private let key: SymmetricKey
    private let room: UInt64
    private let session: UInt32
    private let nodeName: String

    // MARK: - The direct path
    //
    // Everything works over the relay alone. But the relay is a hop neither side
    // needs once they can see each other, and with one on the far side of the
    // world it is the largest delay in the chain. So when the relay says where
    // the other end is (PROTOCOL.md, type 14), a second socket is opened straight
    // at it and kept warm alongside the first.
    //
    // Nothing is taken on trust. The direct path is used only after a packet
    // arrives over it that decrypts — a forged introduction buys an attacker a
    // few probe packets aimed at nowhere. The relay link stays open the whole
    // time and takes over again the moment the direct one goes quiet, so this
    // can only make the call shorter, never break it.

    /// The second socket, aimed where the relay said the other end is.
    private var directLink: NWConnection?

    /// What `directLink` was built for, so a repeated introduction is not a rebuild.
    private var directEndpoint: NWEndpoint?

    /// When something last arrived over the direct path and decrypted.
    private var lastDirectAt: Date?

    /// Whether data is going direct. Flipped on by a packet that decrypted, off
    /// by silence.
    private(set) var usingDirect = false

    /// Whether the address we were given turned out to be a relay.
    ///
    /// Only a relay sends the introduction (PROTOCOL.md, type 14), so one
    /// arriving is the answer to a question nothing else here can ask. Latched
    /// rather than sampled: a relay does not stop being one between two of its
    /// own packets.
    private var relayAnnouncedItself = false

    /// How long the direct path may be silent before the relay takes over. Three
    /// missed keepalives: long enough not to flap on one lost packet.
    private static let directGrace: TimeInterval = 3.5

    private var seq: UInt32 = 0
    /// Audio frames get their own counter: `seq` also covers HELLO packets, so
    /// using it for jitter-buffer ordering would look like one lost frame a second.
    private var frameIndex: UInt32 = 0
    /// Same reasoning for HID input reports, and separate from audio so that a
    /// gap in one stream is never blamed on the other — and one counter per
    /// device, because a shared counter would make every switch between two
    /// forwarded devices look like a lost report on both of them.
    private var deviceReportIndex = [UInt32](repeating: 0, count: 4)
    private let lock = NSLock()

    // Counters, read by the stats printer.
    private(set) var packetsSent: UInt64 = 0
    private(set) var bytesSent: UInt64 = 0
    private(set) var lastPongAt: Date?
    private(set) var lastRTTms: Double?
    private(set) var remoteReceived: UInt64 = 0
    private(set) var remoteLost: UInt64 = 0
    private(set) var lastError: String?

    // Device channel counters, deliberately apart from the audio ones: the stats
    // line and the UI both report "packets sent" meaning audio, and a gamepad
    // adding 250 packets a second to that number would make it useless.
    private(set) var deviceReportsSent: UInt64 = 0
    private(set) var deviceOutputsReceived: UInt64 = 0

    /// HAPTIC blocks taken off the socket, before anything decides to play them.
    private(set) var hapticBlocksReceived: UInt64 = 0

    /// Called on the connection queue when the host sends a DEV_OUT. Set it
    /// before `start()`; it is not synchronised.
    var onDeviceOutput: ((UInt8, [UInt8]) -> Void)?

    /// Called on the connection queue for every bulk packet (types 9…12). Set it
    /// before `start()`; it is not synchronised. The transport does not look
    /// inside — reliable delivery is `BulkChannel`'s job, not the socket's.
    var onBulkPacket: ((Wire.PacketType, [UInt8]) -> Void)?

    /// Called on the connection queue when the host confirms a DEV_ATTACH.
    /// Same threading contract as `onDeviceOutput`.
    var onDeviceAck: ((UInt8) -> Void)?

    /// Called on the connection queue for every HAPTIC block. Same threading
    /// contract as `onDeviceOutput`, and a closure for the same reason: the
    /// socket has no business knowing what a voice coil is.
    var onHaptic: ((Wire.Haptics.Block) -> Void)?

    var muted = false

    /// True while what we send is going through a relay rather than straight at
    /// the other machine.
    ///
    /// Asked by the bulk channel, which has to keep its speed under what a relay
    /// carries: a relay drops what exceeds its per-endpoint limit, and a dropped
    /// chunk comes back as a hole and is sent twice. A direct path has no such
    /// limit, so the moment one comes up the answer changes.
    var throughRelay: Bool {
        lock.lock()
        defer { lock.unlock() }
        return relayAnnouncedItself && !usingDirect
    }

    init(target: NWEndpoint, key: SymmetricKey, nodeName: String) {
        self.target = target
        self.key = key
        self.room = Wire.roomID(psk: key)
        self.session = UInt32.random(in: 1...UInt32.max)
        self.nodeName = nodeName

        connection = NWConnection(to: target, using: Self.parameters())
    }

    private static func parameters() -> NWParameters {
        let params = NWParameters.udp
        params.serviceClass = .responsiveData
        return params
    }

    func start() {
        attach()
        connection.start(queue: queue)
        receiveLoop()
    }

    private func attach() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed(let error):
                self.lock.lock()
                self.lastError = error.localizedDescription
                self.lock.unlock()
                // Terminal for this object. Without a replacement the microphone
                // stays dead until the whole agent is restarted — which is exactly
                // what happens when the host is simply switched off for the night.
                self.scheduleRebuild()
            case .waiting(let error):
                // Not terminal: NWConnection retries these itself.
                self.lock.lock()
                self.lastError = error.localizedDescription
                self.lock.unlock()
            case .ready:
                self.lock.lock()
                self.lastError = nil
                self.retryDelay = 2
                self.lock.unlock()
            default:
                break
            }
        }
    }

    private func scheduleRebuild() {
        lock.lock()
        if stopped {
            lock.unlock()
            return
        }
        let delay = retryDelay
        retryDelay = min(retryDelay * 2, 60)
        lock.unlock()

        queue.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let done = self.stopped
            self.lock.unlock()
            if done { return }

            self.connection.cancel()
            self.connection = NWConnection(to: self.target, using: Self.parameters())
            self.attach()
            self.connection.start(queue: self.queue)
            self.receiveLoop()
        }
    }

    func stop() {
        lock.lock()
        stopped = true
        let direct = directLink
        directLink = nil
        lock.unlock()
        direct?.cancel()
        connection.cancel()
    }

    /// Sends one encoded Opus frame, prefixed with its frame index.
    func sendAudio(_ opusPacket: [UInt8]) {
        lock.lock()
        let index = frameIndex
        frameIndex &+= 1
        lock.unlock()

        var payload = [UInt8]()
        payload.reserveCapacity(4 + opusPacket.count)
        payload.appendLE(index)
        payload.append(contentsOf: opusPacket)
        send(type: .audio, flags: muted ? .muted : [], payload: payload)
    }

    /// Announces us to the relay and asks the host for a PONG.
    func sendHello() {
        tendDirectPath()
        var payload = [UInt8]()
        payload.appendLE(UInt64(Date().timeIntervalSince1970 * 1000))
        payload.append(0)  // role: sender
        let name = Array(nodeName.utf8.prefix(64))
        payload.append(UInt8(name.count))
        payload.append(contentsOf: name)
        send(type: .hello, flags: muted ? .muted : [], payload: payload)
    }

    // MARK: - Device channel

    /// Announces one HID device. Repeated once a second by the caller until
    /// DEV_ACK comes back, because a receiver that started late has no other way
    /// to learn the device exists.
    func sendDeviceAttach(device: UInt8, descriptors: [Wire.DeviceChannel.Descriptor]) {
        let payload = Wire.DeviceChannel.attachPayload(device: device, descriptors: descriptors)
        // 24 header + 16 tag. A DualSense with its three feature snapshots needs
        // 663 bytes, well inside the MTU — but an arbitrary HID device can have
        // a far bigger report descriptor, and a silently truncated attach would
        // be much worse than a refused one.
        guard payload.count + Wire.headerSize + Wire.tagSize <= Wire.maxPacket else {
            lock.lock()
            print("hexbridge: descriptors for device \(device) do not fit in one packet"
                + " (\(payload.count) bytes)")
            lastError = L.t("sender.error.descriptorTooBig")
            lock.unlock()
            return
        }
        send(type: .deviceAttach, flags: [], payload: payload)
    }

    func sendDeviceDetach(device: UInt8) {
        send(type: .deviceDetach, flags: [], payload: Wire.DeviceChannel.detachPayload(device: device))
    }

    /// One HID input report. Called up to 250 times a second per device.
    func sendDeviceInput(device: UInt8, report: [UInt8]) {
        // Device numbers are 0…3 by contract; anything else has no counter and
        // no virtual port waiting for it on the other side.
        guard device < UInt8(deviceReportIndex.count) else { return }

        lock.lock()
        let slot = Int(device)
        let index = deviceReportIndex[slot]
        deviceReportIndex[slot] &+= 1
        deviceReportsSent &+= 1
        lock.unlock()

        send(
            type: .deviceInput,
            flags: [],
            payload: Wire.DeviceChannel.inputPayload(device: device, index: index, report: report)
        )
    }

    // MARK: - Reliable channel

    /// One packet of the reliable-delivery layer. `BulkChannel` decides what to
    /// send and when; this only puts the bytes on the socket.
    func sendBulk(type: Wire.PacketType, payload: [UInt8]) {
        send(type: type, flags: [], payload: payload)
    }

    // MARK: - Private

    private func send(type: Wire.PacketType, flags: Wire.Flags, payload: [UInt8]) {
        lock.lock()
        let currentSeq = seq
        seq &+= 1
        lock.unlock()

        let header = Wire.Header(type: type, flags: flags, room: room, session: session, seq: currentSeq)
        guard let datagram = try? Wire.seal(header: header, payload: payload, key: key, direction: .senderToReceiver) else {
            return
        }

        lock.lock()
        let link = usingDirect ? (directLink ?? connection) : connection
        lock.unlock()

        link.send(content: Data(datagram), completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.lock.lock()
            defer { self.lock.unlock() }
            if let error {
                self.lastError = error.localizedDescription
            } else if type == .audio {
                self.packetsSent &+= 1
                self.bytesSent &+= UInt64(datagram.count)
            }
        })
    }

    private func receiveLoop() { receiveLoop(on: connection, direct: false) }

    private func receiveLoop(on link: NWConnection, direct: Bool) {
        link.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.handle(Array(data), direct: direct)
            }
            if error == nil {
                self.receiveLoop(on: link, direct: direct)
            }
        }
    }

    private func handle(_ datagram: [UInt8], direct: Bool) {
        // The introduction is the one packet whose payload is not encrypted — it
        // comes from the relay, which has no key. It is never a reason to trust
        // anything, only a suggestion of where to knock.
        if let header = Wire.Header.decode(datagram[...]), header.type == .peer, header.room == room, !direct {
            lock.lock()
            relayAnnouncedItself = true
            lock.unlock()
            noteCandidate(in: datagram)
            return
        }

        guard let (header, payload) = Wire.open(datagram: datagram, key: key, direction: .receiverToSender),
              header.room == room else { return }

        // Something that decrypted arrived over the direct socket: the path is
        // real, and from here the audio goes that way.
        if direct {
            lock.lock()
            lastDirectAt = Date()
            let firstTime = !usingDirect
            usingDirect = true
            lock.unlock()
            if firstTime { print("hexbridge: " + L.t("relay.direct.up")) }
        }

        switch header.type {
        case .pong where payload.count >= 24:
            handlePong(payload)
        case .deviceOutput:
            guard let (device, report) = Wire.DeviceChannel.decodeOutput(payload) else { return }
            lock.lock()
            deviceOutputsReceived &+= 1
            lock.unlock()
            onDeviceOutput?(device, report)
        case .deviceAck:
            guard let device = Wire.DeviceChannel.decodeAck(payload) else { return }
            onDeviceAck?(device)
        case .haptic:
            guard let block = Wire.Haptics.decode(payload) else { return }
            lock.lock()
            hapticBlocksReceived &+= 1
            lock.unlock()
            onHaptic?(block)
        case .bulkOffer, .bulkChunk, .bulkAck, .bulkDone:
            onBulkPacket?(header.type, payload)
        default:
            return
        }
    }

    /// Opens, or re-aims, the second socket at the address the relay named.
    ///
    /// The payload is `family, address, port` in the clear. Anything that does
    /// not parse, or that names where we are already pointing, is ignored — a
    /// repeated introduction every two seconds must not rebuild the socket.
    private func noteCandidate(in datagram: [UInt8]) {
        let body = Array(datagram[Wire.headerSize...])
        guard body.count >= 3 else { return }

        let width = body[0] == 4 ? 4 : (body[0] == 6 ? 16 : 0)
        guard width > 0, body.count >= 1 + width + 2 else { return }

        let raw = Array(body[1..<(1 + width)])
        let port = UInt16(body[1 + width]) | (UInt16(body[2 + width]) << 8)
        guard port != 0 else { return }

        let text = width == 4
            ? raw.map(String.init).joined(separator: ".")
            : stride(from: 0, to: 16, by: 2)
                .map { String(format: "%x", (UInt16(raw[$0]) << 8) | UInt16(raw[$0 + 1])) }
                .joined(separator: ":")

        guard let host = IPv4Address(text).map({ NWEndpoint.Host.ipv4($0) })
            ?? IPv6Address(text).map({ NWEndpoint.Host.ipv6($0) }),
            let endpointPort = NWEndpoint.Port(rawValue: port) else { return }

        let candidate = NWEndpoint.hostPort(host: host, port: endpointPort)

        lock.lock()
        let known = directEndpoint
        let alreadyStopped = stopped
        lock.unlock()

        guard !alreadyStopped, candidate != known, candidate != target else { return }

        let link = NWConnection(to: candidate, using: Self.parameters())
        link.stateUpdateHandler = { state in
            // A direct path that will not open is the ordinary case, not a
            // fault: the relay exists for exactly that. Nothing is reported.
            if case .failed = state { link.cancel() }
        }

        lock.lock()
        let previous = directLink
        directLink = link
        directEndpoint = candidate
        lock.unlock()

        previous?.cancel()
        link.start(queue: queue)
        receiveLoop(on: link, direct: true)
        print("hexbridge: " + L.t("relay.direct.trying", "\(candidate)"))
    }

    /// Knocks on the direct path, and gives up on it after a silence.
    ///
    /// Called from the same once-a-second keepalive as the relay hello, because
    /// it is the same job: a mapping that is not used closes, and a path nobody
    /// has heard from is not a path.
    private func tendDirectPath() {
        lock.lock()
        let link = directLink
        let silentFor = lastDirectAt.map { Date().timeIntervalSince($0) } ?? .infinity
        let wasUsing = usingDirect
        if wasUsing && silentFor > Self.directGrace { usingDirect = false }
        let dropped = wasUsing && !usingDirect
        lock.unlock()

        if dropped { print("hexbridge: " + L.t("relay.direct.down")) }
        guard let link else { return }

        // Sent over the direct socket specifically, so both ends keep a mapping
        // open even while the audio is still going through the relay.
        var payload = [UInt8]()
        payload.appendLE(UInt64(Date().timeIntervalSince1970 * 1000))
        payload.append(0)
        let name = Array(nodeName.utf8.prefix(64))
        payload.append(UInt8(name.count))
        payload.append(contentsOf: name)

        lock.lock()
        let currentSeq = seq
        seq &+= 1
        lock.unlock()

        let header = Wire.Header(type: .hello, flags: [], room: room, session: session, seq: currentSeq)
        guard let datagram = try? Wire.seal(header: header, payload: payload, key: key, direction: .senderToReceiver) else {
            return
        }
        link.send(content: Data(datagram), completion: .idempotent)
    }

    private func handlePong(_ payload: [UInt8]) {
        let echo: UInt64 = payload.readLE(at: 0)
        let received: UInt64 = payload.readLE(at: 8)
        let lost: UInt64 = payload.readLE(at: 16)
        let now = Date().timeIntervalSince1970 * 1000

        lock.lock()
        lastPongAt = Date()
        lastRTTms = max(0, now - Double(echo))
        remoteReceived = received
        remoteLost = lost
        lock.unlock()
    }

    /// A consistent snapshot for the stats line.
    func snapshot() -> (sent: UInt64, bytes: UInt64, pong: Date?, rtt: Double?, received: UInt64, lost: UInt64, error: String?) {
        lock.lock()
        defer { lock.unlock() }
        return (packetsSent, bytesSent, lastPongAt, lastRTTms, remoteReceived, remoteLost, lastError)
    }

    func deviceSnapshot() -> (sent: UInt64, outputs: UInt64) {
        lock.lock()
        defer { lock.unlock() }
        return (deviceReportsSent, deviceOutputsReceived)
    }
}
