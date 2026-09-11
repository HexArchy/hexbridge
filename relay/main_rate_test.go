package main

import (
	"net/netip"
	"testing"
	"time"
)

// The limit is what a sender aims just under, so a relay built without anyone
// setting it must still carry traffic rather than refuse everything.
func TestARelayBuiltWithoutSayingSoStillCarries(t *testing.T) {
	r := newRelay()

	if r.ratePerSec != defaultRatePerSec {
		t.Fatalf("rate is %d, expected the default %d", r.ratePerSec, defaultRatePerSec)
	}
}

func TestTheLimitIsTheOneItWasGiven(t *testing.T) {
	r := newRelay()
	r.ratePerSec = 3
	room := uint64(9)
	mac := netip.MustParseAddrPort("203.0.113.7:50000")
	pc := netip.MustParseAddrPort("198.51.100.9:47702")
	now := time.Now()

	// Two endpoints, so there is somewhere to forward to and route answers.
	r.route(room, pc, now)

	carried := 0
	for i := 0; i < 6; i++ {
		if peers, _ := r.route(room, mac, now); len(peers) > 0 {
			carried++
		}
	}

	if carried != 3 {
		t.Fatalf("carried %d packets, expected the limit of 3", carried)
	}

	// The window is a second wide, so the next one starts clean.
	if peers, _ := r.route(room, mac, now.Add(time.Second+time.Millisecond)); len(peers) == 0 {
		t.Fatal("still refusing after the window rolled over")
	}
}
