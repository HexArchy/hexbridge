package ru.hexarch.hexbridge.tv

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack

/**
 * HD haptics: the PS5's signature effect is sound rather than rumble bytes, and
 * over USB it is written into the controller's own audio interface — channels 2
 * and 3 drive the voice coils in the grips.
 *
 * <p>So this is an `AudioTrack` aimed at the DualSense, which Android sees as a
 * USB audio device when it is plugged in. It is the one part of the television
 * client that cannot be promised in advance: routing to a particular USB device
 * is a request, not a guarantee, and a box that refuses it leaves everything else
 * working. Which is why nothing above waits on it, and why it says plainly
 * whether it found the controller's audio at all.</p>
 */
class HapticSink private constructor(private val track: AudioTrack) {

    companion object {
        const val RATE = 48_000

        /**
         * Opens the controller's audio, or returns null with the reason — never
         * throws: no haptics is a quieter grip, not a broken bridge.
         */
        fun open(context: Context, onNote: (String) -> Unit): HapticSink? {
            val manager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
            val usb = manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
                .firstOrNull { it.type == AudioDeviceInfo.TYPE_USB_DEVICE }
            if (usb == null) {
                onNote("HD-хаптика: аудиоустройство контроллера не найдено, гриппы будут молчать")
                return null
            }

            val format = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(RATE)
                .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                .build()
            // A block is 5 ms; four of them buffered is enough to ride out a
            // scheduling hiccup without adding a delay anybody can feel.
            val minimum = AudioTrack.getMinBufferSize(RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val track = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_GAME)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build()
                )
                .setAudioFormat(format)
                .setBufferSizeInBytes(maxOf(minimum, 4 * 960))
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()

            if (!track.setPreferredDevice(usb)) {
                onNote("HD-хаптика: приставка не отдала аудио контроллера, гриппы будут молчать")
                track.release()
                return null
            }

            track.play()
            onNote("HD-хаптика: играем в аудиоустройство контроллера")
            return HapticSink(track)
        }
    }

    /** One 5 ms block, S16LE, the two actuator channels interleaved. */
    fun play(pcm: ByteArray) {
        runCatching { track.write(pcm, 0, pcm.size) }
    }

    fun close() {
        runCatching {
            track.pause()
            track.flush()
            track.release()
        }
    }
}
