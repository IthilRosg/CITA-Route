//go:build windows

package main

import (
	"github.com/tailscale/wf"
	"testing"
)

func systemFixture(t *testing.T) *wf.Rule {
	t.Helper()
	const run = "0123456789abcdef0123456789abcdef"
	f := connectorFixture()
	f.Conditions[0].Value = "System"
	f.Name = marker + run + ":system"
	f.Provider = wf.ProviderID(identity(run, "provider"))
	f.Sublayer = wf.SublayerID(identity(run, "sublayer"))
	f.ProviderData = []byte(marker + run)
	hash, e := connectorRuleIdentity(f)
	if e != nil {
		t.Fatal(e)
	}
	f.ID = wf.RuleID(identity(run, "system-"+hash))
	return f
}
func TestSystemCompanionScope(t *testing.T) {
	const run = "0123456789abcdef0123456789abcdef"
	if !isSystemCompanion(run, systemFixture(t)) {
		t.Fatal("exact dynamic tuple rejected")
	}
	for _, mutate := range []func(*wf.Rule){
		func(f *wf.Rule) { f.Persistent = true }, func(f *wf.Rule) { f.Name += "other" }, func(f *wf.Rule) { f.ProviderData = []byte("other") },
		func(f *wf.Rule) { f.HardAction = true }, func(f *wf.Rule) { f.Disabled = true }, func(f *wf.Rule) { f.Conditions[0].Value = "system" },
		func(f *wf.Rule) { f.Conditions[6].Value = uint16(52002) }, func(f *wf.Rule) { f.Action = wf.ActionBlock },
	} {
		f := systemFixture(t)
		mutate(f)
		if isSystemCompanion(run, f) {
			t.Fatal("broadened or unowned dynamic permission accepted")
		}
	}
	for i := 0; i < 7; i++ {
		f := systemFixture(t)
		f.Conditions = append(f.Conditions[:i], f.Conditions[i+1:]...)
		if isSystemCompanion(run, f) {
			t.Fatal("missing dynamic socket condition accepted", i)
		}
	}
	// No mutation may turn the System permission into a persistent exception.
	f := systemFixture(t)
	f.Persistent = true
	if _, e := connectorRuleIdentity(f); e == nil {
		t.Fatal("persistent System tuple accepted")
	}
}
func TestDynamicAgentPermissionCannotPersist(t *testing.T) {
	const run = "0123456789abcdef0123456789abcdef"
	f := connectorFixture()
	f.Name = marker + run + ":socket-agent"
	f.Provider = wf.ProviderID(identity(run, "provider"))
	f.Sublayer = wf.SublayerID(identity(run, "sublayer"))
	f.ProviderData = []byte(marker + run)
	h, e := connectorRuleIdentity(f)
	if e != nil {
		t.Fatal(e)
	}
	f.ID = wf.RuleID(identity(run, "socket-agent-"+h))
	if !isDynamicSocketAgent(run, f) {
		t.Fatal("exact dynamic agent rule rejected")
	}
	f.Persistent = true
	if isDynamicSocketAgent(run, f) {
		t.Fatal("persistent socket agent rule accepted")
	}
	f.Persistent = false
	f.Conditions[0].Value = "System"
	if isDynamicSocketAgent(run, f) {
		t.Fatal("dynamic System mistaken for agent")
	}
}
