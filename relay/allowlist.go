package main

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// Who the relay is willing to carry.
//
// Without a list it carries anyone. That is fine on a machine only its owner
// knows about and wrong on a VPS with a public address: the header is plaintext
// by design, so a stranger can pick any room id and use the box as a free
// forwarder. There is no amplification in it — one packet in, one packet out to
// an endpoint that has to already be in the same room — and the rate limit caps
// each endpoint, but none of that makes it *theirs* to use.
//
// A room is derived from the pre-shared key: the clients show it, and it goes on
// the relay's command line. The relay still never sees the key, only the eight
// bytes hashed out of it, and the list does not make it able to read anything it
// carries. It is a guest list, not a lock.

type allowlist struct {
	rooms map[uint64]struct{}
}

// parseAllowlist reads the -rooms flag: a comma-separated list of room ids, or
// @path to read them one per line. An empty flag means everyone is welcome.
//
// Ids are accepted as the clients print them, with or without a 0x prefix, in
// either case.
func parseAllowlist(spec string) (*allowlist, error) {
	spec = strings.TrimSpace(spec)
	if spec == "" {
		return &allowlist{}, nil
	}

	var fields []string
	if strings.HasPrefix(spec, "@") {
		file, err := os.Open(spec[1:])
		if err != nil {
			return nil, err
		}
		defer file.Close()

		scanner := bufio.NewScanner(file)
		for scanner.Scan() {
			line := strings.TrimSpace(scanner.Text())
			// Somewhere to write down which room belongs to which machine.
			if line == "" || strings.HasPrefix(line, "#") {
				continue
			}
			fields = append(fields, line)
		}
		if err := scanner.Err(); err != nil {
			return nil, err
		}
	} else {
		fields = strings.Split(spec, ",")
	}

	list := &allowlist{rooms: make(map[uint64]struct{}, len(fields))}
	for _, field := range fields {
		field = strings.TrimSpace(field)
		if field == "" {
			continue
		}

		id, err := parseRoom(field)
		if err != nil {
			return nil, fmt.Errorf("room %q: %w", field, err)
		}
		list.rooms[id] = struct{}{}
	}

	if len(list.rooms) == 0 {
		return nil, fmt.Errorf("no rooms in %q", spec)
	}
	return list, nil
}

func parseRoom(field string) (uint64, error) {
	text := strings.TrimPrefix(strings.TrimPrefix(strings.ToLower(field), "0x"), "0X")
	return strconv.ParseUint(text, 16, 64)
}

// open reports whether the relay carries anybody who turns up.
func (a *allowlist) open() bool { return len(a.rooms) == 0 }

func (a *allowlist) permits(room uint64) bool {
	if a.open() {
		return true
	}
	_, ok := a.rooms[room]
	return ok
}

func (a *allowlist) size() int { return len(a.rooms) }
