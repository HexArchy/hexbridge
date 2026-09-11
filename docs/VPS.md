# The relay on a VPS

The relay is only needed when the direct path `Mac → the host's public IP` does not
work. It decrypts nothing: it sees the 24-byte header, matches participants up by
`room`, and forwards datagrams byte for byte.

## As a standalone compose file

The quickest option, and it does not disturb an existing stack.

Below, `vps` is your ssh alias for the server.

```bash
rsync -a relay/ vps:/opt/hexbridge-relay/
ssh vps 'cd /opt/hexbridge-relay && docker compose up -d --build'
ssh vps 'ufw allow 47702/udp comment "hexbridge relay"'
```

To check:

```bash
ssh vps 'docker logs -f hexbridge-relay'
# hexbridge-relay listening on [::]:47702
# rooms=1 endpoints=2 forwarded=648 dropped=3
```

`endpoints=2` means both sides found each other. `endpoints=1` means the other side
never reached the relay.

## Inside an existing stack

If the relay has to live alongside the rest of `~/Workspace/vps`:

1. Copy `relay/` into the repository as `hexbridge/`.
2. Add the service to `docker-compose.yml`:

```yaml
  hexbridge-relay:
    build: ./hexbridge
    container_name: hexbridge-relay
    restart: unless-stopped
    ports:
      - "47702:47702/udp"
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"
```

3. Add the port to `setup-firewall.sh` next to the other rules:

```bash
ufw allow 47702/udp comment "hexbridge relay"
```

4. `./deploy.sh` — it does the rsync and the `docker compose up -d` itself.

## Pairing through the relay

The relay also runs a small TCP listener — `-pair-listen`, `:47703` by default, the same
`+1` convention the PC uses — that lets two machines pair when neither can accept a
connection. The PC leaves its sealed answer there and the Mac collects it; nothing else
is served, and every other path answers 404.

Open **both** ports on the VPS: UDP for the audio and TCP for pairing.

```
ufw allow 47702/udp
ufw allow 47703/tcp
```

The relay never learns the pairing code, so it cannot read what it is holding. See
"Pairing through the relay" in [PROTOCOL.md](PROTOCOL.md) for why that is true rather
than merely intended.

Set the relay's address in the PC's Settings, under the key and relay section. From then
on the pairing screen shows the relay's address instead of the PC's local ones, and that
is the single address to type on the Mac — whatever network either machine is on.

## How fast it will carry

`--rate` is the packets per second one endpoint may send, and it defaults to 20 000 —
about 20 MB/s at this packet size. It used to be 2 000, which was sized for a voice
call and a gamepad and is far too little now that a file rides the same socket.

Raising it is not generosity. A relay that drops a chunk does not slow a transfer
down politely: the chunk comes back as a hole in the next acknowledgement and is sent
a second time, so a cap set too low costs more traffic than it saves. The floor is
1 000 and the relay refuses to start below it, because one voice call and one
forwarded gamepad already need more than that.

```
hexbridge-relay --rate 40000
```

The senders aim just under whatever this is rather than discovering it by losing
chunks, so it is worth setting to something your VPS can actually push.

## Carrying only your own machines

With no list the relay forwards for anyone who finds it. The header is plaintext by
design, so a stranger can pick any room id and use a public VPS as a free forwarder.
There is no amplification in that — one packet in, one packet out, and only to an
endpoint already in the same room — and each endpoint is rate limited, but none of that
makes the box theirs to use.

`--rooms` closes it:

```
hexbridge-relay --rooms 1122334455667788
hexbridge-relay --rooms @/etc/hexbridge/rooms      # one per line, # for comments
```

The room id is on the PC, in Settings under the key and relay section. It is derived
from the pre-shared key and gives nothing of the key away, so it is safe to put in a
command line or a config file. The relay still cannot read a byte of what it carries;
this is a guest list, not a lock.

A relay started with a list it cannot parse — or an empty file — refuses to start rather
than falling back to carrying everybody.

## Why not go through the existing Hysteria2

Hysteria2 on UDP/443 is a proxy for client traffic, and there is no reason to route
the voice stream into it: it would add its own congestion control and its own
encryption on top of an already encrypted stream, which means nothing but latency.
The relay does exactly one thing — forward a datagram — and costs roughly zero CPU.

## Limits

* 512 rooms, 4 addresses per room, address TTL 60 seconds.
* 2000 packets/s per address: voice is 51 packets/s, but a forwarded gamepad adds
  one packet per HID report.
* IPv4/IPv6 UDP only, with no TLS wrapper: the payload is already under
  AES-256-GCM.
