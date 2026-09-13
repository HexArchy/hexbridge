package ru.hexarch.hexbridge

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The wire format, as `docs/PROTOCOL.md` defines it and as the Mac and Windows
 * sides already speak it.
 *
 * Nothing here knows about Android. That is deliberate: the hard part of a third
 * implementation is the contract, not the platform, and a contract that can only
 * run on a television is a contract nobody can test. This object runs on a plain
 * JVM, which is how it gets pointed at the real Windows receiver before any of
 * it has ever seen a set-top box.
 */
object Wire {

    const val HEADER_SIZE = 24
    const val TAG_SIZE = 16

    /** The contract's ceiling: one datagram fits a typical MTU without fragmenting. */
    const val MAX_PACKET = 1400

    val MAGIC = byteArrayOf('M'.code.toByte(), 'B'.code.toByte(), 'G'.code.toByte(), '1'.code.toByte())
    const val VERSION: Byte = 1

    /**
     * Which way a packet travels, and the third word of its nonce.
     *
     * Not decoration: one key seals both directions, and `session` and `seq` are
     * the sender's own counters, so without this the two sides would eventually
     * seal two different packets under one nonce.
     */
    enum class Direction(val code: Int) { TO_PC(0), TO_CLIENT(1) }

    object Type {
        const val AUDIO = 1
        const val HELLO = 2
        const val PONG = 3
        const val DEV_ATTACH = 4
        const val DEV_DETACH = 5
        const val DEV_IN = 6
        const val DEV_OUT = 7
        const val DEV_ACK = 8
        const val HAPTIC = 13
        const val PEER = 14
    }

    data class Header(
        val type: Int,
        val flags: Int = 0,
        val room: Long,
        val session: Int,
        val seq: Int,
    )

    /**
     * `room = LE_u64( SHA256("hexbridge-room-v1" || PSK)[0..8] )`.
     *
     * Over the raw 32 key bytes, never over their base64: the other two
     * implementations hash the key itself, and a room that disagrees is a relay
     * that silently routes nothing.
     */
    fun roomId(psk: ByteArray): Long {
        val label = "hexbridge-room-v1".toByteArray(Charsets.UTF_8)
        val digest = MessageDigest.getInstance("SHA-256").digest(label + psk)
        return ByteBuffer.wrap(digest, 0, 8).order(ByteOrder.LITTLE_ENDIAN).long
    }

    fun writeHeader(into: ByteArray, header: Header) {
        val buffer = ByteBuffer.wrap(into).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(MAGIC)
        buffer.put(VERSION)
        buffer.put(header.type.toByte())
        buffer.putShort(header.flags.toShort())
        buffer.putLong(header.room)
        buffer.putInt(header.session)
        buffer.putInt(header.seq)
    }

    /** The header of a datagram, or null when it is not one of ours. */
    fun readHeader(datagram: ByteArray, length: Int = datagram.size): Header? {
        if (length < HEADER_SIZE + TAG_SIZE) return null
        for (i in MAGIC.indices) if (datagram[i] != MAGIC[i]) return null
        if (datagram[4] != VERSION) return null

        val buffer = ByteBuffer.wrap(datagram, 0, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        buffer.position(5)
        val type = buffer.get().toInt() and 0xFF
        val flags = buffer.short.toInt() and 0xFFFF
        return Header(type, flags, buffer.long, buffer.int, buffer.int)
    }

    private fun nonce(session: Int, seq: Int, direction: Direction): ByteArray {
        val raw = ByteArray(12)
        ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(session).putInt(seq).putInt(direction.code)
        return raw
    }

    /**
     * One complete datagram: `header || ciphertext || tag`.
     *
     * The whole 24-byte header is the associated data. It travels in the clear so
     * that a relay can route on `room` without holding the key, and it is
     * authenticated so that nobody can edit it on the way.
     */
    fun seal(psk: ByteArray, header: Header, payload: ByteArray, direction: Direction): ByteArray {
        val head = ByteArray(HEADER_SIZE)
        writeHeader(head, header)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(
            Cipher.ENCRYPT_MODE,
            SecretKeySpec(psk, "AES"),
            GCMParameterSpec(TAG_SIZE * 8, nonce(header.session, header.seq, direction)),
        )
        cipher.updateAAD(head)
        // doFinal hands back ciphertext followed by the tag, which is the order
        // the contract puts them in.
        return head + cipher.doFinal(payload)
    }

    /** The payload of a datagram, or null when it does not open under this key. */
    fun open(psk: ByteArray, datagram: ByteArray, length: Int, direction: Direction): Pair<Header, ByteArray>? {
        val header = readHeader(datagram, length) ?: return null

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(
            Cipher.DECRYPT_MODE,
            SecretKeySpec(psk, "AES"),
            GCMParameterSpec(TAG_SIZE * 8, nonce(header.session, header.seq, direction)),
        )
        cipher.updateAAD(datagram, 0, HEADER_SIZE)
        return try {
            header to cipher.doFinal(datagram, HEADER_SIZE, length - HEADER_SIZE)
        } catch (_: javax.crypto.AEADBadTagException) {
            // Somebody else's key, somebody else's room, or a packet that was
            // edited on the way. All three end here and go no further.
            null
        }
    }
}
