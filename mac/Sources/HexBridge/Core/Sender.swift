import CryptoKit
import Foundation
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
        lock.unlock()
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
            lastError = "дескрипторы устройства \(device) не помещаются в пакет (\(payload.count) байт)"
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

        connection.send(content: Data(datagram), completion: .contentProcessed { [weak self] error in
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

    private func receiveLoop() {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.handle(Array(data))
            }
            if error == nil {
                self.receiveLoop()
            }
        }
    }

    private func handle(_ datagram: [UInt8]) {
        guard let (header, payload) = Wire.open(datagram: datagram, key: key, direction: .receiverToSender),
              header.room == room else { return }

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
