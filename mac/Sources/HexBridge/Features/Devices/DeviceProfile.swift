import Foundation

/// How one chosen device is remembered between launches.
///
/// Model, plus the serial number when the device has one. A DualSense reports
/// `iSerial = 0` and so do most pads, wheels and pedal sets, so for them the
/// identity is the model and ticking the box forwards every copy of it — which
/// is what somebody who owns two identical pads meant by ticking it.
struct DeviceIdentity: Hashable, Codable, CustomStringConvertible {
    let vendorID: Int
    let productID: Int
    /// Nil when the device has none, which is the common case.
    let serial: String?

    var description: String {
        let model = String(format: "%04X:%04X", vendorID, productID)
        guard let serial, !serial.isEmpty else { return model }
        return "\(model):\(serial)"
    }

    /// Round-trips `description`. Returns nil for a string that never came from
    /// one, so a hand-edited config degrades to "that device is not selected"
    /// rather than to a crash.
    init?(_ text: String) {
        let parts = text.split(separator: ":", maxSplits: 2, omittingEmptySubsequences: false)
        guard parts.count >= 2,
              let vendor = Int(parts[0], radix: 16),
              let product = Int(parts[1], radix: 16) else { return nil }
        vendorID = vendor
        productID = product
        serial = parts.count > 2 && !parts[2].isEmpty ? String(parts[2]) : nil
    }

    init(vendorID: Int, productID: Int, serial: String? = nil) {
        self.vendorID = vendorID
        self.productID = productID
        self.serial = serial?.isEmpty == true ? nil : serial
    }

    /// VID/PID without the serial, for showing next to the name.
    var modelDescription: String { String(format: "%04X:%04X", vendorID, productID) }
}

/// What a device is for, as far as its HID usages are willing to say.
///
/// This exists for exactly one reason, and it is not tidiness. Devices are
/// opened without `kIOHIDOptionsTypeSeizeDevice`, so the Mac goes on receiving
/// everything it forwards. For a pad that is the whole point — Steam keeps
/// working. For a keyboard it means every keystroke lands on both machines at
/// once, and the user has to be told before, not after.
enum DeviceCategory: String, Codable, Sendable {
    case gamepad
    case keyboard
    case pointer
    case other

    /// Usage page 0x01 (Generic Desktop) carries all four of the usages that
    /// matter here; anything else is `other` and gets no opinion from us.
    static func of(_ pairs: [(page: Int, usage: Int)]) -> DeviceCategory {
        let desktop = pairs.filter { $0.page == 0x01 }.map(\.usage)
        // Keyboard first: a pad with a keyboard collection still types.
        if desktop.contains(6) || desktop.contains(7) { return .keyboard }
        if desktop.contains(2) || desktop.contains(1) { return .pointer }
        if desktop.contains(4) || desktop.contains(5) || desktop.contains(8) { return .gamepad }
        return .other
    }

    var label: String {
        switch self {
        case .gamepad: return "Контроллер"
        case .keyboard: return "Клавиатура"
        case .pointer: return "Мышь или трекпад"
        case .other: return "HID-устройство"
        }
    }

    var symbolName: String {
        switch self {
        case .gamepad: return "gamecontroller"
        case .keyboard: return "keyboard"
        case .pointer: return "computermouse"
        case .other: return "cable.connector"
        }
    }

    /// True for the devices that will type or click on both machines at once.
    /// Wheels, pedals and HOTAS are ordinary hardware and get no warning.
    var warnsAboutDoubleInput: Bool { self == .keyboard || self == .pointer }

    var doubleInputWarning: String? {
        switch self {
        case .keyboard:
            return "Клавиатура остаётся подключённой к Mac: набранное будет печататься одновременно здесь и на Windows."
        case .pointer:
            return "Мышь остаётся подключённой к Mac: курсор будет двигаться одновременно здесь и на Windows."
        case .gamepad, .other:
            return nil
        }
    }
}

/// Why a device is or is not offered for forwarding.
enum DeviceEligibility: Equatable {
    case eligible
    /// The keyboard and trackpad in the lid. Never offered at all: forwarding
    /// them would double every keystroke on the machine the user is typing on,
    /// with no way to stop except walking to the other keyboard.
    case builtIn
    /// Bluetooth, SPI, virtual — anything with no USB device behind it. The
    /// receiver builds a *USB* device out of real descriptors, and there are
    /// none to send.
    case notUSB

    var reason: String? {
        switch self {
        case .eligible: return nil
        case .builtIn: return "Встроенное устройство Mac — не пробрасывается"
        case .notUSB: return "Не USB — дескрипторы прочитать нельзя"
        }
    }

    static func of(_ device: HIDDevice) -> DeviceEligibility {
        if device.isBuiltIn { return .builtIn }
        guard device.isUSB, device.hasUSBParent else { return .notUSB }
        return .eligible
    }
}

/// Extra knowledge about one model, switched on by VID/PID.
///
/// The device channel itself knows nothing about any of this: `DEV_ATTACH`
/// carries real descriptors and `DEV_IN`/`DEV_OUT` carry reports as they came.
/// A profile only adds two things a generic HID passthrough cannot invent —
/// which feature reports to snapshot before the device is announced, and how to
/// turn an input report into something a screen can draw.
struct DeviceProfile {
    enum Kind: String, Sendable {
        case dualSense
    }

    let kind: Kind
    let name: String
    /// Feature reports to read once and ship inside `DEV_ATTACH`, so the
    /// receiver can answer `GET_REPORT` without a network round trip.
    let featureReports: [(id: UInt8, length: Int)]
    /// Decodes an input report for the visualisation. Never on the forwarding
    /// path: the bytes go out untouched whatever this returns.
    let parse: ([UInt8]) -> GamepadState?

    /// Sony Interactive Entertainment / DualSense Wireless Controller.
    static let dualSense = DeviceProfile(
        kind: .dualSense,
        name: "DualSense",
        // 0x20 firmware, 0x09 MAC addresses, 0x05 sensor calibration. Zeros in
        // 0x05 are a divide by zero inside games, so the snapshot is not
        // decoration — it is what makes the pad acceptable to Steam.
        featureReports: [(0x20, 64), (0x09, 20), (0x05, 41)],
        parse: GamepadState.parse
    )

    /// Sony Interactive Entertainment.
    static let dualSenseVendorID = 0x054C
    /// DualSense (CFI-ZCT1) and DualSense Edge (CFI-ZCP1) share the report layout.
    static let dualSenseProductIDs = [0x0CE6, 0x0DF2]

    /// The profile for a model, or nil — and nil is the ordinary case. A device
    /// with no profile is forwarded exactly as well as one with a profile; it
    /// just cannot be drawn as anything but a name and an activity light.
    static func of(vendorID: Int, productID: Int) -> DeviceProfile? {
        if vendorID == dualSenseVendorID, dualSenseProductIDs.contains(productID) { return dualSense }
        return nil
    }
}
