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
            log("HID-устройств не найдено. Подключите контроллер, руль или другой USB-девайс и повторите.")
            return
        }

        let eligibility = DeviceEligibility.of(device)
        if let reason = eligibility.reason {
            log("ВНИМАНИЕ: \(reason). Проброс этого устройства невозможен, но диагностика ниже всё равно выполнится.")
        }

        describe(device, log: log)
        readDescriptors(device, log: log)

        let openResult = device.open()
        log("")
        log("открытие (kIOHIDOptionsTypeNone): \(IOKitError.describe(openResult))")
        guard openResult == kIOReturnSuccess else {
            log("без открытия ни чтение, ни запись невозможны — дальше идти незачем")
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
            log("— тест записи output-репорта —")
            log("Пропущен: содержимое output-репорта зависит от модели, а профиля для этой нет.")
            log("Проброс от этого не страдает — DEV_OUT переносит репорт как есть, не заглядывая внутрь.")
        }
    }

    // MARK: - Identity

    private static func describe(_ device: HIDDevice, log: (String) -> Void) {
        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
        log("")
        log("устройство:      \(device.displayName)")
        log("производитель:   \(device.manufacturer)")
        log("тип:             \(device.category.label)")
        log("профиль:         \(profile?.name ?? "нет — пробрасывается как есть, разбор состояния недоступен")")
        log("транспорт:       \(device.transport)")
        log("VID/PID:         0x\(hex16(device.vendorID)) / 0x\(hex16(device.productID))")
        log("версия:          0x\(hex16(device.versionNumber))")
        log("серийный номер:  \(device.serialNumber ?? "нет (iSerial = 0)")")
        log("идентификатор:   \(device.identity)")
        log("locationID:      0x\(String(device.locationID, radix: 16, uppercase: true))  интерфейс \(device.interfaceNumber)")
        log("размер репортов: input \(device.maxInputReportSize), output \(device.maxOutputReportSize), feature \(device.maxFeatureReportSize)")
        if device.category.warnsAboutDoubleInput {
            log("ДВОЙНОЙ ВВОД:    \(device.category.doubleInputWarning ?? "")")
        }
    }

    // MARK: - Descriptors

    private static func readDescriptors(_ device: HIDDevice, log: (String) -> Void) {
        log("")
        log("— дескрипторы (без открытия устройства и без прав) —")

        if let report = device.reportDescriptor {
            log("HID report descriptor: \(report.count) байт  \(preview(report))")
        } else {
            log("HID report descriptor: свойство \(kIOHIDReportDescriptorKey) недоступно")
        }

        let usb = device.usbDescriptors()
        if let error = usb.error {
            log("USB: \(error)")
        }
        if let descriptor = usb.device {
            log("device descriptor:     \(descriptor.count) байт   \(preview(descriptor))")
            if descriptor.count >= 18 {
                let vid = Int(descriptor[8]) | (Int(descriptor[9]) << 8)
                let pid = Int(descriptor[10]) | (Int(descriptor[11]) << 8)
                let bcd = Int(descriptor[12]) | (Int(descriptor[13]) << 8)
                log("  из дескриптора: idVendor 0x\(hex16(vid)), idProduct 0x\(hex16(pid)), bcdDevice 0x\(hex16(bcd)), iSerial \(descriptor[16])")
            }
        } else {
            log("device descriptor:     не прочитан")
        }
        if let descriptor = usb.configuration {
            log("config descriptor:     \(descriptor.count) байт  \(preview(descriptor))")
        } else {
            log("config descriptor:     не прочитан")
        }

        let total = (device.reportDescriptor?.count ?? 0) + (usb.device?.count ?? 0) + (usb.configuration?.count ?? 0)
        log("итого для DEV_ATTACH:  \(total) байт")
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
        log("— чтение input-репортов, \(Int(seconds)) с —")

        Thread.sleep(forTimeInterval: seconds)

        let snapshot = stats.snapshot()
        guard snapshot.count > 0 else {
            log("репортов не пришло. Контроллер открыт, но данные не идут — проверьте кабель.")
            return
        }

        // The measured window is first-to-last report, not the sleep: the first
        // report arrives some unknown time after scheduling.
        let rate = snapshot.seconds > 0 ? Double(snapshot.count - 1) / snapshot.seconds : 0
        log(String(format: "репортов: %d за %.3f с → %.1f Гц", snapshot.count, snapshot.seconds, rate))
        log("длины репортов: \(snapshot.lengths.map(String.init).joined(separator: ", "))")
        log("report id: \(snapshot.ids.map { "0x" + hexByte($0) }.joined(separator: ", "))")
        if profile != nil {
            log("пропусков в счётчике: \(snapshot.gaps)")
        }

        guard let parse = profile?.parse else {
            log("последний репорт: \(preview(snapshot.last, limit: 32))")
            log("Разбор состояния недоступен: профиля для этой модели нет. На проброс это не влияет.")
            return
        }
        guard let state = parse(snapshot.last) else {
            log("разобрать репорт не удалось: \(preview(snapshot.last))")
            return
        }
        printState(state, log: log)
    }

    private static func printState(_ state: GamepadState, log: (String) -> Void) {
        let left = state.left.normalized
        let right = state.right.normalized
        log(String(format: "стик L:  x %+.2f  y %+.2f  (сырьё %3d %3d)", left.x, left.y, state.left.x, state.left.y))
        log(String(format: "стик R:  x %+.2f  y %+.2f  (сырьё %3d %3d)", right.x, right.y, state.right.x, state.right.y))
        log("триггеры: L2 \(state.l2)  R2 \(state.r2)")
        log("d-pad: \(state.dpad.label)  (\(state.dpad))")
        let pressed = state.buttons.labels
        log("кнопки: \(pressed.isEmpty ? "ничего не нажато" : pressed.joined(separator: " "))")
        if state.vendorButtons != 0 {
            log("доп. кнопки (Edge): 0x\(hexByte(state.vendorButtons))")
        }
        log("гироскоп:      x \(state.gyro.x)  y \(state.gyro.y)  z \(state.gyro.z)")
        log("акселерометр:  x \(state.accel.x)  y \(state.accel.y)  z \(state.accel.z)")
        log("таймстамп сенсоров: \(state.timestamp)")
        for (index, point) in state.touch.enumerated() {
            let description = point.active ? "x \(point.x)  y \(point.y)  id \(point.id)" : "нет касания"
            log("тачпад \(index + 1): \(description)")
        }
        log("батарея: \(state.batteryDescription)  (уровень \(state.batteryLevel), статус 0x\(String(state.batteryStatus, radix: 16)))")
    }

    // MARK: - Feature reports

    /// The reports a game asks for before it accepts the device as genuine.
    /// They travel inside DEV_ATTACH, so if they cannot be read here the Windows
    /// side has nothing to answer GET_REPORT with. Which reports matter is the
    /// profile's business — a generic HID device has no such list.
    private static func readFeatureReports(_ device: HIDDevice, profile: DeviceProfile?, log: (String) -> Void) {
        log("")
        log("— feature-репорты —")

        guard let profile, !profile.featureReports.isEmpty else {
            log("Профиля для этой модели нет — снимки feature-репортов не отправляются.")
            return
        }

        let names: [UInt8: String] = [0x20: "прошивка", 0x09: "MAC-адреса", 0x05: "калибровка гироскопа"]
        let wanted = profile.featureReports.map {
            (id: $0.id, length: $0.length, what: names[$0.id] ?? "снимок")
        }

        for report in wanted {
            let result = device.featureReport(id: report.id, length: report.length)
            guard result.status == kIOReturnSuccess else {
                log("0x\(hexByte(report.id)) \(report.what): ОШИБКА \(IOKitError.describe(result.status))")
                continue
            }
            log("0x\(hexByte(report.id)) \(report.what): \(result.data.count) байт (ожидается \(report.length))  \(preview(result.data))")

            if report.id == 0x20, result.data.count >= 20 {
                let date = ascii(result.data[1..<12])
                let time = ascii(result.data[12..<20])
                log("     сборка прошивки: \(date) \(time)")
            }
            if report.id == 0x09, result.data.count >= 7 {
                // Stored little-endian, so the printed MAC is the reverse.
                let mac = (1...6).reversed().map { hexByte(result.data[$0]) }.joined(separator: ":")
                log("     MAC контроллера: \(mac)")
            }
        }
    }

    // MARK: - Output

    /// The question this whole subcommand was written for.
    private static func testOutput(_ device: HIDDevice, motion: InputStats, log: (String) -> Void) {
        log("")
        log("— тест записи output-репорта 0x02 —")
        log("СМОТРИТЕ НА КОНТРОЛЛЕР: дальше подсветка и вибрация должны меняться.")

        var output = GamepadOutput()
        output.lightbar = .init(red: 255, green: 0, blue: 0)
        let report = output.encoded()
        log("длина репорта: \(report.count) байт (ID 0x\(hexByte(report[0])) + \(report.count - 1))")

        let first = device.setOutputReport(report)
        log("IOHIDDeviceSetReport → \(IOKitError.describe(first))")

        guard first == kIOReturnSuccess else {
            log("")
            log("ЗАПИСЬ ЗАБЛОКИРОВАНА. Адаптивные триггеры, подсветка и вибрация недоступны.")
            return
        }

        // A returned success still only means the transaction was accepted, so
        // the rest of this is paced for a human to watch.
        log("СЕЙЧАС: подсветка должна быть КРАСНОЙ (1 с)")
        Thread.sleep(forTimeInterval: 1)

        var status: [IOReturn] = [first]
        for (color, name) in [(GamepadOutput.Color(red: 0, green: 255, blue: 0), "ЗЕЛЁНОЙ"),
                              (GamepadOutput.Color(red: 0, green: 0, blue: 255), "СИНЕЙ")] {
            var step = GamepadOutput()
            step.lightbar = color
            let result = device.setOutputReport(step.encoded())
            status.append(result)
            log("СЕЙЧАС: подсветка должна быть \(name) (1 с) — \(IOKitError.describe(result))")
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
        log("СЕЙЧАС: короткая ВИБРАЦИЯ, 0.4 с — \(IOKitError.describe(rumbleResult))")
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
        log("вибрация выключена, подсветка приглушена — \(IOKitError.describe(restore))")

        log("")
        log(String(
            format: "гироскоп в покое: σ %.1f (%d репортов), под вибрацией: σ %.1f (%d репортов)",
            quiet.sigma, quiet.samples, shaking.sigma, shaking.samples
        ))
        // A return code only proves the USB stack took the packet. The motors
        // moving the gyro proves the controller acted on it.
        let confirmed = shaking.samples > 10 && shaking.sigma > max(50, quiet.sigma * 4)
        if confirmed {
            log("вибрация подтверждена приборно: гироскоп зафиксировал тряску от моторов.")
        } else {
            log("тряски по гироскопу не видно — либо контроллер лежал в руке, либо мотор не отработал.")
        }

        let failures = status.filter { $0 != kIOReturnSuccess }
        log("")
        if failures.isEmpty {
            log("ЗАПИСЬ РАБОТАЕТ: все \(status.count) вызовов SetReport вернули успех\(confirmed ? ", эффект подтверждён" : "").")
            if !confirmed {
                log("Если подсветка при этом не менялась — код принят, но эффекта нет; сообщите об этом.")
            }
        } else {
            log("ЧАСТИЧНО: \(failures.count) из \(status.count) вызовов SetReport не прошли.")
        }
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
            log("HID-устройств не найдено")
            return
        }
        for (index, device) in devices.enumerated() {
            let eligibility = DeviceEligibility.of(device)
            let note = eligibility.reason.map { "   [\($0)]" } ?? ""
            let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)
            log("\(index)  \(device.displayName)  \(device.transport)\(note)")
            log("   \(device.identity)  \(device.category.label)\(profile.map { " · профиль \($0.name)" } ?? "")")
            log("   VID/PID 0x\(hex16(device.vendorID))/0x\(hex16(device.productID))  версия 0x\(hex16(device.versionNumber))  locationID 0x\(String(device.locationID, radix: 16))")
            log("   репорты: input \(device.maxInputReportSize), output \(device.maxOutputReportSize), feature \(device.maxFeatureReportSize)  дескриптор \(device.reportDescriptor?.count ?? 0) байт")
            if let warning = device.category.doubleInputWarning, eligibility == .eligible {
                log("   ВНИМАНИЕ: \(warning)")
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
            log("HID-устройств не найдено. Подключите устройство по USB.")
            return
        }
        let profile = DeviceProfile.of(vendorID: device.vendorID, productID: device.productID)

        let openResult = device.open()
        guard openResult == kIOReturnSuccess else {
            log("открыть не удалось: \(IOKitError.describe(openResult))")
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

        log("\(device.displayName) через \(device.transport). Ctrl-C для выхода.")

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
                let line = String(format: "%5.0f Гц │ %@", rate, preview(snapshot.last, limit: 24))
                print(line.padding(toLength: max(line.count, 96), withPad: " ", startingAt: 0), terminator: "\r")
                fflush(stdout)
                continue
            }
            guard let state = parse(snapshot.last) else { continue }
            let left = state.left.normalized
            let right = state.right.normalized
            let pressed = state.buttons.labels.joined(separator: " ")
            let line = String(
                format: "%5.0f Гц │ L %+.2f %+.2f │ R %+.2f %+.2f │ L2 %3d R2 %3d │ %@ │ %@",
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
