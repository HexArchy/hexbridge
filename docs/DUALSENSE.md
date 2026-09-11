# DualSense forwarding

The DualSense is plugged into the Mac over USB, and Windows sees it as a real
`054C:0CE6` controller — with adaptive triggers, gyro and touchpad, without
DS4Windows or any other shim in between.

## Why it is built this way

```
     Mac                                          Windows
┌──────────────┐                          ┌──────────────────────────┐
│ IOHIDManager │  HID reports             │ HexBridge  ──USB/IP──▶   │
│ real gamepad ├─────────────────────────▶│ 127.0.0.1:3240           │
│              │◀─────────────────────────┤      usbip-win2 vhci     │
└──────────────┘  rumble, triggers,       │              │           │
                  lightbar                │      games ◀─┘           │
                                          └──────────────────────────┘
```

The boundary runs along HID rather than USB, and that is not a convenience choice.

Forwarding a USB device off macOS with USB/IP is **impossible**: `usbipd-mac`
refuses to `bind` anything in the HID class, because `IOHIDFamily` holds the
interface exclusively and it cannot be detached. Apple rejected the project
author's request for a DriverKit entitlement to claim HID in February 2026. So the
Mac hands over only what it can get at without privileges — HID reports and the
descriptor — and the virtual USB device is assembled in full on the Windows side.

A side benefit: the Windows driver can be swapped out without touching any Mac
code.

## What to install on Windows

One driver: **usbip-win2**. It provides the virtual USB controller that HexBridge
plugs the assembled device into.

The version is pinned deliberately:

| | |
|---|---|
| Version | `0.9.8.0`, 7 September 2026 |
| File | `USBip-0.9.8.0-x64.exe`, 25 MB |
| Link | https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.0 |
| SHA256 | `81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39` |

Check it before installing:

```powershell
(Get-FileHash .\USBip-0.9.8.0-x64.exe -Algorithm SHA256).Hash
# must match the line above
```

### Nothing has to be disabled

The drivers are **signed by Microsoft**, through [OSSign](https://github.com/OSSign).
Checked on a machine with 0.9.8.0 installed rather than taken from the release
notes: `pnputil /enum-drivers` reports the signer as *Microsoft Windows Hardware
Compatibility Publisher*, which is the attestation-signing chain, for both
architectures. An earlier version of this document claimed WHLK certification for
x64; that is not what the shipped catalog says, and the distinction matters — WHLK
means the driver passed the hardware lab, attestation means Microsoft signed what
it was handed.

Either way the practical answer is the same, and the release notes put it in as
many words: *"The installer and all binaries are signed by Microsoft"* and
*"Windows Test Signing Mode activation is not required"*.

| Requirement | Needed? |
|---|---|
| Disable Secure Boot | No |
| `bcdedit /set testsigning on` | No |
| Install a third-party root certificate | No |
| Administrator rights, one UAC prompt | Yes |
| Reboot | No |

Instructions on the internet that tell you to disable Secure Boot describe builds
from 2021–2023 and are out of date.

**The one side effect:** installing the driver reinitializes every USB 3.0 hub
once, so connected devices drop out for a second. It is worth creating a restore
point beforehand: this is a kernel-mode driver.

### Why 0.9.8.0 specifically

This version added a receive mode based on WSK event callbacks, which the author
put in for exactly our case — "devices that generate little data but at high
frequency, such as HID keyboards and mice". For a gamepad sending hundreds of
reports per second that is a noticeable latency reduction:

```powershell
usbip.exe attach --receive-mode=low-latency -r 127.0.0.1 -b 1-1
```

The fallback, if 0.9.8.0 turns out to be raw (it was released recently), is
`0.9.7.7` — the version other projects pin. Do not go older than `0.9.7.7`: it had
a bug enumerating Full-Speed devices, and it was missing several stability fixes
besides.

The DualSense, incidentally, is **High-Speed**, not Full-Speed as is sometimes
claimed. The arithmetic shows it: the isochronous OUT endpoint has
`wMaxPacketSize 392` at `bInterval 4`, that is 392 bytes per millisecond — exactly
4 channels × 16 bits × 48 kHz. That does not fit on Full Speed. The same
arithmetic gives the HID rate: `bInterval 6` on High Speed is 32 microframes =
4 ms = **250 Hz**, which matches the hardware measurements.

## The Mac-side risk to check first

Input Monitoring permission is **not required** for a gamepad — the TCC gate in
`IOHIDFamily` only fires for keyboards, mice and trackpads, and a DualSense
presents itself as a single Game Pad collection. The only thing that matters is
matching strictly on `VID 0x054C / PID 0x0CE6`: a broad filter would catch the
keyboard and trigger the dialog.

Writing output reports on **macOS 26**, however, is an open question. There are
independent reports that third-party apps outside the App Store cannot change the
adaptive triggers or the lightbar — the report is accepted silently but never
applied. Technically that looks like `IOHIDLibUserClient::setReport()` returning
`kIOReturnNotPrivileged` (`0xE00002C1`) to an unprivileged client.

That is exactly the functionality this whole thing exists for, so it is the first
thing to check:

```
HexBridge gamepad probe
```

The command opens the controller non-exclusively, reads input reports, writes an
output report and **prints the return code**. If it says `0xE00002C1`, triggers are
not going anywhere from this Mac — and that is something to find out before writing
a USB/IP server, not after.

One level down:

```
log stream --predicate 'subsystem == "com.apple.iohid"'
```

## Pitfalls

* **Anti-cheats.** The virtual USB controller is visible in the device tree, and
  kernel-level anti-cheats notice it. For games with aggressive protection that is
  a risk.
* **A second gamepad.** If you also plug a physical pad into the Windows machine,
  games will see two devices. [HidHide](https://github.com/nefarius/HidHide) fixes
  that.
* **The product string.** 2020-vintage pads report themselves as
  `Wireless Controller`, newer ones as `DualSense Wireless Controller`, with the
  same `bcdDevice`. HexBridge takes the string from your physical device rather
  than from a lookup table.
* **Steam** queries feature reports itself before claiming a device. If you answer
  them with zeros, the pad will show up in Steam Input but be invisible to the
  game — which is why HexBridge proxies those requests to the real pad. Three
  matter: `0x20` (firmware), `0x09` (MAC), `0x05` (sensor calibration — zeros there
  give a division by zero and the game rejects the pad).
* **Do not claim the device exclusively.** `kIOHIDOptionsTypeSeizeDevice` is
  available without privileges, but it disconnects the DualSense from the system,
  from Steam and from GameController.framework. We open non-exclusively.
* **The system writes to the controller too.** macOS sets its own lightbar color on
  connect, and any app using GameController.framework will overwrite our state: the
  DualSense applies the whole of the last `0x02` report.
* **LaunchAgent only, never LaunchDaemon.** `IOHIDDeviceOpen` requires a local
  graphical session; it will not work over SSH. We already run as an agent, so
  there is nothing to change.

## HD haptics

Supported, but **off by default** — it has its own toggle, independent of triggers
and rumble.

The caution is about the driver, not our code: usbip-win2 has an open bug,
[#181](https://github.com/vadimgrn/usbip-win2/issues/181), a request-lifetime race
when an audio pin is closed — that is, exactly on the isochronous path. The risk is
reduced by three things: with haptics off, the audio functions are not in the
configuration descriptor at all and Windows never even loads `usbaudio.sys`; the
microphone endpoint answers with silence rather than an error, so as not to provoke
a pin open-and-close loop; and every isochronous request is answered immediately, so
none is left hanging. None of that fixes the bug itself. The first time you turn
haptics on, it is worth disabling autostart of the receiver.

### What the real controller turned out to do

A DualSense **boots with haptics muted** and silently discards the PCM you send it
until the mute is lifted by a separate output report. This is not documented
anywhere; we found it by measurement — feeding a 60 Hz tone into channels 2–3 and
watching the controller's own gyro: σ 0.9 with the mute untouched, 128 after
lifting it.

A separate trap: the `HAPTICS_SELECT` flag in `valid_flag0` **turns this path off**,
handing the actuators over to the classic rumble emulator.

Channel assignment was confirmed by frequency signature: at 40 Hz, channels 2–3
give σ 188 and channels 0–1 give 4.7. The first is the voice coils, the second is
the piezo speaker.

Adaptive triggers are an ordinary HID report `0x02`, and they work. The DualSense's
signature haptics is a different thing entirely: a 48 kHz isochronous audio stream
over four channels, where channels 2–3 drive the voice-coil actuators. Carrying it
requires assembling a composite USB device with full audio interfaces, and that path
runs into the open usbip-win2 bug with a BSOD on audio pin close. Rumble, meanwhile,
works through the ordinary rumble bytes in that same `0x02` report.
