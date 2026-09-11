package main

import (
	"encoding/binary"
	"net/netip"
	"testing"
	"time"
)

func header(room uint64) []byte {
	h := make([]byte, headerSize)
	copy(h, magic[:])
	h[4] = 1
	h[5] = 1 // audio, so the type below is visibly overwritten
	binary.LittleEndian.PutUint64(h[8:16], room)
	binary.LittleEndian.PutUint32(h[16:20], 0xAABBCCDD)
	binary.LittleEndian.PutUint32(h[20:24], 42)
	return h
}

func TestAnIntroductionNamesTheAddress(t *testing.T) {
	about := netip.MustParseAddrPort("203.0.113.7:47702")

	packet := introduction(header(0x1122334455667788), about)
	if packet == nil {
		t.Fatal("no packet")
	}

	if got := packet[5]; got != peerPacketType {
		t.Fatalf("type %d, expected %d", got, peerPacketType)
	}
	if room := binary.LittleEndian.Uint64(packet[8:16]); room != 0x1122334455667788 {
		t.Fatalf("room %x was not carried over from the packet being routed", room)
	}

	body := packet[headerSize:]
	if body[0] != 4 {
		t.Fatalf("family %d, expected 4", body[0])
	}
	if addr, _ := netip.AddrFromSlice(body[1:5]); addr.String() != "203.0.113.7" {
		t.Fatalf("address %s", addr)
	}
	if port := binary.LittleEndian.Uint16(body[5:7]); port != 47702 {
		t.Fatalf("port %d", port)
	}
}

// The relay has no session and is not part of anybody's numbering, so it must
// not hand over numbers that look like the peer's.
func TestAnIntroductionCarriesNoSessionOrSequence(t *testing.T) {
	packet := introduction(header(1), netip.MustParseAddrPort("203.0.113.7:47702"))

	if s := binary.LittleEndian.Uint32(packet[16:20]); s != 0 {
		t.Fatalf("session %x, expected zero", s)
	}
	if q := binary.LittleEndian.Uint32(packet[20:24]); q != 0 {
		t.Fatalf("seq %d, expected zero", q)
	}
	if f := binary.LittleEndian.Uint16(packet[6:8]); f != 0 {
		t.Fatalf("flags %x, expected zero", f)
	}
}

func TestAnIPv6IntroductionIsMarkedAsSuch(t *testing.T) {
	packet := introduction(header(1), netip.MustParseAddrPort("[2001:db8::1]:47702"))
	if packet == nil {
		t.Fatal("no packet")
	}

	body := packet[headerSize:]
	if body[0] != 6 {
		t.Fatalf("family %d, expected 6", body[0])
	}
	if len(body) != 1+16+2 {
		t.Fatalf("body is %d bytes, expected %d", len(body), 1+16+2)
	}
	if port := binary.LittleEndian.Uint16(body[17:19]); port != 47702 {
		t.Fatalf("port %d", port)
	}
}

// A v4 address arriving as a v4-mapped v6 one must go out as four bytes, or the
// other side dials an address its stack may not even have a route for.
func TestAMappedAddressIsSentAsIPv4(t *testing.T) {
	mapped := netip.AddrPortFrom(netip.MustParseAddr("::ffff:203.0.113.7"), 47702)

	packet := introduction(header(1), mapped)
	if packet == nil {
		t.Fatal("no packet")
	}
	if body := packet[headerSize:]; body[0] != 4 {
		t.Fatalf("family %d, expected 4", body[0])
	}
}

func TestBothEndsAreIntroducedAndThenLeftAlone(t *testing.T) {
	r := newRelay()
	room := uint64(7)
	mac := netip.MustParseAddrPort("203.0.113.7:50000")
	pc := netip.MustParseAddrPort("198.51.100.9:47702")
	now := time.Now()

	// Only one endpoint so far: nobody to introduce anyone to.
	if _, intros := r.route(room, mac, now); len(intros) != 0 {
		t.Fatalf("%d introductions with one endpoint", len(intros))
	}

	_, intros := r.route(room, pc, now)
	if len(intros) != 2 {
		t.Fatalf("%d introductions, expected both ends to be told", len(intros))
	}

	told := map[netip.AddrPort]netip.AddrPort{}
	for _, intro := range intros {
		told[intro.to] = intro.about
	}
	if told[mac] != pc || told[pc] != mac {
		t.Fatalf("each end was not told about the other: %v", told)
	}

	// Repeating on every packet would be fifty introductions a second.
	if _, again := r.route(room, pc, now.Add(100*time.Millisecond)); len(again) != 0 {
		t.Fatalf("%d introductions straight after the first", len(again))
	}

	// But a mapping can change, so it is said again eventually.
	if _, later := r.route(room, pc, now.Add(introduceEvery+time.Second)); len(later) == 0 {
		t.Fatal("never repeated")
	}
}
