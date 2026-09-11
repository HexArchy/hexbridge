// Command hexbridge-relay is a stateless UDP rendezvous relay for HexBridge.
//
// It never sees the pre-shared key: packet headers carry a room id derived from
// that key, and everything after the 24-byte header stays AES-GCM encrypted.
// The relay simply remembers which endpoints have recently spoken in a room and
// forwards every packet from one of them to the others.
package main

import (
	"encoding/binary"
	"flag"
	"log"
	"net"
	"net/netip"
	"os"
	"sync"
	"sync/atomic"
	"time"
)

const (
	headerSize = 24
	maxPacket  = 1400

	maxRooms            = 512
	maxEndpointsPerRoom = 4
	endpointTTL         = 60 * time.Second
	sweepInterval       = 15 * time.Second

	// Per-endpoint rate limit. Audio is 50 packets/s plus 1 hello/s, but a
	// forwarded gamepad adds one packet per HID report — hundreds per second —
	// so the cap has to clear that with room to spare.
	rateLimitPerSec = 2000
)

var magic = [4]byte{'M', 'B', 'G', '1'}

type endpoint struct {
	addr     netip.AddrPort
	lastSeen time.Time

	// Rate limiting state, reset once per second.
	windowStart time.Time
	windowCount int
}

type room struct {
	endpoints []*endpoint
}

// pick returns the endpoint record for addr, creating it if the room has space.
// It returns nil when the room is full of other live endpoints.
func (r *room) pick(addr netip.AddrPort, now time.Time) *endpoint {
	for _, ep := range r.endpoints {
		if ep.addr == addr {
			return ep
		}
	}

	// Reclaim a slot from an endpoint that has gone quiet.
	for _, ep := range r.endpoints {
		if now.Sub(ep.lastSeen) > endpointTTL {
			ep.addr = addr
			ep.windowStart = time.Time{}
			ep.windowCount = 0
			return ep
		}
	}

	if len(r.endpoints) >= maxEndpointsPerRoom {
		return nil
	}

	ep := &endpoint{addr: addr}
	r.endpoints = append(r.endpoints, ep)
	return ep
}

type relay struct {
	mu    sync.Mutex
	rooms map[uint64]*room

	forwarded atomic.Uint64
	dropped   atomic.Uint64

	// Counted apart from dropped: a refusal is a stranger being turned away,
	// which is the guest list working, while a drop is usually something wrong.
	refused atomic.Uint64
}

func newRelay() *relay {
	return &relay{rooms: make(map[uint64]*room)}
}

// route registers the sender and returns the peers a packet should go to.
func (r *relay) route(roomID uint64, from netip.AddrPort, now time.Time) []netip.AddrPort {
	r.mu.Lock()
	defer r.mu.Unlock()

	rm, ok := r.rooms[roomID]
	if !ok {
		if len(r.rooms) >= maxRooms {
			return nil
		}
		rm = &room{}
		r.rooms[roomID] = rm
	}

	ep := rm.pick(from, now)
	if ep == nil {
		return nil
	}

	if now.Sub(ep.windowStart) >= time.Second {
		ep.windowStart = now
		ep.windowCount = 0
	}
	ep.windowCount++
	if ep.windowCount > rateLimitPerSec {
		return nil
	}
	ep.lastSeen = now

	peers := make([]netip.AddrPort, 0, len(rm.endpoints)-1)
	for _, other := range rm.endpoints {
		if other == ep || other.addr == from {
			continue
		}
		if now.Sub(other.lastSeen) > endpointTTL {
			continue
		}
		peers = append(peers, other.addr)
	}
	return peers
}

// sweep drops rooms whose endpoints have all expired.
func (r *relay) sweep(now time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()

	for id, rm := range r.rooms {
		live := rm.endpoints[:0]
		for _, ep := range rm.endpoints {
			if now.Sub(ep.lastSeen) <= endpointTTL {
				live = append(live, ep)
			}
		}
		rm.endpoints = live
		if len(rm.endpoints) == 0 {
			delete(r.rooms, id)
		}
	}
}

func (r *relay) stats() (rooms, endpoints int) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, rm := range r.rooms {
		rooms++
		endpoints += len(rm.endpoints)
	}
	return
}

func main() {
	listen := flag.String("listen", ":47702", "UDP address to listen on")
	// Same +1 convention the PC uses for its own exchange, so one number in the
	// clients' settings describes the whole relay.
	pairListen := flag.String("pair-listen", ":47703", "TCP address for the pairing rendezvous, empty to disable")
	rooms := flag.String("rooms", "", "room ids this relay carries, comma separated or @file; empty carries anyone")
	quiet := flag.Bool("quiet", false, "suppress the periodic stats line")
	flag.Parse()

	guests, err := parseAllowlist(*rooms)
	if err != nil {
		log.Fatalf("bad -rooms: %v", err)
	}

	addr, err := net.ResolveUDPAddr("udp", *listen)
	if err != nil {
		log.Fatalf("bad -listen %q: %v", *listen, err)
	}

	conn, err := net.ListenUDP("udp", addr)
	if err != nil {
		log.Fatalf("listen %s: %v", *listen, err)
	}
	defer conn.Close()

	// A generous socket buffer keeps bursts from being dropped by the kernel.
	_ = conn.SetReadBuffer(1 << 20)
	_ = conn.SetWriteBuffer(1 << 20)

	log.Printf("hexbridge-relay listening on %s", conn.LocalAddr())
	if guests.open() {
		log.Printf("no -rooms given: this relay carries anybody who finds it")
	} else {
		log.Printf("carrying %d room(s)", guests.size())
	}

	r := newRelay()

	// Pairing needs a way in that does not depend on either side accepting a
	// connection — otherwise the one situation the relay exists for is the one
	// situation you cannot pair in.
	meeting := newRendezvous()
	if *pairListen != "" {
		_, ln, err := meeting.serve(*pairListen)
		if err != nil {
			log.Fatalf("listen %s: %v", *pairListen, err)
		}
		log.Printf("hexbridge-relay pairing rendezvous on %s", ln.Addr())
	}

	go func() {
		ticker := time.NewTicker(sweepInterval)
		defer ticker.Stop()
		for now := range ticker.C {
			r.sweep(now)
			if !*quiet {
				live, endpoints := r.stats()
				log.Printf("rooms=%d endpoints=%d forwarded=%d dropped=%d refused=%d pairings=%d",
					live, endpoints, r.forwarded.Load(), r.dropped.Load(), r.refused.Load(), meeting.count())
			}
		}
	}()

	buf := make([]byte, maxPacket)
	for {
		n, from, err := conn.ReadFromUDPAddrPort(buf)
		if err != nil {
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				continue
			}
			log.Printf("read: %v", err)
			// A closed socket is fatal; anything else is transient.
			if os.IsNotExist(err) {
				return
			}
			continue
		}

		if n < headerSize || n > maxPacket {
			r.dropped.Add(1)
			continue
		}
		if buf[0] != magic[0] || buf[1] != magic[1] || buf[2] != magic[2] || buf[3] != magic[3] {
			r.dropped.Add(1)
			continue
		}
		if buf[4] != 1 {
			r.dropped.Add(1)
			continue
		}

		roomID := binary.LittleEndian.Uint64(buf[8:16])
		if !guests.permits(roomID) {
			r.refused.Add(1)
			continue
		}

		peers := r.route(roomID, from, time.Now())
		if len(peers) == 0 {
			r.dropped.Add(1)
			continue
		}

		for _, peer := range peers {
			if _, err := conn.WriteToUDPAddrPort(buf[:n], peer); err != nil {
				log.Printf("forward to %s: %v", peer, err)
				continue
			}
			r.forwarded.Add(1)
		}
	}
}
