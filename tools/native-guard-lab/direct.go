//go:build windows

package main

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"github.com/tailscale/wf"
	"net/netip"
	"path/filepath"
	"sort"
	"strings"
	"unsafe"
)

// A Direct exception is never a prefix/application-wide bypass. It authorizes
// one protected core image, remote IPv4/port/protocol and a physical NIC LUID.
type directPermission struct {
	Image, Server, Protocol string
	Port                    uint16
	InterfaceIndex          uint32
}

func directTarget(d *directPermission) (netip.Addr, uint8, error) {
	if d == nil || d.Port == 0 || d.InterfaceIndex == 0 || !filepath.IsAbs(d.Image) || !strings.EqualFold(filepath.Base(d.Image), "native-core.exe") {
		return netip.Addr{}, 0, errors.New("direct identity")
	}
	a, e := netip.ParseAddr(d.Server)
	if e != nil || !a.Is4() || !a.IsGlobalUnicast() || a.IsPrivate() || a.IsLoopback() || a.IsLinkLocalUnicast() || netip.MustParsePrefix("100.64.0.0/10").Contains(a) {
		return netip.Addr{}, 0, errors.New("direct public IPv4 required")
	}
	switch d.Protocol {
	case "tcp":
		return a, 6, nil
	case "udp":
		return a, 17, nil
	}
	return netip.Addr{}, 0, errors.New("direct protocol")
}
func physicalLuid(index uint32) (uint64, error) {
	var id uint64
	code, _, _ := ipHelper.NewProc("ConvertInterfaceIndexToLuid").Call(uintptr(index), uintptr(unsafe.Pointer(&id)))
	// IF_TYPE_ETHERNET_CSMACD / IF_TYPE_IEEE80211. Do not accidentally pin a TUN,
	// loopback, Tailscale or arbitrary unrecognised virtual interface.
	if code != 0 || id == 0 || id>>48 != 6 && id>>48 != 71 {
		return 0, errors.New("physical interface required")
	}
	return id, nil
}
func directHash(app string, a netip.Addr, port uint16, protocol uint8, luid uint64) string {
	h := sha256.Sum256([]byte(fmt.Sprintf("%s\n%s\n%d\n%d\n%d", strings.ToLower(app), a, port, protocol, luid)))
	return hex.EncodeToString(h[:])
}
func directRuleIdentity(f *wf.Rule) (string, error) {
	if f.Action != wf.ActionPermit || f.Layer != wf.LayerALEAuthConnectV4 || f.Disabled || f.HardAction || len(f.Conditions) != 5 || f.Weight != 0 && f.Weight != 15 {
		return "", errors.New("direct rule shape")
	}
	var app string
	var a netip.Addr
	var port uint16
	var protocol uint8
	var luid uint64
	seen := map[string]bool{}
	for _, c := range f.Conditions {
		key := fmt.Sprint(c.Field)
		if seen[key] {
			return "", errors.New("duplicate direct condition")
		}
		seen[key] = true
		if c.Op != wf.MatchTypeEqual {
			return "", errors.New("direct condition op")
		}
		switch c.Field {
		case wf.FieldALEAppID:
			app, _ = c.Value.(string)
		case wf.FieldIPRemoteAddress:
			p, ok := conditionPrefix(c.Value)
			if ok && p.Addr().Is4() && p.Bits() == 32 {
				a = p.Addr()
			}
		case wf.FieldIPRemotePort:
			port, _ = c.Value.(uint16)
		case wf.FieldIPProtocol:
			switch v := c.Value.(type) {
			case uint8:
				protocol = v
			case wf.IPProto:
				protocol = uint8(v)
			}
		case wf.FieldIPLocalInterface:
			luid, _ = c.Value.(uint64)
		default:
			return "", errors.New("direct condition field")
		}
	}
	if app == "" || !a.IsValid() || port == 0 || protocol != 6 && protocol != 17 || luid == 0 || luid>>48 != 6 && luid>>48 != 71 {
		return "", errors.New("direct scope incomplete")
	}
	return directHash(app, a, port, protocol, luid), nil
}
func describeDirect(rules []*wf.Rule) (int, string, error) {
	var hashes []string
	for _, f := range rules {
		if f.Action == wf.ActionPermit && len(f.Conditions) == 5 {
			h, e := directRuleIdentity(f)
			if e != nil {
				return 0, "", e
			}
			hashes = append(hashes, h)
		}
	}
	if len(hashes) == 0 {
		return 0, "", nil
	}
	sort.Strings(hashes)
	h := sha256.Sum256([]byte(strings.Join(hashes, "\n")))
	return len(hashes), hex.EncodeToString(h[:]), nil
}
func updateDirect(s *wf.Session, r request, rules []*wf.Rule, grant bool) error {
	a, protocol, e := directTarget(r.Direct)
	if e != nil {
		return e
	}
	app, e := wf.AppID(r.Direct.Image)
	if e != nil {
		return e
	}
	luid, e := physicalLuid(r.Direct.InterfaceIndex)
	if e != nil {
		return e
	}
	// The encrypted owner and the Direct core must be siblings in the already
	// protected bundle. A request cannot turn some unrelated executable into a bypass.
	owner, e := wf.AppID(filepath.Join(filepath.Dir(r.Direct.Image), "native-owner.exe"))
	if e != nil {
		return e
	}
	foundOwner := false
	for _, f := range rules {
		if f.Action == wf.ActionPermit && len(f.Conditions) == 4 {
			for _, c := range f.Conditions {
				if c.Field == wf.FieldALEAppID {
					value, _ := c.Value.(string)
					foundOwner = strings.EqualFold(value, owner)
				}
			}
		}
	}
	if !foundOwner {
		return errors.New("direct core/owner identity mismatch")
	}
	count, _, e := describeDirect(rules)
	if e != nil {
		return e
	}
	hash := directHash(app, a, r.Direct.Port, protocol, luid)
	id := wf.RuleID(identity(r.Run, "direct-"+hash))
	for _, f := range rules {
		if f.ID == id {
			h, e := directRuleIdentity(f)
			if e != nil || h != hash {
				return errors.New("direct rule identity mismatch")
			}
			if !grant {
				return s.DeleteRule(id)
			}
			return nil
		}
	}
	if !grant {
		return nil
	}
	if count >= 256 {
		return errors.New("direct permission budget")
	}
	return s.AddRule(&wf.Rule{ID: id, Name: marker + r.Run, Provider: wf.ProviderID(identity(r.Run, "provider")), ProviderData: []byte(marker + r.Run), Sublayer: wf.SublayerID(identity(r.Run, "sublayer")), Layer: wf.LayerALEAuthConnectV4, Weight: 15, Action: wf.ActionPermit, Persistent: true, Conditions: []*wf.Match{{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: app}, {Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.PrefixFrom(a, 32)}, {Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: r.Direct.Port}, {Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProto(protocol)}, {Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: luid}}})
}
