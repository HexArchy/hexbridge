# HexBridge wire protocol v1

A one-way voice stream, `Mac → Windows`, over UDP. The transport does not depend on
Moonlight or Sunshine: it is a separate channel, which is why it works with any
stock client.

## Packet

```
+--------------------- header, 24 bytes, plaintext ----------------------+
| off | size | field   | description                                     |
|-----|------|---------|-------------------------------------------------|
|  0  |  4   | magic   | ASCII "MBG1"                                    |
|  4  |  1   | version | 1                                               |
|  5  |  1   | type    | 1=AUDIO, 2=HELLO, 3=PONG, 4=DEV_ATTACH,         |
|     |      |         | 5=DEV_DETACH, 6=DEV_IN, 7=DEV_OUT, 8=DEV_ACK,   |
|     |      |         | 9=BULK_OFFER, 10=BULK_CHUNK, 11=BULK_ACK,       |
|     |      |         | 12=BULK_DONE, 13=HAPTIC                         |
|  6  |  2   | flags   | LE u16, bit0=MUTED, bit1=DTX_GAP                |
|  8  |  8   | room    | LE u64, room id for the relay                   |
| 16  |  4   | session | LE u32, random on every sender start            |
| 20  |  4   | seq     | LE u32, monotonic packet counter                |
+------------------------------------------------------------------------+
| ciphertext (variable length)                                           |
+------------------------------------------------------------------------+
| GCM tag, 16 bytes                                                      |
+------------------------------------------------------------------------+
```

The maximum packet size is 1400 bytes, which fits a typical MTU without
fragmentation.

## Encryption

AES-256-GCM.

* **key** — a 32-byte pre-shared key (PSK) shared by sender and receiver.
* **nonce** (12 bytes) — `session (LE u32) || seq (LE u32) || direction (LE u32)`,
  where `direction` is 0 for `Mac → Windows` and 1 for control packets going the
  other way. `session` is random on every start, so the (session, seq) pair never
  repeats.
* **AAD** — all 24 bytes of the header. The header is not encrypted, so that the
  relay can route packets without knowing the PSK.
* **tag** — 16 bytes, appended at the end.

The receiver keeps a 1024-packet anti-replay window: a packet whose `seq` has
already been seen is dropped, and so is one that is too old
(`seq < max_seq - 1024`).

## room

```
room = LE_u64( SHA256("hexbridge-room-v1" || PSK)[0..8] )
```

The relay routes by `room`, but it does not know the PSK and cannot decrypt the
payload. Leaking `room` buys an attacker nothing more than the ability to make
noise in someone else's room — those packets are dropped when the GCM tag is
checked.

## Packet types

### AUDIO (type=1)

```
| off | size | field                                      |
|  0  |  4   | audio frame number, LE u32                 |
|  4  |  N   | Opus packet                                |
```

Opus: 48 000 Hz, mono, 20 ms frames (960 samples), `OPUS_APPLICATION_VOIP`, inband
FEC on. Packets go out exactly every 20 ms while the microphone is unmuted.

The frame number is deliberately separate from `seq`: `seq` also counts HELLO
packets, so it cannot be used to order frames in the jitter buffer — you would get
one phantom "lost" frame every second.

### HELLO (type=2)

Sent once a second by both sides. It serves two purposes: the relay learns the
addresses of the room's participants, and the receiver learns that the sender is
alive.

```
| off | size | field                           |
|  0  |  8   | unix send time, ms, LE u64      |
|  8  |  1   | role: 0=sender, 1=receiver      |
|  9  |  1   | name length, N                  |
| 10  |  N   | node name, UTF-8                |
```

### PONG (type=3)

The receiver's answer to the sender's HELLO, direction=1. It lets the sender show
an RTT and confirm that audio is arriving.

```
| off | size | field                                     |
|  0  |  8   | echo of the unix time from HELLO, LE u64  |
|  8  |  8   | AUDIO packets received, LE u64            |
| 16  |  8   | AUDIO packets lost, LE u64                |
```

## Device channel (HID)

Same socket, same key and `session`, separate packet types. Gamepad forwarding
runs over this channel.

The line of responsibility runs along HID: **the Mac sends reports and
descriptors**, while assembling the virtual USB device and speaking USB/IP live
entirely on Windows.

This is not a convenience choice. Forwarding a USB device off macOS is impossible
in principle: `IOHIDFamily` holds the HID interface exclusively (`USBInterfaceOpen`
on it always returns `kIOReturnExclusiveAccess`), and Apple does not hand the
DriverKit entitlement for claiming a device to ordinary developers. The
**descriptors themselves, however, are freely readable**, with no `open` and no
privileges: the full configuration via `GetConfigurationDescriptorPtr`, and the HID
report descriptor via the `kIOHIDReportDescriptorKey` property. So what travels to
Windows is the device's real descriptors, not homemade ones.

### Multiple devices and arbitrary HID

The device number runs from 0 to 3, so up to four devices at once. That limit is
deliberate rather than technical: each device means a separate virtual USB device
on Windows and a separate stream of reports, and there are essentially no games
that tell more than four controllers apart.

The number is assigned by the Mac side on attach and stays with the physical device
for as long as it is plugged in. Once it detaches, the number is released and may
go to another device. Windows must distinguish devices by that number and nothing
else: most gamepads have no serial number, and `locationID` changes from port to
port.

**Any HID device** can be forwarded, not just a DualSense: wheels, pedals, HOTAS,
keyboards, mice. Nothing in the channel is PS5-specific — `DEV_ATTACH` carries the
real descriptors, and `DEV_IN` and `DEV_OUT` move reports through as-is without
looking inside. Parsing the contents is only needed by features that want to show
device state in the UI.

Non-HID devices cannot be forwarded: on macOS a system driver holds them, and
`USBInterfaceOpen` on such an interface always returns `kIOReturnExclusiveAccess`.
The only way around that is the DriverKit entitlement, which Apple does not hand to
ordinary developers.

### DEV_ATTACH (type=4), Mac → Windows

Sent when a device is attached and repeated once a second until Windows
acknowledges it. It carries everything the receiver needs to assemble a virtual USB
device without asking for anything else.

```
| off | size | field                                   |
|  0  |  1   | device number (0..3)                    |
|  1  |  1   | number of descriptor blocks, K          |
|  2  |  …   | K blocks                                |
```

Each block:

```
| off | size | field                                          |
|  0  |  1   | type: 1=device, 2=configuration, 3=HID report, |
|     |      |       4=feature report snapshot                |
|  1  |  2   | length, LE u16                                 |
|  3  |  N   | raw bytes exactly as the device gave them      |
```

For a type-4 block the first data byte is the feature report number, followed by
its contents.

Feature reports travel alongside the descriptors on purpose. Windows has to answer
`GET_REPORT` from games and from Steam, and the data lives on the Mac; a
request-response over the network for every such call would add latency at exactly
the moment a game is deciding whether to accept the controller. The values are
static for the lifetime of the session, so the snapshot is handed over up front.
Three are mandatory: `0x20` — firmware, `0x09` — MAC addresses, `0x05` — sensor
calibration. Zeros in `0x05` cause a division by zero in games, so an empty
snapshot is not acceptable: if the report cannot be read, the device is not
forwarded at all.

For a DualSense that is 18 + 227 + 273 = 518 bytes plus headers — one packet, no
splitting needed. The receiver takes VID, PID and `bcdDevice` from the device
descriptor rather than from separate fields: fewer places where the data can drift
apart.

A DualSense has no serial number (`iSerial = 0`), so several controllers are
distinguished only by the device number in this field.

### DEV_ACK (type=8), Windows → Mac, direction=1

Confirmation that the virtual device has been assembled and the receiver is ready
for `DEV_IN`. One field: the device number, u8.

Without it the sender has no idea whether `DEV_ATTACH` arrived. It is tempting to
treat the first `DEV_OUT` as the acknowledgement, but that is a trap: a game that
never touches rumble or the lightbar will not send a single `DEV_OUT`, and the Mac
would repeat a 569-byte `DEV_ATTACH` once a second forever. So the acknowledgement
is separate and mandatory.

The receiver sends `DEV_ACK` for every `DEV_ATTACH` it gets, not just the first
one: the packet could have been lost, and the retry has to be able to cure that.

### DEV_DETACH (type=5), Mac → Windows

One field: the device number, u8. Windows detaches the virtual device.

### DEV_IN (type=6), Mac → Windows

An input HID report as-is, report id included.

```
| off | size | field                    |
|  0  |  1   | device number            |
|  1  |  4   | report number, LE u32    |
|  5  |  N   | HID input report         |
```

The counter is separate from audio and from `seq` — it is how the receiver spots
gaps. Losing an input report is not a problem: the next one carries the full state,
so unlike audio there is nothing to reconstruct.

Over USB a DualSense sends report `0x01`, 64 bytes long, at **250 Hz**
(`bInterval = 6` on High Speed). That is 16 kB/s of raw data, roughly
**128 kbit/s** per controller — three times the voice stream, but still not much.
On a DualSense Edge the input runs at 1000 Hz, while output on both models is
capped at the same 250 Hz.

### DEV_OUT (type=7), Windows → Mac, direction=1

An output HID report: rumble, adaptive triggers, lightbar.

```
| off | size | field                    |
|  0  |  1   | device number            |
|  1  |  N   | HID output report        |
```

Sent only when the state changes, not as a stream: rumble and triggers change
rarely, and there is no reason to push 250 packets a second back the other way.

For a DualSense this is report `0x02`, 48 bytes long (1 byte of ID plus 47 per the
descriptor). That is exactly how much to send — Linux pads it to 63, but that is
unnecessary.


## Reliable transfer of large objects

Voice and input are built so that a lost packet is cheaper to drop than to resend:
the next one carries fresh state anyway. The clipboard is the opposite — you cannot
deliver *most* of an image. So a small acknowledged layer lives on top of the same
socket, shared by every feature that needs integrity.

It is deliberately primitive: a fixed-size window, retransmit on timeout, receive by
chunk number. No congestion control and no reordering — objects are measured in
megabytes and seconds, not gigabytes, and building a second TCP on top of UDP is not
worth it.

### BULK_OFFER (type=9)

An offer to transfer an object. Repeated until a `BULK_ACK` arrives with
`accepted = 1`, but no more than once a second and for no more than ten attempts.

```
| off | size | field                                        |
|  0  |  4   | transfer id, LE u32                          |
|  4  |  1   | kind: 1=clipboard                            |
|  5  |  1   | format: 1=UTF-8 text, 2=PNG, 3=arbitrary     |
|  6  |  4   | total size in bytes, LE u32                  |
| 10  |  4   | number of chunks, LE u32                     |
| 14  | 32   | SHA-256 of the whole object                  |
| 46  |  2   | description length, LE u16, N                |
| 48  |  N   | description for the UI, UTF-8                |
```

The size is capped at 16 MiB. Anything bigger is file transfer, which will get a
kind of its own.

The hash is not only for verification: on receiving a `BULK_OFFER` whose hash
matches something it already has, the receiver answers `accepted = 0` and does not
pull the data. That also kills the loop when both sides are syncing the clipboard to
each other.

### BULK_CHUNK (type=10)

```
| off | size | field                          |
|  0  |  4   | transfer id, LE u32            |
|  4  |  4   | chunk number, LE u32           |
|  8  |  N   | chunk data                     |
```

A chunk is 1024 bytes, except the last one. The size is chosen so that a packet with
its header, tag and control fields stays comfortably below a typical MTU: UDP
fragmentation along the way through relays and tunnels turns one loss into the loss
of the whole chunk.

### BULK_ACK (type=11)

The acknowledgement. Sent in response to `BULK_OFFER` and then every 200 ms for as
long as the transfer is running.

```
| off | size | field                                             |
|  0  |  4   | transfer id, LE u32                               |
|  4  |  1   | accepted: 1 = send it, 0 = not needed             |
|  5  |  4   | number of the first missing chunk, LE u32         |
|  9  |  2   | how many chunk numbers follow, LE u16, K          |
| 11  | 4·K  | numbers of missing chunks after the first, LE u32 |
```

The sender resends only what is listed. The list is capped at 256 numbers. There is
no truncation flag in the packet: exactly 256 listed numbers is itself the signal
that "there may be more", and the sender starts over from the first missing chunk.
With exactly 257 holes that costs one redundant resend — at that link quality
selective retransmission is not winning anyway.

When nothing is missing, the "first missing chunk" field carries the chunk count,
that is one more than the last chunk, and `K` is zero. Otherwise the sender would
read that field as a real chunk number and resend a chunk that does not exist.

### BULK_DONE (type=12)

One field: the transfer id, LE u32. The receiver sends it once the object is
assembled and the hash matches. Until then the sender holds the data in memory.

If the hash does not match, `BULK_DONE` is not sent; a `BULK_ACK` listing every
chunk as missing goes out instead. There is no such thing as silent corruption.
This repeats at most three times: against a sender that corrupts the data the same
way every time, the loop would otherwise never end.

A lost `BULK_DONE` would otherwise hang the transfer forever: the receiver considers
itself finished and stops sending anything, while the sender keeps the data in
memory. So the receiver remembers completed transfers for 30 seconds and answers any
packet about them with `BULK_DONE` again, while the sender, having sent everything
and heard nothing back, repeats the last chunk every 500 ms as a nudge.

### Limits and timeouts

| | |
|---|---|
| Object size | from 1 byte to 16 MiB; an empty object is rejected |
| Description | at most 256 bytes, truncated on a UTF-8 boundary |
| Send rate | at most 1500 chunks per second |
| Sender silence | 15 s — the receiver forgets the transfer |
| Idle incoming transfer | 30 s |

The rate limit is not politeness: 16 MiB is around 16 000 packets, and without a cap
one image would blow through the relay's limit of 2000 packets per second and take
voice and input down with it.

Reusing a transfer id with a different hash counts as a new transfer, not a
continuation of the old one.

## DualSense haptics (type=13), Windows → Mac, direction=1

The PS5's signature haptics is not rumble bytes but sound: over USB the controller
exposes a 48 kHz, four-channel audio interface where channels 0–1 go to the speaker
and the headset jack, and 2–3 drive the voice-coil actuators in the grips. The game
writes ordinary PCM there.

That is why the channel is separate from `DEV_OUT`: that one carries occasional
commands, this one a continuous 384 kB/s stream. Mixing them into one type would
mean pushing haptics through logic designed for "once a second".

```
| off | size | field                                   |
|  0  |  1   | device number                           |
|  1  |  1   | channels in the block, always 2         |
|  2  |  4   | block number, LE u32                    |
|  6  |  N   | PCM, S16LE, channel-interleaved         |
```

A block is 5 ms of audio, that is 240 frames per channel, and **only channels 2 and
3**, the voice-coil actuators. That comes to 960 bytes, which fits inside the MTU
with room to spare along with the header and the tag.

Sending four channels is not ruled out to save bandwidth: 4 × 240 × 2 = 1920 bytes
plus header and tag makes 1966 — past the 1400 limit, so the datagram would
fragment. And there would be no point: channels 0–1 are the speaker and the headset,
not haptics.

Silence is not transmitted at all. The numbering keeps counting through it, so a gap
is unambiguously distinguishable from a pause, and an idle channel costs nothing.

A lost block is not recovered: by the time a retransmission arrived, the moment of
impact would have passed. The receiver inserts silence — on haptics that feels like
a skipped beat rather than a crackle.

The Mac plays what it receives into the controller itself: a DualSense plugged in
over USB comes up as an ordinary CoreAudio device, so the return path needs neither
privileges nor raw USB.


## Host discovery

Pairing by QR code stays, but on a home network the machines should find each other
on their own. The host publishes the `_hexbridge._udp.local.` service, and the Mac
looks for it.

The danger is obvious: in a dorm, a coworking space, or at a friend's place, there
may be someone else's HexBridge on the network, and connecting to it must never
happen under any circumstances.

### A tag instead of a name

What the TXT record publishes is neither a name nor the key, but a **tag** derived
from the shared key:

```
tag = base64url( SHA256("hexbridge-discovery-v1" || PSK)[0..16] )
```

The Mac connects automatically **only** to a host whose tag matches the tag of its
own key. Someone else's host has a different key, therefore a different tag, and as
far as we are concerned it does not exist.

The tag does not give the key away: it is a one-way hash of 32 random bytes and
cannot be inverted. It gives away exactly one fact — "this pair is already paired" —
which is obvious from the traffic to anyone watching the network anyway.

### Before there is a pair

An unpaired Mac has no key, and therefore no tag to compare against. Such a Mac sees
the hosts on the network but **does not connect to them silently**: it lists them,
and pairing still requires the short code from the host's screen. Discovery saves
you typing an address here; it does not replace the confirmation.

Otherwise the first stranger's host on the network would become "ours" — precisely
what must not be allowed.

### What else is in the TXT record

```
v    protocol version, integer
port data port, integer
name host name to show in the list, UTF-8
tag  the tag, see above
```

The name is only shown in the list during first pairing. It cannot be relied on: the
host's owner chooses it, and matching names prove nothing — only the tag decides.

### Changing networks

The tag is not tied to an address, so a host moving to a different IP does not
require re-pairing: the Mac finds it again by tag. That also covers the case where
the address comes from DHCP and changes after the router reboots — the most common
cause of "it worked yesterday, it doesn't today".

## Relay

The VPS relay is needed only when direct delivery is impossible. It does not decrypt
traffic, and works like this:

1. A packet arrives from address `E` for room `R`.
2. `E` is recorded in room `R` (TTL 60 s, at most 4 addresses per room).
3. The packet is forwarded byte for byte to every other live address in that room.

Limits: no more than 512 rooms, and 2000 packets/s from a single address — voice is
51 packets/s, but a forwarded gamepad adds another 250.
