package ru.hexarch.hexbridge.tv

import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbDeviceConnection
import android.hardware.usb.UsbEndpoint
import android.hardware.usb.UsbInterface
import android.hardware.usb.UsbManager
import ru.hexarch.hexbridge.DeviceChannel

/**
 * A controller on the television's USB port, read at the level the bridge needs:
 * its descriptors once, and its input reports for as long as it is plugged in.
 *
 * <p>Everything here is the public USB host API, so none of it wants root. That
 * is only true over a cable: a DualSense paired over Bluetooth belongs to
 * Android's input stack, which hands out buttons and axes and never the raw
 * reports — and without raw reports there are no adaptive triggers, which is the
 * entire reason for doing any of this.</p>
 */
class Controller(
    private val manager: UsbManager,
    val device: UsbDevice,
) {

    companion object {
        const val SONY = 0x054C
        const val DUALSENSE = 0x0CE6
        const val DUALSENSE_EDGE = 0x0DF2

        /** Standard GET_DESCRIPTOR on an interface, which is where a HID report descriptor lives. */
        private const val GET_DESCRIPTOR = 0x06
        private const val HID_REPORT_DESCRIPTOR = 0x22
        /** HID class GET_REPORT, and the feature report type in the high byte of wValue. */
        private const val GET_REPORT = 0x01
        private const val FEATURE = 0x03

        private const val CONTROL_TIMEOUT_MS = 1000

        fun attached(manager: UsbManager): List<UsbDevice> =
            manager.deviceList.values.filter { it.vendorId == SONY && (it.productId == DUALSENSE || it.productId == DUALSENSE_EDGE) }
    }

    private var connection: UsbDeviceConnection? = null
    private var hid: UsbInterface? = null
    private var input: UsbEndpoint? = null
    private var output: UsbEndpoint? = null

    /** How many bytes an input report is — 64 on every DualSense so far, but asked rather than assumed. */
    val inputReportSize: Int get() = input?.maxPacketSize ?: 64

    class Opened(val descriptors: DeviceChannel.Descriptors)

    /**
     * Claims the HID interface and reads everything that only has to be read once.
     *
     * <p>`forceClaimInterface` rather than the polite one: Android's own input
     * stack has already taken this controller, and until it lets go the reports
     * are going to the system instead of to the game on the other machine.</p>
     */
    fun open(): Result<Opened> {
        val connection = manager.openDevice(device)
            ?: return Result.failure(IllegalStateException("устройство не открылось — нет разрешения?"))
        this.connection = connection

        val hid = (0 until device.interfaceCount)
            .map { device.getInterface(it) }
            .firstOrNull { it.interfaceClass == UsbConstants.USB_CLASS_HID }
            ?: return fail(connection, "у устройства нет HID-интерфейса")
        this.hid = hid

        for (index in 0 until hid.endpointCount) {
            val endpoint = hid.getEndpoint(index)
            if (endpoint.type != UsbConstants.USB_ENDPOINT_XFER_INT) continue
            if (endpoint.direction == UsbConstants.USB_DIR_IN) input = endpoint else output = endpoint
        }
        if (input == null) return fail(connection, "у HID-интерфейса нет входящей interrupt-точки")

        if (!connection.claimInterface(hid, true)) return fail(connection, "интерфейс занят и не отдаётся")

        // Device descriptor first, then the configuration one: this is exactly
        // the byte stream the far side expects to build a virtual device from,
        // and the configuration descriptor says its own total length.
        val raw = connection.rawDescriptors ?: return fail(connection, "дескрипторы не прочитались")
        if (raw.size < 18) return fail(connection, "дескриптор устройства короче 18 байт")
        val deviceDescriptor = raw.copyOfRange(0, 18)
        val total = (raw[20].toInt() and 0xFF) or ((raw[21].toInt() and 0xFF) shl 8)
        if (total <= 0 || 18 + total > raw.size) return fail(connection, "конфигурационный дескриптор не помещается")
        val configuration = raw.copyOfRange(18, 18 + total)

        val reportDescriptor = readReportDescriptor(connection, hid.id)
            ?: return fail(connection, "HID report descriptor не прочитался")

        val features = LinkedHashMap<Int, ByteArray>()
        for (report in DeviceChannel.REQUIRED_FEATURE_REPORTS) {
            // One byte less than the block will be: a control GET_REPORT does not
            // return the report number — it went out in wValue — and the packet
            // builder puts it back. See DeviceChannel.FEATURE_REPORT_SIZES.
            val size = DeviceChannel.FEATURE_REPORT_SIZES.getValue(report) - 1
            val body = readFeatureReport(connection, hid.id, report, size)
                ?: return fail(connection, "feature-отчёт 0x%02X не прочитался".format(report))
            features[report] = body
        }

        return Result.success(
            Opened(
                DeviceChannel.Descriptors(
                    device = deviceDescriptor,
                    configuration = configuration,
                    hidReport = reportDescriptor,
                    featureReports = features,
                )
            )
        )
    }

    private fun fail(connection: UsbDeviceConnection, why: String): Result<Opened> {
        close()
        return Result.failure(IllegalStateException(why))
    }

    private fun readReportDescriptor(connection: UsbDeviceConnection, interfaceId: Int): ByteArray? {
        // 4 KiB is more than any report descriptor and the transfer answers with
        // what it has; a DualSense's is 273 bytes.
        val buffer = ByteArray(4096)
        val read = connection.controlTransfer(
            UsbConstants.USB_DIR_IN or UsbConstants.USB_TYPE_STANDARD or 0x01,
            GET_DESCRIPTOR,
            (HID_REPORT_DESCRIPTOR shl 8),
            interfaceId,
            buffer,
            buffer.size,
            CONTROL_TIMEOUT_MS,
        )
        return if (read > 0) buffer.copyOf(read) else null
    }

    private fun readFeatureReport(connection: UsbDeviceConnection, interfaceId: Int, report: Int, size: Int): ByteArray? {
        val buffer = ByteArray(size)
        val read = connection.controlTransfer(
            UsbConstants.USB_DIR_IN or UsbConstants.USB_TYPE_CLASS or 0x01,
            GET_REPORT,
            (FEATURE shl 8) or report,
            interfaceId,
            buffer,
            buffer.size,
            CONTROL_TIMEOUT_MS,
        )
        // Short is as bad as absent here: zeros in the calibration report divide
        // by zero inside games, so a partial read is a reason not to forward the
        // controller at all.
        return if (read == size) buffer else null
    }

    /**
     * Blocks until the next input report, or until the timeout runs out.
     *
     * Returns the number of bytes, 0 on a timeout, and -1 when the pipe is gone —
     * which is how a controller being unplugged arrives here.
     */
    fun readInto(buffer: ByteArray, timeoutMs: Int): Int {
        val connection = connection ?: return -1
        val endpoint = input ?: return -1
        return connection.bulkTransfer(endpoint, buffer, buffer.size, timeoutMs)
    }

    /** An output report — rumble, adaptive triggers, the lightbar — on its way to the grip. */
    fun write(report: ByteArray): Boolean {
        val connection = connection ?: return false
        val endpoint = output ?: return false
        return connection.bulkTransfer(endpoint, report, report.size, CONTROL_TIMEOUT_MS) == report.size
    }

    fun close() {
        val connection = connection ?: return
        hid?.let { connection.releaseInterface(it) }
        connection.close()
        this.connection = null
        hid = null
        input = null
        output = null
    }
}
