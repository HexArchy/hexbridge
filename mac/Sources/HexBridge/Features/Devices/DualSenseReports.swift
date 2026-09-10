import Foundation
import HexBridgeText

/// Pure byte-level codec for the DualSense HID reports.
///
/// Deliberately free of IOKit: the same code has to run against a live device,
/// against a report that arrived over the wire, and in a unit test with a
/// hand-written buffer. `DualSenseDevice` does the I/O, this file does the bytes.
///
/// Layouts follow the USB reports, where the buffer on the wire always carries
/// the report id in byte 0. Bluetooth wraps the same payload in report `0x31`
/// with a CRC32 trailer; that is not implemented here because the bridge only
/// claims the USB-attached controller.
enum DualSenseReport {
    static let inputReportID: UInt8 = 0x01
    /// 1 byte of report id + 63 declared by the descriptor.
    static let inputReportSize = 64

    static let outputReportID: UInt8 = 0x02
    /// 1 byte of report id + 47 declared by the descriptor. Linux pads this to
    /// 63; the extra bytes are ignored by the controller, so we do not send them.
    static let outputReportSize = 48

    /// Unmutes the audio-driven haptics, and does nothing else.
    ///
    /// A DualSense boots with the mute bits in byte 10 set, and PCM written to
    /// the actuator channels is accepted and silently discarded until they are
    /// cleared. A PS5 clears them; nothing on a PC does, which is why HD haptics
    /// over an audio device look like a dead end until you find this. Byte 10 is
    /// only read when bit 1 of `valid_flag1` says it is, so both bytes have to
    /// be here.
    ///
    /// Found by measurement on 2026-09-10 rather than from a datasheet: with a
    /// 60 Hz tone on channels 2 and 3, the controller's own gyroscope reads
    /// σ 0.9 with byte 10 untouched, σ 128 with it zeroed, and σ 4 with bit 7 of
    /// it set on its own. Bit 7 is the haptic mute; nothing else in the byte
    /// changes the answer.
    ///
    /// `valid_flag0` is deliberately left at zero. Setting HAPTICS_SELECT there
    /// hands the actuators to the classic rumble emulator instead and measures
    /// σ 0.9 again — so the flag whose name sounds like it enables haptics is
    /// the one that turns this path off.
    static var audioHapticsEnable: [UInt8] {
        var report = [UInt8](repeating: 0, count: outputReportSize)
        report[0] = outputReportID
        report[2] = 0x02   // valid_flag1: POWER_SAVE_CONTROL_ENABLE, which validates byte 10
        report[10] = 0x00  // nothing muted: the voice coils follow the audio stream
        return report
    }
}

// MARK: - Input

/// One decoded input report. Every field is the raw controller value: scaling
/// belongs to whoever displays it, and the bridge forwards the untouched bytes.
struct GamepadState {
    struct Stick {
        var x: UInt8 = 0x80
        var y: UInt8 = 0x80

        /// -1…1, y flipped so that up is positive, like every gamepad API.
        var normalized: (x: Double, y: Double) {
            ((Double(x) - 127.5) / 127.5, -((Double(y) - 127.5) / 127.5))
        }
    }

    struct Vector3 {
        var x: Int16 = 0
        var y: Int16 = 0
        var z: Int16 = 0
    }

    struct TouchPoint {
        var active = false
        var id: UInt8 = 0
        var x: UInt16 = 0
        var y: UInt16 = 0
    }

    /// The d-pad arrives as a hat switch in the low nibble, not as four bits.
    enum DPad: UInt8 {
        case up = 0, upRight, right, downRight, down, downLeft, left, upLeft, neutral

        var label: String {
            switch self {
            case .up: return "↑"
            case .upRight: return "↗"
            case .right: return "→"
            case .downRight: return "↘"
            case .down: return "↓"
            case .downLeft: return "↙"
            case .left: return "←"
            case .upLeft: return "↖"
            case .neutral: return "·"
            }
        }
    }

    struct Buttons: OptionSet {
        let rawValue: UInt32

        static let square = Buttons(rawValue: 1 << 0)
        static let cross = Buttons(rawValue: 1 << 1)
        static let circle = Buttons(rawValue: 1 << 2)
        static let triangle = Buttons(rawValue: 1 << 3)
        static let l1 = Buttons(rawValue: 1 << 4)
        static let r1 = Buttons(rawValue: 1 << 5)
        static let l2 = Buttons(rawValue: 1 << 6)
        static let r2 = Buttons(rawValue: 1 << 7)
        static let create = Buttons(rawValue: 1 << 8)
        static let options = Buttons(rawValue: 1 << 9)
        static let l3 = Buttons(rawValue: 1 << 10)
        static let r3 = Buttons(rawValue: 1 << 11)
        static let ps = Buttons(rawValue: 1 << 12)
        static let touchpad = Buttons(rawValue: 1 << 13)
        static let mute = Buttons(rawValue: 1 << 14)

        static let names: [(Buttons, String)] = [
            (.square, "□"), (.cross, "✕"), (.circle, "○"), (.triangle, "△"),
            (.l1, "L1"), (.r1, "R1"), (.l2, "L2"), (.r2, "R2"),
            (.create, "Create"), (.options, "Options"), (.l3, "L3"), (.r3, "R3"),
            (.ps, "PS"), (.touchpad, "Touchpad"), (.mute, "Mute"),
        ]

        var labels: [String] {
            Buttons.names.compactMap { contains($0.0) ? $0.1 : nil }
        }
    }

    var left = Stick()
    var right = Stick()
    var l2: UInt8 = 0
    var r2: UInt8 = 0
    /// Rolls over every 256 reports; a gap means a dropped USB frame.
    var sequence: UInt8 = 0
    var dpad: DPad = .neutral
    var buttons: Buttons = []
    /// Raw fourth button byte: Edge paddles and vendor bits live here.
    var vendorButtons: UInt8 = 0
    var gyro = Vector3()
    var accel = Vector3()
    /// Free-running sensor clock, ~1 tick per 0.33 µs.
    var timestamp: UInt32 = 0
    var touch: [TouchPoint] = [TouchPoint(), TouchPoint()]
    /// 0…15 as reported; only 0…10 is meaningful while charging or discharging.
    var batteryLevel: UInt8 = 0
    var batteryStatus: UInt8 = 0

    /// nil when the controller reports a fault code instead of a charge level.
    var batteryPercent: Int? {
        switch batteryStatus {
        case 0x0, 0x1: return min(Int(batteryLevel) * 10 + 5, 100)
        case 0x2: return 100
        default: return nil
        }
    }

    var batteryDescription: String {
        // The U+00A0 before the sign lives in `unit.percent`: §10.1 of the
        // design document requires units to be glued to their number so a line
        // break cannot separate them.
        let percent = batteryPercent.map { L.percent(Double($0), decimals: 0) } ?? "?"
        switch batteryStatus {
        case 0x0: return L.t("battery.discharging", percent)
        case 0x1: return L.t("battery.charging", percent)
        case 0x2: return L.t("battery.charged", percent)
        case 0xa: return L.t("battery.overheated")
        case 0xb: return L.t("battery.tooCold")
        case 0xf: return L.t("battery.fault")
        default: return L.t("battery.unknown", percent)
        }
    }

    /// Decodes report `0x01`. `bytes` must include the report id in byte 0,
    /// which is how both IOHIDManager and the wire deliver it.
    static func parse(_ bytes: [UInt8]) -> GamepadState? {
        guard bytes.count >= DualSenseReport.inputReportSize,
              bytes[0] == DualSenseReport.inputReportID else { return nil }

        var state = GamepadState()
        state.left = Stick(x: bytes[1], y: bytes[2])
        state.right = Stick(x: bytes[3], y: bytes[4])
        state.l2 = bytes[5]
        state.r2 = bytes[6]
        state.sequence = bytes[7]

        state.dpad = DPad(rawValue: bytes[8] & 0x0F) ?? .neutral
        // Face buttons sit in the high nibble of the same byte as the hat.
        let raw = UInt32(bytes[8] >> 4)
            | (UInt32(bytes[9]) << 4)
            | (UInt32(bytes[10] & 0x07) << 12)
        state.buttons = Buttons(rawValue: raw)
        state.vendorButtons = bytes[11]

        // Gyro comes before the accelerometer, which is the opposite of what
        // most third-party notes claim.
        state.gyro = Vector3(x: int16(bytes, 16), y: int16(bytes, 18), z: int16(bytes, 20))
        state.accel = Vector3(x: int16(bytes, 22), y: int16(bytes, 24), z: int16(bytes, 26))
        state.timestamp = bytes.readLE(at: 28)

        state.touch = [touchPoint(bytes, 33), touchPoint(bytes, 37)]

        state.batteryLevel = bytes[53] & 0x0F
        state.batteryStatus = (bytes[53] >> 4) & 0x0F
        return state
    }

    private static func int16(_ bytes: [UInt8], _ offset: Int) -> Int16 {
        Int16(bitPattern: UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8))
    }

    /// Four bytes: a contact id whose bit 7 means "finger lifted", then a 12-bit
    /// x and a 12-bit y packed across the remaining three bytes.
    private static func touchPoint(_ bytes: [UInt8], _ offset: Int) -> TouchPoint {
        let contact = bytes[offset]
        return TouchPoint(
            active: contact & 0x80 == 0,
            id: contact & 0x7F,
            x: UInt16(bytes[offset + 1]) | (UInt16(bytes[offset + 2] & 0x0F) << 8),
            y: UInt16(bytes[offset + 2] >> 4) | (UInt16(bytes[offset + 3]) << 4)
        )
    }
}

// MARK: - Adaptive triggers

/// One trigger's force profile. The ten parameter bytes are a packed
/// description of ten 1 mm zones along the trigger travel, which is why the
/// constructors take positions rather than raw bytes.
struct TriggerEffect: Equatable {
    enum Mode: UInt8 {
        case off = 0x05
        case feedback = 0x21
        case weapon = 0x25
        case vibration = 0x26
    }

    var mode: Mode = .off
    /// Always padded to ten bytes when encoded.
    var parameters: [UInt8] = []

    static let off = TriggerEffect()

    /// Constant resistance from `position` (0…9) to the end of the travel.
    /// `strength` 0 releases the trigger, 1…8 is soft…hard.
    static func feedback(position: UInt8, strength: UInt8) -> TriggerEffect {
        guard position <= 9, strength >= 1, strength <= 8 else { return .off }
        let force = UInt32((strength - 1) & 0x07)
        var forceZones: UInt32 = 0
        var activeZones: UInt16 = 0
        for zone in Int(position)...9 {
            forceZones |= force << (3 * UInt32(zone))
            activeZones |= 1 << UInt16(zone)
        }
        return TriggerEffect(mode: .feedback, parameters: [
            UInt8(activeZones & 0xFF), UInt8(activeZones >> 8),
            UInt8(forceZones & 0xFF), UInt8((forceZones >> 8) & 0xFF),
            UInt8((forceZones >> 16) & 0xFF), UInt8((forceZones >> 24) & 0xFF),
            0, 0, 0, 0,
        ])
    }

    /// Resistance between `start` and `end` that snaps through, like a trigger
    /// on a firearm. `start` 2…7, `end` > start and ≤ 8, `strength` 1…8.
    static func weapon(start: UInt8, end: UInt8, strength: UInt8) -> TriggerEffect {
        guard start >= 2, start <= 7, end > start, end <= 8, strength >= 1, strength <= 8 else { return .off }
        let zones = (UInt16(1) << UInt16(start)) | (UInt16(1) << UInt16(end))
        return TriggerEffect(mode: .weapon, parameters: [
            UInt8(zones & 0xFF), UInt8(zones >> 8), strength - 1,
            0, 0, 0, 0, 0, 0, 0,
        ])
    }

    /// Buzzes from `position` onwards. `amplitude` 1…8, `frequency` in Hz.
    static func vibration(position: UInt8, amplitude: UInt8, frequency: UInt8) -> TriggerEffect {
        guard position <= 9, amplitude >= 1, amplitude <= 8, frequency > 0 else { return .off }
        let strength = UInt32((amplitude - 1) & 0x07)
        var amplitudeZones: UInt32 = 0
        var activeZones: UInt16 = 0
        for zone in Int(position)...9 {
            amplitudeZones |= strength << (3 * UInt32(zone))
            activeZones |= 1 << UInt16(zone)
        }
        return TriggerEffect(mode: .vibration, parameters: [
            UInt8(activeZones & 0xFF), UInt8(activeZones >> 8),
            UInt8(amplitudeZones & 0xFF), UInt8((amplitudeZones >> 8) & 0xFF),
            UInt8((amplitudeZones >> 16) & 0xFF), UInt8((amplitudeZones >> 24) & 0xFF),
            0, 0, 0, frequency,
        ])
    }

    /// Mode byte followed by exactly ten parameter bytes.
    var encoded: [UInt8] {
        var out = [mode.rawValue]
        out.append(contentsOf: parameters.prefix(10))
        out.append(contentsOf: [UInt8](repeating: 0, count: 11 - out.count))
        return out
    }
}

// MARK: - Output

/// A DualSense output report under construction.
///
/// Every optional is "leave this subsystem alone": the controller only applies
/// the parts whose valid flag is set, so a report that changes the lightbar must
/// not accidentally clear the trigger effects that a previous report installed.
struct GamepadOutput {
    struct Color: Equatable {
        var red: UInt8
        var green: UInt8
        var blue: UInt8

        static let off = Color(red: 0, green: 0, blue: 0)
    }

    var rumbleLeft: UInt8?
    var rumbleRight: UInt8?
    var lightbar: Color?
    /// Bit mask over the five LEDs under the touchpad, bit 0 leftmost.
    var playerLEDs: UInt8?
    /// 0 bright, 1 medium, 2 dim. Only sent together with `playerLEDs`.
    var ledBrightness: UInt8 = 0
    var microphoneLED: Bool?
    var rightTrigger: TriggerEffect?
    var leftTrigger: TriggerEffect?
    /// Stops the connect animation and fades the bar out. Mutually exclusive
    /// with `lightbar` in practice: the fade wins.
    var fadeOutLightbar = false

    // Bits of valid_flag0 / valid_flag1 / valid_flag2, named as the controller
    // firmware understands them.
    private static let flag0CompatibleRumble: UInt8 = 0x01
    private static let flag0HapticsSelect: UInt8 = 0x02
    private static let flag0RightTrigger: UInt8 = 0x04
    private static let flag0LeftTrigger: UInt8 = 0x08
    private static let flag1MicrophoneLED: UInt8 = 0x01
    private static let flag1Lightbar: UInt8 = 0x04
    private static let flag1PlayerLEDs: UInt8 = 0x10
    private static let flag2LightbarSetup: UInt8 = 0x02
    private static let lightbarSetupLightOut: UInt8 = 0x02

    /// Builds the 48 bytes that go on the wire, report id included.
    func encoded() -> [UInt8] {
        var report = [UInt8](repeating: 0, count: DualSenseReport.outputReportSize)
        report[0] = DualSenseReport.outputReportID

        if rumbleLeft != nil || rumbleRight != nil {
            // HAPTICS_SELECT routes the classic rumble values to the voice-coil
            // actuators; without it the motors stay silent on newer firmware.
            report[1] |= Self.flag0CompatibleRumble | Self.flag0HapticsSelect
            report[3] = rumbleRight ?? 0
            report[4] = rumbleLeft ?? 0
        }

        if let right = rightTrigger {
            report[1] |= Self.flag0RightTrigger
            report.replaceSubrange(11..<22, with: right.encoded)
        }
        if let left = leftTrigger {
            report[1] |= Self.flag0LeftTrigger
            report.replaceSubrange(22..<33, with: left.encoded)
        }

        if let microphoneLED {
            report[2] |= Self.flag1MicrophoneLED
            report[9] = microphoneLED ? 1 : 0
        }

        if let playerLEDs {
            report[2] |= Self.flag1PlayerLEDs
            report[43] = ledBrightness
            report[44] = playerLEDs
        }

        // Channel order verified against real hardware on 2026-09-10: the probe
        // drove 45/46/47 in turn and an observer confirmed red, then green, then
        // blue. The controller cannot be read back, so this is the only proof
        // available — do not "fix" the order without repeating that test.
        if let lightbar {
            report[2] |= Self.flag1Lightbar
            report[45] = lightbar.red
            report[46] = lightbar.green
            report[47] = lightbar.blue
        }

        // valid_flag2 + lightbar_setup exist to kill the blue fade-in the
        // controller plays on connect. Sending LIGHT_OUT here would switch the
        // bar off instead of colouring it, so it is a separate opt-in.
        if fadeOutLightbar {
            report[39] |= Self.flag2LightbarSetup
            report[42] = Self.lightbarSetupLightOut
        }

        return report
    }
}
