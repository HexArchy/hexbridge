import AudioToolbox
import CoreAudio
import Foundation
import os

/// Plays the PCM that arrives on the haptics channel into the controller itself.
///
/// The whole feature rests on one fact about a wired DualSense: macOS enumerates
/// it as an ordinary CoreAudio output device with four channels at 48 kHz. So
/// the return path needs no entitlement, no raw USB and no driver — it is an
/// audio unit writing to a device, the same as playing a sound file.
///
/// Channels 0 and 1 are the speaker and the headset jack. Channels 2 and 3 are
/// the voice coils in the grips, and they are the only two written here: the
/// other pair belongs to whatever the user is actually listening to, and putting
/// a game's rumble through a speaker held in both hands would be a surprise
/// nobody asked for. Verified against the hardware — a 40 Hz tone on 2/3 moves
/// the controller's own gyroscope forty times as much as the same tone on 0/1,
/// which is the difference between a voice coil and a piezo speaker.
///
/// One thing is not obvious and cost an afternoon to find: the audio path is
/// **muted until an output report unmutes it**. A DualSense boots with the
/// haptic mute bit set, the PS5 clears it, and nothing on a PC does. So the
/// player asks its owner to send `DualSenseReport.audioHapticsEnable` when the
/// stream starts and once a second while it runs. Without that the PCM is
/// carried the whole way, accepted by the controller and silently discarded.
final class HapticPlayer {
    /// Nothing plays until the ring holds this much. Blocks are 5 ms and the
    /// network delivers them in bursts, so starting on the first one would mean
    /// an underrun inside the first 5 ms every single time.
    private static let prerollFrames = 48000 * 20 / 1000

    /// The ceiling on latency. Past this the oldest frames are thrown away to
    /// make room for the newest, because a haptic effect 100 ms behind the
    /// picture is not a weaker effect, it is the wrong one.
    private static let maxBufferedFrames = 48000 * 100 / 1000

    /// A gap longer than this is a silent passage the sender chose not to send,
    /// not a loss. Filling it with zeros would be the same silence at the cost
    /// of the arithmetic, so the ring is simply allowed to run dry.
    private static let maxGapBlocks: UInt32 = 4

    /// How long after the last block the audio unit is torn down. The device is
    /// shared, so holding it open costs nothing but the CPU of a render callback
    /// producing zeros — which is exactly what a couple of seconds is worth to
    /// avoid restarting on every pause in a game.
    private static let idleTimeout: TimeInterval = 2

    /// What the screen can honestly say about the haptic path.
    struct Status {
        var deviceName: String?
        /// The audio unit is running: the device is open and being fed.
        var playing = false
        var blocksPlayed: UInt64 = 0
        /// Blocks the numbering says were lost in flight, silence filled in.
        var blocksLost: UInt64 = 0
        /// Times the render callback found the ring dry with a stream running.
        var underruns: UInt64 = 0
        /// Blocks that arrived with the ring already a tenth of a second deep,
        /// so older audio was discarded to keep the newest. Non-zero means the
        /// link is delivering faster than the controller consumes, which after
        /// the first moments of a stream means it is delivering in bursts.
        var blocksDropped: UInt64 = 0
        var lastError: String?
    }

    /// One matching CoreAudio device.
    struct Output {
        let id: AudioDeviceID
        let name: String
        let channels: Int
    }

    private let vendorID: Int
    private let productID: Int
    private let locationID: Int

    /// Held by the render callback and by the socket queue, and by nothing else.
    /// An unfair lock rather than `NSLock` because one of those two is a
    /// real-time thread: it donates priority instead of inverting it, and the
    /// critical section is a memcpy of a few hundred floats.
    private let ring = OSAllocatedUnfairLock()
    private var samples: [Float]
    private var readIndex = 0
    private var writeIndex = 0
    private var buffered = 0
    /// True until enough has arrived to start. Set again after every dry spell.
    private var filling = true
    /// False until the render callback has run once for this stream.
    private var warmedUp = false

    /// Everything below is written on the owner's thread and read on it too;
    /// the audio unit is only ever touched from `start`/`stop`.
    private var unit: AudioUnit?
    private var outputChannels = 4
    private var firstHapticChannel = 2
    private var deviceName: String?
    private var lastBlockAt: DispatchTime?
    private var lastIndex: UInt32?
    private var lastError: String?

    private let counters = OSAllocatedUnfairLock()
    private var blocksPlayed: UInt64 = 0
    private var blocksLost: UInt64 = 0
    private var underruns: UInt64 = 0
    private var blocksDropped: UInt64 = 0

    init(vendorID: Int, productID: Int, locationID: Int) {
        self.vendorID = vendorID
        self.productID = productID
        self.locationID = locationID
        samples = [Float](repeating: 0, count: Self.ringFrames * 2)
    }

    /// Frames the ring holds. A power of two so the wrap is a mask, and deep
    /// enough that the drop path is reached only by a genuinely stuck consumer.
    private static let ringFrames = 16384

    deinit {
        teardown()
    }

    // MARK: - What the bridge calls

    /// One decoded block off the socket. Called on the connection queue at up to
    /// 200 blocks a second while a game is playing, and never while it is not.
    ///
    /// Returns true when the caller should (re)assert the unmute report: on the
    /// first block of a stream, because the controller boots muted.
    @discardableResult
    func play(_ block: Wire.Haptics.Block) -> Bool {
        guard block.channels >= 2, block.frames > 0 else { return false }

        let started = unit == nil
        if started, !start() { return false }

        lastBlockAt = DispatchTime.now()
        fillGap(before: block.index)
        lastIndex = block.index

        // Only the last two channels are the actuators, whatever the sender put
        // in front of them. A two-channel block is already the pair; a
        // four-channel one has the speaker in front of it.
        let stride = block.channels
        let first = stride - 2

        ring.lock()
        var late = false
        for frame in 0..<block.frames {
            let base = frame * stride + first
            if !push(Float(block.samples[base]) / 32768, Float(block.samples[base + 1]) / 32768) {
                late = true
            }
        }
        if buffered >= Self.prerollFrames { filling = false }
        // Overrunning before the audio unit has rendered once is not lateness,
        // it is CoreAudio still opening the device — and the first render
        // callback trims the backlog away. Counting it would put a number on the
        // screen that says "the link is bursting" every time a stream starts.
        let consuming = warmedUp
        ring.unlock()

        counters.lock()
        blocksPlayed &+= 1
        if late, consuming { blocksDropped &+= 1 }
        counters.unlock()

        return started
    }

    /// Called once a second by the bridge. Returns true when the stream is live
    /// and the unmute report is worth re-asserting — a game's own output report
    /// can select classic rumble and take the audio path away from us, and one
    /// HID write a second is cheap insurance against that.
    @discardableResult
    func tick() -> Bool {
        guard unit != nil else { return false }
        guard let lastBlockAt else { return false }

        let idle = Double(DispatchTime.now().uptimeNanoseconds &- lastBlockAt.uptimeNanoseconds) / 1e9
        if idle > Self.idleTimeout {
            stop()
            return false
        }
        return true
    }

    /// Lets the device go. Safe to call when nothing was ever started.
    func stop() {
        teardown()
        lastIndex = nil
        lastBlockAt = nil
        ring.lock()
        readIndex = 0
        writeIndex = 0
        buffered = 0
        filling = true
        warmedUp = false
        ring.unlock()
    }

    func status() -> Status {
        counters.lock()
        defer { counters.unlock() }
        return Status(
            deviceName: deviceName,
            playing: unit != nil,
            blocksPlayed: blocksPlayed,
            blocksLost: blocksLost,
            underruns: underruns,
            blocksDropped: blocksDropped,
            lastError: lastError
        )
    }

    /// Whether this controller has a CoreAudio device at all. Read before any
    /// block arrives, so the UI can say "there is nowhere to play this" instead
    /// of staying silent about it.
    func findOutput() -> Output? {
        Self.find(vendorID: vendorID, productID: productID, locationID: locationID)
    }

    // MARK: - Loss

    /// Silence for the blocks that never arrived.
    ///
    /// Only for a short gap. The sender skips runs of silence on purpose, so a
    /// long gap is not loss — it is nothing happening, and the ring drains to
    /// nothing on its own. A short one is a block that really was lost, and
    /// filling it keeps everything after it in the right place.
    private func fillGap(before index: UInt32) {
        guard let lastIndex, index > lastIndex &+ 1 else { return }
        let missing = index &- lastIndex &- 1
        guard missing <= Self.maxGapBlocks else { return }

        counters.lock()
        blocksLost &+= UInt64(missing)
        counters.unlock()

        ring.lock()
        for _ in 0..<(Int(missing) * Wire.Haptics.framesPerBlock) { push(0, 0) }
        ring.unlock()
    }

    /// Appends one frame, discarding the oldest if the ring is already at its
    /// latency ceiling. Returns false when something was discarded to make room.
    /// Called with the lock held.
    ///
    /// Overwriting rather than refusing is the only choice that makes sense for
    /// a real-time stream: what has to go is the audio that is already too late
    /// to be right, not the audio that has just arrived.
    @discardableResult
    private func push(_ left: Float, _ right: Float) -> Bool {
        var kept = true
        if buffered >= Self.maxBufferedFrames {
            readIndex = (readIndex + 1) % Self.ringFrames
            buffered -= 1
            kept = false
        }
        samples[writeIndex * 2] = left
        samples[writeIndex * 2 + 1] = right
        writeIndex = (writeIndex + 1) % Self.ringFrames
        buffered += 1
        return kept
    }

    /// Throws away everything but the newest `frames`. Called with the lock held.
    private func trim(to frames: Int) {
        guard buffered > frames else { return }
        readIndex = (readIndex + buffered - frames) % Self.ringFrames
        buffered = frames
    }

    // MARK: - The audio unit

    private func start() -> Bool {
        guard let output = findOutput() else {
            note("контроллер не виден как аудиоустройство — HD-хаптика недоступна")
            return false
        }

        deviceName = output.name
        outputChannels = max(2, output.channels)
        firstHapticChannel = outputChannels - 2

        var description = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0,
            componentFlagsMask: 0
        )
        guard let component = AudioComponentFindNext(nil, &description) else {
            note("в системе нет HAL output unit")
            return false
        }

        var created: AudioUnit?
        var status = AudioComponentInstanceNew(component, &created)
        guard status == noErr, let created else {
            note("AudioComponentInstanceNew: \(status)")
            return false
        }

        func fail(_ what: String, _ code: OSStatus) -> Bool {
            note("\(what): \(code)")
            AudioComponentInstanceDispose(created)
            return false
        }

        var enable: UInt32 = 1
        status = AudioUnitSetProperty(
            created, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0,
            &enable, UInt32(MemoryLayout<UInt32>.size)
        )
        guard status == noErr else { return fail("EnableIO", status) }

        var device = output.id
        status = AudioUnitSetProperty(
            created, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
            &device, UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        guard status == noErr else { return fail("CurrentDevice", status) }

        // Interleaved float32 at the device's own rate and width. The DualSense
        // engine reports exactly this, so no conversion unit is in the path and
        // the render callback writes the frames the hardware will send.
        var format = AudioStreamBasicDescription(
            mSampleRate: Double(Wire.Haptics.sampleRate),
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: UInt32(outputChannels * 4),
            mFramesPerPacket: 1,
            mBytesPerFrame: UInt32(outputChannels * 4),
            mChannelsPerFrame: UInt32(outputChannels),
            mBitsPerChannel: 32,
            mReserved: 0
        )
        status = AudioUnitSetProperty(
            created, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0,
            &format, UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        )
        guard status == noErr else { return fail("StreamFormat", status) }

        var callback = AURenderCallbackStruct(
            inputProc: hapticRenderCallback,
            inputProcRefCon: Unmanaged.passUnretained(self).toOpaque()
        )
        status = AudioUnitSetProperty(
            created, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0,
            &callback, UInt32(MemoryLayout<AURenderCallbackStruct>.size)
        )
        guard status == noErr else { return fail("SetRenderCallback", status) }

        status = AudioUnitInitialize(created)
        guard status == noErr else { return fail("AudioUnitInitialize", status) }

        status = AudioOutputUnitStart(created)
        guard status == noErr else {
            AudioUnitUninitialize(created)
            return fail("AudioOutputUnitStart", status)
        }

        unit = created
        lastError = nil
        return true
    }

    private func teardown() {
        guard let unit else { return }
        self.unit = nil
        AudioOutputUnitStop(unit)
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
    }

    private func note(_ message: String) {
        lastError = message
    }

    // MARK: - Rendering

    /// Runs on the audio thread. Nothing here allocates, and nothing here can
    /// block for longer than the socket queue holds the ring.
    fileprivate func render(_ frames: UInt32, into list: UnsafeMutablePointer<AudioBufferList>) {
        let buffers = UnsafeMutableAudioBufferListPointer(list)
        var dry = false

        ring.lock()
        if !warmedUp {
            warmedUp = true
            // CoreAudio takes tens of milliseconds to spin a USB device up, and
            // the socket has been filling the ring the whole time. Begin from the
            // newest preroll rather than from the oldest frame in there: starting
            // at the front would mean every stream opened a tenth of a second
            // behind the game and stayed there.
            trim(to: Self.prerollFrames)
        }

        let idle = filling || buffered == 0
        if !idle {
            for buffer in buffers {
                guard let raw = buffer.mData else { continue }
                let channels = Int(buffer.mNumberChannels)
                let out = raw.assumingMemoryBound(to: Float.self)

                for frame in 0..<Int(frames) {
                    var left: Float = 0
                    var right: Float = 0
                    if buffered > 0 {
                        left = samples[readIndex * 2]
                        right = samples[readIndex * 2 + 1]
                        readIndex = (readIndex + 1) % Self.ringFrames
                        buffered -= 1
                    } else {
                        dry = true
                    }

                    // Everything but the actuator pair is left at zero: the
                    // speaker is not ours to drive.
                    for channel in 0..<channels {
                        let value: Float
                        switch channel - firstHapticChannel {
                        case 0: value = left
                        case 1: value = right
                        default: value = 0
                        }
                        out[frame * channels + channel] = value
                    }
                }
            }
            // Having run out, wait for the buffer to refill before starting
            // again. Alternating between a frame of sound and a frame of nothing
            // is what turns a dropped packet into a crackle.
            if dry { filling = true }
        }
        ring.unlock()

        if idle {
            for buffer in buffers {
                guard let raw = buffer.mData else { continue }
                memset(raw, 0, Int(buffer.mDataByteSize))
            }
            return
        }

        if dry {
            counters.lock()
            underruns &+= 1
            counters.unlock()
        }
    }

    // MARK: - Finding the device

    /// The CoreAudio output device that belongs to one physical controller.
    ///
    /// Matched on two things, in order. `kAudioDevicePropertyModelUID` carries
    /// the VID and PID — `DualSense Wireless Controller:054C:0CE6` — which
    /// identifies the model. The device UID carries the USB location id, which
    /// identifies *which* of two identical controllers this is. Model alone is
    /// the fallback: it is right whenever only one is plugged in, and that is
    /// the case worth degrading gracefully into.
    static func find(vendorID: Int, productID: Int, locationID: Int) -> Output? {
        let model = String(format: "%04X:%04X", vendorID, productID)
        let location = String(locationID, radix: 16)

        var byModel: Output?
        for id in deviceIDs() {
            guard let modelUID = string(id, kAudioDevicePropertyModelUID),
                  modelUID.uppercased().contains(model) else { continue }

            let channels = outputChannelCount(id)
            guard channels >= 2 else { continue }

            let output = Output(
                id: id,
                name: string(id, kAudioObjectPropertyName) ?? "DualSense",
                channels: channels
            )
            if let uid = string(id, kAudioDevicePropertyDeviceUID),
               uid.lowercased().contains(location) {
                return output
            }
            byModel = byModel ?? output
        }
        return byModel
    }

    private static func deviceIDs() -> [AudioDeviceID] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size
        ) == noErr, size > 0 else { return [] }

        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids
        ) == noErr else { return [] }
        return ids
    }

    private static func outputChannelCount(_ id: AudioDeviceID) -> Int {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }

        let raw = UnsafeMutableRawPointer.allocate(
            byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }

        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    private static func string(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size = UInt32(MemoryLayout<CFString?>.size)
        var value: CFString?
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, pointer)
        }
        guard status == noErr, let value else { return nil }
        return value as String
    }
}

/// C callback: the player comes back through `refcon`, the same way the HID
/// input callback does. It cannot capture, which is why this is a free function.
private let hapticRenderCallback: AURenderCallback = { context, _, _, _, frames, list in
    // `inRefCon` arrives as a non-optional raw pointer; the buffer list is the
    // one of the two that can be nil, and is when the unit is only pulling a
    // timestamp.
    guard let list else { return noErr }
    Unmanaged<HapticPlayer>.fromOpaque(context).takeUnretainedValue().render(frames, into: list)
    return noErr
}
