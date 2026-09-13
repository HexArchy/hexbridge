package ru.hexarch.hexbridge.tv

import android.content.Context
import ru.hexarch.hexbridge.Settings
import java.net.InetSocketAddress

/**
 * Where the gaming PC is and what key to seal with.
 *
 * <p>Typing forty-four characters of base64 with a remote control is nobody's
 * idea of a setup, so this can also be filled in from the outside — see
 * [MainActivity], which reads the same two fields off the launch intent. That is
 * one `adb shell am start` away, and it is how this gets configured until the
 * twelve-character pairing code lands on the television as well.</p>
 */
class Config(context: Context) {

    private val store = context.getSharedPreferences("hexbridge", Context.MODE_PRIVATE)

    var target: String
        get() = store.getString(TARGET, "") ?: ""
        set(value) = store.edit().putString(TARGET, value.trim()).apply()

    var psk: String
        get() = store.getString(PSK, "") ?: ""
        set(value) = store.edit().putString(PSK, value.trim()).apply()

    var name: String
        get() = store.getString(NAME, "Mi Box") ?: "Mi Box"
        set(value) = store.edit().putString(NAME, value.trim()).apply()

    val isConfigured: Boolean get() = address() != null && key() != null

    /** The address to dial, or null when what is stored is not one. */
    fun address(): InetSocketAddress? {
        val parsed = Settings.address(target) ?: return null
        // Resolution is the one part that has to happen here: it touches the
        // network, which is why the parsing it follows lives where tests can
        // reach it.
        return runCatching { InetSocketAddress(parsed.host, parsed.port) }.getOrNull()
    }

    /** The 32 key bytes, or null when what is stored is not a key. */
    fun key(): ByteArray? = Settings.key(psk)

    private companion object {
        const val TARGET = "target"
        const val PSK = "psk"
        const val NAME = "name"
    }
}
