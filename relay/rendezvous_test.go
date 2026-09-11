package main

import (
	"bytes"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// The id is derived identically by three implementations, so it is pinned here
// and in the two clients' suites against the same code. Change the prefix or the
// truncation and all three fail, which is the point.
func TestRendezvousIDMatchesTheClients(t *testing.T) {
	const code = "TUJJC8XU3LJ4"
	const want = "rzWzGdiB0Jjyt5YKBdmhXA"

	if got := RendezvousID(code); got != want {
		t.Fatalf("RendezvousID(%q) = %q, want %q", code, got, want)
	}
}

func TestRendezvousIDRevealsNothingAboutNeighbours(t *testing.T) {
	a := RendezvousID("TUJJC8XU3LJ4")
	b := RendezvousID("TUJJC8XU3LJ5")

	if a == b {
		t.Fatal("one character of the code did not change the id")
	}
	if len(a) != rendezvousIDLength {
		t.Fatalf("id is %d characters, expected %d", len(a), rendezvousIDLength)
	}
}

// What the PC leaves is what the Mac collects, byte for byte. The relay is not
// supposed to understand it, so anything it did to it would be damage.
func TestABlobSurvivesTheRoundTrip(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	id := RendezvousID("TUJJC8XU3LJ4")
	blob := "AQECAwQFBgcICQoLDA0ODxCgoaKjpKWmp6ipqqs="

	put(t, server.URL, id, blob, http.StatusNoContent)

	if got := get(t, server.URL, id, http.StatusOK); got != blob {
		t.Fatalf("collected %q, left %q", got, blob)
	}
}

// A Mac whose connection dropped halfway must be able to ask again, rather than
// having to make a new code on a machine that is in another building.
func TestCollectingTwiceWorks(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	id := RendezvousID("TUJJC8XU3LJ4")
	put(t, server.URL, id, "blob", http.StatusNoContent)

	get(t, server.URL, id, http.StatusOK)
	get(t, server.URL, id, http.StatusOK)
}

func TestANewCodeReplacesTheOldOne(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	id := RendezvousID("TUJJC8XU3LJ4")
	put(t, server.URL, id, "first", http.StatusNoContent)
	put(t, server.URL, id, "second", http.StatusNoContent)

	if got := get(t, server.URL, id, http.StatusOK); got != "second" {
		t.Fatalf("collected %q, expected the newer deposit", got)
	}
}

func TestAnUnknownIDIsNotFound(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	get(t, server.URL, RendezvousID("AAAABBBBCCCC"), http.StatusNotFound)
}

func TestAMalformedIDIsNotFound(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	for _, id := range []string{"", "short", strings.Repeat("A", 23), "!!!!!!!!!!!!!!!!!!!!!!"} {
		get(t, server.URL, id, http.StatusNotFound)
	}
}

// The relay is a letterbox, not a disk.
func TestAnOversizedDepositIsRefused(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	put(t, server.URL, RendezvousID("TUJJC8XU3LJ4"), strings.Repeat("x", maxRendezvousBody+1), http.StatusBadRequest)
}

func TestAnEmptyDepositIsRefused(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	put(t, server.URL, RendezvousID("TUJJC8XU3LJ4"), "", http.StatusBadRequest)
}

func TestABlobExpiresWithItsCode(t *testing.T) {
	meeting := newRendezvous()
	id := RendezvousID("TUJJC8XU3LJ4")
	start := time.Now()

	meeting.put(id, []byte("blob"), "1.2.3.4", start)

	if _, ok := meeting.get(id, start.Add(rendezvousTTL-time.Second)); !ok {
		t.Fatal("gone before its code expired")
	}
	if _, ok := meeting.get(id, start.Add(rendezvousTTL+time.Second)); ok {
		t.Fatal("still there after its code expired")
	}
}

func TestTheStoreIsBounded(t *testing.T) {
	meeting := newRendezvous()
	now := time.Now()

	for i := 0; i < maxRendezvousEntries; i++ {
		if !meeting.put(fmt.Sprintf("id-%d", i), []byte("blob"), "1.2.3.4", now) {
			t.Fatalf("refused deposit %d, below the limit", i)
		}
	}
	if meeting.put("one-too-many", []byte("blob"), "1.2.3.4", now) {
		t.Fatal("accepted a deposit past the limit")
	}

	// Replacing an existing id is not a new entry and must still work when full.
	if !meeting.put("id-0", []byte("newer"), "1.2.3.4", now) {
		t.Fatal("refused to replace an existing deposit while full")
	}
}

func TestSweepingMakesRoomAgain(t *testing.T) {
	meeting := newRendezvous()
	now := time.Now()

	for i := 0; i < maxRendezvousEntries; i++ {
		meeting.put(fmt.Sprintf("id-%d", i), []byte("blob"), "1.2.3.4", now)
	}

	later := now.Add(rendezvousTTL + time.Second)
	if !meeting.put("after-the-sweep", []byte("blob"), "1.2.3.4", later) {
		t.Fatal("expired deposits did not free their slots")
	}
	if meeting.count() != 1 {
		t.Fatalf("%d entries left, expected only the new one", meeting.count())
	}
}

func TestHammeringIsRateLimited(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	id := RendezvousID("TUJJC8XU3LJ4")
	limited := false
	for i := 0; i < rendezvousRatePerSec+3; i++ {
		response, err := http.Get(server.URL + "/rendezvous?id=" + id)
		if err != nil {
			t.Fatalf("get: %v", err)
		}
		response.Body.Close()
		if response.StatusCode == http.StatusTooManyRequests {
			limited = true
		}
	}
	if !limited {
		t.Fatal("no request was ever refused")
	}
}

// A scanner should learn nothing. Every path but the one gets the same 404 a
// bare Go server gives, with no header saying what this is.
func TestAnythingElseIsNotFound(t *testing.T) {
	server := httptest.NewServer(newRendezvous().handler())
	defer server.Close()

	for _, path := range []string{"/", "/pair", "/rendezvous/", "/index.html"} {
		response, err := http.Get(server.URL + path)
		if err != nil {
			t.Fatalf("get %s: %v", path, err)
		}
		response.Body.Close()
		if response.StatusCode != http.StatusNotFound {
			t.Fatalf("%s answered %d, expected 404", path, response.StatusCode)
		}
	}
}

// MARK: - helpers

func put(t *testing.T, base, id, body string, want int) {
	t.Helper()

	request, err := http.NewRequest(http.MethodPut, base+"/rendezvous?id="+id, bytes.NewBufferString(body))
	if err != nil {
		t.Fatalf("build request: %v", err)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatalf("put: %v", err)
	}
	defer response.Body.Close()

	if response.StatusCode != want {
		t.Fatalf("put answered %d, expected %d", response.StatusCode, want)
	}
}

func get(t *testing.T, base, id string, want int) string {
	t.Helper()

	response, err := http.Get(base + "/rendezvous?id=" + id)
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer response.Body.Close()

	if response.StatusCode != want {
		t.Fatalf("get answered %d, expected %d", response.StatusCode, want)
	}
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	return string(body)
}
