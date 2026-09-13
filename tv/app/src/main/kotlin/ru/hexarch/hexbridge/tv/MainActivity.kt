package ru.hexarch.hexbridge.tv

import android.app.Activity
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Color
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView

/**
 * One screen, because there is one thing to do.
 *
 * <p>A television is not a desk: every extra control is another six presses of a
 * D-pad away. So this shows a sentence about what is happening, the two fields
 * that have to be filled in once, and one button. Both fields can also be filled
 * from the launch intent, which is what makes setting this up bearable at all —
 * `adb shell am start -n ru.hexarch.hexbridge.tv/.MainActivity -e target
 * HOST:47702 -e psk BASE64` beats typing base64 with a remote.</p>
 */
class MainActivity : Activity() {

    private lateinit var config: Config
    private lateinit var status: TextView
    private lateinit var target: EditText
    private lateinit var psk: EditText
    private lateinit var action: Button

    private val permissionAction = "ru.hexarch.hexbridge.tv.USB_PERMISSION"

    /**
     * Android asks about a USB device once, through a dialog, and answers here.
     * Starting the bridge before that answer arrives would fail on a device we
     * are not allowed to open yet.
     */
    private val permissions = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != permissionAction) return
            if (intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)) {
                BridgeService.start(this@MainActivity)
            } else {
                State.say("Доступ к контроллеру не дали")
            }
            redraw()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        config = Config(this)
        adopt(intent)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(64, 48, 64, 48)
            setBackgroundColor(Color.parseColor("#0B0E14"))
        }

        root.addView(label("HexBridge — DualSense на ПК", 34f, Color.WHITE))
        root.addView(label("Контроллер подключается к приставке кабелем.", 18f, Color.parseColor("#8A93A6")))

        status = label(State.text(), 22f, Color.parseColor("#7AA2F7"))
        root.addView(status)

        target = field("Адрес ПК, например 192.168.1.10:47702", config.target)
        psk = field("Ключ, 32 байта в base64", config.psk)
        root.addView(label("Адрес ПК", 16f, Color.parseColor("#8A93A6")))
        root.addView(target)
        root.addView(label("Ключ", 16f, Color.parseColor("#8A93A6")))
        root.addView(psk)

        action = Button(this).apply {
            textSize = 20f
            setOnClickListener { toggle() }
        }
        root.addView(action)

        setContentView(root)
        redraw()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        adopt(intent)
        target.setText(config.target)
        psk.setText(config.psk)
        redraw()
    }

    /** Settings handed in from outside, so that nobody has to type base64 with a remote. */
    private fun adopt(intent: Intent?) {
        intent?.getStringExtra("target")?.let { config.target = it }
        intent?.getStringExtra("psk")?.let { config.psk = it }
        intent?.getStringExtra("name")?.let { config.name = it }
    }

    override fun onStart() {
        super.onStart()
        State.onChange = { runOnUiThread { redraw() } }
        val filter = IntentFilter(permissionAction)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(permissions, filter, RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            registerReceiver(permissions, filter)
        }
        redraw()
    }

    override fun onStop() {
        State.onChange = null
        runCatching { unregisterReceiver(permissions) }
        super.onStop()
    }

    private fun toggle() {
        if (State.bridge != null) {
            BridgeService.stop(this)
            State.say("Остановлено")
            redraw()
            return
        }

        config.target = target.text.toString()
        config.psk = psk.text.toString()
        if (!config.isConfigured) {
            State.say("Нужен адрес вида HOST:ПОРТ и ключ в 32 байта")
            redraw()
            return
        }

        val manager = getSystemService(Context.USB_SERVICE) as UsbManager
        val device = Controller.attached(manager).firstOrNull()
        if (device == null) {
            State.say("DualSense не виден — подключите его к приставке кабелем")
            redraw()
            return
        }

        if (manager.hasPermission(device)) {
            BridgeService.start(this)
        } else {
            manager.requestPermission(
                device,
                PendingIntent.getBroadcast(
                    this, 0, Intent(permissionAction).setPackage(packageName),
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                ),
            )
        }
        redraw()
    }

    private fun redraw() {
        status.text = State.text()
        action.text = if (State.bridge != null) "Остановить" else "Запустить"
    }

    private fun label(text: String, size: Float, colour: Int) = TextView(this).apply {
        this.text = text
        textSize = size
        setTextColor(colour)
        gravity = Gravity.START
        setPadding(0, 12, 0, 12)
    }

    private fun field(hint: String, value: String) = EditText(this).apply {
        this.hint = hint
        setText(value)
        textSize = 18f
        setTextColor(Color.WHITE)
        setHintTextColor(Color.parseColor("#5A6274"))
        inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        isSingleLine = true
        layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
        )
    }
}
