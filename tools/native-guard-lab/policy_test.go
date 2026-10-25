package main

import (
	"net/netip"
	"testing"
)

func TestTrustedPolicyExclusionsAndDigest(t *testing.T) {
	p := guardPolicy{Vpn: []string{"0.0.0.0/0", "::/0"}, Direct: []string{"10.0.0.0/8", "100.64.0.0/10", "fe80::/10", "::1/128"}}
	prefixes, e := compilePolicy(p)
	if e != nil {
		t.Fatal(e)
	}
	for text, want := range map[string]bool{"1.1.1.1": true, "10.1.2.3": false, "100.100.1.1": false, "2606:4700::1111": true, "fe80::1": false, "::1": false} {
		addr := netip.MustParseAddr(text)
		found := false
		for _, prefix := range prefixes {
			if prefix.Contains(addr) {
				found = true
			}
		}
		if found != want {
			t.Fatal("exclusion", text)
		}
	}
	other, e := compilePolicy(guardPolicy{Vpn: []string{"::/0", "0.0.0.0/1", "128.0.0.0/1"}, Direct: p.Direct})
	if e != nil || policyDigest(other) != policyDigest(prefixes) {
		t.Fatal("policy identity depends on ordering")
	}
	for _, invalid := range []guardPolicy{{Vpn: []string{"1.1.1.2/24"}}, {Vpn: []string{"0.0.0.0/0"}, Direct: []string{"0.0.0.0/0"}}, {Vpn: []string{"::ffff:1.1.1.1/128"}}} {
		if _, e := compilePolicy(invalid); e == nil {
			t.Fatal("invalid policy accepted")
		}
	}
}
