package ru.hexarch.hexbridge

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * The device channel's payloads.
 *
 * These are the bytes Windows turns into a virtual USB device, and it does that
 * without asking a single question back — so anything wrong here is not a
 * negotiation that fails, it is a controller that appears and behaves oddly.
 */
class DeviceChannelTest {

    private val descriptors = DeviceChannel.Descriptors(
        device = ByteArray(18) { 0x11 },
        configuration = ByteArray(227) { 0x22 },
        hidReport = ByteArray(273) { 0x33 },
        featureReports = linkedMapOf(
            0x20 to ByteArray(63) { 0x44 },
            0x09 to ByteArray(19) { 0x55 },
            0x05 to ByteArray(40) { 0x66 },
        ),
    )

    @Test
    fun `an attach carries the device number and its blocks`() {
        val attach = DeviceChannel.attach(2, descriptors)

        assertEquals(2, attach[0].toInt())
        assertEquals(6, attach[1].toInt(), "три дескриптора и три feature-отчёта")
    }

    @Test
    fun `every block says its own length`() {
        val attach = DeviceChannel.attach(0, descriptors)

        var offset = 2
        val seen = ArrayList<Pair<Int, Int>>()
        repeat(attach[1].toInt()) {
            val type = attach[offset].toInt()
            val length = ByteBuffer.wrap(attach, offset + 1, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt()
            seen += type to length
            offset += 3 + length
        }

        assertEquals(attach.size, offset, "блоки должны сойтись ровно в конец пакета")
        assertEquals(
            listOf(1 to 18, 2 to 227, 3 to 273, 4 to 64, 4 to 20, 4 to 41),
            seen,
            "feature-блок длиннее своего отчёта на байт номера",
        )
    }

    /**
     * The report number goes first inside a feature block, and the three reports
     * below are the ones without which the device is not forwarded at all: zeros
     * in the calibration report divide by zero inside games.
     */
    @Test
    fun `a feature block begins with its report number`() {
        val attach = DeviceChannel.attach(0, descriptors)

        val first = attach.indexOfFirst { it == 4.toByte() }.let { start ->
            // The first type-4 block's data begins three bytes after its type.
            attach[start + 3].toInt()
        }
        assertEquals(0x20, first)
        assertContentEquals(intArrayOf(0x20, 0x09, 0x05), DeviceChannel.REQUIRED_FEATURE_REPORTS)
    }

    @Test
    fun `an input report keeps its number and its bytes`() {
        val report = ByteArray(64) { it.toByte() }

        val payload = DeviceChannel.input(1, 0x01020304, report)

        assertEquals(1, payload[0].toInt())
        assertEquals(0x01020304, ByteBuffer.wrap(payload, 1, 4).order(ByteOrder.LITTLE_ENDIAN).int)
        assertContentEquals(report, payload.copyOfRange(5, payload.size))
    }

    @Test
    fun `an output report is split from its device number`() {
        val out = assertNotNull(DeviceChannel.output(byteArrayOf(3, 0x02, 0x7F, 0x10)))

        assertEquals(3, out.device)
        assertContentEquals(byteArrayOf(0x02, 0x7F, 0x10), out.report)
    }

    @Test
    fun `a haptic block is device, channels, number and PCM`() {
        val pcm = ByteArray(960) { it.toByte() }
        val payload = ByteBuffer.allocate(6 + pcm.size).order(ByteOrder.LITTLE_ENDIAN)
            .put(0).put(2).putInt(77).put(pcm).array()

        val block = assertNotNull(DeviceChannel.haptic(payload))

        assertEquals(0, block.device)
        assertEquals(2, block.channels)
        assertEquals(77L, block.block)
        assertContentEquals(pcm, block.pcm)
    }

    /** Short payloads come off a network and are not to be trusted with an index. */
    @Test
    fun `anything too short is refused rather than indexed`() {
        assertNull(DeviceChannel.output(byteArrayOf(1)))
        assertNull(DeviceChannel.haptic(byteArrayOf(0, 2, 1, 0)))
        assertNull(DeviceChannel.ackedDevice(byteArrayOf()))
    }

    @Test
    fun `a hello carries the time, the role and the name`() {
        val hello = DeviceChannel.hello("Mi Box", role = 0, now = 1_700_000_000_000)

        val buffer = ByteBuffer.wrap(hello).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(1_700_000_000_000, buffer.long)
        assertEquals(0, buffer.get().toInt())
        assertEquals(6, buffer.get().toInt())
        assertEquals("Mi Box", String(hello, 10, 6))
    }

    @Test
    fun `a name longer than the length byte can say is cut`() {
        val hello = DeviceChannel.hello("я".repeat(400))

        assertEquals(255, hello[9].toInt() and 0xFF)
        assertEquals(10 + 255, hello.size)
    }
}

/**
 * The one place a client reading over USB and a client reading over IOKit will
 * disagree without either of them looking wrong. Split out so that the number it
 * pins is impossible to change by accident.
 */
class FeatureReportSizeTest {

    @Test
    fun `a block is as long as the report, number counted in`() {
        val descriptors = DeviceChannel.Descriptors(
            device = ByteArray(18),
            configuration = ByteArray(227),
            hidReport = ByteArray(273),
            featureReports = DeviceChannel.REQUIRED_FEATURE_REPORTS.associateWith { report ->
                ByteArray(DeviceChannel.FEATURE_REPORT_SIZES.getValue(report) - 1)
            },
        )

        val attach = DeviceChannel.attach(0, descriptors)

        var offset = 2
        val lengths = LinkedHashMap<Int, Int>()
        repeat(attach[1].toInt()) {
            val type = attach[offset].toInt()
            val length = ByteBuffer.wrap(attach, offset + 1, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt()
            if (type == DeviceChannel.Block.FEATURE_REPORT) {
                lengths[attach[offset + 3].toInt() and 0xFF] = length
            }
            offset += 3 + length
        }

        // The lengths the Mac produces from IOKit, which Windows already accepts.
        assertEquals(mapOf(0x20 to 64, 0x09 to 20, 0x05 to 41), lengths)
    }
}
