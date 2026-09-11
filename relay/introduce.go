package main

import (
	"encoding/binary"
	"net/netip"
	"time"
)

// Introducing the two ends to each other.
//
// Everything works without this: each side dials the relay, and the relay
// forwards. But it forwards *every* packet of a voice call and a gamepad, all
// day, through a machine that may be on another continent — a hop neither side
// needs once they can see each other. Most of the time they can: two sides that
// are both sending outward have both opened a mapping, and a packet aimed
// straight at the other one arrives.
//
// So the relay says who it is talking to. It is the only party that can: it sees
// both public addresses, and neither endpoint can see its own.
//
// # Why this payload is in the clear
//
// The relay has no key, by design — that is the whole basis of it being safe to
// run on somebody else's VPS. It therefore cannot seal anything, and this is the
// only packet type whose payload is plaintext.
//
// Nothing is trusted on the strength of it. It names an address worth trying;
// the path is adopted only after a packet arrives from that address and
// decrypts. A forged introduction costs a handful of probe packets aimed at an
// address that will not answer, and buys the forger nothing: they cannot make
// either side accept data, and they cannot learn anything by being told about
// it.

const (
	// One introduction per endpoint per interval. Both sides need it repeated —
	// a mapping can change, a side can restart — but it is housekeeping, not
	// traffic, and once a second is already more than enough.
	introduceEvery = 2 * time.Second

	// type 14, PacketType.Peer on both clients.
	peerPacketType = 14
)

// introduction returns the packet telling `to` where `about` is, or nil when
// there is nothing useful to say.
//
// The header is copied from a packet the room is already carrying, so the room
// id, the version and the magic are right by construction; only the type and the
// payload are ours.
func introduction(header []byte, about netip.AddrPort) []byte {
	addr := about.Addr().Unmap()
	if !addr.IsValid() {
		return nil
	}

	raw := addr.AsSlice()
	if len(raw) != 4 && len(raw) != 16 {
		return nil
	}

	packet := make([]byte, headerSize+1+len(raw)+2)
	copy(packet, header[:headerSize])
	packet[5] = peerPacketType

	// Flags, session and seq are meaningless coming from the relay: it has no
	// session and is not part of anybody's sequence. Zeroed rather than copied,
	// so a client that ever did look at them sees nothing it could mistake for
	// the peer's own numbering.
	packet[6], packet[7] = 0, 0
	binary.LittleEndian.PutUint32(packet[16:20], 0)
	binary.LittleEndian.PutUint32(packet[20:24], 0)

	body := packet[headerSize:]
	if len(raw) == 4 {
		body[0] = 4
	} else {
		body[0] = 6
	}
	copy(body[1:], raw)
	binary.LittleEndian.PutUint16(body[1+len(raw):], about.Port())

	return packet
}

// due reports whether this endpoint is owed another introduction, and records
// that it is being sent.
func (e *endpoint) due(now time.Time) bool {
	if now.Sub(e.lastIntroduced) < introduceEvery {
		return false
	}
	e.lastIntroduced = now
	return true
}
