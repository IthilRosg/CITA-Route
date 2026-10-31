//go:build windows

package main

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"github.com/tailscale/wf"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// A connector exception belongs to ONE held source socket. This does not
// expand the existing native-core Direct permission to arbitrary executables.
type connectorPermission struct {
	Image, LocalAddress, Server string
	LocalPort                   uint16
	InterfaceIndex              uint32
	Role                        string
}

func connectorTarget(d *connectorPermission) (netip.Addr, netip.Addr, error) {
	if d == nil || !filepath.IsAbs(d.Image) || d.LocalPort < 1024 || d.InterfaceIndex == 0 {
		return netip.Addr{}, netip.Addr{}, errors.New("connector identity")
	}
	expectedName := "RoutePilot.Agent.exe"
	if d.Role == "injector" {
		expectedName = "winws2.exe"
	} else if d.Role != "" && d.Role != "agent" {
		return netip.Addr{}, netip.Addr{}, errors.New("connector role")
	}
	if !strings.EqualFold(filepath.Base(d.Image), expectedName) {
		return netip.Addr{}, netip.Addr{}, errors.New("connector role image")
	}
	local, e := netip.ParseAddr(d.LocalAddress)
	if e != nil || !local.Is4() || !local.IsGlobalUnicast() || local.IsLoopback() || local.IsLinkLocalUnicast() || local.String() != d.LocalAddress {
		return netip.Addr{}, netip.Addr{}, errors.New("connector local IPv4")
	}
	remote, e := netip.ParseAddr(d.Server)
	if e != nil || !remote.Is4() || !remote.IsGlobalUnicast() || remote.IsPrivate() || remote.IsLoopback() || remote.IsLinkLocalUnicast() || remote.String() != d.Server {
		return netip.Addr{}, netip.Addr{}, errors.New("connector public IPv4")
	}
	for _, prefix := range []string{"0.0.0.0/8", "26.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24"} {
		if netip.MustParsePrefix(prefix).Contains(remote) {
			return netip.Addr{}, netip.Addr{}, errors.New("connector excluded IPv4")
		}
	}
	return local, remote, nil
}
func connectorHash(base string, local netip.Addr, port uint16) string {
	hash := sha256.Sum256([]byte(fmt.Sprintf("connector\n%s\n%s\n%d", base, local, port)))
	return hex.EncodeToString(hash[:])
}
func connectorRuleIdentity(f *wf.Rule) (string, error) {
	if len(f.Conditions) != 7 {
		return "", errors.New("connector condition count")
	}
	base := *f
	base.Conditions = nil
	var local netip.Addr
	var port uint16
	var image string
	seen := map[wf.FieldID]bool{}
	for _, condition := range f.Conditions {
		if seen[condition.Field] || condition.Op != wf.MatchTypeEqual {
			return "", errors.New("connector condition")
		}
		seen[condition.Field] = true
		switch condition.Field {
		case wf.FieldIPLocalAddress:
			prefix, ok := conditionPrefix(condition.Value)
			if ok && prefix.Bits() == 32 {
				local = prefix.Addr()
			}
		case wf.FieldIPLocalPort:
			port, _ = condition.Value.(uint16)
		default:
			base.Conditions = append(base.Conditions, condition)
			if condition.Field == wf.FieldALEAppID {
				image, _ = condition.Value.(string)
			}
		}
	}
	if !local.IsValid() || !local.Is4() || local.IsLoopback() || local.IsUnspecified() || port < 1024 || !strings.EqualFold(filepath.Base(image), "RoutePilot.Agent.exe") && !strings.EqualFold(filepath.Base(image), "winws2.exe") && !(image == "System" && !f.Persistent) {
		return "", errors.New("connector socket scope")
	}
	hash, e := directRuleIdentity(&base)
	if e != nil {
		return "", e
	}
	// Direct shape accepts both TCP and UDP/ports. Connector is TLS/TCP443 only.
	for _, condition := range base.Conditions {
		if condition.Field == wf.FieldIPRemotePort && condition.Value != uint16(443) {
			return "", errors.New("connector port")
		}
		if condition.Field == wf.FieldIPProtocol {
			switch p := condition.Value.(type) {
			case uint8:
				if p != 6 {
					return "", errors.New("connector protocol")
				}
			case wf.IPProto:
				if p != wf.IPProtoTCP {
					return "", errors.New("connector protocol")
				}
			default:
				return "", errors.New("connector protocol")
			}
		}
	}
	return connectorHash(hash, local, port), nil
}
func describeConnectors(rules []*wf.Rule) (int, string, error) {
	var hashes []string
	for _, rule := range rules {
		if rule.Action == wf.ActionPermit && len(rule.Conditions) == 7 {
			hash, e := connectorRuleIdentity(rule)
			if e != nil {
				return 0, "", e
			}
			hashes = append(hashes, hash)
		}
	}
	if len(hashes) == 0 {
		return 0, "", nil
	}
	sort.Strings(hashes)
	hash := sha256.Sum256([]byte(strings.Join(hashes, "\n")))
	return len(hashes), hex.EncodeToString(hash[:]), nil
}
func updateConnector(s *wf.Session, r request, rules []*wf.Rule, protected []netip.Prefix, grant bool) error {
	local, remote, e := connectorTarget(r.Connector)
	if e != nil {
		return e
	}
	image, e := os.Executable()
	if e != nil {
		return e
	}
	expected := filepath.Join(filepath.Dir(filepath.Dir(image)), "RoutePilot.Agent.exe")
	if r.Connector.Role == "injector" {
		expected = filepath.Join(filepath.Dir(filepath.Dir(image)), "module", "winws2.exe")
	}
	if !strings.EqualFold(filepath.Clean(expected), filepath.Clean(r.Connector.Image)) {
		return errors.New("connector installed agent identity")
	}
	app, e := wf.AppID(expected)
	if e != nil {
		return e
	}
	owner, e := wf.AppID(filepath.Join(filepath.Dir(image), "native-owner.exe"))
	if e != nil {
		return e
	}
	foundOwner, contained := false, false
	for _, rule := range rules {
		if rule.Action == wf.ActionPermit && len(rule.Conditions) == 4 {
			for _, condition := range rule.Conditions {
				if condition.Field == wf.FieldALEAppID {
					value, _ := condition.Value.(string)
					if strings.EqualFold(value, owner) {
						foundOwner = true
					}
				}
			}
		}
	}
	for _, prefix := range protected {
		if prefix.Contains(remote) {
			contained = true
		}
	}
	if !foundOwner || !contained {
		return errors.New("connector outside owned protection")
	}
	// Revocation must still work after an interface disappears or its index is
	// reused. Match the held socket tuple using the verified rule's OLD LUID.
	if !grant {
		for _, rule := range rules {
			if rule.Action != wf.ActionPermit || len(rule.Conditions) != 7 {
				continue
			}
			actual, e := connectorRuleIdentity(rule)
			if e != nil {
				return e
			}
			var oldLuid uint64
			for _, condition := range rule.Conditions {
				if condition.Field == wf.FieldIPLocalInterface {
					oldLuid, _ = condition.Value.(uint64)
				}
			}
			baseHash := directHash(app, remote, 443, 6, oldLuid)
			want := connectorHash(baseHash, local, r.Connector.LocalPort)
			if actual == want {
				if rule.ID != wf.RuleID(identity(r.Run, "connector-"+want)) {
					return errors.New("connector revoke identity mismatch")
				}
				return s.DeleteRule(rule.ID)
			}
		}
		return nil
	}
	luid, e := physicalLuid(r.Connector.InterfaceIndex)
	if e != nil {
		return e
	}
	if e := connectorSourceReady(r.Connector.InterfaceIndex, local); e != nil {
		return e
	}
	baseHash := directHash(app, remote, 443, 6, luid)
	hash := connectorHash(baseHash, local, r.Connector.LocalPort)
	id := wf.RuleID(identity(r.Run, "connector-"+hash))
	count, _, e := describeConnectors(rules)
	if e != nil {
		return e
	}
	for _, rule := range rules {
		if rule.ID == id {
			actual, e := connectorRuleIdentity(rule)
			if e != nil || actual != hash {
				return errors.New("connector rule identity mismatch")
			}
			return nil
		}
	}
	if count >= 128 {
		return errors.New("connector permission budget")
	}
	conditions := []*wf.Match{{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: app}, {Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.PrefixFrom(remote, 32)},
		{Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: uint16(443)}, {Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProtoTCP},
		{Field: wf.FieldIPLocalAddress, Op: wf.MatchTypeEqual, Value: netip.PrefixFrom(local, 32)}, {Field: wf.FieldIPLocalPort, Op: wf.MatchTypeEqual, Value: r.Connector.LocalPort}}
	conditions = append(conditions, &wf.Match{Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: luid})
	return s.AddRule(&wf.Rule{ID: id, Name: marker + r.Run, Provider: wf.ProviderID(identity(r.Run, "provider")), ProviderData: []byte(marker + r.Run), Sublayer: wf.SublayerID(identity(r.Run, "sublayer")), Layer: wf.LayerALEAuthConnectV4, Weight: 15, Action: wf.ActionPermit, Persistent: true, Conditions: conditions})
}
func connectorSourceReady(index uint32, local netip.Addr) error {
	nic, e := net.InterfaceByIndex(int(index))
	if e != nil || nic.Flags&net.FlagUp == 0 {
		return errors.New("connector interface unavailable")
	}
	addresses, e := nic.Addrs()
	if e != nil {
		return e
	}
	foundLocal := false
	for _, address := range addresses {
		prefix, e := netip.ParsePrefix(address.String())
		if e == nil && prefix.Addr() == local {
			foundLocal = true
		}
	}
	if !foundLocal {
		return errors.New("connector source/interface mismatch")
	}
	return nil
}
