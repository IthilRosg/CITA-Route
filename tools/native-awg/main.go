package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/netip"
	"os"
	"strings"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

type result struct {
	Schema       int    `json:"schema"`
	Success      bool   `json:"success"`
	Stage        string `json:"stage"`
	Milliseconds int64  `json:"milliseconds"`
	EgressHash   string `json:"egressHash,omitempty"`
	Engine       string `json:"engine"`
}

func probe(input io.Reader) (r result) {
	r = result{Schema: 1, Stage: "Configuration", Engine: "amneziawg-go/v3@v3.1.20260828"}
	began := time.Now()
	defer func() { r.Milliseconds = time.Since(began).Milliseconds() }()
	data, e := io.ReadAll(io.LimitReader(input, 65537))
	if e != nil {
		return r
	}
	c, e := parseConfig(string(data))
	clear(data)
	if e != nil {
		return r
	}
	r.Stage = "Netstack"
	tun, network, e := netstack.CreateNetTUN(c.address, c.dns, c.mtu)
	if e != nil {
		return r
	}
	dev := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer dev.Close()
	r.Stage = "EngineConfiguration"
	// Upstream errors can include configuration values; never serialize/log them.
	if dev.IpcSet(c.ipc) != nil {
		return r
	}
	c.ipc = ""
	r.Stage = "HandshakeAndHttps"
	if dev.Up() != nil {
		return r
	}
	ctx, cancel := context.WithTimeout(context.Background(), 22*time.Second)
	defer cancel()
	transport := &http.Transport{DialContext: network.DialContext, DisableKeepAlives: true, ForceAttemptHTTP2: false, MaxResponseHeaderBytes: 16384, TLSHandshakeTimeout: 7 * time.Second, ResponseHeaderTimeout: 7 * time.Second}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	req, e := http.NewRequestWithContext(ctx, http.MethodGet, "https://www.cloudflare.com/cdn-cgi/trace", nil)
	if e != nil {
		return r
	}
	response, e := client.Do(req)
	if e != nil {
		return r
	}
	defer response.Body.Close()
	r.Stage = "Response"
	if response.StatusCode != 200 {
		return r
	}
	body, e := io.ReadAll(io.LimitReader(response.Body, 8193))
	if e != nil || len(body) > 8192 {
		return r
	}
	found := false
	for _, line := range strings.Split(string(body), "\n") {
		if strings.HasPrefix(line, "ip=") {
			if found {
				return r
			}
			ip, e := netip.ParseAddr(strings.TrimSpace(strings.TrimPrefix(line, "ip=")))
			if e != nil || !ip.IsGlobalUnicast() || ip.IsPrivate() {
				return r
			}
			hash := sha256.Sum256([]byte(ip.String()))
			r.EgressHash = hex.EncodeToString(hash[:])
			found = true
		}
	}
	r.Success = found
	if found {
		r.Stage = "VerifiedHttps"
	}
	return r
}
func main() {
	if len(os.Args) != 2 || os.Args[1] != "--probe" {
		fmt.Fprintln(os.Stderr, "Experimental VM-only probe; configuration is read from stdin.")
		os.Exit(2)
	}
	r := probe(os.Stdin)
	_ = json.NewEncoder(os.Stdout).Encode(r)
	if !r.Success {
		os.Exit(1)
	}
}
