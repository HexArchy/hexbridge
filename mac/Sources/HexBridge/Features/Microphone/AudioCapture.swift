import AVFoundation
import CoreAudio
import Foundation

enum CaptureError: Error, CustomStringConvertible {
    case permissionDenied
    case deviceNotFound(String)
    case selectDevice(OSStatus)
    case noInputFormat
    case converter
    case engine(String)

    var description: String {
        switch self {
        case .permissionDenied:
            return "доступ к микрофону не выдан (Системные настройки → Конфиденциальность → Микрофон)"
        case .deviceNotFound(let s):
            return "устройство ввода не найдено: \(s)"
        case .selectDevice(let status):
            return "не удалось выбрать устройство ввода: OSStatus \(status)"
        case .noInputFormat:
            return "у устройства ввода нет пригодного формата"
        case .converter:
            return "не удалось создать конвертер в 48 кГц моно"
        case .engine(let msg):
            return "AVAudioEngine: \(msg)"
        }
    }
}

/// Captures the selected input device and emits 20 ms mono 48 kHz float frames.
final class AudioCapture {
    /// Called on the audio thread with exactly `VoiceEncoder.frameSamples` samples.
    typealias FrameHandler = (UnsafePointer<Float>, Float) -> Void

    private let engine = AVAudioEngine()
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: Double(VoiceEncoder.sampleRate),
        channels: 1,
        interleaved: false
    )!

    private var sinkNode: AVAudioSinkNode?
    private var converter: AVAudioConverter?
    private var converterInputFormat: AVAudioFormat?
    private var pending = [Float]()
    private let onFrame: FrameHandler
    /// Read on the audio thread, written from the UI. A plain 4-byte store is
    /// atomic on every architecture we ship, and a stale value for one 20 ms
    /// frame is inaudible, so this deliberately skips a lock the audio thread
    /// would otherwise have to take 50 times a second.
    var gain: Float

    private(set) var deviceName = "(default)"
    /// Format the device actually hands us, filled in by `start`. Useful in diagnostics.
    private(set) var inputFormatDescription = "(unknown)"

    init(gain: Float, onFrame: @escaping FrameHandler) {
        self.gain = gain
        self.onFrame = onFrame
        pending.reserveCapacity(VoiceEncoder.frameSamples * 4)
    }

    /// Blocks until the user answers the microphone prompt.
    private static let requestLock = NSLock()
    private static var requestInFlight = false

    static func requestPermission() -> Bool {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            return true
        case .notDetermined:
            // The retry loop calls this repeatedly. macOS keeps the first prompt on
            // screen, so asking again would only queue duplicates behind it.
            requestLock.lock()
            if requestInFlight {
                requestLock.unlock()
                return false
            }
            requestInFlight = true
            requestLock.unlock()
            defer {
                requestLock.lock()
                requestInFlight = false
                requestLock.unlock()
            }

            let semaphore = DispatchSemaphore(value: 0)
            var granted = false
            AVCaptureDevice.requestAccess(for: .audio) { ok in
                granted = ok
                semaphore.signal()
            }
            // Bounded on purpose. Started by launchd there may be nobody to show the
            // prompt to, and an unbounded wait then wedges the whole pipeline: the UI
            // sits on "запускается" forever and the log stays silent, which is exactly
            // the failure this bound exists to turn into a legible one.
            if semaphore.wait(timeout: .now() + 20) == .timedOut {
                print("hexbridge: запрос доступа к микрофону остался без ответа — "
                    + "разрешите доступ в Системных настройках → Конфиденциальность → Микрофон")
                return false
            }
            return granted
        default:
            print("hexbridge: доступ к микрофону запрещён (статус \(status.rawValue))")
            return false
        }
    }

    func start(deviceSelector: String?) throws {
        guard Self.requestPermission() else { throw CaptureError.permissionDenied }

        let device: AudioDevice?
        if let deviceSelector {
            guard let found = AudioDevices.find(selector: deviceSelector) else {
                throw CaptureError.deviceNotFound(deviceSelector)
            }
            device = found
        } else {
            device = AudioDevices.defaultInputDevice()
        }

        // The device has to be bound to the AUHAL before we read its format.
        if let device {
            deviceName = device.name
            if let unit = engine.inputNode.audioUnit {
                var id = device.id
                let status = AudioUnitSetProperty(
                    unit,
                    kAudioOutputUnitProperty_CurrentDevice,
                    kAudioUnitScope_Global,
                    0,
                    &id,
                    UInt32(MemoryLayout<AudioDeviceID>.size)
                )
                guard status == noErr else { throw CaptureError.selectDevice(status) }
            }
        }

        let inputFormat = engine.inputNode.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
            throw CaptureError.noInputFormat
        }

        inputFormatDescription = "\(Int(inputFormat.sampleRate)) Гц, \(inputFormat.channelCount) ch, " +
            "\(inputFormat.isInterleaved ? "interleaved" : "planar"), common=\(inputFormat.commonFormat.rawValue)"

        // A tap on the input node alone does not make the engine render on macOS —
        // the graph needs a real destination. An explicit sink node is that
        // destination, and it hands us the raw buffers directly.
        let sink = AVAudioSinkNode { [weak self] _, frameCount, audioBufferList in
            self?.process(audioBufferList, frameCount: frameCount, format: inputFormat)
            return noErr
        }
        engine.attach(sink)
        engine.connect(engine.inputNode, to: sink, format: inputFormat)
        sinkNode = sink

        engine.prepare()
        do {
            try engine.start()
        } catch {
            throw CaptureError.engine(error.localizedDescription)
        }
    }

    func stop() {
        engine.stop()
        if let sinkNode {
            engine.detach(sinkNode)
            self.sinkNode = nil
        }
    }

    // MARK: - Private

    private func process(
        _ audioBufferList: UnsafePointer<AudioBufferList>,
        frameCount: AVAudioFrameCount,
        format: AVAudioFormat
    ) {
        guard frameCount > 0,
              let buffer = AVAudioPCMBuffer(
                  pcmFormat: format,
                  bufferListNoCopy: audioBufferList
              ) else { return }

        buffer.frameLength = frameCount
        process(buffer)
    }

    private func process(_ buffer: AVAudioPCMBuffer) {
        guard let mono = convert(buffer), mono.frameLength > 0,
              let channel = mono.floatChannelData?[0] else { return }

        let count = Int(mono.frameLength)
        if gain == 1 {
            pending.append(contentsOf: UnsafeBufferPointer(start: channel, count: count))
        } else {
            for i in 0..<count {
                pending.append(channel[i] * gain)
            }
        }

        let frameSamples = VoiceEncoder.frameSamples
        var offset = 0
        while pending.count - offset >= frameSamples {
            pending.withUnsafeBufferPointer { buf in
                let base = buf.baseAddress! + offset
                var peak: Float = 0
                for i in 0..<frameSamples {
                    peak = max(peak, abs(base[i]))
                }
                onFrame(base, peak)
            }
            offset += frameSamples
        }
        if offset > 0 {
            pending.removeFirst(offset)
        }
    }

    private func convert(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        let inputFormat = buffer.format

        // Devices can be swapped or reconfigured underneath us; rebuild on change.
        if converter == nil || converterInputFormat != inputFormat {
            converter = AVAudioConverter(from: inputFormat, to: targetFormat)
            converterInputFormat = inputFormat
            pending.removeAll(keepingCapacity: true)
        }
        guard let converter else { return nil }

        if inputFormat == targetFormat {
            return buffer
        }

        let ratio = targetFormat.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return nil }

        var supplied = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, outStatus in
            if supplied {
                outStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard status != .error else { return nil }
        return output
    }
}
