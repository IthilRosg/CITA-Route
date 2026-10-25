package main

import (
	"strings"
	"testing"
)

func TestLabScope(t *testing.T) {
	run := strings.Repeat("a", 32)
	for _, p := range []string{"0.0.0.0/0", "::/0", "127.0.0.1/32", "1.1.1.2/24", "ff00::/8", "::ffff:1.1.1.1/128"} {
		if _, e := validate(request{Action: "Arm", Run: run, Prefixes: []string{p}}); e == nil {
			t.Fatal("unsafe lab scope", p)
		}
	}
	if _, e := validate(request{Action: "Arm", Run: run, Prefixes: []string{"1.1.1.0/24", "fdc1:1234::/64"}}); e != nil {
		t.Fatal(e)
	}
	if identity(run, "rule-0") == identity(run, "provider") || identity(run, "provider") == identity(strings.Repeat("b", 32), "provider") {
		t.Fatal("ownership collision")
	}
	if _, e := validate(request{Action: "Cleanup", Run: run, Prefixes: []string{"1.1.1.0/24"}}); e == nil {
		t.Fatal("cleanup policy")
	}
}
