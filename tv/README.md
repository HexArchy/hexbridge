# HexBridge for Android TV

Moonlight already runs on the television. What is missing there is the
controller: Moonlight hands the host a virtual DS4, and a DS4 has no adaptive
triggers. This gives the PC a real `054C:0CE6` instead — the same thing the Mac
client gives it, from the box under the screen.

## Why this is only a client and not a port

The device channel runs along HID, not along USB. This side sends the
controller's own descriptors and its reports; Windows assembles the virtual USB
device and speaks USB/IP entirely by itself — see `docs/PROTOCOL.md`. So a client
needs raw HID access and nothing else: no USB/IP implementation, no driver, no
kernel module. Android hands that out through the USB host API without root.

## What it does and does not do

* **Adaptive triggers, rumble, lightbar, gyro, touchpad** — all of it is HID
  reports, all of it crosses.
* **Cable only.** A DualSense paired over Bluetooth belongs to Android's input
  stack, which gives an app buttons and axes and never the raw reports. Without
  raw reports there are no adaptive triggers, which is the whole point. Root
  would change that; this does not rely on it.
* **HD haptics is attempted, not promised.** It arrives as PCM and is played into
  the controller's own USB audio device. Whether a particular box lets an app
  route audio to a particular USB device is up to the box. Everything else works
  either way, and the app says which happened.
* One controller. The contract allows four.

## Building

Needs a JDK and the Android SDK (platform 34). The Gradle wrapper pins the
version, because the current Android plugin does not run on the newest Gradle.

```
cd tv
./gradlew :core:test            # the protocol core, no SDK needed
./gradlew :app:assembleDebug    # the APK
```

The APK lands in `app/build/outputs/apk/debug/`.

## Installing and setting it up

```
adb connect <адрес приставки>:5555
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Typing forty-four characters of base64 with a remote control is nobody's idea of
a setup, so the two settings can be handed in from outside:

```
adb shell am start -n ru.hexarch.hexbridge.tv/.MainActivity \
  -e target 192.168.1.10:47702 \
  -e psk '<the same key the PC has>'
```

They can also be typed into the two fields on screen. The twelve-character
pairing code the Mac uses is not implemented here yet.

Then plug the DualSense into the box with a cable, allow the USB access dialog
once, and press Запустить. The bridge runs as a foreground service, so it keeps
going after you switch to Moonlight — which is the only time it matters.

## What has actually been verified

The protocol core has been run against the real Windows receiver from a desktop
JVM: Windows answered `PONG`, acknowledged a `DEV_ATTACH` built by this code,
reported one device forwarded, and Device Manager showed
`USB\VID_054C&PID_0CE6` as a composite device with a Wireless Controller
function. Twenty-five tests cover the rest of the contract.

Nothing that touches USB or audio has run on hardware yet — there is no
controller and no set-top box on the machine this was written on. The first run
on a real Mi Box is the outstanding test.
