//go:build windows

package main

import (
	"github.com/tailscale/wf"
	"net/netip"
	"testing"
)

func TestConnectorPermissionBoundaries(t *testing.T) {
	good := connectorPermission{Image: `C:\Program Files\RoutePilot\Agent\RoutePilot.Agent.exe`, LocalAddress: "192.168.1.124", Server: "1.1.1.1", LocalPort: 52001, InterfaceIndex: 25}
	if _, _, err := connectorTarget(&good); err != nil {
		t.Fatal(err)
	}
	injector := good
	injector.Role = "injector"
	injector.Image = `C:\Program Files\RoutePilot\Agent\module\winws2.exe`
	if _, _, err := connectorTarget(&injector); err != nil {
		t.Fatal("fixed injector role rejected", err)
	}
	injector.Role = ""
	if _, _, err := connectorTarget(&injector); err == nil {
		t.Fatal("injector cannot masquerade as agent role")
	}
	for _, address := range []string{"0.1.2.3", "26.68.227.253", "127.0.0.1", "10.0.0.1", "192.168.1.1", "100.100.100.100", "169.254.1.1", "198.18.0.1", "192.0.2.1", "198.51.100.1", "203.0.113.1", "::1", "224.0.0.1"} {
		bad := good
		bad.Server = address
		if _, _, err := connectorTarget(&bad); err == nil {
			t.Fatal("unsafe connector destination accepted", address)
		}
	}
	for _, mutate := range []func(*connectorPermission){func(p *connectorPermission) { p.Image = `C:\Windows\System32\curl.exe` }, func(p *connectorPermission) { p.Image = "RoutePilot.Agent.exe" }, func(p *connectorPermission) { p.LocalPort = 0 }, func(p *connectorPermission) { p.InterfaceIndex = 0 }, func(p *connectorPermission) { p.LocalAddress = "0.0.0.0" }, func(p *connectorPermission) { p.LocalAddress = "127.0.0.1" }} {
		bad := good
		mutate(&bad)
		if _, _, err := connectorTarget(&bad); err == nil {
			t.Fatal("unsafe connector source accepted")
		}
	}
	request := request{Action: "GrantConnector", Run: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Connector: &good}
	if _, err := validate(request); err == nil {
		t.Fatal("connector without protected endpoint/policy accepted")
	}
}
func connectorFixture() *wf.Rule {
	return &wf.Rule{Action: wf.ActionPermit, Layer: wf.LayerALEAuthConnectV4, Weight: 15, Conditions: []*wf.Match{
		{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: `\device\volume\RoutePilot.Agent.exe`},
		{Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.MustParsePrefix("1.1.1.1/32")},
		{Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: uint16(443)},
		{Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProtoTCP},
		{Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: uint64(71)<<48 | 25},
		{Field: wf.FieldIPLocalAddress, Op: wf.MatchTypeEqual, Value: netip.MustParsePrefix("192.168.1.124/32")},
		{Field: wf.FieldIPLocalPort, Op: wf.MatchTypeEqual, Value: uint16(52001)},
	}}
}
func TestConnectorRuleShape(t *testing.T) {
	fixture := connectorFixture()
	original, err := connectorRuleIdentity(fixture)
	if err != nil {
		t.Fatal(err)
	}
	for i := range fixture.Conditions {
		rule := connectorFixture()
		condition := *rule.Conditions[i]
		condition.Op = wf.MatchTypeNotEqual
		rule.Conditions[i] = &condition
		if _, err := connectorRuleIdentity(rule); err == nil {
			t.Fatal("broadened connector match accepted", i)
		}
		rule = connectorFixture()
		rule.Conditions = append(rule.Conditions[:i], rule.Conditions[i+1:]...)
		if _, err := connectorRuleIdentity(rule); err == nil {
			t.Fatal("missing connector condition accepted", i)
		}
	}
	for _, mutate := range []func(*wf.Rule){func(f *wf.Rule) { f.Conditions[2].Value = uint16(80) }, func(f *wf.Rule) { f.Conditions[3].Value = wf.IPProtoUDP }, func(f *wf.Rule) { f.Conditions[0].Value = `\device\volume\curl.exe` }, func(f *wf.Rule) { f.Conditions[6] = f.Conditions[5] }} {
		rule := connectorFixture()
		mutate(rule)
		if _, err := connectorRuleIdentity(rule); err == nil {
			t.Fatal("unsafe connector shape accepted")
		}
	}
	different := connectorFixture()
	different.Conditions[6].Value = uint16(52002)
	hash, err := connectorRuleIdentity(different)
	if err != nil || hash == original {
		t.Fatal("source port must participate in rule identity")
	}
	fixture.Weight = 0
	if _, err := connectorRuleIdentity(fixture); err != nil {
		t.Fatal("Windows unavailable effective weight rejected", err)
	}
	count, digest, err := describeConnectors([]*wf.Rule{fixture, different})
	if err != nil || count != 2 || len(digest) != 64 {
		t.Fatal("connector snapshot invalid")
	}
}
