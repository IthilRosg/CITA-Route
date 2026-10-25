//go:build windows

package main

import (
	"github.com/tailscale/wf"
	"net/netip"
	"testing"
)

func TestDirectPermissionBoundaries(t *testing.T) {
	good := directPermission{Image: `C:\Program Files\RoutePilot\Agent\native\native-core.exe`, Server: "1.1.1.1", Port: 443, Protocol: "tcp", InterfaceIndex: 7}
	if _, _, e := directTarget(&good); e != nil {
		t.Fatal(e)
	}
	for _, ip := range []string{"::1", "2001:4860:4860::8888", "127.0.0.1", "10.0.0.1", "192.168.1.1", "100.100.100.100", "169.254.1.1", "0.0.0.0", "224.0.0.1", "1.1.1.1/24"} {
		d := good
		d.Server = ip
		if _, _, e := directTarget(&d); e == nil {
			t.Fatalf("unsafe address accepted: %s", ip)
		}
	}
	for _, mutate := range []func(*directPermission){func(d *directPermission) { d.Image = `C:\Windows\System32\curl.exe` }, func(d *directPermission) { d.Image = `native-core.exe` }, func(d *directPermission) { d.Port = 0 }, func(d *directPermission) { d.InterfaceIndex = 0 }, func(d *directPermission) { d.Protocol = "icmp" }} {
		d := good
		mutate(&d)
		if _, _, e := directTarget(&d); e == nil {
			t.Fatal("unsafe scope accepted")
		}
	}
	r := request{Action: "GrantDirect", Run: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Direct: &good, ExpectedPolicy: string(make([]byte, 64)), ExpectedEndpoint: "a"}
	if _, e := validate(r); e == nil {
		t.Fatal("guard proof missing")
	}
}
func TestDirectRuleShape(t *testing.T) {
	f := &wf.Rule{Action: wf.ActionPermit, Layer: wf.LayerALEAuthConnectV4, Weight: 15, Conditions: []*wf.Match{{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: `\device\volume\native-core.exe`}, {Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.MustParsePrefix("1.1.1.1/32")}, {Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: uint16(443)}, {Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProtoTCP}, {Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: uint64(6)<<48 | 123}}}
	if _, e := directRuleIdentity(f); e != nil {
		t.Fatal(e)
	}
	before := *f
	for _, i := range []int{0, 1, 2, 3, 4} {
		f.Conditions = append([]*wf.Match{}, before.Conditions...)
		c := *f.Conditions[i]
		c.Op = wf.MatchTypeNotEqual
		f.Conditions[i] = &c
		if _, e := directRuleIdentity(f); e == nil {
			t.Fatal("broadened match accepted")
		}
	}
	f.Conditions = append([]*wf.Match{}, before.Conditions...)
	f.Conditions[4] = f.Conditions[0]
	if _, e := directRuleIdentity(f); e == nil {
		t.Fatal("missing NIC accepted")
	}
}
