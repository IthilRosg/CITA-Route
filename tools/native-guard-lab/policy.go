package main

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"net/netip"
	"strings"

	"go4.org/netipx"
)

type guardPolicy struct{ Vpn, Direct []string }

func compilePolicy(p guardPolicy) ([]netip.Prefix, error) {
	if len(p.Vpn) == 0 || len(p.Vpn) > 256 || len(p.Direct) > 256 {
		return nil, errors.New("policy size")
	}
	var set netipx.IPSetBuilder
	for _, group := range []struct {
		values []string
		remove bool
	}{{p.Vpn, false}, {p.Direct, true}} {
		for _, text := range group.values {
			prefix, e := netip.ParsePrefix(text)
			if e != nil || prefix != prefix.Masked() || prefix.Addr().Is4In6() {
				return nil, errors.New("noncanonical policy")
			}
			if group.remove {
				set.RemovePrefix(prefix)
			} else {
				set.AddPrefix(prefix)
			}
		}
	}
	// Local IPC never traverses a VPN. Keep it usable even for a default-route
	// policy; this exclusion cannot permit traffic to a remote public address.
	set.RemovePrefix(netip.MustParsePrefix("127.0.0.0/8"))
	set.RemovePrefix(netip.MustParsePrefix("::1/128"))
	result, e := set.IPSet()
	if e != nil {
		return nil, e
	}
	prefixes := result.Prefixes()
	if len(prefixes) == 0 || len(prefixes) > 4096 {
		return nil, errors.New("empty or oversized compiled policy")
	}
	return prefixes, nil
}
func policyDigest(prefixes []netip.Prefix) string {
	var set netipx.IPSetBuilder
	for _, p := range prefixes {
		set.AddPrefix(p)
	}
	value, e := set.IPSet()
	if e != nil {
		return ""
	}
	var lines []string
	for _, p := range value.Prefixes() {
		lines = append(lines, p.String())
	}
	sum := sha256.Sum256([]byte(strings.Join(lines, "\n")))
	return hex.EncodeToString(sum[:])
}
