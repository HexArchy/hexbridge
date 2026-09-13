package ru.hexarch.hexbridge

import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertContentEquals

class SettingsTest {

    private val key = ByteArray(32) { it.toByte() }
    private val encoded = Base64.getEncoder().encodeToString(key)

    @Test
    fun `an ordinary address is read`() {
        assertEquals(Settings.Address("192.168.1.10", 47702), Settings.address("192.168.1.10:47702"))
    }

    @Test
    fun `spaces around what somebody typed do not count`() {
        assertEquals(Settings.Address("pc.example", 47702), Settings.address("  pc.example:47702  "))
    }

    /** A relay address can be a literal, and a literal is mostly colons. */
    @Test
    fun `an IPv6 literal keeps its colons and loses only the port`() {
        assertEquals(Settings.Address("[2a00:1450::1]", 47702), Settings.address("[2a00:1450::1]:47702"))
    }

    @Test
    fun `anything that is not an address is refused`() {
        assertNull(Settings.address(""))
        assertNull(Settings.address("192.168.1.10"))
        assertNull(Settings.address("192.168.1.10:"))
        assertNull(Settings.address(":47702"))
        assertNull(Settings.address("192.168.1.10:0"))
        assertNull(Settings.address("192.168.1.10:70000"))
        assertNull(Settings.address("192.168.1.10:порт"))
    }

    @Test
    fun `a key of the right length is read`() {
        assertContentEquals(key, Settings.key(encoded))
        assertContentEquals(key, Settings.key("  $encoded  "))
    }

    /**
     * The failure this guards against is silent: a key of the wrong length seals
     * packets the far side drops without a word, and looks from here exactly like
     * a PC that is switched off.
     */
    @Test
    fun `a key of any other length is not a key`() {
        assertNull(Settings.key(""))
        assertNull(Settings.key("не base64!"))
        assertNull(Settings.key(Base64.getEncoder().encodeToString(ByteArray(31))))
        assertNull(Settings.key(Base64.getEncoder().encodeToString(ByteArray(33))))
    }
}
