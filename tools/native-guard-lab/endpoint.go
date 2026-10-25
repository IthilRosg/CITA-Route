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
	"strings"
)

type endpointPermission struct {
	Image, Server string
	Port          uint16
}

func endpointHash(app string, server netip.Addr, port uint16) string {
	s := sha256.Sum256([]byte(fmt.Sprintf("%s\n%s\n%d", strings.ToLower(app), server, port)))
	return hex.EncodeToString(s[:])
}
func endpointIdentity(ep *endpointPermission) (string, netip.Addr, error) {
	if ep == nil {
		return "", netip.Addr{}, nil
	}
	addr, e := netip.ParseAddr(ep.Server)
	if e != nil || !addr.Is4() || addr.IsLoopback() || addr.IsUnspecified() || addr.IsMulticast() || ep.Port == 0 || !filepath.IsAbs(ep.Image) || !strings.HasSuffix(strings.ToLower(ep.Image), ".exe") {
		return "", netip.Addr{}, errors.New("encrypted endpoint scope")
	}
	app, e := wf.AppID(ep.Image)
	return app, addr, e
}
func endpointRuleIdentity(f *wf.Rule) (string, error) {
	if f.Action != wf.ActionPermit || f.Layer != wf.LayerALEAuthConnectV4 || len(f.Conditions) != 4 || f.HardAction || f.Disabled || f.Weight != 0 && f.Weight != 20 {
		return "", errors.New("endpoint permit shape")
	}
	var app string
	var server netip.Addr
	var port uint16
	udp := false
	seen := map[wf.FieldID]bool{}
	for _, c := range f.Conditions {
		if c.Op != wf.MatchTypeEqual || seen[c.Field] {
			return "", errors.New("endpoint permit condition")
		}
		seen[c.Field] = true
		switch c.Field {
		case wf.FieldALEAppID:
			app, _ = c.Value.(string)
		case wf.FieldIPRemoteAddress:
			p, ok := conditionPrefix(c.Value)
			if ok && p.Addr().Is4() && p.Bits() == 32 {
				server = p.Addr()
			}
		case wf.FieldIPRemotePort:
			port, _ = c.Value.(uint16)
		case wf.FieldIPProtocol:
			switch p := c.Value.(type) {
			case uint8:
				udp = p == 17
			case wf.IPProto:
				udp = p == wf.IPProtoUDP
			}
		default:
			return "", errors.New("endpoint permit field")
		}
	}
	if app == "" || !server.IsValid() || port == 0 || !udp {
		return "", errors.New("endpoint permit scope")
	}
	return endpointHash(app, server, port), nil
}
func describeEndpoint(rules []*wf.Rule) (string, error) {
	var digest string
	for _, f := range rules {
		if f.Action == wf.ActionPermit && len(f.Conditions) == 4 {
			value, e := endpointRuleIdentity(f)
			if e != nil || digest != "" {
				return "", errors.New("endpoint permits")
			}
			digest = value
		}
	}
	return digest, nil
}
func armEndpoint(s *wf.Session, r request, rules []*wf.Rule) error {
	if r.Endpoint == nil {
		for _, f := range rules {
			if f.Action == wf.ActionPermit && len(f.Conditions) == 4 {
				return errors.New("endpoint permission changed")
			}
		}
		return nil
	}
	app, addr, e := endpointIdentity(r.Endpoint)
	if e != nil {
		return e
	}
	wanted := endpointHash(app, addr, r.Endpoint.Port)
	existing, e := describeEndpoint(rules)
	if e != nil {
		return e
	}
	if existing == wanted {
		return nil
	}
	if existing != "" {
		return errors.New("endpoint change requires disconnect")
	}
	return s.AddRule(&wf.Rule{ID: wf.RuleID(identity(r.Run, "encrypted-server")), Name: marker + r.Run, Provider: wf.ProviderID(identity(r.Run, "provider")), ProviderData: []byte(marker + r.Run), Sublayer: wf.SublayerID(identity(r.Run, "sublayer")), Layer: wf.LayerALEAuthConnectV4, Weight: 20, Action: wf.ActionPermit, Persistent: true, Conditions: []*wf.Match{{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: app}, {Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProtoUDP}, {Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: netip.PrefixFrom(addr, 32)}, {Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: r.Endpoint.Port}}})
}
