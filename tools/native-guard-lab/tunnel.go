//go:build windows

package main

import (
	"errors"
	"net/netip"
	"unsafe"

	"github.com/tailscale/wf"
	"golang.org/x/sys/windows"
)

var ipHelper = windows.NewLazySystemDLL("iphlpapi.dll")

func conditionPrefix(value any) (netip.Prefix, bool) {
	switch v := value.(type) {
	case netip.Prefix:
		return v, true
	case netip.Addr:
		return netip.PrefixFrom(v, v.BitLen()), true
	}
	return netip.Prefix{}, false
}

func tunnelLuid(index uint32) (uint64, error) {
	var luid uint64
	code, _, _ := ipHelper.NewProc("ConvertInterfaceIndexToLuid").Call(uintptr(index), uintptr(unsafe.Pointer(&luid)))
	if code != 0 || luid == 0 {
		return 0, errors.New("interface identity")
	}
	var alias [257]uint16
	code, _, _ = ipHelper.NewProc("ConvertInterfaceLuidToAlias").Call(uintptr(unsafe.Pointer(&luid)), uintptr(unsafe.Pointer(&alias[0])), uintptr(len(alias)))
	if code != 0 {
		return 0, errors.New("interface alias")
	}
	name := windows.UTF16ToString(alias[:])
	kind := luid >> 48
	if name != "Cita-NativeLab" && name != "Cita-Route" && name!="Cita-Native" || kind != 53 && kind != 131 {
		return 0, errors.New("not an owned tunnel")
	}
	return luid, nil
}
func describe(rules []*wf.Rule) (prefixes []netip.Prefix, luid uint64, permits int, err error) {
	for _, f := range rules {
		if f.Disabled || f.HardAction && f.Action == wf.ActionPermit || f.Layer != wf.LayerALEAuthConnectV4 && f.Layer != wf.LayerALEAuthConnectV6 {
			return nil, 0, 0, errors.New("guard rule shape")
		}
		if f.Action == wf.ActionBlock {
			// The pinned library reads EffectiveWeight, which Windows enumeration
			// leaves unavailable (zero) here. Ownership/action/conditions establish
			// the protection; an unavailable weight is not a missing block. Writes
			// still always use explicit priorities, and nonzero mismatches fail.
			if len(f.Conditions) != 1 || f.Weight != 1 && f.Weight != 0 || f.Conditions[0].Field != wf.FieldIPRemoteAddress || f.Conditions[0].Op != wf.MatchTypeEqual {
				return nil, 0, 0, errors.New("block shape")
			}
			p, ok := conditionPrefix(f.Conditions[0].Value)
			if !ok || p != p.Masked() || p.Addr().Is4() != (f.Layer == wf.LayerALEAuthConnectV4) {
				return nil, 0, 0, errors.New("block prefix")
			}
			prefixes = append(prefixes, p)
		} else if f.Action == wf.ActionPermit {
			if len(f.Conditions) == 5 {
				if _, e := directRuleIdentity(f); e != nil {
					return nil, 0, 0, e
				}
				continue
			}
			if len(f.Conditions) == 4 {
				if _, e := endpointRuleIdentity(f); e != nil {
					return nil, 0, 0, e
				}
				continue
			}
			if f.Layer != wf.LayerALEAuthConnectV4 || f.Weight != 10 && f.Weight != 0 || len(f.Conditions) != 2 {
				return nil, 0, 0, errors.New("permit shape")
			}
			var id uint64
			var prefix netip.Prefix
			for _, c := range f.Conditions {
				if c.Op != wf.MatchTypeEqual {
					return nil, 0, 0, errors.New("permit op")
				}
				switch c.Field {
				case wf.FieldIPLocalInterface:
					id, _ = c.Value.(uint64)
				case wf.FieldIPRemoteAddress:
					prefix, _ = conditionPrefix(c.Value)
				default:
					return nil, 0, 0, errors.New("permit field")
				}
			}
			if id == 0 || !prefix.IsValid() || !prefix.Addr().Is4() || luid != 0 && luid != id {
				return nil, 0, 0, errors.New("permit identity")
			}
			luid = id
			permits++
		} else {
			return nil, 0, 0, errors.New("guard action")
		}
	}
	for _, f := range rules {
		if f.Action == wf.ActionPermit && len(f.Conditions) == 2 {
			for _, c := range f.Conditions {
				if c.Field == wf.FieldIPRemoteAddress {
					found := false
					for _, p := range prefixes {
						seen, ok := conditionPrefix(c.Value)
						if ok && seen == p {
							found = true
						}
					}
					if !found {
						return nil, 0, 0, errors.New("permit outside protection")
					}
				}
			}
		}
	}
	return
}
func closeTunnel(s *wf.Session, rules []*wf.Rule) error {
	for _, f := range rules {
		if f.Action == wf.ActionPermit && (len(f.Conditions) == 2 || len(f.Conditions) == 5) {
			if e := s.DeleteRule(f.ID); e != nil {
				return e
			}
		}
	}
	return nil
}
func bindTunnel(s *wf.Session, r request, rules []*wf.Rule) (uint64, error) {
	prefixes, current, count, e := describe(rules)
	if e != nil || len(prefixes) == 0 || policyDigest(prefixes) != r.ExpectedPolicy {
		return 0, errors.New("policy mismatch")
	}
	luid, e := tunnelLuid(r.InterfaceIndex)
	if e != nil {
		return 0, e
	}
	wanted := 0
	for _, p := range prefixes {
		if p.Addr().Is4() {
			wanted++
		}
	}
	if current == luid && count == wanted {
		return luid, nil
	}
	// Keep every blocking filter in place while replacing tunnel permissions.
	if e = closeTunnel(s, rules); e != nil {
		return 0, e
	}
	for i, p := range prefixes {
		if !p.Addr().Is4() {
			continue
		}
		f := &wf.Rule{ID: wf.RuleID(identity(r.Run, "tun-"+p.String())), Name: marker + r.Run, Provider: wf.ProviderID(identity(r.Run, "provider")), ProviderData: []byte(marker + r.Run), Sublayer: wf.SublayerID(identity(r.Run, "sublayer")), Layer: wf.LayerALEAuthConnectV4, Weight: 10, Action: wf.ActionPermit, Persistent: true, Conditions: []*wf.Match{{Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: p}, {Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: luid}}}
		_ = i
		if e = s.AddRule(f); e != nil {
			now, _ := owned(s, r)
			_ = closeTunnel(s, now)
			return 0, e
		}
	}
	return luid, nil
}
