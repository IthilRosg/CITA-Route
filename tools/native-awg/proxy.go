package main

import (
	"context"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

// Stdin remains open as an ownership lease. No credentials in argv or readiness.
type proxyStart struct {
	Config     string
	Token      string
	Session    string
	Generation uint64
}
type proxyReady struct {
	Stage      string `json:"stage"`
	Port       int    `json:"port"`
	Session    string `json:"session"`
	Generation uint64 `json:"generation"`
}
type tunnelDial func(context.Context, string, string) (net.Conn, error)

var errProxy = errors.New("proxy request refused")

func validHex(text string, size int) bool {
	b, e := hex.DecodeString(text)
	return e == nil && len(b) == size
}
func publicTarget(ip netip.Addr) bool {
	if !ip.Is4() || !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
		return false
	}
	for _, prefix := range []string{"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4"} {
		if netip.MustParsePrefix(prefix).Contains(ip) {
			return false
		}
	}
	return true
}
func safeDomain(name string) bool {
	if len(name) > 253 || !strings.Contains(name, ".") || strings.HasSuffix(name, ".") {
		return false
	}
	for _, label := range strings.Split(name, ".") {
		if len(label) < 1 || len(label) > 63 || label[0] == '-' || label[len(label)-1] == '-' {
			return false
		}
		for _, c := range label {
			if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-') {
				return false
			}
		}
	}
	return true
}

// Addresses are resolved and validated inside the netstack, then pinned for the
// dial. No host resolver, implicit proxy, private-address access or IPv6 fallback.
func restrictedDial(network *netstack.Net) tunnelDial {
	return func(ctx context.Context, kind, address string) (net.Conn, error) {
		host, port, e := net.SplitHostPort(address)
		if e != nil || kind != "tcp" {
			return nil, errProxy
		}
		ips := []string{host}
		if _, e = netip.ParseAddr(host); e != nil {
			if !safeDomain(host) {
				return nil, errProxy
			}
			ips, e = network.LookupContextHost(ctx, host)
			if e != nil || len(ips) == 0 || len(ips) > 32 {
				return nil, errProxy
			}
		}
		for _, text := range ips {
			ip, e := netip.ParseAddr(text)
			if e != nil || !publicTarget(ip) {
				return nil, errProxy
			}
		}
		// One validated address per CONNECT. Never replay a stream on another route.
		return network.DialContext(ctx, "tcp", net.JoinHostPort(ips[0], port))
	}
}

func proxyMain(input io.Reader, output io.Writer) int {
	return proxyMainMode(input, output, false)
}
func proxyMainMode(input io.Reader, output io.Writer, lossLab bool) int {
	return proxyMainTransport(input, output, lossLab, false)
}
func proxyMainTransport(input io.Reader, output io.Writer, lossLab, udpLab bool) int {
	return proxyTransport(input, output, lossLab, udpLab, false)
}
func proxyTransport(input io.Reader, output io.Writer, lossLab, udpLab, recoveryLab bool, owned ...bool) int {
	var spec proxyStart
	decoder := json.NewDecoder(io.LimitReader(input, 131073))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&spec) != nil || !validHex(spec.Token, 32) || !validHex(spec.Session, 16) || spec.Generation == 0 {
		return 1
	}
	c, e := parseConfig(spec.Config)
	spec.Config = ""
	if e != nil {
		return 1
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if !lossLab {
		go func() { _, _ = io.Copy(io.Discard, input); cancel() }()
	}
	tun, network, e := netstack.CreateNetTUN(c.address, c.dns, c.mtu)
	if e != nil {
		return 1
	}
	bind := conn.NewDefaultBind()
	fault := newFaultBind(bind)
	fault.ownedControl = len(owned) == 1 && owned[0]
	var outer conn.Bind = bind
	if lossLab {
		outer = fault
	}
	dev := device.NewDevice(tun, outer, device.NewLogger(device.LogLevelSilent, ""))
	defer dev.Close()
	if dev.IpcSet(c.ipc) != nil {
		return 1
	}
	c.ipc = ""
	if dev.Up() != nil {
		return 1
	}
	listener, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		return 1
	}
	defer listener.Close()
	if ctx.Err() != nil {
		return 1
	}
	ready := proxyReady{"ListeningUnverified", listener.Addr().(*net.TCPAddr).Port, spec.Session, spec.Generation}
	if json.NewEncoder(output).Encode(ready) != nil {
		return 1
	}
	dial := restrictedDial(network)
	if recoveryLab {
		fault.recovery = newTransportRecovery()
		healthTransport := &http.Transport{DialContext: dial, Proxy: nil, DisableKeepAlives: true, TLSHandshakeTimeout: time.Second, ResponseHeaderTimeout: time.Second}
		healthClient := &http.Client{Transport: healthTransport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
		probe := func(ctx context.Context) bool {
			results := make(chan bool, 2)
			go func() { results <- udpDNSDeadline(network, c.dns[0], 1500*time.Millisecond) }()
			go func() {
				req, _ := http.NewRequestWithContext(ctx, "GET", "https://1.1.1.1/cdn-cgi/trace", nil)
				resp, err := healthClient.Do(req)
				ok := false
				if err == nil {
					body, e := io.ReadAll(io.LimitReader(resp.Body, 8193))
					resp.Body.Close()
					ok = e == nil && resp.StatusCode == 200 && len(body) <= 8192 && traceHash(body) != ""
				}
				results <- ok
			}()
			// Both checks finish before the next cycle; either success prevents repair.
			one, two := <-results, <-results
			return one || two
		}
		done := make(chan struct{})
		go func() {
			defer close(done)
			runTransportRecovery(ctx, fault.recovery, probe, func() error { return dev.IpcSet("listen_port=0\n") })
		}()
		defer func() { cancel(); <-done; healthTransport.CloseIdleConnections() }()
	}
	var dials atomic.Int64
	var udpOptions []udpTunnelDial
	if udpLab {
		udpOptions = append(udpOptions, func(target netip.AddrPort) (net.Conn, error) {
			socket, e := network.DialUDPAddrPort(netip.AddrPort{}, target)
			if e == nil {
				fault.udpDials.Add(1)
			}
			return socket, e
		})
	}
	if lossLab {
		go func() {
			defer cancel()
			if udpLab {
				proxyLossControl(io.MultiReader(decoder.Buffered(), input), output, spec.Session, spec.Generation, fault, &dials, func() error { return dev.IpcSet("listen_port=0\n") })
			} else {
				proxyLossControl(io.MultiReader(decoder.Buffered(), input), output, spec.Session, spec.Generation, fault, &dials)
			}
		}()
		return serveProxy(ctx, listener, spec.Token, func(ctx context.Context, kind, address string) (net.Conn, error) {
			dials.Add(1)
			return dial(ctx, kind, address)
		}, udpOptions...)
	}
	return serveProxy(ctx, listener, spec.Token, dial, udpOptions...)
}

func serveProxy(ctx context.Context, listener net.Listener, token string, dial tunnelDial, udp ...udpTunnelDial) int {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	active := map[net.Conn]struct{}{}
	var gate sync.Mutex
	var workers sync.WaitGroup
	stopped := make(chan struct{})
	defer close(stopped)
	go func() {
		select {
		case <-ctx.Done():
		case <-stopped:
			return
		}
		listener.Close()
		gate.Lock()
		defer gate.Unlock()
		for c := range active {
			c.Close()
		}
	}()
	slots := make(chan struct{}, 32)
	for {
		local, e := listener.Accept()
		if e != nil {
			break
		}
		gate.Lock()
		if ctx.Err() != nil {
			gate.Unlock()
			local.Close()
			break
		}
		active[local] = struct{}{}
		gate.Unlock()
		select {
		case slots <- struct{}{}:
		default:
			gate.Lock()
			delete(active, local)
			gate.Unlock()
			local.Close()
			continue
		}
		workers.Add(1)
		go func() {
			defer workers.Done()
			defer func() { <-slots; gate.Lock(); delete(active, local); gate.Unlock(); local.Close() }()
			handleProxy(ctx, local, token, dial, udp...)
		}()
	}
	// Unexpected listener failure also cancels dialers and all established relays.
	expectedStop := ctx.Err() != nil
	cancel()
	gate.Lock()
	for c := range active {
		c.Close()
	}
	gate.Unlock()
	workers.Wait()
	if expectedStop {
		return 0
	}
	return 1
}

func handleProxy(ctx context.Context, local net.Conn, token string, dial tunnelDial, udp ...udpTunnelDial) {
	local.SetDeadline(time.Now().Add(5 * time.Second))
	var hello [2]byte
	if _, e := io.ReadFull(local, hello[:]); e != nil || hello[0] != 5 || hello[1] == 0 {
		return
	}
	methods := make([]byte, int(hello[1]))
	if _, e := io.ReadFull(local, methods); e != nil {
		return
	}
	auth := false
	for _, m := range methods {
		auth = auth || m == 2
	}
	if !auth {
		local.Write([]byte{5, 255})
		return
	}
	if _, e := local.Write([]byte{5, 2}); e != nil {
		return
	}
	var header [2]byte
	if _, e := io.ReadFull(local, header[:]); e != nil || header[0] != 1 || header[1] != 4 {
		return
	}
	user := make([]byte, 4)
	if _, e := io.ReadFull(local, user); e != nil {
		return
	}
	var length [1]byte
	if _, e := io.ReadFull(local, length[:]); e != nil || length[0] != 64 {
		return
	}
	password := make([]byte, 64)
	if _, e := io.ReadFull(local, password); e != nil {
		return
	}
	authenticated := subtle.ConstantTimeCompare(user, []byte("cita")) & subtle.ConstantTimeCompare(password, []byte(token))
	clear(password)
	if authenticated != 1 {
		local.Write([]byte{1, 1})
		return
	}
	if _, e := local.Write([]byte{1, 0}); e != nil {
		return
	}
	var request [4]byte
	if _, e := io.ReadFull(local, request[:]); e != nil || request[0] != 5 || request[2] != 0 {
		return
	}
	reply := func(code byte) bool { _, e := local.Write([]byte{5, code, 0, 1, 0, 0, 0, 0, 0, 0}); return e == nil }
	if request[1] == 3 && len(udp) == 1 {
		handleUDPAssociation(ctx, local, request[3], udp[0])
		return
	}
	if request[1] != 1 {
		reply(7)
		return
	} // UDP ASSOCIATE/BIND deliberately unsupported.
	host := ""
	switch request[3] {
	case 1:
		var ip [4]byte
		if _, e := io.ReadFull(local, ip[:]); e != nil {
			return
		}
		address := netip.AddrFrom4(ip)
		if !publicTarget(address) {
			reply(2)
			return
		}
		host = address.String()
	case 3:
		if _, e := io.ReadFull(local, length[:]); e != nil || length[0] == 0 {
			return
		}
		name := make([]byte, int(length[0]))
		if _, e := io.ReadFull(local, name); e != nil {
			return
		}
		host = string(name)
		if !safeDomain(host) {
			reply(8)
			return
		}
	default:
		reply(8)
		return
	}
	var portBytes [2]byte
	if _, e := io.ReadFull(local, portBytes[:]); e != nil {
		return
	}
	port := binary.BigEndian.Uint16(portBytes[:])
	if port == 0 {
		reply(2)
		return
	}
	connecting, cancel := context.WithTimeout(ctx, 8*time.Second)
	if local.SetDeadline(time.Now().Add(8*time.Second)) != nil {
		cancel()
		return
	}
	remote, e := dial(connecting, "tcp", net.JoinHostPort(host, strconv.Itoa(int(port))))
	cancel()
	if e != nil {
		reply(4)
		return
	}
	defer remote.Close()
	// Covers stop while a peer is silent and both copy loops are blocked.
	finished := make(chan struct{})
	defer close(finished)
	go func() {
		select {
		case <-ctx.Done():
			remote.Close()
			local.Close()
		case <-finished:
		}
	}()
	if ctx.Err() != nil {
		return
	}
	if !reply(0) {
		return
	}
	if local.SetDeadline(time.Time{}) != nil {
		return
	}
	copied := make(chan struct{})
	go func() {
		defer close(copied)
		_, _ = io.Copy(remote, local)
		if half, ok := remote.(interface{ CloseWrite() error }); ok {
			half.CloseWrite()
		} else {
			remote.Close()
		}
	}()
	_, _ = io.Copy(local, remote)
	local.Close()
	remote.Close()
	<-copied
}
