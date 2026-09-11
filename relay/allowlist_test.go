package main

import (
	"os"
	"path/filepath"
	"testing"
)

// A relay with no list is the relay this project shipped with, and upgrading
// must not lock anybody out of their own VPS overnight.
func TestNoListCarriesAnybody(t *testing.T) {
	list, err := parseAllowlist("")
	if err != nil {
		t.Fatalf("empty spec: %v", err)
	}

	if !list.open() {
		t.Fatal("an empty list should be open")
	}
	if !list.permits(0xDEADBEEFCAFEBABE) {
		t.Fatal("an open relay refused a room")
	}
}

func TestAListCarriesOnlyWhatItNames(t *testing.T) {
	list, err := parseAllowlist("0x1122334455667788, 99aabbccddeeff00")
	if err != nil {
		t.Fatalf("parse: %v", err)
	}

	if list.open() {
		t.Fatal("a list with rooms in it is not open")
	}
	if !list.permits(0x1122334455667788) {
		t.Fatal("refused a room it names")
	}
	if !list.permits(0x99aabbccddeeff00) {
		t.Fatal("refused the second room it names")
	}
	if list.permits(0x1122334455667789) {
		t.Fatal("carried a room one bit away from a listed one")
	}
}

// The clients may print the id either way, and a person retyping it should not
// have to care.
func TestTheIdIsAcceptedHoweverItIsWritten(t *testing.T) {
	for _, spec := range []string{"1122334455667788", "0x1122334455667788", "0X1122334455667788", "1122334455667788 "} {
		list, err := parseAllowlist(spec)
		if err != nil {
			t.Fatalf("%q: %v", spec, err)
		}
		if !list.permits(0x1122334455667788) {
			t.Fatalf("%q did not permit the room it names", spec)
		}
	}
}

func TestRubbishIsRefusedAtStartupRatherThanIgnored(t *testing.T) {
	for _, spec := range []string{"nonsense", "0xZZ", "12345678901234567890", ","} {
		if _, err := parseAllowlist(spec); err == nil {
			t.Fatalf("%q was accepted", spec)
		}
	}
}

func TestAListCanLiveInAFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "rooms")
	body := "# the gaming pc\n1122334455667788\n\n# the laptop\n0x99aabbccddeeff00\n"
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}

	list, err := parseAllowlist("@" + path)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}

	if list.size() != 2 {
		t.Fatalf("read %d rooms, expected 2", list.size())
	}
	if !list.permits(0x1122334455667788) || !list.permits(0x99aabbccddeeff00) {
		t.Fatal("a room from the file was refused")
	}
}

func TestAMissingFileIsAnErrorNotAnOpenRelay(t *testing.T) {
	if _, err := parseAllowlist("@/no/such/file"); err == nil {
		t.Fatal("a missing file quietly became an open relay")
	}
}

// A file of nothing but comments is a mistake, and the dangerous reading of it
// would be "carry everybody".
func TestAnEmptyFileIsAnError(t *testing.T) {
	path := filepath.Join(t.TempDir(), "rooms")
	if err := os.WriteFile(path, []byte("# nothing here yet\n"), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}

	if _, err := parseAllowlist("@" + path); err == nil {
		t.Fatal("a file with no rooms quietly became an open relay")
	}
}
