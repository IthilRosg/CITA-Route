package main

import (
	"encoding/hex"
	"encoding/json"
	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"io"
	"strconv"
	"strings"
	"time"
)

// Explicit diagnostic for restricted profiles. Sends only the configured peer's
// handshake; it neither widens AllowedIPs nor sends traffic to arbitrary targets.
func handshakeOnly(input io.Reader, output io.Writer) int {
	r := result{Schema: 1, Stage: "Configuration", Engine: "amneziawg-go/v3@v3.1.20260828"}
	began := time.Now()
	defer func() { r.Milliseconds = time.Since(began).Milliseconds(); json.NewEncoder(output).Encode(r) }()
	data, e := io.ReadAll(io.LimitReader(input, 65537))
	if e != nil {
		return 1
	}
	c, e := parseConfigMode(string(data), false)
	clear(data)
	if e != nil {
		return 1
	}
	r.Stage = "Netstack"
	tun, _, e := netstack.CreateNetTUN(c.address, c.dns, c.mtu)
	if e != nil {
		return 1
	}
	dev := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer dev.Close()
	r.Stage = "EngineConfiguration"
	if dev.IpcSet(c.ipc) != nil {
		return 1
	}
	var public device.NoisePublicKey
	for _, line := range strings.Split(c.ipc, "\n") {
		if value, yes := strings.CutPrefix(line, "public_key="); yes {
			b, e := hex.DecodeString(value)
			if e != nil || len(b) != 32 {
				return 1
			}
			copy(public[:], b)
		}
	}
	c.ipc = ""
	if dev.Up() != nil {
		return 1
	}
	peer := dev.LookupPeer(public)
	if peer == nil {
		return 1
	}
	r.Stage = "HandshakeAwaiting"
	if peer.SendHandshakeInitiation(false) != nil {
		return 1
	}
	until := time.Now().Add(18 * time.Second)
	for time.Now().Before(until) {
		stats, e := dev.IpcGet()
		if e != nil {
			return 1
		}
		for _, line := range strings.Split(stats, "\n") {
			if value, yes := strings.CutPrefix(line, "last_handshake_time_sec="); yes {
				n, e := strconv.ParseInt(value, 10, 64)
				if e == nil && n > 0 {
					r.Success = true
					r.Stage = "HandshakeVerifiedNoApplicationTraffic"
					return 0
				}
			}
		}
		time.Sleep(200 * time.Millisecond)
	}
	return 1
}
