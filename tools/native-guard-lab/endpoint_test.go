package main

import (
	"github.com/tailscale/wf"
	"net/netip"
	"testing"
)

func TestEncryptedEndpointScope(t *testing.T) {
	rule := func() *wf.Rule {
		return &wf.Rule{Action: wf.ActionPermit, Layer: wf.LayerALEAuthConnectV4, Weight: 20, Conditions: []*wf.Match{{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: `\device\disk\native-owner.exe`}, {Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: uint8(17)}, {Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.MustParseAddr("203.0.113.7")}, {Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: uint16(3080)}}}
	}
	original, e := endpointRuleIdentity(rule())
	if e != nil || original == "" {
		t.Fatal("valid scoped endpoint", e)
	}
	for _, change := range []func(*wf.Rule){func(r *wf.Rule) { r.Conditions = r.Conditions[:3] }, func(r *wf.Rule) { r.Conditions[1].Value = uint8(6) }, func(r *wf.Rule) { r.Conditions[2].Value = netip.MustParsePrefix("203.0.113.0/24") }, func(r *wf.Rule) { r.HardAction = true }, func(r *wf.Rule) { r.Conditions[3].Field = wf.FieldALEAppID }} {
		r := rule()
		change(r)
		if _, e := endpointRuleIdentity(r); e == nil {
			t.Fatal("broadened permission accepted")
		}
	}
	for _, change := range []func(*wf.Rule){func(r *wf.Rule) { r.Conditions[0].Value = `\device\disk\another.exe` }, func(r *wf.Rule) { r.Conditions[2].Value = netip.MustParseAddr("203.0.113.8") }, func(r *wf.Rule) { r.Conditions[3].Value = uint16(3081) }} {
		r := rule()
		change(r)
		digest, e := endpointRuleIdentity(r)
		if e != nil || digest == original {
			t.Fatal("endpoint identity did not change")
		}
	}
	r := rule()
	r.Weight = 0
	if digest, e := endpointRuleIdentity(r); e != nil || digest != original {
		t.Fatal("unavailable effective weight changed endpoint identity")
	}
}
