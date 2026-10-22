//go:build windows

package main

import (
	"encoding/json"
	"testing"
)

func TestPublicScope(t *testing.T) {
	target := publicTarget{"cloudflare-quic.com", "104.16.0.1", "Ethernet"}
	for _, scenario := range []string{"valid", "default-route", "extra-route", "global-dns", "strict-routing", "external-proxy", "wrong-interface", "unexpected-domain", "private-address", "loopback-address", "reserved-address", "extra-outbound"} {
		t.Run(scenario, func(t *testing.T) {
			selected := target
			tun := map[string]any{"type": "tun", "interface_name": "Cita-PublicWeb", "route_address": []string{target.Address + "/32"}, "auto_route": true, "strict_route": false, "dns_mode": "disabled"}
			proxy := map[string]any{"type": "mixed", "tag": "policy-ack", "listen": "127.0.0.1"}
			wan := map[string]any{"type": "direct", "tag": "wan", "bind_interface": target.BindInterface}
			outbounds := []any{wan, map[string]any{"type": "direct", "tag": "proof"}}
			switch scenario {
			case "default-route":
				tun["route_address"] = []string{"0.0.0.0/0"}
			case "extra-route":
				tun["route_address"] = []string{target.Address + "/32", "0.0.0.0/0"}
			case "global-dns":
				tun["dns_mode"] = "hijack"
			case "strict-routing":
				tun["strict_route"] = true
			case "external-proxy":
				proxy["listen"] = "0.0.0.0"
			case "wrong-interface":
				wan["bind_interface"] = "Cita-PublicWeb"
			case "unexpected-domain":
				selected.Domain = "example.org"
			case "private-address":
				selected.Address = "10.0.0.1"
			case "loopback-address":
				selected.Address = "127.0.0.1"
			case "reserved-address":
				selected.Address = "203.0.113.1"
			case "extra-outbound":
				outbounds = append(outbounds, map[string]any{"type": "socks", "tag": "unowned"})
			}
			data, _ := json.Marshal(map[string]any{"inbounds": []any{tun, proxy}, "outbounds": outbounds})
			if (validatePublicConfig(data, selected) == nil) != (scenario == "valid") {
				t.Fatal("fixture scope validation disagrees with the test")
			}
		})
	}
}
