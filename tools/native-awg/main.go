package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"os"
	"strings"
	"sync/atomic"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

type result struct {
	Schema       int              `json:"schema"`
	Success      bool             `json:"success"`
	Stage        string           `json:"stage"`
	Milliseconds int64            `json:"milliseconds"`
	EgressHash   string           `json:"egressHash,omitempty"`
	Engine       string           `json:"engine"`
	UDPDNS       bool             `json:"udpDnsVerified"`
	Retention    *retentionResult `json:"retention,omitempty"`
}

func probe(input io.Reader) (r result) {
	return probeMode(input, false)
}
func probeMode(input io.Reader, retain bool) (r result) {
	return probeLoss(input, retain, false)
}
func probeLoss(input io.Reader, retain bool, inboundOnly bool) (r result) {
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
	bind := newFaultBind(conn.NewDefaultBind())
	bind.inboundOnly.Store(inboundOnly)
	dev := device.NewDevice(tun, bind, device.NewLogger(device.LogLevelSilent, ""))
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
	var dials atomic.Int64
	dial := func(ctx context.Context, kind, address string) (net.Conn, error) {
		dials.Add(1)
		return network.DialContext(ctx, kind, address)
	}
	transport := &http.Transport{DialContext: dial, DisableKeepAlives: !retain, ForceAttemptHTTP2: false, MaxResponseHeaderBytes: 16384, TLSHandshakeTimeout: 7 * time.Second, ResponseHeaderTimeout: 10 * time.Second, MaxConnsPerHost: 1}
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
	response.Body.Close()
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
	if found {
		r.Stage = "UdpDns"
		r.UDPDNS = udpDNS(network, c.dns[0])
	}
	r.Success = found && r.UDPDNS
	if r.Success && retain {
		r.Stage = "RetainingTcp"
		retention := retainTCP(ctx, client, bind, &dials, r.EgressHash)
		r.Retention = &retention
		r.Success = retention.Success
		if r.Success {
			r.Stage = "RetainedTcpAfterLoss"
		}
		return r
	}
	if found {
		if r.Success {
			r.Stage = "VerifiedHttpsAndUdpDns"
		}
	}
	return r
}
func main() {
	if len(os.Args) == 2 && os.Args[1] == "--serve-proxy-migration-lab" {
		os.Exit(proxyMainTransport(os.Stdin, os.Stdout, true, true))
	}
	if len(os.Args) == 2 && os.Args[1] == "--proxy-udp-client" {
		os.Exit(proxyUDPClient(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--serve-proxy-udp-lab" {
		os.Exit(proxyMainTransport(os.Stdin, os.Stdout, false, true))
	}
	if len(os.Args) == 2 && os.Args[1] == "--serve-proxy-loss-lab" {
		os.Exit(proxyMainMode(os.Stdin, os.Stdout, true))
	}
	if len(os.Args) == 2 && os.Args[1] == "--proxy-retention-client" {
		os.Exit(proxyRetentionClient(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--serve-proxy" {
		os.Exit(proxyMain(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--proxy-client" {
		os.Exit(proxyClient(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--capture-summary" {
		os.Exit(captureMain())
	}
	if len(os.Args) != 2 || (os.Args[1] != "--probe" && os.Args[1] != "--retention" && os.Args[1] != "--retention-inbound") {
		fmt.Fprintln(os.Stderr, "Experimental VM-only probe; configuration is read from stdin.")
		os.Exit(2)
	}
	r := probeLoss(os.Stdin, os.Args[1] != "--probe", os.Args[1] == "--retention-inbound")
	_ = json.NewEncoder(os.Stdout).Encode(r)
	if !r.Success {
		os.Exit(1)
	}
}
