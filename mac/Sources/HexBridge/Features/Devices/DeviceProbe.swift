import Foundation
import IOKit
import IOKit.hid

/// Diagnostics for the device side of the bridge, the counterpart of `MicProbe`.
///
/// It exists because two of the assumptions the passthrough rests on cannot be
/// verified by reading documentation: whether macOS lets an unsigned,
/// non-App-Store binary write HID output reports at all, and whether the
/// descriptors the Windows side needs are readable without a driver. Both
/// answers are printed here, and the output-report test is deliberately visible
/// to the naked eye — a success code with a dark lightbar would be a silent lie.
///
/// Anything HID can be probed. The parts that need to know what a report means —
/// the decoded state, the feature-report list, the rumble test — are gated on a
/// `DeviceProfile` and simply say so when there is none.
enum DeviceProbe {
    /// The device to work on: the one named by `--device`, else the first
    /// DualSense, else the first forwardable device at all.
    ///
    /// The selector is `VID:PID` in hex, or any substring of the product name,
    /// so `--device 054C:0CE6` and `--device DualSense` both work.
    static func pick(_ selector: String?) -> HIDDevice? {
        let all = HIDDevice.findAll().filter { !$0.isBuiltIn }
        let usable = all.filter { DeviceEligibility.of($0) == .eligible }

        if let selector, !selector.isEmpty {
            if let identity = DeviceIdentity(selector) {
                if let match = (usable + all).first(where: {
                    $0.vendorID == identity.vendorID && $0.productID == identity.productID
                }) { return match }
            }
            return (usable + all).first {
                $0.displayName.localizedCaseInsensitiveContains(selector)
                    || $0.manufacturer.localizedCaseInsensitiveContains(selector)
            }
        }

        let dualSense = usable.first {
            DeviceProfile.of(vendorID: $0.vendorID, productID: $0.productID)?.kind == .dualSense
        }
        return dualSense ?? usable.first ?? all.first
    }

    static func run(seconds: Double = 3, selector: String? = nil, log: (String) -> Void) {
        guard let device = pick(selector) else {
            log("No HID devices found. Plug in a controller, a wheel or another USB device and try again.")
            return
        }

        let eligibility = DeviceEligibility.of(device)
        if let reason = eligibility.reason {
            log("NOTE: \(reason). This device cannot be forwarded, but the diagnostics below still run.")
        }

        describe(device, log: log)
        readDescriptors(device, log: log)

        let openResult = device.open()
        log("")
        log("open (kIOHIDOptionsTypeNone): \(IOKitError.describe(openResult))")
        guard openResult == kIOReturnSuccess else {
            log("without an open device there is neither reading nor writing — no point going on")
            return
        }
        defer { device.close() }

        // One reader for the whole probe: the output test uses the live gyro to
        // prove that a write actually reached the hardware, so the input stream
        // has to stay up past the read section.
        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
        let stats = InputStats(profile: profile)
        let thread = HIDRunLoopThread()
        thread.start(name: "hexbridge.devices.probe")
        device.startReading(on: thread) { stats.add($0) }
        defer {
            device.stopReading()
            thread.stop()
        }

        readInput(stats, profile: profile, seconds: seconds, log: log)
        readFeatureReports(device, profile: profile, log: log)
        if profile?.kind == .dualSense {
            testOutput(device, motion: stats, log: log)
        } else {
            log("")
            log("— output report write test —")
            log("Skipped: the contents of an output report depend on the model, and there is no profile for this one.")
            log("Forwarding is unaffected — DEV_OUT carries the report as it is, without looking inside.")
        }
    }

    // MARK: - Identity

    private static func describe(_ device: HIDDevice, log: (String) -> Void) {
        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
        log("")
        log("device:          \(device.displayName)")
        log("manufacturer:    \(device.manufacturer)")
        log("kind:            \(device.category.label)")
        log("profile:         \(profile?.name ?? "none — forwarded as is, state cannot be decoded")")
        log("transport:       \(device.transport)")
        log("VID/PID:         0x\(hex16(device.vendorID)) / 0x\(hex16(device.productID))")
        log("version:         0x\(hex16(device.versionNumber))")
        log("serial number:   \(device.serialNumber ?? "none (iSerial = 0)")")
        log("identity:        \(device.identity)")
        log("locationID:      0x\(String(device.locationID, radix: 16, uppercase: true))  interface \(device.interfaceNumber)")
        log("report sizes:    input \(device.maxInputReportSize), output \(device.maxOutputReportSize), feature \(device.maxFeatureReportSize)")
        if device.category.warnsAboutDoubleInput {
            log("DOUBLE INPUT:    \(device.category.doubleInputWarning ?? "")")
        }
    }

    // MARK: - Descriptors

    private static func readDescriptors(_ device: HIDDevice, log: (String) -> Void) {
        log("")
        log("— descriptors (no open, no privileges) —")

        if let report = device.reportDescriptor {
            log("HID report descriptor: \(report.count) bytes  \(preview(report))")
        } else {
            log("HID report descriptor: property \(kIOHIDReportDescriptorKey) is not available")
        }

        let usb = device.usbDescriptors()
        if let error = usb.error {
            log("USB: \(error)")
        }
        if let descriptor = usb.device {
            log("device descriptor:     \(descriptor.count) bytes   \(preview(descriptor))")
            if descriptor.count >= 18 {
                let vid = Int(descriptor[8]) | (Int(descriptor[9]) << 8)
                let pid = Int(descriptor[10]) | (Int(descriptor[11]) << 8)
                let bcd = Int(descriptor[12]) | (Int(descriptor[13]) << 8)
                log("  from the descriptor: idVendor 0x\(hex16(vid)), idProduct 0x\(hex16(pid)), bcdDevice 0x\(hex16(bcd)), iSerial \(descriptor[16])")
            }
        } else {
            log("device descriptor:     not read")
        }
        if let descriptor = usb.configuration {
            log("config descriptor:     \(descriptor.count) bytes  \(preview(descriptor))")
        } else {
            log("config descriptor:     not read")
        }

        let total = (device.reportDescriptor?.count ?? 0) + (usb.device?.count ?? 0) + (usb.configuration?.count ?? 0)
        log("total for DEV_ATTACH:  \(total) bytes")
    }

    // MARK: - Input

    /// Thread-safe accumulator: the HID callback runs on the run loop thread and
    /// the printer runs here.
    private final class InputStats {
        /// Nil for a model with no profile: the bytes are still counted and
        /// timed, they are simply never given a meaning.
        private let parse: (([UInt8]) -> GamepadState?)?
        private let isDualSense: Bool

        init(profile: DeviceProfile?) {
            parse = profile?.parse
            isDualSense = profile?.kind == .dualSense
        }

        private let lock = NSLock()
        private(set) var count = 0
        private(set) var lengths = Set<Int>()
        private(set) var reportIDs = Set<UInt8>()
        private(set) var last: [UInt8] = []
        private(set) var gaps = 0
        private var previousSequence: UInt8?
        private(set) var firstAt: DispatchTime?
        private(set) var lastAt: DispatchTime?

        func add(_ report: [UInt8]) {
            lock.lock()
            defer { lock.unlock() }
            let now = DispatchTime.now()
            if firstAt == nil { firstAt = now }
            lastAt = now
            count += 1
            lengths.insert(report.count)
            if let id = report.first { reportIDs.insert(id) }
            last = report
            // Byte 7 is a per-report counter on a DualSense; a jump means the
            // USB stack dropped a frame between us and the controller. Only a
            // profiled model has a counter we know how to find.
            if isDualSense, report.count > 7, report[0] == DualSenseReport.inputReportID {
                if let previous = previousSequence, report[7] != previous &+ 1 {
                    gaps += 1
                }
                previousSequence = report[7]
            }
            if let state = parse?(report) {
                let magnitude = sqrt(
                    pow(Double(state.gyro.x), 2) + pow(Double(state.gyro.y), 2) + pow(Double(state.gyro.z), 2)
                )
                motionCount += 1
                motionSum += magnitude
                motionSumSq += magnitude * magnitude
            }
        }

        // MARK: Motion witness
        //
        // The rumble motors shake the whole shell, so the gyro sees them. That
        // makes it possible to prove an output report physically landed instead
        // of trusting a return code that only says the USB transaction was
        // accepted.
        private var motionCount = 0
        private var motionSum = 0.0
        private var motionSumSq = 0.0

        func resetMotion() {
            lock.lock()
            motionCount = 0
            motionSum = 0
            motionSumSq = 0
            lock.unlock()
        }

        /// Standard deviation of the gyro magnitude over the current window.
        func motionDeviation() -> (samples: Int, sigma: Double) {
            lock.lock()
            defer { lock.unlock() }
            guard motionCount > 1 else { return (motionCount, 0) }
            let n = Double(motionCount)
            let mean = motionSum / n
            let variance = max(0, motionSumSq / n - mean * mean)
            return (motionCount, sqrt(variance))
        }

        func snapshot() -> (count: Int, lengths: [Int], ids: [UInt8], last: [UInt8], gaps: Int, seconds: Double) {
            lock.lock()
            defer { lock.unlock() }
            var span = 0.0
            if let firstAt, let lastAt {
                span = Double(lastAt.uptimeNanoseconds - firstAt.uptimeNanoseconds) / 1e9
            }
            return (count, lengths.sorted(), reportIDs.sorted(), last, gaps, span)
        }
    }

    private static func readInput(_ stats: InputStats, profile: DeviceProfile?, seconds: Double, log: (String) -> Void) {
        log("")
        log("— reading input reports, \(Int(seconds)) s —")

        Thread.sleep(forTimeInterval: seconds)

        let snapshot = stats.snapshot()
        guard snapshot.count > 0 else {
            log("no reports arrived. The device is open but nothing is coming — check the cable.")
            return
        }

        // The measured window is first-to-last report, not the sleep: the first
        // report arrives some unknown time after scheduling.
        let rate = snapshot.seconds > 0 ? Double(snapshot.count - 1) / snapshot.seconds : 0
        log(String(format: "reports: %d in %.3f s → %.1f Hz", snapshot.count, snapshot.seconds, rate))
        log("report lengths: \(snapshot.lengths.map(String.init).joined(separator: ", "))")
        log("report id: \(snapshot.ids.map { "0x" + hexByte($0) }.joined(separator: ", "))")
        if profile != nil {
            log("gaps in the counter: \(snapshot.gaps)")
        }

        guard let parse = profile?.parse else {
            log("last report: \(preview(snapshot.last, limit: 32))")
            log("State cannot be decoded: there is no profile for this model. Forwarding is unaffected.")
            return
        }
        guard let state = parse(snapshot.last) else {
            log("the report could not be decoded: \(preview(snapshot.last))")
            return
        }
        printState(state, log: log)
    }

    private static func printState(_ state: GamepadState, log: (String) -> Void) {
        let left = state.left.normalized
        let right = state.right.normalized
        log(String(format: "stick L: x %+.2f  y %+.2f  (raw %3d %3d)", left.x, left.y, state.left.x, state.left.y))
        log(String(format: "stick R: x %+.2f  y %+.2f  (raw %3d %3d)", right.x, right.y, state.right.x, state.right.y))
        log("triggers: L2 \(state.l2)  R2 \(state.r2)")
        log("d-pad: \(state.dpad.label)  (\(state.dpad))")
        let pressed = state.buttons.labels
        log("buttons: \(pressed.isEmpty ? "nothing pressed" : pressed.joined(separator: " "))")
        if state.vendorButtons != 0 {
            log("extra buttons (Edge): 0x\(hexByte(state.vendorButtons))")
        }
        log("gyro:          x \(state.gyro.x)  y \(state.gyro.y)  z \(state.gyro.z)")
        log("accelerometer: x \(state.accel.x)  y \(state.accel.y)  z \(state.accel.z)")
        log("sensor timestamp: \(state.timestamp)")
        for (index, point) in state.touch.enumerated() {
            let description = point.active ? "x \(point.x)  y \(point.y)  id \(point.id)" : "no touch"
            log("touchpad \(index + 1): \(description)")
        }
        log("battery: \(state.batteryDescription)  (level \(state.batteryLevel), status 0x\(String(state.batteryStatus, radix: 16)))")
    }

    // MARK: - Feature reports

    /// The reports a game asks for before it accepts the device as genuine.
    /// They travel inside DEV_ATTACH, so if they cannot be read here the Windows
    /// side has nothing to answer GET_REPORT with. Which reports matter is the
    /// profile's business — a generic HID device has no such list.
    private static func readFeatureReports(_ device: HIDDevice, profile: DeviceProfile?, log: (String) -> Void) {
        log("")
        log("— feature reports —")

        guard let profile, !profile.featureReports.isEmpty else {
            log("There is no profile for this model — no feature report snapshots are sent.")
            return
        }

        let names: [UInt8: String] = [0x20: "firmware", 0x09: "MAC addresses", 0x05: "gyro calibration"]
        let wanted = profile.featureReports.map {
            (id: $0.id, length: $0.length, what: names[$0.id] ?? "snapshot")
        }

        for report in wanted {
            let result = device.featureReport(id: report.id, length: report.length)
            guard result.status == kIOReturnSuccess else {
                log("0x\(hexByte(report.id)) \(report.what): FAILED \(IOKitError.describe(result.status))")
                continue
            }
            log("0x\(hexByte(report.id)) \(report.what): \(result.data.count) bytes (expected \(report.length))  \(preview(result.data))")

            if report.id == 0x20, result.data.count >= 20 {
                let date = ascii(result.data[1..<12])
                let time = ascii(result.data[12..<20])
                log("     firmware build: \(date) \(time)")
            }
            if report.id == 0x09, result.data.count >= 7 {
                // Stored little-endian, so the printed MAC is the reverse.
                let mac = (1...6).reversed().map { hexByte(result.data[$0]) }.joined(separator: ":")
                log("     controller MAC: \(mac)")
            }
        }
    }

    // MARK: - Output

    /// The question this whole subcommand was written for.
    private static func testOutput(_ device: HIDDevice, motion: InputStats, log: (String) -> Void) {
        log("")
        log("— output report 0x02 write test —")
        log("WATCH THE CONTROLLER: the lighting and the rumble should change from here on.")

        var output = GamepadOutput()
        output.lightbar = .init(red: 255, green: 0, blue: 0)
        let report = output.encoded()
        log("report length: \(report.count) bytes (ID 0x\(hexByte(report[0])) + \(report.count - 1))")

        let first = device.setOutputReport(report)
        log("IOHIDDeviceSetReport → \(IOKitError.describe(first))")

        guard first == kIOReturnSuccess else {
            log("")
            log("WRITING IS BLOCKED. Adaptive triggers, lighting and rumble are unavailable.")
            return
        }

        // A returned success still only means the transaction was accepted, so
        // the rest of this is paced for a human to watch.
        log("NOW: the light bar should be RED (1 s)")
        Thread.sleep(forTimeInterval: 1)

        var status: [IOReturn] = [first]
        for (color, name) in [(GamepadOutput.Color(red: 0, green: 255, blue: 0), "GREEN"),
                              (GamepadOutput.Color(red: 0, green: 0, blue: 255), "BLUE")] {
            var step = GamepadOutput()
            step.lightbar = color
            let result = device.setOutputReport(step.encoded())
            status.append(result)
            log("NOW: the light bar should be \(name) (1 s) — \(IOKitError.describe(result))")
            Thread.sleep(forTimeInterval: 1)
        }

        // Baseline first: whatever the controller is doing while it sits still.
        motion.resetMotion()
        Thread.sleep(forTimeInterval: 0.4)
        let quiet = motion.motionDeviation()

        var buzz = GamepadOutput()
        buzz.rumbleLeft = 160
        buzz.rumbleRight = 160
        buzz.lightbar = .init(red: 0, green: 0, blue: 255)
        let rumbleResult = device.setOutputReport(buzz.encoded())
        status.append(rumbleResult)
        log("NOW: a short RUMBLE, 0.4 s — \(IOKitError.describe(rumbleResult))")
        motion.resetMotion()
        Thread.sleep(forTimeInterval: 0.4)
        let shaking = motion.motionDeviation()

        var calm = GamepadOutput()
        calm.rumbleLeft = 0
        calm.rumbleRight = 0
        calm.lightbar = .init(red: 0, green: 20, blue: 80)
        calm.playerLEDs = 0b00100  // middle LED, the "player 1" pattern
        let restore = device.setOutputReport(calm.encoded())
        status.append(restore)
        log("rumble off, light bar dimmed — \(IOKitError.describe(restore))")

        log("")
        log(String(
            format: "gyro at rest: σ %.1f (%d reports), under rumble: σ %.1f (%d reports)",
            quiet.sigma, quiet.samples, shaking.sigma, shaking.samples
        ))
        // A return code only proves the USB stack took the packet. The motors
        // moving the gyro proves the controller acted on it.
        let confirmed = shaking.samples > 10 && shaking.sigma > max(50, quiet.sigma * 4)
        if confirmed {
            log("rumble confirmed by instrument: the gyro registered the shake from the motors.")
        } else {
            log("the gyro saw no shake — either the controller was lying still, or the motor did nothing.")
        }

        let failures = status.filter { $0 != kIOReturnSuccess }
        log("")
        if failures.isEmpty {
            log("WRITING WORKS: all \(status.count) SetReport calls succeeded\(confirmed ? ", and the effect was confirmed" : "").")
            if !confirmed {
                log("If the light bar did not change: the report was accepted but had no effect — please report that.")
            }
        } else {
            log("PARTIAL: \(failures.count) of \(status.count) SetReport calls failed.")
        }
    }

    // MARK: - HD haptics

    /// Proves the HD haptics path end to end on this machine, with no Windows
    /// and no network in it.
    ///
    /// The blocks are synthesised here and handed to `HapticPlayer.play` — the
    /// same call the socket makes for a block that arrived over the wire — so
    /// everything downstream of the decoder is the shipping code: the ring
    /// buffer, the audio unit, the channel mapping and the unmute report.
    ///
    /// Proof is the controller's own gyroscope, the same witness the rumble test
    /// uses. A return code from CoreAudio says the samples were accepted, not
    /// that anything moved; σ of the gyro magnitude says something moved. The
    /// tone is 40 Hz because that is where the two possible answers separate
    /// cleanly: the voice coils reproduce it and the tiny speaker on channels 0
    /// and 1 cannot, so a build that had confused the two pairs would measure
    /// nothing at all rather than measuring slightly less.
    static func haptics(seconds: Double = 2, selector: String? = nil, log: (String) -> Void) {
        guard let device = pick(selector) else {
            log("No HID devices found. Plug a controller in over USB and try again.")
            return
        }

        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
        log("device:          \(device.displayName)  (\(device.identity))")
        log("locationID:      0x\(String(device.locationID, radix: 16, uppercase: true))")

        let player = HapticPlayer(
            vendorID: device.vendorID, productID: device.productID, locationID: device.locationID
        )
        guard let output = player.findOutput() else {
            log("")
            log("The system has no audio device for this controller.")
            log("HD haptics are played through CoreAudio, so without it there is no path:")
            log("check that the controller is on a cable rather than on Bluetooth.")
            return
        }
        log("audio output:    \(output.name), \(output.channels) channels")
        log("haptic channels: \(output.channels - 2) and \(output.channels - 1)")

        guard profile?.kind == .dualSense else {
            log("")
            log("No profile for this model: nothing can unmute the actuators and nothing can read the gyro.")
            log("The blocks would play, but the effect could not be confirmed by instrument.")
            return
        }

        let openResult = device.open()
        log("open:            \(IOKitError.describe(openResult))")
        guard openResult == kIOReturnSuccess else {
            log("Without an open device the actuators cannot be unmuted and the gyro cannot be read.")
            return
        }
        defer { device.close() }

        let stats = InputStats(profile: profile)
        let thread = HIDRunLoopThread()
        thread.start(name: "hexbridge.devices.haptics")
        device.startReading(on: thread) { stats.add($0) }
        defer {
            device.stopReading()
            thread.stop()
        }

        let unmute = device.setOutputReport(DualSenseReport.audioHapticsEnable)
        log("unmute:          \(IOKitError.describe(unmute))")
        log("")
        log("HOLD THE CONTROLLER: you should feel a hum in the grips from here on.")

        // Baseline first: whatever the controller does while nothing is playing.
        Thread.sleep(forTimeInterval: 0.4)
        stats.resetMotion()
        Thread.sleep(forTimeInterval: 0.6)
        let quiet = stats.motionDeviation()

        let pass = feed(player, seconds: seconds, frequency: 40, amplitude: 0.9, stats: stats)
        let shaking = stats.motionDeviation()
        let status = player.status()

        // A second pass with every eighth block thrown away on purpose. The wire
        // loses blocks and the sender skips silent ones, and the player has to
        // tell those two apart from the numbering alone — this is the only way to
        // watch it do so without waiting for a bad network.
        let lossy = feed(
            player, seconds: seconds, frequency: 40, amplitude: 0.9, stats: stats, dropEvery: 8
        )
        let shakenWithLoss = stats.motionDeviation()
        let after = player.status()

        // And back to nothing, so the run does not end with the actuators still
        // ringing from the last block.
        player.stop()
        Thread.sleep(forTimeInterval: 0.3)

        log("")
        log("blocks sent: \(pass.blocks), played \(status.blocksPlayed), "
            + "lost \(status.blocksLost), too late \(status.blocksDropped)")
        log("buffer underruns:  \(status.underruns)")
        if let error = after.lastError {
            log("audio error:       \(error)")
        }
        log(String(
            format: "gyro at rest: σ %.1f (%d reports), under haptics: σ %.1f (%d reports)",
            quiet.sigma, quiet.samples, shaking.sigma, shaking.samples
        ))

        let noticed = after.blocksLost - status.blocksLost
        log("")
        log("— block loss —")
        log("every 8th dropped: \(lossy.blocks - lossy.dropped) of \(lossy.blocks) arrived, "
            + "the player counted \(noticed) lost")
        log(String(format: "gyro under loss: σ %.1f (%d reports)",
                   shakenWithLoss.sigma, shakenWithLoss.samples))
        log(noticed == UInt64(lossy.dropped)
            ? "The gaps were spotted from the numbering and filled with silence; the stream held together."
            : "WARNING: the gaps were counted wrong — check the block numbering.")

        let confirmed = shaking.samples > 10 && shaking.sigma > max(20, quiet.sigma * 4)
        log("")
        if confirmed {
            log("HAPTICS CONFIRMED BY INSTRUMENT: the gyro registered the shake from the actuators.")
        } else if status.blocksPlayed == 0 {
            log("NOT WORKING: not one block reached the audio device.")
        } else {
            log("The blocks played, but the gyro saw no shake.")
            log("That usually means one of two things: the controller was lying on the desk — hold it,")
            log("or the actuators were never unmuted — see the return code above.")
        }
    }

    /// Synthesises 5 ms blocks in real time and pushes them through the player.
    ///
    /// Real time on purpose. A burst would overrun the ring and be dropped —
    /// which is the correct behaviour, and proves nothing about whether the
    /// audio path works.
    private static func feed(
        _ player: HapticPlayer, seconds: Double, frequency: Double, amplitude: Double,
        stats: InputStats, dropEvery: UInt32 = 0
    ) -> (blocks: UInt32, dropped: UInt32) {
        let frames = Wire.Haptics.framesPerBlock
        let step = 2 * Double.pi * frequency / Double(Wire.Haptics.sampleRate)
        let blockSeconds = Double(frames) / Double(Wire.Haptics.sampleRate)
        let total = UInt32(max(1, (seconds / blockSeconds).rounded()))

        var phase = 0.0
        var index: UInt32 = 0
        var dropped: UInt32 = 0
        var deadline = Date()
        // The gyro window opens with the first block, not with the timer: the
        // actuators do nothing until the ring has its preroll, and measuring
        // through that silence would dilute the very thing being measured.
        var started = false

        while index < total {
            var samples = [Int16]()
            samples.reserveCapacity(frames * 2)
            for _ in 0..<frames {
                let value = Int16(clamping: Int(sin(phase) * amplitude * 32000))
                samples.append(value)
                samples.append(value)
                phase += step
                if phase > 2 * Double.pi { phase -= 2 * Double.pi }
            }

            // Through the codec, not around it: a block that is encoded and
            // decoded again is the block the socket would have handed over, and
            // the two halves of that codec live on different machines.
            let payload = Wire.Haptics.encode(device: 0, channels: 2, index: index, samples: samples)
            // The last block is always kept. A drop with nothing sent after it is
            // a drop the far end cannot notice, and counting it would make the
            // check fail for the one reason that is not a bug.
            let keep = dropEvery == 0 || index % dropEvery != dropEvery - 1 || index == total - 1
            if keep, let block = Wire.Haptics.decode(payload) {
                player.play(block)
            } else {
                dropped += 1
            }
            index += 1

            if !started {
                started = true
                Thread.sleep(forTimeInterval: 0.05)
                stats.resetMotion()
                // Restart the clock rather than carrying the pause as a debt.
                // Catching up would send ten blocks back to back, and the player
                // would correctly shed the backlog — proving nothing except that
                // this loop had burst.
                deadline = Date()
            }

            deadline = deadline.addingTimeInterval(blockSeconds)
            let wait = deadline.timeIntervalSinceNow
            if wait > 0 { Thread.sleep(forTimeInterval: wait) }
        }
        return (index, dropped)
    }

    // MARK: - list

    /// Every HID device the machine can see, without opening any of them.
    ///
    /// The built-in keyboard and trackpad are left out entirely, exactly as they
    /// are in the picker: they are not offered, so listing them would only
    /// invite the question of why they cannot be chosen.
    static func list(log: (String) -> Void) {
        let devices = DeviceProbe.physical(HIDDevice.findAll().filter { !$0.isBuiltIn })
        guard !devices.isEmpty else {
            log("No HID devices found")
            return
        }
        for (index, device) in devices.enumerated() {
            let eligibility = DeviceEligibility.of(device)
            let note = eligibility.reason.map { "   [\($0)]" } ?? ""
            let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
            log("\(index)  \(device.displayName)  \(device.transport)\(note)")
            log("   \(device.identity)  \(device.category.label)\(profile.map { " · profile \($0.name)" } ?? "")")
            log("   VID/PID 0x\(hex16(device.vendorID))/0x\(hex16(device.productID))  version 0x\(hex16(device.versionNumber))  locationID 0x\(String(device.locationID, radix: 16))")
            log("   reports: input \(device.maxInputReportSize), output \(device.maxOutputReportSize), feature \(device.maxFeatureReportSize)  descriptor \(device.reportDescriptor?.count ?? 0) bytes")
            if let warning = device.category.doubleInputWarning, eligibility == .eligible {
                log("   NOTE: \(warning)")
            }
        }
    }

    /// One row per physical device: a composite device shows up once per HID
    /// interface and listing it three times helps nobody.
    private static func physical(_ devices: [HIDDevice]) -> [HIDDevice] {
        var seen = Set<Int>()
        return devices.filter { seen.insert($0.locationID).inserted }
    }

    // MARK: - monitor

    /// Live state until Ctrl-C. The point is to see whether a stick or a button
    /// reaches us at all, so it prints the decoded values rather than hex.
    static func monitor(selector: String? = nil, log: (String) -> Void) {
        guard let device = pick(selector) else {
            log("No HID devices found. Plug a device in over USB.")
            return
        }
        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)

        let openResult = device.open()
        guard openResult == kIOReturnSuccess else {
            log("could not open: \(IOKitError.describe(openResult))")
            return
        }
        defer { device.close() }

        let stats = InputStats(profile: profile)
        let thread = HIDRunLoopThread()
        thread.start(name: "hexbridge.devices.monitor")
        device.startReading(on: thread) { stats.add($0) }
        defer {
            device.stopReading()
            thread.stop()
        }

        log("\(device.displayName) over \(device.transport). Ctrl-C to leave.")

        var previousCount = 0
        var previousAt = DispatchTime.now()
        while true {
            Thread.sleep(forTimeInterval: 0.1)
            let snapshot = stats.snapshot()
            let now = DispatchTime.now()
            let seconds = Double(now.uptimeNanoseconds - previousAt.uptimeNanoseconds) / 1e9
            let rate = seconds > 0 ? Double(snapshot.count - previousCount) / seconds : 0
            previousCount = snapshot.count
            previousAt = now

            guard let parse = profile?.parse else {
                let line = String(format: "%5.0f Hz │ %@", rate, preview(snapshot.last, limit: 24))
                print(line.padding(toLength: max(line.count, 96), withPad: " ", startingAt: 0), terminator: "\r")
                fflush(stdout)
                continue
            }
            guard let state = parse(snapshot.last) else { continue }
            let left = state.left.normalized
            let right = state.right.normalized
            let pressed = state.buttons.labels.joined(separator: " ")
            let line = String(
                format: "%5.0f Hz │ L %+.2f %+.2f │ R %+.2f %+.2f │ L2 %3d R2 %3d │ %@ │ %@",
                rate, left.x, left.y, right.x, right.y, state.l2, state.r2,
                state.dpad.label, pressed.isEmpty ? "—" : pressed
            )
            // Overwrite in place: a 10 Hz scroll of near-identical lines is
            // unreadable, and this is a tool for watching, not for logging.
            print(line.padding(toLength: max(line.count, 96), withPad: " ", startingAt: 0), terminator: "\r")
            fflush(stdout)
        }
    }

    // MARK: - Formatting

    private static func hex16(_ value: Int) -> String {
        String(format: "%04X", value)
    }

    private static func hexByte(_ value: UInt8) -> String {
        String(format: "%02X", value)
    }

    private static func preview(_ bytes: [UInt8], limit: Int = 16) -> String {
        let head = bytes.prefix(limit).map { hexByte($0) }.joined(separator: " ")
        return bytes.count > limit ? "[\(head) …]" : "[\(head)]"
    }

    private static func ascii(_ bytes: ArraySlice<UInt8>) -> String {
        String(bytes.map { $0 >= 0x20 && $0 < 0x7F ? Character(UnicodeScalar($0)) : " " })
            .trimmingCharacters(in: .whitespaces)
    }
}
