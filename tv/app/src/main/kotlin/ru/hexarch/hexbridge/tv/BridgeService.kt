package ru.hexarch.hexbridge.tv

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.IBinder

/**
 * Keeps the bridge up while somebody is inside Moonlight.
 *
 * <p>That is the whole reason this is a service at all: on Android an app that is
 * not in front stops getting time, and the controller has to keep reporting for
 * exactly as long as the game is being played — which is precisely when this app
 * is behind another one.</p>
 */
class BridgeService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIFICATION, notification(State.text()))

        if (State.bridge != null) return START_STICKY

        val config = Config(this)
        val address = config.address()
        val key = config.key()
        if (address == null || key == null) {
            State.say("Не настроено: нужен адрес ПК и ключ")
            stopSelf()
            return START_NOT_STICKY
        }

        val manager = getSystemService(Context.USB_SERVICE) as UsbManager
        val device = Controller.attached(manager).firstOrNull()
        if (device == null) {
            State.say("Контроллер не найден — подключите DualSense кабелем")
            stopSelf()
            return START_NOT_STICKY
        }
        if (!manager.hasPermission(device)) {
            State.say("Нет разрешения на доступ к контроллеру")
            stopSelf()
            return START_NOT_STICKY
        }

        val controller = Controller(manager, device)
        val haptics = HapticSink.open(this) { State.say(it) }
        val bridge = Bridge(address, key, config.name, controller, haptics) { event -> onEvent(event) }

        val started = bridge.start()
        if (started.isFailure) {
            State.say("Не вышло: ${started.exceptionOrNull()?.message}")
            stopSelf()
            return START_NOT_STICKY
        }

        State.bridge = bridge
        State.say("Ждём ПК…")
        return START_STICKY
    }

    private fun onEvent(event: Bridge.Event) {
        when (event) {
            is Bridge.Event.Forwarding -> State.say("Контроллер проброшен на ПК")
            is Bridge.Event.Stopped -> {
                State.bridge = null
                State.say(event.why ?: "Остановлено")
                stopSelf()
            }
            is Bridge.Event.Counters -> {
                State.counters = event
                if (event.silentSeconds >= 5) State.say("ПК молчит ${event.silentSeconds} с")
            }
        }
        notify(State.text())
    }

    override fun onDestroy() {
        State.bridge?.stop()
        State.bridge = null
        super.onDestroy()
    }

    private fun notify(text: String) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION, notification(text))
    }

    private fun notification(text: String): Notification {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL, "HexBridge", NotificationManager.IMPORTANCE_LOW)
            )
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
        return builder
            .setContentTitle("HexBridge")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val CHANNEL = "bridge"
        private const val NOTIFICATION = 1

        fun start(context: Context) {
            val intent = Intent(context, BridgeService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            State.bridge?.stop()
            context.stopService(Intent(context, BridgeService::class.java))
        }
    }
}

/**
 * What the screen shows, in one place.
 *
 * A television app is looked at rarely and briefly — it is started, and then
 * somebody switches to Moonlight — so the state it keeps is deliberately one
 * sentence and a few counters rather than a model with a lifecycle of its own.
 */
object State {
    @Volatile var bridge: Bridge? = null
    @Volatile var line: String = "Не запущено"
    @Volatile var counters: Bridge.Event.Counters? = null
    @Volatile var onChange: (() -> Unit)? = null

    fun say(text: String) {
        line = text
        onChange?.invoke()
    }

    fun text(): String {
        val counters = counters ?: return line
        return "$line\nотчётов ${counters.reports}, команд ${counters.outputs}, хаптики ${counters.hapticBlocks}"
    }
}
