package ru.hexarch.hexbridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The payloads of the device channel.
 *
 * The line of responsibility runs along HID, which is the single fact that makes
 * a television client possible at all: this side sends descriptors and reports,
 * and Windows assembles the virtual USB device and speaks USB/IP on its own. A
 * client therefore needs raw HID access and nothing more — no USB/IP, no driver,
 * no kernel of its own.
 */
object DeviceChannel {

    /** The blocks a DEV_ATTACH is made of. */
    object Block {
        const val DEVICE_DESCRIPTOR = 1
        const val CONFIGURATION_DESCRIPTOR = 2
        const val HID_REPORT_DESCRIPTOR = 3
        const val FEATURE_REPORT = 4
    }

    /**
     * The three feature reports without which a controller is not forwarded at all.
     *
     * `0x05` is sensor calibration, and zeros in it divide by zero inside games —
     * so an unreadable snapshot is a reason to leave the device alone rather than
     * to send something hopeful.
     */
    val REQUIRED_FEATURE_REPORTS = intArrayOf(0x20, 0x09, 0x05)

    /**
     * How long each of those reports is **with its number counted in**, which is
     * the length the block ends up being.
     *
     * This is the seam where two implementations drift apart without either of
     * them looking wrong. macOS hands back a feature report with the report
     * number already in byte zero, and the Mac puts that buffer into the block
     * untouched. A USB control `GET_REPORT` does not return the number at all —
     * it travelled in `wValue`, and the host is expected to know it. So a client
     * reading over USB must ask for one byte less and let [attach] put the number
     * back, or Windows hands games a report one byte too long with its first byte
     * repeated, and nothing anywhere reports an error.
     */
    val FEATURE_REPORT_SIZES = mapOf(0x20 to 64, 0x09 to 20, 0x05 to 41)

    class Descriptors(
        val device: ByteArray,
        val configuration: ByteArray,
        val hidReport: ByteArray,
        /** Report number to its contents, for the snapshot blocks. */
        /**
         * Report number to its contents **without** the number, which [attach]
         * puts back. See [FEATURE_REPORT_SIZES].
         */
        val featureReports: Map<Int, ByteArray>,
    )

    /**
     * `DEV_ATTACH`: everything the far side needs to build the virtual device
     * without asking a single question back.
     *
     * The questions are the point. A `GET_REPORT` answered over the network would
     * land exactly while a game is deciding whether to accept the controller, so
     * the static answers travel up front.
     */
    fun attach(device: Int, descriptors: Descriptors): ByteArray {
        val blocks = ArrayList<Pair<Int, ByteArray>>(3 + descriptors.featureReports.size)
        blocks += Block.DEVICE_DESCRIPTOR to descriptors.device
        blocks += Block.CONFIGURATION_DESCRIPTOR to descriptors.configuration
        blocks += Block.HID_REPORT_DESCRIPTOR to descriptors.hidReport
        for ((report, body) in descriptors.featureReports) {
            blocks += Block.FEATURE_REPORT to (byteArrayOf(report.toByte()) + body)
        }

        val size = 2 + blocks.sumOf { 3 + it.second.size }
        val out = ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)
        out.put(device.toByte())
        out.put(blocks.size.toByte())
        for ((type, body) in blocks) {
            out.put(type.toByte())
            out.putShort(body.size.toShort())
            out.put(body)
        }
        return out.array()
    }

    /** `DEV_IN`: one input report as it came off the wire, report id included. */
    fun input(device: Int, reportNumber: Int, report: ByteArray, length: Int = report.size): ByteArray {
        val out = ByteBuffer.allocate(5 + length).order(ByteOrder.LITTLE_ENDIAN)
        out.put(device.toByte())
        out.putInt(reportNumber)
        out.put(report, 0, length)
        return out.array()
    }

    fun detach(device: Int): ByteArray = byteArrayOf(device.toByte())

    /** The device number inside a `DEV_ACK`, or null if there is not one there. */
    fun ackedDevice(payload: ByteArray): Int? =
        if (payload.isEmpty()) null else payload[0].toInt() and 0xFF

    class Output(val device: Int, val report: ByteArray)

    /** A `DEV_OUT` — rumble, adaptive triggers, the lightbar — split into its two parts. */
    fun output(payload: ByteArray): Output? {
        if (payload.size < 2) return null
        return Output(payload[0].toInt() and 0xFF, payload.copyOfRange(1, payload.size))
    }

    class HapticBlock(val device: Int, val channels: Int, val block: Long, val pcm: ByteArray)

    /**
     * A `HAPTIC` block: 5 ms of S16LE stereo for the two voice coils in the grips.
     *
     * The PS5's signature haptics is sound rather than rumble bytes, which is why
     * it has a type of its own: `DEV_OUT` carries the occasional command, and this
     * carries 384 kB/s.
     */
    fun haptic(payload: ByteArray): HapticBlock? {
        if (payload.size < 6) return null
        val head = ByteBuffer.wrap(payload, 0, 6).order(ByteOrder.LITTLE_ENDIAN)
        val device = head.get().toInt() and 0xFF
        val channels = head.get().toInt() and 0xFF
        val block = head.int.toLong() and 0xFFFFFFFFL
        return HapticBlock(device, channels, block, payload.copyOfRange(6, payload.size))
    }

    /** `HELLO`, sent once a second so the far side and any relay know we are here. */
    fun hello(name: String, role: Int = 0, now: Long = System.currentTimeMillis()): ByteArray {
        val label = name.toByteArray(Charsets.UTF_8).let { if (it.size > 255) it.copyOf(255) else it }
        val out = ByteBuffer.allocate(10 + label.size).order(ByteOrder.LITTLE_ENDIAN)
        out.putLong(now)
        out.put(role.toByte())
        out.put(label.size.toByte())
        out.put(label)
        return out.array()
    }
}
