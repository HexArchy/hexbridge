package ru.hexarch.hexbridge

import java.util.Base64

/**
 * Reading the two things a client has to be told: where the PC is, and what key
 * to seal with.
 *
 * <p>Trivial, and in `core` on purpose. The last time this project let a config
 * field be parsed loosely, one missing field threw away a working key and a
 * person had to pair their machines again. Both of these come from a text field
 * on a television, typed with a remote control, so every way they can be wrong is
 * a way somebody will get them wrong.</p>
 */
object Settings {

    data class Address(val host: String, val port: Int)

    /** `HOST:PORT`, or null when that is not what it is. */
    fun address(text: String): Address? {
        val trimmed = text.trim()
        // `substringBeforeLast` so that an IPv6 literal keeps its colons, and a
        // trailing `:port` is still the port.
        val host = trimmed.substringBeforeLast(':', "").trim()
        val port = trimmed.substringAfterLast(':', "").trim().toIntOrNull()
        if (host.isEmpty() || port == null || port !in 1..65535) return null
        return Address(host, port)
    }

    /**
     * The 32 key bytes, or null.
     *
     * Length is checked rather than assumed: base64 that decodes to anything else
     * is a key that will seal packets nobody can open, and the far side has no way
     * to say so — a wrong key looks exactly like silence.
     */
    fun key(text: String): ByteArray? {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return null
        val bytes = runCatching { Base64.getDecoder().decode(trimmed) }.getOrNull() ?: return null
        return if (bytes.size == 32) bytes else null
    }
}
