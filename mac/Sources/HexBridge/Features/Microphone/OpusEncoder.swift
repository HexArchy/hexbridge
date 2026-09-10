import COpusShim
import Foundation

struct OpusError: Error, CustomStringConvertible {
    let stage: String
    let code: Int32

    var description: String {
        "\(stage) failed: \(String(cString: mb_opus_strerror(code)))"
    }
}

/// Mono 48 kHz voice encoder producing one packet per 20 ms frame.
final class VoiceEncoder {
    static let sampleRate = 48000
    static let frameSamples = 960  // 20 ms
    /// Opus never emits more than this for a single frame.
    private static let maxPacketBytes = 1275

    private let encoder: OpaquePointer
    private var out = [UInt8](repeating: 0, count: maxPacketBytes)

    init(bitrate: Int32, complexity: Int32, fec: Bool, expectedLossPercent: Int32) throws {
        var error: Int32 = 0
        guard let handle = mb_encoder_create(
            Int32(Self.sampleRate), bitrate, complexity, fec ? 1 : 0, expectedLossPercent, &error
        ) else {
            throw OpusError(stage: "opus_encoder_create", code: error)
        }
        encoder = handle
    }

    deinit {
        mb_encoder_destroy(encoder)
    }

    /// Encodes exactly `frameSamples` mono float samples into one Opus packet.
    func encode(_ pcm: UnsafePointer<Float>) throws -> [UInt8] {
        let written = out.withUnsafeMutableBufferPointer { buf in
            mb_encoder_encode_float(encoder, pcm, Int32(Self.frameSamples), buf.baseAddress, Int32(buf.count))
        }
        guard written > 0 else { throw OpusError(stage: "opus_encode_float", code: written) }
        return Array(out[0..<Int(written)])
    }
}
