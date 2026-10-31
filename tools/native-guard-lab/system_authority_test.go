//go:build windows

package main

import (
	"path/filepath"
	"testing"
)

func TestProductionSocketAuthority(t *testing.T) {
	root := `C:\Program Files`
	image := filepath.Join(root, "RoutePilot", "Agent", "native", "native-guard.exe")
	if !productionSocketIdentity(image, root, "S-1-5-18") {
		t.Fatal("protected LocalSystem guard rejected")
	}
	for _, sample := range []struct{ image, root, sid string }{
		{image, root, "S-1-5-32-544"}, // admin group is not the service identity
		{image, root, "S-1-5-21-1-2-3-1001"},
		{filepath.Join(root, "RoutePilot", "Agent", "zapret-live-lab", "native", "native-guard.exe"), root, "S-1-5-18"},
		{`C:\Users\User\native-guard.exe`, root, "S-1-5-18"},
		{filepath.Join(root, "Other", "Agent", "native", "native-guard.exe"), root, "S-1-5-18"},
		{"native-guard.exe", root, "S-1-5-18"},
		{image, "relative", "S-1-5-18"},
	} {
		if productionSocketIdentity(sample.image, sample.root, sample.sid) {
			t.Fatalf("untrusted production socket authority accepted: %q", sample.image)
		}
	}
}

func TestProductionSocketAuthorityIgnoresEnvironmentRoot(t *testing.T) {
	root := `C:\Program Files`
	image := filepath.Join(root, "RoutePilot", "Agent", "native", "native-guard.exe")
	for _, env := range []string{"", `C:\Users\User\Untrusted`} {
		t.Setenv("ProgramFiles", env)
		if !productionSocketIdentity(image, root, "S-1-5-18") {
			t.Fatal("protected known-folder identity depends on an environment override")
		}
	}
}
