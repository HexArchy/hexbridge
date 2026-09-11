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
|  4  |  1   | kind: 1=clipboard, 2=file                    |
|  5  |  1   | format: 1=UTF-8 text, 2=PNG, 3=arbitrary     |
|  6  |  4   | total size in bytes, LE u32                  |
| 10  |  4   | number of chunks, LE u32                     |
| 14  | 32   | SHA-256 of the whole object                  |
| 46  |  2   | description length, LE u16, N                |
| 48  |  N   | description for the UI, UTF-8                |
```

### How big, and how fast

The wire format does not change for either of these. `size` is `u32`, so **4 GiB less
one byte** is the ceiling the format itself imposes, and the chunk count of such an
object — 4 194 304 — still fits its own `u32`. The ack already copes: it names the
first missing chunk and up to 256 more, and "exactly 256" already means "there are
more, start again from the first".

The caps are per kind, because the two kinds are not alike:

| kind | cap | why |
|---|---|---|
| clipboard | 64 MiB | it is held in memory at both ends, and a clipboard that large is a mistake rather than a use |
| file | 4 GiB − 1 | the format's own limit |

**A file is never held whole in memory.** The side sending it reads each chunk off
disk as it goes, which it can do because it still has the file; the side receiving it
writes each chunk straight into a temporary file at that chunk's offset and remembers
which have landed in a bitmap — 512 KB for a full-size object, against four gigabytes
for the obvious implementation. The hash is checked when the last hole fills, and only
then does the file get its real name.

**Pacing is a local decision, not a contract.** Each side may send as fast as it likes;
nothing in the format says otherwise. What the format does say is what happens when it
sends too fast — the missing lists come back longer — and that is the signal a sender
is expected to slow down on. A sender that ignores it will go slower overall, not
faster, because every chunk it loses it sends twice.

A relay in the path has its own per-endpoint limit and drops what exceeds it. Dropped
chunks come back as holes, so aiming above a relay's limit is a way of making a
transfer slower. A sender that knows it is talking through a relay should not try.

### Files, kind 2

A file is the same object as anything else on this channel, with two rules on top.

* **`format` is 3, arbitrary.** Not the text or image formats — a `.txt` is a file
  because somebody dropped a file, and turning it back into clipboard text on the
  other side would be a surprise.
* **`description` is the file's name and nothing else.** No path, no directory, just
  `notes.txt`. It is capped at 256 bytes like every other description, so a longer
  name is trimmed before it is sent, keeping the extension.

**The name is not to be trusted.** It crossed a network, and the side that receives
it writes to disk with it. Before that it is stripped of everything but its own last
component, and of any character the local filesystem gives meaning to — a name that
is empty, or is `.`, or `..`, after that becomes `file`. A received name never
decides which directory is written to, only what the file inside it is called.

Files land in the user's Downloads folder. A name already taken gets ` (2)`, ` (3)`
and so on before the extension, so nothing is ever written over.

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

The sender resends what is listed. The list is capped at 256 numbers. There is no
truncation flag in the packet: exactly 256 listed numbers is itself the signal that
"there may be more".

What the sender does about that "more" is its own business, and only two things are
required of it: answer every number it was given, and keep making progress past the
last one. The obvious reading — start again from the first missing chunk — is one way
and an expensive one: it costs about ×1.75 in chunks on the wire at 1 % loss, and on a
four-gigabyte object it means re-sending the whole thing every 200 ms because of a
single early loss. Both implementations instead answer the listed stretch exactly and,
beyond it, send only chunks they have never sent. Nothing is lost by that: the
receiver goes on acknowledging every 200 ms, so a hole it could not name this time is
named next time.

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
| Object size | 1 byte to 64 MiB for a clipboard, to 4 GiB − 1 for a file; empty is rejected |
| Description | at most 256 bytes, truncated on a UTF-8 boundary |
| Send rate | no fixed ceiling; see "How big, and how fast" |
| Sender silence | 15 s — the receiver forgets the transfer |
| Idle incoming transfer | 30 s |

The send rate used to be fixed at 1500 chunks a second, which was there so that one
clipboard image could not blow through a relay's limit and take voice and input down
with it. It is not fixed any more, because a file is not one image: a ceiling low
enough to be safe for a relay made a gigabyte take eleven minutes. What replaced it
is the loss response above — the rate finds the path instead of guessing at it — and
the reserve that keeps voice and input out of the bulk budget.

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

## Leaving the relay behind

A relay carries every packet of a call through a machine that may be on another
continent. Once both ends can see each other it is a hop neither of them needs — and
most of the time they can, because two sides that are both sending outward have both
opened a mapping.

The relay is the only party that can arrange it: it sees both public addresses, and
neither endpoint can see its own. So every two seconds it tells each end where the
other is.

### Type 14, `PEER` — relay to both ends

The one packet whose payload is **not** encrypted. It could not be otherwise: the relay
has no key, which is the whole basis of it being safe to run on somebody else's box.

```
byte 0        address family: 4 or 6
bytes 1…n     address, 4 or 16 bytes
last 2 bytes  port, LE u16
```

The header is copied from a packet the room is already carrying, so the magic, version
and room are right by construction. Flags, session and seq are zeroed: the relay has no
session and is not part of anybody's numbering.

### What each side does with it

Nothing on trust. It names an address worth knocking on.

* **Both ends** start sending their once-a-second keepalive to that address as well as
  to the relay. That is the hole punch: each opens a mapping the other can use.
* **The Mac** opens a second socket to it and moves the audio there only once a packet
  arrives over it **that decrypts**. If the direct path is then silent for 3.5 seconds —
  three missed keepalives — the relay takes over again. Both sockets stay open the whole
  time, so the fallback costs nothing.
* **The PC** already replies wherever an authenticated packet last came from, so it
  follows automatically.
* An introduction is only accepted from the relay the endpoint is already talking to.

A forged introduction costs a few probe packets aimed at an address that will not
answer. It cannot make either side accept data, because accepting still requires a
packet that decrypts.

## The fast path for files (TCP, data port + 2)

Measured between a Mac and a Windows VM on one virtual network: the chunked UDP
channel above moved a file at **2.8 MB/s**, and a plain TCP stream between the same
two machines moved one at **370 MB/s**. The reason is not the encryption or the disk.
Every byte of a file on that channel travels in a 1024-byte piece inside its own
datagram, so 50 MB/s would need fifty thousand datagrams a second, each of them
sealed, framed and handed to the kernel one at a time.

So a file does not use it when it does not have to. The reliable UDP channel stays
exactly as it is — it is what the clipboard uses, and it is the fallback for a file
when no connection can be made — but the normal case for a file is a TCP stream.

Nothing here replaces the UDP channel's guarantees; it sidesteps the need for them.
TCP already delivers every byte, in order, once, with the kernel's own congestion
control, and one write hands over a megabyte instead of a kilobyte. There is no offer,
no acknowledgement, no bitmap and no pacing on this path.

### When it is not even tried

A link known to run through a relay skips the stream and goes straight to the UDP
path. The contract already says a relay cannot carry one, so spending the three-second
deadline discovering that on every file is three seconds wasted each time.

Falling back happens on a failure to connect, and only then. A stream that breaks
part-way reports a failure instead — re-sending gigabytes down a path that has just
died is not a recovery.

### Making the connection

The machine that would receive listens on **data port + 2** — 47704 beside the usual
47702. The machine that would send connects, and if it cannot within **three seconds**
it falls back to the UDP channel and the transfer happens the slow way rather than not
at all.

### What crosses it

```
magic   "MBG1", 4 bytes, then version 1
room    LE u64, the same room the UDP header carries
opening AES-256-GCM, nonce = 0, sealing: name length LE u16, name UTF-8, size LE u32, SHA-256
records AES-256-GCM, nonce = record number, 1…65536 bytes of plaintext each
end     a record whose plaintext is empty
```

Every record — the opening one, each data record, and the empty one that ends the
stream — is preceded by **LE u32 giving the length of `ciphertext || tag`**. Something
has to say where a record ends, and a sealed 64 KiB record is 65 552 bytes, which does
not fit in two. The nonce is the record number written **little-endian across all
twelve bytes**, so that a reader padding a u32 and a reader padding a u64 arrive at the
same value. No record carries associated data. The opening record is number 0, data
records follow from 1, and the empty record takes the number after the last of them.
The name is cut to 255 bytes, extension kept, before it is sealed.

None of that is interesting, and all of it is the kind of thing two implementations
settle differently and discover months later, so it is written down rather than left to
whoever writes the second one.

The magic and the room travel in the clear, exactly as they do in the UDP header and
for the same reason: something has to be readable before there is a key to read with.
Everything after is sealed under the pairing key, so a connection from somebody who
does not hold it fails at the opening record and is dropped without a byte of the file
being written.

The nonce is the record number, and a record number is never reused on a connection —
one connection carries one file. The receiving side hashes as it writes and compares
at the end; a mismatch means the file is deleted rather than renamed, the same as on
the other path.

**A relay cannot carry this.** It forwards datagrams, and this is a stream. Two
machines that can only reach each other through a relay keep the UDP path, which is
why that path is not going anywhere.

## The short-code exchange

A twelve-character code cannot carry a 32-byte key, so it is a one-time ticket. The PC
holds a TCP listener on `data port + 1` for three minutes and answers one request:

```
GET /pair?code=<normalised code>&enc=1 HTTP/1.1
```

The code is normalised before it is sent and before it is compared: upper case, alphabet
only, hyphens dropped. The alphabet is `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` — 32 symbols,
twelve characters, exactly 60 bits. A wrong code is answered `403`, an unknown path `404`,
and those two statuses are the only ones either side turns into "the PC did not accept the
code".

### The sealed answer

With `enc=1` the body is base64 of

```
byte  0            format version, currently 1
bytes 1…16         salt
bytes 17…28        nonce
bytes 29…n-17      ciphertext
last 16 bytes      GCM tag
```

* key — `PBKDF2-HMAC-SHA256(password = normalised code, salt, 600 000 rounds) → 32 bytes`
* cipher — AES-256-GCM, associated data = the single version byte
* plaintext — the `hexbridge://pair?…` URI, UTF-8

Without `enc=1` the body is that URI in the clear. That is what a Mac built before this
existed asks for, and it still works; a Mac that asks for `enc=1` and gets plaintext back
refuses it and says the PC needs updating, rather than accepting a key that crossed the
network unprotected.

**Why PBKDF2 and not HKDF.** 60 bits behind a fast derivation is worth roughly a weekend
of rented GPUs to anyone who captured the blob and can then guess offline. 600 000 rounds
multiply that by about 2²⁰ at a cost of one derivation per side, during a step a person is
already waiting on. The PC derives once per code rather than per request, so the cost
cannot be spent by a stranger, and the code has to match before there is anything to
decrypt at all.

**Why the Mac does not use URLSession.** The peer has no certificate, so App Transport
Security refuses the request outright. The exchange goes over `NWConnection` — the same
socket the rest of the protocol uses — rather than switching ATS off for the whole
application.

### Pairing through the relay

The direct exchange needs the Mac to open a TCP connection to the PC. When neither
machine can accept one — both behind NAT, which is the situation a relay exists for —
that cannot happen, so the relay holds the answer instead. Both sides only ever dial
out.

```
PUT /rendezvous?id=<id>     the PC leaves the sealed blob, 204 on success
GET /rendezvous?id=<id>     the Mac collects it, 200 with the blob, or 404
```

The blob is the *same* sealed answer the direct exchange returns, unchanged. The relay
stores it opaquely for three minutes, replaces it when a new code is made, and serves it
more than once so a dropped connection can be retried.

`id = base64url_nopad(SHA256("hexbridge-pair-rendezvous-v1" ‖ normalised code)[0..16])`

**The relay is never told the code.** The key to the blob is derived from the code, so a
relay that learned it could read what it is carrying — the one thing this relay is built
not to do. It gets the id instead: enough to match two parties who both know the code,
useless to anyone who does not. Guessing an id means guessing the code, which is 60 bits,
and the blob behind it is still behind PBKDF2.

Limits: 8 KB per blob, 256 concurrent pairings, 5 requests/s per address, and every other
path answers 404.

The Mac asks for `/rendezvous` first and `/pair` second, against whatever address was
typed. One of the two answers 404 and the other hands over the answer, so a person does
not have to know whether they are pointing at a relay or at the PC.

When a relay is configured, the PC puts the **relay's** address into the payload rather
than its own, so the Mac aims its audio at the relay without a second setting. The host
is stored as typed and resolved by the Mac at connect time, so a relay behind a name that
moves keeps working.

### Test vector

Both implementations are pinned to this, in
`mac/Tests/HexBridgePairingTests/PairingSealTests.swift`,
`win/src/HexBridge.Tests/PairingSealTests.cs` and, for the id, `relay/rendezvous_test.go`. Salt is `01 02 … 10`, nonce is `A0 A1 … AB`.

```
code       TUJJC8XU3LJ4
plaintext  hexbridge://pair?v=1&h=10.0.0.7&p=47702&k=3q2-796tvu_erb7v3q2-796tvu_erb7v3q2-796tvu8&n=PC
key        3b5d88c7628acaec690b06bce40aca7174eed292d27a8ded9d3b801d3beb513c
id         rzWzGdiB0Jjyt5YKBdmhXA
blob       AQECAwQFBgcICQoLDA0ODxCgoaKjpKWmp6ipqquYsSxi+zUzZvJq1//po186Z/Mjzmj52LsWiAN6XTnFUHQm
           oJ2ReIRAhxGw77vd7slbDBE7r5lxvBMh0XPAfjdwIa8S86pAsZ3skt5HC/MCC6Rw0CT9aQd5D0b1ZyCUuovF
           ba2HYAgIqgXf
```

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
