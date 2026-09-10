# HexBridge

[![build](https://github.com/HexArchy/hexbridge/actions/workflows/build.yml/badge.svg)](https://github.com/HexArchy/hexbridge/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/HexArchy/hexbridge?sort=semver)](https://github.com/HexArchy/hexbridge/releases/latest)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Your Mac's microphone and DualSense, on your Windows gaming PC — alongside the
game stream, but not through it.

You play over Moonlight, the game-streaming client, while the microphone and the
DualSense are physically plugged into your Mac. Games on the gaming PC see them as
ordinary local devices: the microphone as a microphone, the DualSense as a real
`054C:0CE6` controller with adaptive triggers, gyro and touchpad.

Moonlight and Sunshine are **not patched at all**. HexBridge runs beside them over
its own encrypted channel, so any stock client and any host will do — Sunshine,
Apollo, Vibepollo. Updating your streaming software does not break it.

The machine you sit at can be **a Mac or another Windows PC**. Windows to Windows
carries the microphone and the clipboard; forwarding gamepads needs a Mac on the
sending side, for a reason spelled out under [Limitations](#limitations).

```
        Mac                       network                  Windows
┌──────────────────┐         ┌──────────────┐      ┌────────────────────┐
│ microphone ──────┼── Opus ─┤              ├─────▶│ virtual cable      │──▶ games
│                  │         │ UDP, direct  │      │                    │
│ DualSense ───────┼── HID ──┤ or via relay ├─────▶│ USB/IP → vhci      │──▶ games
│                  │◀── rumble, triggers ───┤      │                    │
└──────────────────┘         └──────────────┘      └────────────────────┘
                              AES-256-GCM
```

## Features

| | What it does | What you need on Windows |
|---|---|---|
| **Microphone** | Audio from the Mac arrives on the PC and shows up to games and Discord as a normal microphone | A virtual audio cable: Steam Streaming Microphone (installed with Steam) or [VB-Cable](https://vb-audio.com/Cable/) |
| **DualSense** | A controller plugged into the Mac appears on the PC as a genuine PS5 gamepad | The [usbip-win2](https://github.com/vadimgrn/usbip-win2) driver, signed by Microsoft |

The features are independent: run only the microphone, only the gamepad, or both.

## Install

**Windows.** Download `HexBridge-Windows-x64.zip` from [Releases](https://github.com/HexArchy/hexbridge/releases),
unpack it, and run as administrator:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The script stops any older version, copies the files, opens the firewall port and
enables autostart. The app then generates a key and shows a QR code.

**macOS.** Download `HexBridge-macOS-arm64.zip`, put `HexBridge.app` into
Applications and launch it. Scan the QR code off the Windows screen, or type in
the short code.

That's it. You get a menu bar icon on the Mac and a tray icon on Windows.

Pairing happens once. After that the Mac finds the PC on its own — by a tag
derived from the shared key rather than by hostname — so an IP change after a
router reboot fixes itself without your involvement. The Mac will never connect to
someone else's HexBridge on the same network: a different PC has a different key,
therefore a different tag. Details in [docs/PROTOCOL.md](docs/PROTOCOL.md), under
"Host discovery".

Both sides check for updates once a day and install only with your consent; one
switch in Settings turns that off ([docs/UPDATES.md](docs/UPDATES.md)).

## How it works

A custom protocol over UDP: a 24-byte plaintext header and a payload under
AES-256-GCM with a shared key. The header is deliberately in the clear so that the
optional VPS relay can route packets without knowing the key and without any way
to decrypt what it forwards.

* **Audio** — Opus, 48 kHz mono, 20 ms frames, ~45 kbit/s. The receiver runs a
  jitter buffer with loss concealment and plays out through WASAPI.
* **Gamepad** — raw HID reports at 250 Hz. Windows assembles a virtual USB device
  from them and hands it to the system over USB/IP.

Full wire format: [docs/PROTOCOL.md](docs/PROTOCOL.md).

### Why the gamepad works this way

Forwarding a USB device off macOS with the stock tooling is **impossible**:
`IOHIDFamily` holds the HID interface exclusively, and Apple does not hand the
DriverKit entitlement for claiming a device to ordinary developers. So the boundary
runs along HID: the Mac ships reports and the device's real descriptors, and the
virtual USB device is assembled on the Windows side. Details and pitfalls in
[docs/DUALSENSE.md](docs/DUALSENSE.md).

## Layout

```
mac/       Swift + SwiftUI, menu bar app and CLI
win/       .NET 10 + Avalonia, tray app and console receiver
relay/     Go, optional relay for when the direct path is unavailable
docs/      protocol, design system, DualSense guide, relay deployment
```

Both platforms are organized by feature: each feature owns its module, its section
of the UI and its set of packet types. The shell iterates over registered features
and does not know any of them by name, so a third one can be added without touching
the first two.

## Build

```bash
brew install opus
swift build -c release --package-path mac      # macOS
bash mac/scripts/test.sh                       # Swift tests
dotnet build win/src/HexBridge.sln -c Release  # Windows
dotnet test  win/src/HexBridge.sln -c Release
go build -C relay .                            # relay
```

Use `mac/scripts/test.sh` rather than a bare `swift test`: on a machine without
Xcode, swift-testing lives where the linker won't find it by itself, and the script
adds exactly those paths.

To see what is visible on the network and which of it is ours:
`mac/build/HexBridge.app/Contents/MacOS/HexBridge discover`.

The Windows build also builds from macOS: `dotnet publish` cross-targets, and
Avalonia needs no Windows-only tooling. The real check runs in CI on
`windows-latest`.

## Relay

Needed only when the Mac cannot reach the PC directly. Both sides dial out to the
relay themselves, so no port forwarding is required on either end. The relay sees
only the header and cannot decrypt either the audio or the input. Deployment:
[docs/VPS.md](docs/VPS.md).

## Limitations

* DualSense HD haptics is **off by default**. It works — the signature haptics is
  an isochronous audio stream driving the voice-coil actuators, and HexBridge
  presents the controller as a composite USB device to carry it — but usbip-win2
  has an open bugcheck on exactly that isochronous audio path. With the switch
  off the audio function is absent from the configuration descriptor entirely, so
  Windows never loads the driver that carries the bug. Turn it on the first time
  with the receiver's autostart disabled. See [docs/DUALSENSE.md](docs/DUALSENSE.md).
* **Gamepad forwarding needs a Mac on the sending side.** The receiver builds a
  virtual USB device out of the real descriptors — device, configuration, HID
  report — and macOS hands all of those over through IOKit for free. Windows does
  not: the HID class driver never exposes the report descriptor at all, only
  preparsed data, and the device and configuration descriptors are reachable only
  by opening the parent hub for write access, which needs administrator rights and
  the port number. Reconstructing a descriptor would produce different bytes than
  the ones the receiver has to replay. The microphone and the clipboard work in
  both directions.
* Connecting the controller over Bluetooth is not supported, USB only.
* Discovery works within a single subnet: mDNS is not routed, and it will not cross
  a guest network or client isolation on the router. The link and the short code
  always work.
* The virtual USB controller is visible in the device tree, and kernel-level
  anti-cheats notice it.
* **Microphone permission has to be granted again after every update.** The build
  is ad-hoc signed rather than Developer ID signed: the code hash changes with each
  version, so macOS treats it as a new app and does not carry the granted access
  over. The app will show the prompt itself and bring the stream up as soon as
  access is granted — but if it was started by launchd and you aren't at the
  computer, the prompt sits there waiting. The fix is a Developer ID signature,
  which is a paid certificate rather than a line of code.

## License

MIT, see [LICENSE](LICENSE).
