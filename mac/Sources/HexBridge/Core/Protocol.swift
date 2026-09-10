import CryptoKit
import Foundation

/// Wire format shared with the Windows receiver. See docs/PROTOCOL.md.
enum Wire {
    static let magic: [UInt8] = [0x4D, 0x42, 0x47, 0x31]  // "MBG1"
    static let version: UInt8 = 1
    static let headerSize = 24
    static let tagSize = 16
    static let maxPacket = 1400

    enum PacketType: UInt8 {
        case audio = 1
        case hello = 2
        case pong = 3
        case deviceAttach = 4
        case deviceDetach = 5
        case deviceInput = 6
        case deviceOutput = 7
    }

    enum Direction: UInt32 {
        case senderToReceiver = 0
        case receiverToSender = 1
    }

    struct Flags: OptionSet {
        let rawValue: UInt16
        static let muted = Flags(rawValue: 1 << 0)
        static let dtxGap = Flags(rawValue: 1 << 1)
    }

    struct Header {
        var type: PacketType
        var flags: Flags
        var room: UInt64
        var session: UInt32
        var seq: UInt32

        func encoded() -> [UInt8] {
            var out = [UInt8]()
            out.reserveCapacity(headerSize)
            out.append(contentsOf: magic)
            out.append(version)
            out.append(type.rawValue)
            out.appendLE(flags.rawValue)
            out.appendLE(room)
            out.appendLE(session)
            out.appendLE(seq)
            return out
        }

        static func decode(_ bytes: ArraySlice<UInt8>) -> Header? {
            guard bytes.count >= headerSize else { return nil }
            let b = Array(bytes.prefix(headerSize))
            guard Array(b[0..<4]) == magic, b[4] == version else { return nil }
            guard let type = PacketType(rawValue: b[5]) else { return nil }
            return Header(
                type: type,
                flags: Flags(rawValue: b.readLE(at: 6)),
                room: b.readLE(at: 8),
                session: b.readLE(at: 16),
                seq: b.readLE(at: 20)
            )
        }
    }

    /// Payloads of the device (HID) channel — the gamepad passthrough.
    ///
    /// The split of responsibility is that the Mac ships raw reports and raw
    /// descriptors and nothing else: assembling a virtual USB device is entirely
    /// the receiver's job, because macOS will never let a userspace process claim
    /// the HID interface it would need to do it here.
    enum DeviceChannel {
        enum DescriptorKind: UInt8 {
            case device = 1
            case configuration = 2
            case hidReport = 3
        }

        struct Descriptor {
            let kind: DescriptorKind
            let bytes: [UInt8]
        }

        /// Everything the receiver needs to build the virtual device. VID, PID
        /// and bcdDevice are read out of the device descriptor rather than sent
        /// separately, so the two can never disagree.
        static func attachPayload(device: UInt8, descriptors: [Descriptor]) -> [UInt8] {
            var payload = [UInt8]()
            payload.append(device)
            payload.append(UInt8(descriptors.count))
            for descriptor in descriptors {
                payload.append(descriptor.kind.rawValue)
                payload.appendLE(UInt16(descriptor.bytes.count))
                payload.append(contentsOf: descriptor.bytes)
            }
            return payload
        }

        static func detachPayload(device: UInt8) -> [UInt8] {
            [device]
        }

        /// The input report verbatim, report id included, with a counter of its
        /// own so the receiver can see gaps without confusing them with audio.
        static func inputPayload(device: UInt8, index: UInt32, report: [UInt8]) -> [UInt8] {
            var payload = [UInt8]()
            payload.reserveCapacity(5 + report.count)
            payload.append(device)
            payload.appendLE(index)
            payload.append(contentsOf: report)
            return payload
        }

        static func decodeOutput(_ payload: [UInt8]) -> (device: UInt8, report: [UInt8])? {
            guard payload.count >= 2 else { return nil }
            return (payload[0], Array(payload.dropFirst()))
        }
    }

    /// Room ids let the relay pair two endpoints without ever holding the PSK.
    static func roomID(psk: SymmetricKey) -> UInt64 {
        var hasher = SHA256()
        hasher.update(data: Data("hexbridge-room-v1".utf8))
        psk.withUnsafeBytes { hasher.update(bufferPointer: $0) }
        let digest = Array(hasher.finalize())
        return digest.readLE(at: 0)
    }

    static func nonce(session: UInt32, seq: UInt32, direction: Direction) -> AES.GCM.Nonce {
        var raw = [UInt8]()
        raw.reserveCapacity(12)
        raw.appendLE(session)
        raw.appendLE(seq)
        raw.appendLE(direction.rawValue)
        // A 12-byte buffer is always a valid GCM nonce.
        return try! AES.GCM.Nonce(data: raw)
    }

    /// Builds a complete `header || ciphertext || tag` datagram.
    static func seal(header: Header, payload: [UInt8], key: SymmetricKey, direction: Direction) throws -> [UInt8] {
        let headerBytes = header.encoded()
        let sealed = try AES.GCM.seal(
            payload,
            using: key,
            nonce: nonce(session: header.session, seq: header.seq, direction: direction),
            authenticating: headerBytes
        )
        return headerBytes + Array(sealed.ciphertext) + Array(sealed.tag)
    }

    static func open(datagram: [UInt8], key: SymmetricKey, direction: Direction) -> (Header, [UInt8])? {
        guard datagram.count >= headerSize + tagSize,
              let header = Header.decode(datagram[...]) else { return nil }

        let headerBytes = Array(datagram[0..<headerSize])
        let cipher = Array(datagram[headerSize..<(datagram.count - tagSize)])
        let tag = Array(datagram[(datagram.count - tagSize)...])

        guard let box = try? AES.GCM.SealedBox(
            nonce: nonce(session: header.session, seq: header.seq, direction: direction),
            ciphertext: cipher,
            tag: tag
        ),
        let plain = try? AES.GCM.open(box, using: key, authenticating: headerBytes) else {
            return nil
        }
        return (header, Array(plain))
    }
}

// MARK: - Little-endian helpers

extension Array where Element == UInt8 {
    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        for byte in 0..<MemoryLayout<T>.size {
            append(UInt8(truncatingIfNeeded: value >> (8 * byte)))
        }
    }

    func readLE<T: FixedWidthInteger>(at offset: Int) -> T {
        var value: T = 0
        for byte in stride(from: MemoryLayout<T>.size - 1, through: 0, by: -1) {
            value = (value << 8) | T(truncatingIfNeeded: self[offset + byte])
        }
        return value
    }
}
