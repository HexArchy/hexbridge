package ru.hexarch.hexbridge

import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The contract, from the third implementation's side.
 *
 * Two implementations that agree with themselves and not with each other is the
 * failure this whole file exists to catch, so the numbers pinned here are not
 * this code's own output: the same derivation was run live against the real
 * Windows receiver, which answered PONG and then assembled a virtual DualSense
 * from a DEV_ATTACH built by the code below. A packet whose room or nonce were
 * wrong would have been dropped without a word.
 */
class WireTest {

    private val psk = ByteArray(32) { it.toByte() }
    private val room = Wire.roomId(psk)

    @Test
    fun `room id matches the other two implementations`() {
        // SHA256("hexbridge-room-v1" || psk)[0..8] read little-endian, over the
        // raw key bytes and never over their base64.
        assertEquals(-8855997442730828616L, Wire.roomId(psk))
        assertEquals(
            Wire.roomId(psk),
            Wire.roomId(Base64.getDecoder().decode("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")),
        )
    }

    @Test
    fun `a header survives the round trip`() {
        val header = Wire.Header(type = Wire.Type.DEV_IN, flags = 3, room = room, session = -7, seq = 99)
        val raw = ByteArray(Wire.HEADER_SIZE + Wire.TAG_SIZE)
        Wire.writeHeader(raw, header)

        assertEquals(header, Wire.readHeader(raw))
    }

    @Test
    fun `the header is twenty four bytes and starts with the magic`() {
        val raw = ByteArray(Wire.HEADER_SIZE + Wire.TAG_SIZE)
        Wire.writeHeader(raw, Wire.Header(type = 1, room = room, session = 1, seq = 1))

        assertContentEquals("MBG1".toByteArray(), raw.copyOfRange(0, 4))
        assertEquals(1, raw[4].toInt())
    }

    @Test
    fun `a sealed packet opens again`() {
        val payload = "какая-то полезная нагрузка".toByteArray()
        val header = Wire.Header(type = Wire.Type.HELLO, room = room, session = 42, seq = 7)

        val packet = Wire.seal(psk, header, payload, Wire.Direction.TO_PC)
        val opened = assertNotNull(Wire.open(psk, packet, packet.size, Wire.Direction.TO_PC))

        assertEquals(header, opened.first)
        assertContentEquals(payload, opened.second)
        assertEquals(Wire.HEADER_SIZE + payload.size + Wire.TAG_SIZE, packet.size)
    }

    @Test
    fun `somebody else's key opens nothing`() {
        val header = Wire.Header(type = Wire.Type.HELLO, room = room, session = 1, seq = 1)
        val packet = Wire.seal(psk, header, byteArrayOf(1, 2, 3), Wire.Direction.TO_PC)

        assertNull(Wire.open(ByteArray(32) { (it + 1).toByte() }, packet, packet.size, Wire.Direction.TO_PC))
    }

    /**
     * The header travels in the clear so a relay can route on it, and is the
     * associated data so that nobody can edit it on the way. Both halves matter:
     * without the second, a relay could retype the room and the packet would
     * still open.
     */
    @Test
    fun `an edited header fails the tag`() {
        val header = Wire.Header(type = Wire.Type.DEV_IN, room = room, session = 5, seq = 5)
        val packet = Wire.seal(psk, header, byteArrayOf(9), Wire.Direction.TO_PC)

        packet[5] = Wire.Type.DEV_OUT.toByte()

        assertNull(Wire.open(psk, packet, packet.size, Wire.Direction.TO_PC))
    }

    /**
     * One key seals both directions, and `session` and `seq` are each sender's
     * own counters — so without the direction in the nonce the two sides would
     * eventually seal two different packets under one, which is the single
     * mistake GCM does not forgive.
     */
    @Test
    fun `a packet does not open as if it came the other way`() {
        val header = Wire.Header(type = Wire.Type.DEV_IN, room = room, session = 1, seq = 1)
        val packet = Wire.seal(psk, header, byteArrayOf(1), Wire.Direction.TO_PC)

        assertNull(Wire.open(psk, packet, packet.size, Wire.Direction.TO_CLIENT))
    }

    @Test
    fun `anything that is not ours is not a header`() {
        assertNull(Wire.readHeader(ByteArray(Wire.HEADER_SIZE + Wire.TAG_SIZE)))
        assertNull(Wire.readHeader(ByteArray(10)))

        val short = ByteArray(Wire.HEADER_SIZE)
        Wire.writeHeader(short, Wire.Header(type = 1, room = room, session = 1, seq = 1))
        // Header but no tag: not a packet, whatever the first four bytes say.
        assertNull(Wire.readHeader(short))
    }

    @Test
    fun `a packet with a payload fits the contract's ceiling`() {
        val header = Wire.Header(type = Wire.Type.DEV_IN, room = room, session = 1, seq = 1)
        val report = ByteArray(64)

        val packet = Wire.seal(psk, header, DeviceChannel.input(0, 1, report), Wire.Direction.TO_PC)

        assertTrue(packet.size < Wire.MAX_PACKET, "пакет ${packet.size} байт")
    }
}
