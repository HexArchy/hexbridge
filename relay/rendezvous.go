package main

import (
	"crypto/sha256"
	"encoding/base64"
	"io"
	"net"
	"net/http"
	"sync"
	"time"
)

// The pairing rendezvous.
//
// Forwarding audio through the relay already works when both machines are behind
// NAT: each dials out, and the relay learns where they are. Pairing did not. It
// needed a TCP connection from the Mac straight to the PC, which is exactly the
// thing neither side can accept in that situation — so the one case the relay
// exists for was the one case you could not get into.
//
// So the PC leaves the sealed answer here and the Mac collects it. Both only ever
// make outbound connections.
//
// # What the relay is not told
//
// Not the code. The answer is sealed with a key derived from those twelve
// characters, so a relay that learned them could open it — and the whole point of
// this relay is that it forwards what it cannot read. It is handed an id derived
// from the code by a one-way hash instead: enough to match two parties who both
// know the code, useless to anyone who does not. What it stores is opaque to it,
// and stays opaque.
//
// An attacker who reached the relay and somehow guessed an id would get the same
// blob an eavesdropper on a direct exchange would, and would face the same
// PBKDF2 wall. Guessing the id means guessing the code: 60 bits.

const (
	// Matches ShortCode.Lifetime on the PC. A blob outliving the code it belongs
	// to would only ever produce «that is not the code I am showing».
	rendezvousTTL = 3 * time.Minute

	// Base64url of SHA-256 truncated to 16 bytes, unpadded.
	rendezvousIDLength = 22

	// The sealed answer is a few hundred bytes. This is room for the URI growing
	// a field or two, and a wall against anyone using the relay as storage.
	maxRendezvousBody = 8 * 1024

	// Concurrent pairings on one relay. A pairing lasts three minutes, so this is
	// far more than a household needs and still bounded.
	maxRendezvousEntries = 256

	rendezvousReadTimeout  = 5 * time.Second
	rendezvousWriteTimeout = 5 * time.Second

	// Deposits and collections per second from one address. Pairing is two
	// requests; this leaves room for retries and none for a sweep of the id space
	// — which would be futile anyway, but there is no reason to serve it.
	rendezvousRatePerSec = 5
)

var rendezvousPrefix = []byte("hexbridge-pair-rendezvous-v1")

// RendezvousID is the name two machines that know the same code agree on without
// either of them saying it. The clients derive it identically:
// `mac/Sources/HexBridgePairing/PairingSeal.swift` and
// `win/src/HexBridge.Core/PairingSeal.cs`, both pinned to the vector in
// docs/PROTOCOL.md.
//
// The code must already be normalised — upper case, alphabet only, no hyphens.
func RendezvousID(normalisedCode string) string {
	sum := sha256.Sum256(append(append([]byte{}, rendezvousPrefix...), normalisedCode...))
	return base64.RawURLEncoding.EncodeToString(sum[:16])
}

type deposit struct {
	blob     []byte
	expires  time.Time
	fromAddr string
}

type rendezvous struct {
	mu      sync.Mutex
	entries map[string]*deposit
	callers map[string]*caller
}

type caller struct {
	windowStart time.Time
	count       int
}

func newRendezvous() *rendezvous {
	return &rendezvous{
		entries: make(map[string]*deposit),
		callers: make(map[string]*caller),
	}
}

// allow applies the per-address rate limit, and sweeps expired entries while it
// already holds the lock.
func (r *rendezvous) allow(addr string, now time.Time) bool {
	c, ok := r.callers[addr]
	if !ok {
		if len(r.callers) > 4*maxRendezvousEntries {
			// Someone is cycling addresses. Forget the lot rather than grow.
			r.callers = make(map[string]*caller)
		}
		c = &caller{}
		r.callers[addr] = c
	}
	if now.Sub(c.windowStart) >= time.Second {
		c.windowStart = now
		c.count = 0
	}
	c.count++
	return c.count <= rendezvousRatePerSec
}

func (r *rendezvous) sweepLocked(now time.Time) {
	for id, d := range r.entries {
		if now.After(d.expires) {
			delete(r.entries, id)
		}
	}
	for addr, c := range r.callers {
		if now.Sub(c.windowStart) > time.Minute {
			delete(r.callers, addr)
		}
	}
}

// put stores a blob under id, replacing whatever was there. The PC re-deposits
// every time it shows a new code, and the old one must not answer for it.
func (r *rendezvous) put(id string, blob []byte, from string, now time.Time) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.sweepLocked(now)

	if _, replacing := r.entries[id]; !replacing && len(r.entries) >= maxRendezvousEntries {
		return false
	}

	r.entries[id] = &deposit{
		blob:     blob,
		expires:  now.Add(rendezvousTTL),
		fromAddr: from,
	}
	return true
}

// get returns the blob, leaving it in place: a Mac whose connection dropped
// halfway should be able to ask again rather than having to make a new code on
// the other machine. It expires on its own three minutes after it was left.
func (r *rendezvous) get(id string, now time.Time) ([]byte, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.sweepLocked(now)

	d, ok := r.entries[id]
	if !ok {
		return nil, false
	}
	return d.blob, true
}

func (r *rendezvous) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.entries)
}

func validID(id string) bool {
	if len(id) != rendezvousIDLength {
		return false
	}
	_, err := base64.RawURLEncoding.DecodeString(id)
	return err == nil
}

// handler serves the two requests a pairing needs and nothing else. Anything
// else — a browser, a scanner — gets 404 and no hint that this is anything.
func (r *rendezvous) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/rendezvous", func(w http.ResponseWriter, req *http.Request) {
		now := time.Now()
		host, _, err := net.SplitHostPort(req.RemoteAddr)
		if err != nil {
			host = req.RemoteAddr
		}

		r.mu.Lock()
		ok := r.allow(host, now)
		r.mu.Unlock()
		if !ok {
			http.Error(w, "", http.StatusTooManyRequests)
			return
		}

		id := req.URL.Query().Get("id")
		if !validID(id) {
			http.NotFound(w, req)
			return
		}

		switch req.Method {
		case http.MethodPut, http.MethodPost:
			blob, err := io.ReadAll(io.LimitReader(req.Body, maxRendezvousBody+1))
			if err != nil || len(blob) == 0 || len(blob) > maxRendezvousBody {
				http.Error(w, "", http.StatusBadRequest)
				return
			}
			if !r.put(id, blob, host, now) {
				http.Error(w, "", http.StatusServiceUnavailable)
				return
			}
			w.WriteHeader(http.StatusNoContent)

		case http.MethodGet:
			blob, found := r.get(id, now)
			if !found {
				http.NotFound(w, req)
				return
			}
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			w.Header().Set("Cache-Control", "no-store")
			_, _ = w.Write(blob)

		default:
			http.Error(w, "", http.StatusMethodNotAllowed)
		}
	})
	return mux
}

func (r *rendezvous) serve(listen string) (*http.Server, net.Listener, error) {
	ln, err := net.Listen("tcp", listen)
	if err != nil {
		return nil, nil, err
	}

	server := &http.Server{
		Handler:           r.handler(),
		ReadHeaderTimeout: rendezvousReadTimeout,
		ReadTimeout:       rendezvousReadTimeout,
		WriteTimeout:      rendezvousWriteTimeout,
		IdleTimeout:       rendezvousReadTimeout,
		MaxHeaderBytes:    4 * 1024,
	}
	go func() { _ = server.Serve(ln) }()
	return server, ln, nil
}
