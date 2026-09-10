# Ideas for the next features

The architecture is already organized by feature: each one owns its module, its
packet types and its section of the UI, and the shell iterates over them without
knowing any of them by name. So everything below can be added without touching the
microphone or the gamepad.

Already shipped, and therefore no longer listed here: HD haptics, the shared
clipboard, up to four controllers, forwarding of any HID device, automatic
updates, and host discovery without a QR code.

The difficulty estimates are rough, relative to the effort already spent.

## Finishing what already exists

**The microphone the other way, Windows to Mac.** The channel is one-way today. If
the gaming PC has a decent microphone and you would rather talk through the Mac,
reversing the direction is nearly free — the transport is already bidirectional.
*Difficulty: low.*

**DualShock 4 and third-party gamepads.** The same path, different descriptors.
Everything is in place for the DS4; an arbitrary HID device would need someone
else's report descriptor parsed.
*Difficulty: medium.*

## New features

**File transfer by drag and drop.** Drop a file onto the menu bar icon and it lands
on the gaming PC. The transport exists; what is needed is a mode with delivery
confirmation instead of the current fire-and-forget.
*Difficulty: medium.*

**Notifications from the PC on the Mac.** The game minimized, Steam finished a
download, a friend messaged — it pops up on the Mac without leaving the stream.
*Difficulty: medium.*

**A global mute hotkey.** Mute is `kill -USR1` today. A proper global hotkey that
works over a fullscreen Moonlight, plus an indicator. The library is already picked
in the design document.
*Difficulty: low. Value: annoying every single day.*

**Noise suppression on the microphone.** RNNoise or similar, ahead of Opus. A laptop
microphone picks up fans and keyboard.
*Difficulty: medium.*

**The webcam as a virtual camera on the PC.** A different class of problem: camera
access on macOS is standard, but a virtual camera on Windows means a driver of its
own, same as with the microphone.
*Difficulty: high.*

## Infrastructure, not features

**Signing and notarization.** The macOS build is ad-hoc signed today and the Windows
one is not signed at all — other people will hit Gatekeeper and SmartScreen.
Certificates are needed, and that is money rather than code.
*Difficulty: low, technically.*

**Metrics and diagnostics in one click.** Collect the state of both sides into a
single file to attach to an issue. It would make supporting other people far easier.
*Difficulty: low.*
