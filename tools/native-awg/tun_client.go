package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptrace"
	"net/netip"
	"sync/atomic"
	"time"
)

// Ordinary OS TCP/TLS and UDP: no SOCKS, proxy, internal netstack or TLS override.
// Guest runner installs only /32 routes for 1.1.1.1 and the approved DNS server.
type tunClientResult struct {
	chainRetention
	UDPBefore    int  `json:"udpBefore"`
	UDPAfter     int  `json:"udpAfter"`
	UDPLocalPort int  `json:"udpLocalPort"`
	TcpFromTun   bool `json:"tcpFromTun"`
	UdpFromTun   bool `json:"udpFromTun"`
}

func tunClient(input io.Reader, output io.Writer) int {
	r := tunClientResult{chainRetention: chainRetention{Stage: "TunClient"}}
	defer func() { json.NewEncoder(output).Encode(r) }()
	var spec struct {
		DnsServer    string
		HoldForFinal bool
	}
	decoder := json.NewDecoder(io.LimitReader(input, 2048))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&spec) != nil {
		return 1
	}
	server, e := netip.ParseAddr(spec.DnsServer)
	if e != nil || !publicTarget(server) {
		return 1
	}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	prefix := netip.MustParsePrefix("172.30.127.0/30")
	localTun := func(a net.Addr) bool {
		host, _, e := net.SplitHostPort(a.String())
		if e != nil {
			return false
		}
		ip, e := netip.ParseAddr(host)
		return e == nil && prefix.Contains(ip)
	}
	var calls atomic.Int64
	var source atomic.Bool
	transport := &http.Transport{Proxy: nil, DialContext: func(ctx context.Context, kind, address string) (net.Conn, error) {
		calls.Add(1)
		c, e := (&net.Dialer{Timeout: 8 * time.Second}).DialContext(ctx, "tcp4", "1.1.1.1:443")
		if e == nil {
			source.Store(localTun(c.LocalAddr()))
		}
		return c, e
	}, ForceAttemptHTTP2: false, MaxConnsPerHost: 1, MaxResponseHeaderBytes: 16384, TLSHandshakeTimeout: 8 * time.Second, ResponseHeaderTimeout: 20 * time.Second}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	get := func(ctx context.Context) string {
		request, e := http.NewRequestWithContext(ctx, "GET", "https://1.1.1.1/cdn-cgi/trace", nil)
		if e != nil {
			return ""
		}
		response, e := client.Do(request)
		if e != nil {
			return ""
		}
		defer response.Body.Close()
		body, e := io.ReadAll(io.LimitReader(response.Body, 8193))
		if e != nil || len(body) > 8192 || response.StatusCode != 200 {
			return ""
		}
		return traceHash(body)
	}
	udp, e := net.DialUDP("udp4", nil, net.UDPAddrFromAddrPort(netip.AddrPortFrom(server, 53)))
	if e != nil {
		return 1
	}
	defer udp.Close()
	r.UdpFromTun = localTun(udp.LocalAddr())
	r.UDPLocalPort = udp.LocalAddr().(*net.UDPAddr).Port
	dns := func() bool {
		packet, id, e := dnsQuery()
		if e != nil {
			return false
		}
		if udp.SetDeadline(time.Now().Add(4*time.Second)) != nil {
			return false
		}
		if _, e = udp.Write(packet); e != nil {
			return false
		}
		buffer := make([]byte, 1233)
		n, e := udp.Read(buffer)
		return e == nil && n <= 1232 && validDNS(buffer[:n], id)
	}
	r.EgressHash = get(ctx)
	r.TcpFromTun = source.Load()
	r.DialsBefore = calls.Load()
	if r.EgressHash == "" || !r.TcpFromTun || !r.UdpFromTun || r.DialsBefore != 1 {
		return 1
	}
	for i := 0; i < 3; i++ {
		if !dns() {
			return 1
		}
		r.UDPBefore++
	}
	if json.NewEncoder(output).Encode(struct {
		Stage string `json:"stage"`
	}{"RetentionClientReady"}) != nil {
		return 1
	}
	var command struct{ Continue bool }
	if decoder.Decode(&command) != nil || !command.Continue {
		return 1
	}
	var reused atomic.Bool
	trace := &httptrace.ClientTrace{GotConn: func(c httptrace.GotConnInfo) { reused.Store(c.Reused) }}
	began := time.Now()
	after := get(httptrace.WithClientTrace(ctx, trace))
	r.RequestMilliseconds = time.Since(began).Milliseconds()
	r.SameExit = after != "" && after == r.EgressHash
	r.ReusedTCP = reused.Load()
	for i := 0; i < 3; i++ {
		if !dns() {
			return 1
		}
		r.UDPAfter++
	}
	r.DialsAfter = calls.Load()
	r.Success = r.SameExit && r.ReusedTCP && r.DialsAfter == 1 && r.UDPAfter == 3
	if spec.HoldForFinal {
		if json.NewEncoder(output).Encode(struct {
			Stage string `json:"stage"`
		}{"RetentionAfterLoss"}) != nil {
			return 1
		}
		var final struct{ Continue bool }
		if decoder.Decode(&final) != nil || !final.Continue {
			return 1
		}
		reused.Store(false)
		last := get(httptrace.WithClientTrace(ctx, trace))
		r.DialsAfter = calls.Load()
		r.SameExit = r.SameExit && last != "" && last == r.EgressHash
		r.ReusedTCP = r.ReusedTCP && reused.Load()
		r.Success = r.Success && r.SameExit && r.ReusedTCP && r.DialsAfter == 1
	}
	if !r.Success {
		return 1
	}
	r.Stage = "VerifiedOsTunTcpUdp"
	return 0
}
func osUDPProbe(input io.Reader, output io.Writer) int {
	var spec struct{ Server string }
	decoder := json.NewDecoder(io.LimitReader(input, 1024))
	decoder.DisallowUnknownFields()
	success := false
	defer func() {
		json.NewEncoder(output).Encode(struct {
			Success bool `json:"success"`
		}{success})
	}()
	if decoder.Decode(&spec) != nil {
		return 1
	}
	ip, e := netip.ParseAddr(spec.Server)
	if e != nil || !publicTarget(ip) {
		return 1
	}
	socket, e := net.DialUDP("udp4", nil, net.UDPAddrFromAddrPort(netip.AddrPortFrom(ip, 53)))
	if e != nil {
		return 1
	}
	defer socket.Close()
	packet, id, e := dnsQuery()
	if e != nil {
		return 1
	}
	socket.SetDeadline(time.Now().Add(1200 * time.Millisecond))
	if _, e = socket.Write(packet); e != nil {
		return 1
	}
	data := make([]byte, 1233)
	n, e := socket.Read(data)
	success = e == nil && n <= 1232 && validDNS(data[:n], id)
	if success {
		return 0
	}
	return 1
}
