import Foundation
import IOKit
import IOKit.hid
import IOKit.usb
import IOKit.usb.IOUSBLib

/// Human-readable `IOReturn`. IOKit hands back bare 32-bit codes and the
/// difference between "the OS refuses to let you write" and "someone else
/// grabbed the device" is the whole diagnosis, so it must never be a bare number.
enum IOKitError {
    private static let names: [UInt32: String] = [
        0x00000000: "kIOReturnSuccess",
        0xE00002BC: "kIOReturnError",
        0xE00002BD: "kIOReturnNoMemory",
        0xE00002BE: "kIOReturnNoResources",
        0xE00002C0: "kIOReturnNoDevice",
        0xE00002C1: "kIOReturnNotPrivileged",
        0xE00002C2: "kIOReturnBadArgument",
        0xE00002C5: "kIOReturnExclusiveAccess",
        0xE00002C7: "kIOReturnUnsupported",
        0xE00002CD: "kIOReturnNotOpen",
        0xE00002CF: "kIOReturnNotWritable",
        0xE00002D5: "kIOReturnBusy",
        0xE00002D6: "kIOReturnTimeout",
        0xE00002D9: "kIOReturnNotAttached",
        0xE00002E2: "kIOReturnNotPermitted",
        0xE00002EB: "kIOReturnAborted",
        0xE00002ED: "kIOReturnNotResponding",
        0xE00002F0: "kIOReturnNotFound",
    ]

    /// Extra wording for the codes this project actually has to reason about.
    private static let hints: [UInt32: String] = [
        0xE00002C1: "система запретила доступ (нет прав / не подписано)",
        0xE00002C5: "устройство занято другим процессом эксклюзивно",
        0xE00002E2: "операция не разрешена",
        0xE00002CD: "устройство не открыто",
        0xE00002C0: "устройство отключено",
    ]

    static func describe(_ code: IOReturn) -> String {
        let raw = UInt32(bitPattern: code)
        let hex = String(format: "0x%08X", raw)
        guard let name = names[raw] else { return hex }
        if let hint = hints[raw] { return "\(hex) \(name) — \(hint)" }
        return "\(hex) \(name)"
    }
}

/// A thread that exists only to pump `CFRunLoop`. HID input callbacks are
/// delivered by a run loop and never arrive without one; the main thread cannot
/// be used because in the app it belongs to AppKit and in the CLI it blocks.
final class HIDRunLoopThread {
    private let ready = DispatchSemaphore(value: 0)
    private var runLoop: CFRunLoop?
    private var thread: Thread?
    private let stopFlag = NSLock()
    private var stopped = false

    /// Returns once the run loop exists, so callers can schedule onto it.
    func start(name: String) {
        let thread = Thread { [self] in
            runLoop = CFRunLoopGetCurrent()

            // A run loop with no input sources finishes instantly, which would
            // turn the loop below into a busy spin. A source that never fires
            // is the cheapest way to keep it parked in `mach_msg`.
            var context = CFRunLoopSourceContext()
            context.perform = { _ in }
            if let keepAlive = CFRunLoopSourceCreate(kCFAllocatorDefault, 0, &context) {
                CFRunLoopAddSource(CFRunLoopGetCurrent(), keepAlive, .defaultMode)
            }
            ready.signal()

            while !isStopped {
                CFRunLoopRunInMode(.defaultMode, 0.2, false)
            }
        }
        thread.name = name
        // Input arrives at 250 Hz; anything below this and the reports bunch up.
        thread.qualityOfService = .userInteractive
        thread.start()
        self.thread = thread
        ready.wait()
    }

    func stop() {
        stopFlag.lock()
        stopped = true
        stopFlag.unlock()
        if let runLoop {
            CFRunLoopStop(runLoop)
        }
        thread = nil
    }

    /// Runs `block` on the run loop thread and waits for it. Scheduling and
    /// unscheduling a HID device has to happen there, not on the caller.
    func sync(_ block: @escaping () -> Void) {
        guard let runLoop, runLoop !== CFRunLoopGetCurrent() else {
            block()
            return
        }
        let done = DispatchSemaphore(value: 0)
        CFRunLoopPerformBlock(runLoop, CFRunLoopMode.defaultMode.rawValue) {
            block()
            done.signal()
        }
        CFRunLoopWakeUp(runLoop)
        done.wait()
    }

    private var isStopped: Bool {
        stopFlag.lock()
        defer { stopFlag.unlock() }
        return stopped
    }
}

/// One DualSense reachable through `IOHIDFamily`.
///
/// Opened with `kIOHIDOptionsTypeNone` on purpose. `kIOHIDOptionsTypeSeizeDevice`
/// would detach the controller from the system HID stack, which kills it for
/// Steam and for every running game — the bridge mirrors the controller, it does
/// not steal it.
final class DualSenseDevice {
    /// Sony Interactive Entertainment / DualSense Wireless Controller. Matched
    /// exactly: a wider match would pull in keyboards and trigger the Input
    /// Monitoring prompt for a device we have no business touching.
    static let vendorID = 0x054C
    static let productID = 0x0CE6

    let device: IOHIDDevice
    /// Kept so the USB descriptors can be found by walking up the registry.
    let service: io_service_t

    private(set) var isOpen = false
    private var inputBuffer: UnsafeMutablePointer<UInt8>?
    private var inputBufferSize = 0
    private var handler: (([UInt8]) -> Void)?
    private var scheduledOn: HIDRunLoopThread?

    init(service: io_service_t) {
        self.service = service
        IOObjectRetain(service)
        self.device = IOHIDDeviceCreate(kCFAllocatorDefault, service)!
    }

    deinit {
        close()
        IOObjectRelease(service)
    }

    // MARK: - Discovery

    /// Every attached DualSense, USB and Bluetooth alike.
    static func findAll() -> [DualSenseDevice] {
        guard let matching = IOServiceMatching(kIOHIDDeviceKey) as NSMutableDictionary? else { return [] }
        matching[kIOHIDVendorIDKey] = vendorID
        matching[kIOHIDProductIDKey] = productID

        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator) == KERN_SUCCESS else {
            return []
        }
        defer { IOObjectRelease(iterator) }

        var found: [DualSenseDevice] = []
        var service = IOIteratorNext(iterator)
        while service != 0 {
            found.append(DualSenseDevice(service: service))
            IOObjectRelease(service)
            service = IOIteratorNext(iterator)
        }
        return found
    }

    /// The matching dictionary for `IOServiceAddMatchingNotification`, so hot
    /// plug uses exactly the same filter as the one-shot scan.
    static func matchingDictionary() -> CFDictionary? {
        guard let matching = IOServiceMatching(kIOHIDDeviceKey) as NSMutableDictionary? else { return nil }
        matching[kIOHIDVendorIDKey] = vendorID
        matching[kIOHIDProductIDKey] = productID
        return matching
    }

    // MARK: - Properties

    private func string(_ key: String) -> String? {
        IOHIDDeviceGetProperty(device, key as CFString) as? String
    }

    private func number(_ key: String) -> Int? {
        (IOHIDDeviceGetProperty(device, key as CFString) as? NSNumber)?.intValue
    }

    var product: String { string(kIOHIDProductKey) ?? "—" }
    var manufacturer: String { string(kIOHIDManufacturerKey) ?? "—" }
    /// "USB" or "Bluetooth"; the report layout differs between the two.
    var transport: String { string(kIOHIDTransportKey) ?? "—" }
    var serialNumber: String? { string(kIOHIDSerialNumberKey) }
    var vendorID: Int { number(kIOHIDVendorIDKey) ?? 0 }
    var productID: Int { number(kIOHIDProductIDKey) ?? 0 }
    var versionNumber: Int { number(kIOHIDVersionNumberKey) ?? 0 }
    var locationID: Int { number(kIOHIDLocationIDKey) ?? 0 }
    var maxInputReportSize: Int { number(kIOHIDMaxInputReportSizeKey) ?? 0 }
    var maxOutputReportSize: Int { number(kIOHIDMaxOutputReportSizeKey) ?? 0 }
    var maxFeatureReportSize: Int { number(kIOHIDMaxFeatureReportSizeKey) ?? 0 }

    var isUSB: Bool { transport.caseInsensitiveCompare("USB") == .orderedSame }

    /// Stable for the lifetime of the connection and unique across the registry.
    /// DualSense has no serial number, so this is the only way to tell two of
    /// them apart, and the only way to notice that "the" controller was swapped.
    var registryID: UInt64 {
        var id: UInt64 = 0
        guard IORegistryEntryGetRegistryEntryID(service, &id) == KERN_SUCCESS else { return 0 }
        return id
    }

    /// The raw HID report descriptor, readable without opening the device and
    /// without any entitlement. This is what the Windows side needs to build a
    /// virtual device that games accept.
    var reportDescriptor: [UInt8]? {
        guard let data = IOHIDDeviceGetProperty(device, kIOHIDReportDescriptorKey as CFString) as? Data else {
            return nil
        }
        return [UInt8](data)
    }

    // MARK: - Open / close

    func open() -> IOReturn {
        let result = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
        if result == kIOReturnSuccess { isOpen = true }
        return result
    }

    func close() {
        stopReading()
        if isOpen {
            IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
            isOpen = false
        }
    }

    // MARK: - Input

    /// Registers `handler` and schedules the device on `thread`'s run loop.
    /// The handler runs on that thread, at up to 250 Hz for USB.
    func startReading(on thread: HIDRunLoopThread, handler: @escaping ([UInt8]) -> Void) {
        stopReading()

        let size = max(maxInputReportSize, DualSenseReport.inputReportSize)
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: size)
        buffer.initialize(repeating: 0, count: size)
        inputBuffer = buffer
        inputBufferSize = size
        self.handler = handler
        scheduledOn = thread

        let context = Unmanaged.passUnretained(self).toOpaque()
        thread.sync { [device] in
            IOHIDDeviceRegisterInputReportCallback(device, buffer, size, inputReportCallback, context)
            IOHIDDeviceScheduleWithRunLoop(device, CFRunLoopGetCurrent(), CFRunLoopMode.defaultMode.rawValue)
        }
    }

    func stopReading() {
        guard let thread = scheduledOn else { return }
        scheduledOn = nil
        let buffer = inputBuffer
        let size = inputBufferSize
        thread.sync { [device] in
            if let buffer {
                IOHIDDeviceRegisterInputReportCallback(device, buffer, size, nil, nil)
            }
            IOHIDDeviceUnscheduleFromRunLoop(device, CFRunLoopGetCurrent(), CFRunLoopMode.defaultMode.rawValue)
        }
        handler = nil
        inputBuffer?.deallocate()
        inputBuffer = nil
        inputBufferSize = 0
    }

    fileprivate func deliver(_ report: [UInt8]) {
        handler?(report)
    }

    // MARK: - Output and feature reports

    /// Writes one output report. Synchronous: it blocks the calling thread for
    /// the length of the USB transaction, so it must not be called from the
    /// input thread or reports start piling up.
    @discardableResult
    func setOutputReport(_ report: [UInt8]) -> IOReturn {
        guard let id = report.first else { return kIOReturnBadArgument }
        // The report id goes in both the argument and byte 0 of the buffer.
        // That is the IOKit convention, not a redundancy we invented.
        return report.withUnsafeBufferPointer { buffer in
            IOHIDDeviceSetReport(device, kIOHIDReportTypeOutput, CFIndex(id), buffer.baseAddress!, buffer.count)
        }
    }

    /// Reads a feature report. `length` is the size the descriptor declares,
    /// report id included.
    func featureReport(id: UInt8, length: Int) -> (data: [UInt8], status: IOReturn) {
        var buffer = [UInt8](repeating: 0, count: length)
        buffer[0] = id
        var size = CFIndex(length)
        let result = buffer.withUnsafeMutableBufferPointer { pointer in
            IOHIDDeviceGetReport(device, kIOHIDReportTypeFeature, CFIndex(id), pointer.baseAddress!, &size)
        }
        guard result == kIOReturnSuccess else { return ([], result) }
        return (Array(buffer.prefix(max(0, Int(size)))), kIOReturnSuccess)
    }

    // MARK: - USB descriptors

    /// Device and configuration descriptors, straight from the USB stack.
    ///
    /// Neither call needs `USBDeviceOpen`, which is what makes this possible at
    /// all: `IOHIDFamily` owns the HID interface exclusively, so the bridge can
    /// never claim the device, but reading its descriptors is unrestricted.
    func usbDescriptors() -> (device: [UInt8]?, configuration: [UInt8]?, error: String?) {
        USBDescriptorReader.read(startingAt: service)
    }
}

/// Free function because `IOHIDReportCallback` is a C function pointer and
/// cannot capture context — the device comes back through `refcon`.
private let inputReportCallback: IOHIDReportCallback = { context, result, _, _, _, report, reportLength in
    guard let context, result == kIOReturnSuccess, reportLength > 0 else { return }
    let device = Unmanaged<DualSenseDevice>.fromOpaque(context).takeUnretainedValue()
    device.deliver(Array(UnsafeBufferPointer(start: report, count: Int(reportLength))))
}

// MARK: - USB descriptors through IOUSBLib

enum USBDescriptorReader {
    /// Walks up the IOService plane from a HID device to its USB device node and
    /// pulls the two descriptors the Windows side needs.
    static func read(startingAt hidService: io_service_t) -> (device: [UInt8]?, configuration: [UInt8]?, error: String?) {
        guard let usbDevice = findUSBDevice(from: hidService) else {
            return (nil, nil, "родительский IOUSBHostDevice не найден")
        }
        defer { IOObjectRelease(usbDevice) }

        var score: Int32 = 0
        var plugin: UnsafeMutablePointer<UnsafeMutablePointer<IOCFPlugInInterface>?>?
        let kr = IOCreatePlugInInterfaceForService(
            usbDevice,
            deviceUserClientTypeID,
            plugInInterfaceID,
            &plugin,
            &score
        )
        guard kr == KERN_SUCCESS, let plugin else {
            return (nil, nil, "IOCreatePlugInInterfaceForService: \(IOKitError.describe(kr))")
        }
        defer { _ = plugin.pointee?.pointee.Release(plugin) }

        var raw: UnsafeMutableRawPointer?
        let hr = withUnsafeMutablePointer(to: &raw) { pointer in
            plugin.pointee!.pointee.QueryInterface(
                plugin,
                CFUUIDGetUUIDBytes(deviceInterfaceID),
                pointer
            )
        }
        guard hr == S_OK, let raw else {
            return (nil, nil, "QueryInterface(IOUSBDeviceInterface): 0x\(String(UInt32(bitPattern: hr), radix: 16))")
        }
        let interface = raw.bindMemory(to: UnsafeMutablePointer<IOUSBDeviceInterface>?.self, capacity: 1)
        defer { _ = interface.pointee?.pointee.Release(interface) }

        return (
            deviceDescriptor(interface),
            configurationDescriptor(interface),
            nil
        )
    }

    private static func findUSBDevice(from hidService: io_service_t) -> io_service_t? {
        var current = hidService
        IOObjectRetain(current)
        // HID device → IOUSBHostInterface → IOUSBHostDevice is two hops, but
        // give it room in case the stack grows another layer.
        for _ in 0..<8 {
            if IOObjectConformsTo(current, "IOUSBHostDevice") != 0 || IOObjectConformsTo(current, "IOUSBDevice") != 0 {
                return current
            }
            var parent: io_registry_entry_t = 0
            guard IORegistryEntryGetParentEntry(current, kIOServicePlane, &parent) == KERN_SUCCESS else { break }
            IOObjectRelease(current)
            current = parent
        }
        IOObjectRelease(current)
        return nil
    }

    /// GET_DESCRIPTOR(device) over the default control pipe. Allowed on an
    /// unopened device, which is the only reason this works without a driver.
    private static func deviceDescriptor(_ interface: UnsafeMutablePointer<UnsafeMutablePointer<IOUSBDeviceInterface>?>) -> [UInt8]? {
        var buffer = [UInt8](repeating: 0, count: 18)
        let ok = buffer.withUnsafeMutableBytes { bytes -> Bool in
            var request = IOUSBDevRequest()
            request.bmRequestType = 0x80  // device to host, standard, device
            request.bRequest = 0x06       // GET_DESCRIPTOR
            request.wValue = 0x0100       // DEVICE descriptor, index 0
            request.wIndex = 0
            request.wLength = UInt16(bytes.count)
            request.pData = bytes.baseAddress
            let result = interface.pointee!.pointee.DeviceRequest(interface, &request)
            return result == kIOReturnSuccess && request.wLenDone > 0
        }
        return ok ? buffer : nil
    }

    /// The whole configuration block — interface, HID and endpoint descriptors
    /// included — exactly as the controller reports it.
    private static func configurationDescriptor(_ interface: UnsafeMutablePointer<UnsafeMutablePointer<IOUSBDeviceInterface>?>) -> [UInt8]? {
        var pointer: IOUSBConfigurationDescriptorPtr?
        let result = interface.pointee!.pointee.GetConfigurationDescriptorPtr(interface, 0, &pointer)
        guard result == kIOReturnSuccess, let pointer else { return nil }

        let raw = UnsafeRawPointer(pointer)
        // wTotalLength is little-endian on the wire and IOKit hands it back
        // untouched, so read the bytes rather than the struct field.
        let total = Int(raw.load(fromByteOffset: 2, as: UInt8.self))
            | (Int(raw.load(fromByteOffset: 3, as: UInt8.self)) << 8)
        guard total >= 9, total <= 4096 else { return nil }
        return Array(UnsafeRawBufferPointer(start: raw, count: total))
    }

    // The IOUSBLib and IOCFPlugIn identifiers are C macros that call
    // CFUUIDGetConstantUUIDWithBytes, which Swift cannot import. Same bytes,
    // spelled out.
    private static let deviceUserClientTypeID = CFUUIDGetConstantUUIDWithBytes(
        nil,
        0x9D, 0xC7, 0xB7, 0x80, 0x9E, 0xC0, 0x11, 0xD4,
        0xA5, 0x4F, 0x00, 0x0A, 0x27, 0x05, 0x28, 0x61
    )

    private static let deviceInterfaceID = CFUUIDGetConstantUUIDWithBytes(
        nil,
        0x5C, 0x81, 0x87, 0xD0, 0x9E, 0xF3, 0x11, 0xD4,
        0x8B, 0x45, 0x00, 0x0A, 0x27, 0x05, 0x28, 0x61
    )

    private static let plugInInterfaceID = CFUUIDGetConstantUUIDWithBytes(
        nil,
        0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4,
        0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F
    )
}
