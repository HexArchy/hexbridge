package ru.hexarch.hexbridge.tv

import ru.hexarch.hexbridge.DeviceChannel
import ru.hexarch.hexbridge.Wire
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread
import kotlin.random.Random

/**
 * The link to the gaming PC: one socket, three loops, and the device channel over
 * it.
 *
 * <p>Nothing here is Android-specific either — it is the protocol core plus the
 * threads that drive it — but it lives with the app rather than in `core` because
 * `core` is the part that must stay testable from a desk, and a socket that talks
 * to a real PC is not that.</p>
 */
class Bridge(
    private val target: InetSocketAddress,
    private val psk: ByteArray,
    private val name: String,
    private val controller: Controller,
    private val haptics: HapticSink?,
    private val onEvent: (Event) -> Unit,
) {

    sealed interface Event {
        /** The far side has assembled the virtual controller. */
        data object Forwarding : Event
        data class Stopped(val why: String?) : Event
        data class Counters(val reports: Long, val outputs: Long, val hapticBlocks: Long, val silentSeconds: Long) : Event
    }

    private val room = Wire.roomId(psk)
    private val session = Random.nextInt()
    private val seq = AtomicInteger(0)
    private val running = AtomicBoolean(false)
    private val acked = AtomicBoolean(false)

    private val reports = AtomicLong(0)
    private val outputs = AtomicLong(0)
    private val hapticBlocks = AtomicLong(0)
    private val lastHeard = AtomicLong(0)

    private var socket: DatagramSocket? = null
    private val threads = ArrayList<Thread>()

    fun start(): Result<Unit> {
        val opened = controller.open().getOrElse { return Result.failure(it) }

        val socket = try {
            DatagramSocket()
        } catch (e: Exception) {
            controller.close()
            return Result.failure(e)
        }
        this.socket = socket
        running.set(true)
        lastHeard.set(System.currentTimeMillis())

        val attach = DeviceChannel.attach(DEVICE_NUMBER, opened.descriptors)

        threads += thread(name = "hexbridge.rx") { receiveLoop(socket) }
        threads += thread(name = "hexbridge.tick") { tickLoop(attach) }
        threads += thread(name = "hexbridge.hid") { inputLoop() }
        return Result.success(Unit)
    }

    fun stop(why: String? = null) {
        if (!running.compareAndSet(true, false)) return
        // Politely first: a detach that arrives lets Windows drop the virtual
        // device now rather than three seconds later when the session times out.
        runCatching { send(Wire.Type.DEV_DETACH, DeviceChannel.detach(DEVICE_NUMBER)) }
        socket?.close()
        controller.close()
        haptics?.close()
        onEvent(Event.Stopped(why))
    }

    private fun send(type: Int, payload: ByteArray) {
        val socket = socket ?: return
        val header = Wire.Header(type = type, room = room, session = session, seq = seq.getAndIncrement())
        val packet = Wire.seal(psk, header, payload, Wire.Direction.TO_PC)
        socket.send(DatagramPacket(packet, packet.size, target))
    }

    private fun receiveLoop(socket: DatagramSocket) {
        val buffer = ByteArray(Wire.MAX_PACKET)
        while (running.get()) {
            val datagram = DatagramPacket(buffer, buffer.size)
            try {
                socket.receive(datagram)
            } catch (e: Exception) {
                if (running.get()) stop("связь оборвалась: ${e.message}")
                return
            }

            val opened = Wire.open(psk, buffer, datagram.length, Wire.Direction.TO_CLIENT) ?: continue
            lastHeard.set(System.currentTimeMillis())
            val (header, payload) = opened
            when (header.type) {
                Wire.Type.DEV_ACK ->
                    if (acked.compareAndSet(false, true)) onEvent(Event.Forwarding)

                Wire.Type.DEV_OUT -> DeviceChannel.output(payload)?.let {
                    if (it.device == DEVICE_NUMBER && controller.write(it.report)) outputs.incrementAndGet()
                }

                Wire.Type.HAPTIC -> DeviceChannel.haptic(payload)?.let {
                    if (it.device == DEVICE_NUMBER) {
                        haptics?.play(it.pcm)
                        hapticBlocks.incrementAndGet()
                    }
                }
            }
        }
    }

    /** HELLO once a second, and the attach until it is acknowledged. */
    private fun tickLoop(attach: ByteArray) {
        while (running.get()) {
            try {
                send(Wire.Type.HELLO, DeviceChannel.hello(name))
                if (!acked.get()) send(Wire.Type.DEV_ATTACH, attach)
                onEvent(
                    Event.Counters(
                        reports.get(),
                        outputs.get(),
                        hapticBlocks.get(),
                        (System.currentTimeMillis() - lastHeard.get()) / 1000,
                    )
                )
            } catch (e: Exception) {
                if (running.get()) stop("не удалось отправить: ${e.message}")
                return
            }
            Thread.sleep(1000)
        }
    }

    /**
     * The hot loop: one interrupt read, one datagram, at whatever rate the
     * controller produces — 250 Hz on a DualSense, 1000 on an Edge.
     */
    private fun inputLoop() {
        val buffer = ByteArray(controller.inputReportSize)
        var number = 0
        while (running.get()) {
            val read = controller.readInto(buffer, READ_TIMEOUT_MS)
            if (read < 0) {
                if (running.get()) stop("контроллер отключили")
                return
            }
            if (read == 0) continue
            if (!acked.get()) continue

            try {
                send(Wire.Type.DEV_IN, DeviceChannel.input(DEVICE_NUMBER, number++, buffer, read))
            } catch (e: Exception) {
                if (running.get()) stop("не удалось отправить отчёт: ${e.message}")
                return
            }
            reports.incrementAndGet()
        }
    }

    private companion object {
        /** One controller for now; the contract allows four. */
        const val DEVICE_NUMBER = 0

        /** Long enough not to spin, short enough to notice `stop()` quickly. */
        const val READ_TIMEOUT_MS = 500
    }
}
