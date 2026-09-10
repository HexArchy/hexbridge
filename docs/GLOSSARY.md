# Interface glossary

The two apps are written in different languages by different people, and the same
idea has to come out as the same word on both. This table is the authority; when a
string on one platform disagrees with a string on the other, this file wins.

English is the default. Russian is the only other language, and it is what the
interface used to be written in — so the Russian column is not a translation to be
invented, it is the wording that already shipped.

## Product concepts

| Concept | English | Русский |
|---|---|---|
| The app itself | HexBridge | HexBridge |
| The machine your peripherals are plugged into | this computer shares its microphone | этот компьютер отдаёт свой микрофон |
| The machine the games run on | this computer receives a microphone | этот компьютер принимает чужой микрофон |
| Pairing two machines | pairing | связывание |
| The shared secret | pairing key | общий ключ |
| The short pairing code | pairing code | короткий код |
| Optional VPS hop | relay | релей |

Never use "sender" or "receiver" in user-facing text on either platform. People do
not think of their computers that way; they think about where the microphone is.

## Features

| Concept | English | Русский |
|---|---|---|
| Microphone feature | Microphone | Микрофон |
| HID forwarding feature | Devices | Устройства |
| Clipboard feature | Clipboard | Буфер обмена |
| Forwarding a device | forwarding | проброс |
| The virtual audio device on Windows | virtual audio cable | виртуальный кабель |
| DualSense signature haptics | HD haptics | HD-хаптика |
| Adaptive trigger resistance | adaptive triggers | адаптивные триггеры |

## States

Every feature reports one of these. Keep them lowercase in the popover, where they
sit under a bold feature name and read as one column.

| State | English | Русский |
|---|---|---|
| Feature switched off | off | выключено |
| Starting up | starting | запускается |
| Our side ready, other side not | waiting for the PC | ждёт Windows |
| Working | working | работает |
| Muted but otherwise working | muted | заглушен |
| Broken | not working | не работает |
| Impossible on this platform | not available here | здесь недоступно |

"not available here" is deliberately different from "off": one is a switch the user
can flip, the other is a thing this platform cannot do. Never render the second as
the first — someone will hunt for a switch that does not exist.

## Words to avoid in English

| Do not write | Write instead |
|---|---|
| receiver, sender | the PC, this computer |
| report, HID report | (say nothing — this is an internal detail) |
| packet, datagram | (say nothing) |
| VID/PID, descriptors | (say nothing; keep them in the log) |
| error codes, `0xE00002C1` | plain description of what went wrong |
| Failed to… | say what is not happening: "No sound is reaching the PC" |

Exclamation marks are not used anywhere. Neither are apologies. A message says what
is happening and, when there is one, what to do next.
