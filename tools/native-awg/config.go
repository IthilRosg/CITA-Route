package main

import (
	"encoding/base64"
	"encoding/hex"
	"errors"
	"net/netip"
	"strconv"
	"strings"
)

type config struct {
	address, dns []netip.Addr
	mtu          int
	ipc          string
}

var errConfig = errors.New("unsupported or invalid configuration")

// Lab deliberately supports one IPv4 peer with a literal endpoint. No scripts,
// OS routes/DNS changes, hostname bootstrap or silently ignored extensions.
func parseConfig(text string) (config, error) {
	return parseConfigMode(text, true)
}
func parseConfigMode(text string, requireControlTarget bool) (config, error) {
	c := config{mtu: 1400}
	section := ""
	iface := map[string]string{}
	peer := map[string]string{}
	peers := 0
	if len(text) > 65536 {
		return c, errConfig
	}
	for _, line := range strings.Split(strings.TrimPrefix(text, "\ufeff"), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "#@ws:") {
			return c, errConfig
		}
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, ";") {
			continue
		}
		if line == "[Interface]" {
			if section != "" {
				return c, errConfig
			}
			section = "interface"
			continue
		}
		if line == "[Peer]" {
			peers++
			if section != "interface" || peers != 1 {
				return c, errConfig
			}
			section = "peer"
			continue
		}
		key, value, ok := strings.Cut(line, "=")
		key = strings.ToLower(strings.TrimSpace(key))
		value = strings.TrimSpace(value)
		if !ok || value == "" || strings.ContainsAny(value, "\r\x00") {
			return c, errConfig
		}
		target := iface
		if section == "peer" {
			target = peer
		} else if section != "interface" {
			return c, errConfig
		}
		if _, exists := target[key]; exists {
			return c, errConfig
		}
		target[key] = value
	}
	if peers != 1 {
		return c, errConfig
	}
	allowedIface := map[string]bool{"privatekey": true, "address": true, "dns": true, "mtu": true, "jc": true, "jmin": true, "jmax": true, "s1": true, "s2": true, "s3": true, "s4": true, "h1": true, "h2": true, "h3": true, "h4": true, "i1": true, "i2": true, "i3": true, "i4": true, "i5": true}
	allowedIface["headerprotectionkey"] = true
	for k := range iface {
		if !allowedIface[k] {
			return c, errConfig
		}
	}
	for k := range peer {
		if k != "publickey" && k != "presharedkey" && k != "endpoint" && k != "allowedips" && k != "persistentkeepalive" {
			return c, errConfig
		}
	}
	keyHex := func(value string) (string, error) {
		b, e := base64.StdEncoding.DecodeString(value)
		if e != nil || len(b) != 32 {
			return "", errConfig
		}
		return hex.EncodeToString(b), nil
	}
	private, e := keyHex(iface["privatekey"])
	if e != nil {
		return c, e
	}
	public, e := keyHex(peer["publickey"])
	if e != nil {
		return c, e
	}
	for _, value := range strings.Split(iface["address"], ",") {
		p, e := netip.ParsePrefix(strings.TrimSpace(value))
		if e != nil || !p.Addr().Is4() || p.Addr().IsUnspecified() || p.Addr().IsLoopback() {
			return c, errConfig
		}
		c.address = append(c.address, p.Addr())
	}
	for _, value := range strings.Split(iface["dns"], ",") {
		ip, e := netip.ParseAddr(strings.TrimSpace(value))
		if e != nil || !ip.Is4() || ip.IsUnspecified() || ip.IsLoopback() || ip.IsMulticast() {
			return c, errConfig
		}
		c.dns = append(c.dns, ip)
	}
	if value, ok := iface["mtu"]; ok {
		c.mtu, e = strconv.Atoi(value)
		if e != nil || c.mtu < 576 || c.mtu > 1420 {
			return c, errConfig
		}
	}
	endpoint, e := netip.ParseAddrPort(peer["endpoint"])
	if e != nil || !endpoint.Addr().Is4() || endpoint.Addr().IsUnspecified() || endpoint.Addr().IsLoopback() || endpoint.Addr().IsMulticast() || endpoint.Port() == 0 {
		return c, errConfig
	}
	// Preserve split IPv4 policies exactly; never widen an imported policy to
	// 0/0 merely to make a laboratory probe pass. The lab requires its fixed
	// HTTPS target to be routed by this peer, and still rejects IPv6 here.
	parts := strings.Split(peer["allowedips"], ",")
	if len(parts) == 0 || len(parts) > 64 {
		return c, errConfig
	}
	allowed := make([]netip.Prefix, 0, len(parts))
	seenAllowed := map[netip.Prefix]bool{}
	probeAllowed := false
	for _, part := range parts {
		prefix, e := netip.ParsePrefix(strings.TrimSpace(part))
		if e != nil || !prefix.Addr().Is4() {
			return c, errConfig
		}
		prefix = prefix.Masked()
		if seenAllowed[prefix] {
			return c, errConfig
		}
		seenAllowed[prefix] = true
		allowed = append(allowed, prefix)
		probeAllowed = probeAllowed || prefix.Contains(netip.MustParseAddr("1.1.1.1"))
	}
	if requireControlTarget && !probeAllowed {
		return c, errConfig
	}
	var ipc strings.Builder
	ipc.WriteString("private_key=" + private + "\n")
	if header, ok := iface["headerprotectionkey"]; ok {
		value, e := keyHex(header)
		if e != nil {
			return c, errConfig
		}
		ipc.WriteString("header_protection_key=" + value + "\n")
	}
	for _, k := range []string{"jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5"} {
		if value, ok := iface[k]; ok {
			ipc.WriteString(k + "=" + value + "\n")
		}
	}
	ipc.WriteString("replace_peers=true\npublic_key=" + public + "\nendpoint=" + endpoint.String() + "\n")
	for _, prefix := range allowed {
		ipc.WriteString("allowed_ip=" + prefix.String() + "\n")
	}
	if psk, ok := peer["presharedkey"]; ok {
		value, e := keyHex(psk)
		if e != nil {
			return c, e
		}
		ipc.WriteString("preshared_key=" + value + "\n")
	}
	if keep, ok := peer["persistentkeepalive"]; ok {
		n, e := strconv.Atoi(keep)
		if e != nil || n < 0 || n > 65535 {
			return c, errConfig
		}
		ipc.WriteString("persistent_keepalive_interval=" + keep + "\n")
	}
	c.ipc = ipc.String()
	return c, nil
}
