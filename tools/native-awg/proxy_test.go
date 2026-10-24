package main

import (
	"context"
	"golang.org/x/net/proxy"
	"io"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// In-memory listener keeps security/lifecycle tests independent of host network
// filters. Real loopback sockets and the actual routing core are tested in VM.
type memoryListener struct {
	incoming chan net.Conn
	closed   chan struct{}
	once     sync.Once
}

func newMemoryListener() *memoryListener {
	return &memoryListener{incoming: make(chan net.Conn), closed: make(chan struct{})}
}
func (l *memoryListener) Accept() (net.Conn, error) {
	select {
	case c := <-l.incoming:
		return c, nil
	case <-l.closed:
		return nil, net.ErrClosed
	}
}
func (l *memoryListener) Close() error   { l.once.Do(func() { close(l.closed) }); return nil }
func (l *memoryListener) Addr() net.Addr { return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 1} }
func (l *memoryListener) Dial(kind, address string) (net.Conn, error) {
	return l.DialContext(context.Background(), kind, address)
}
func (l *memoryListener) DialContext(ctx context.Context, kind, address string) (net.Conn, error) {
	a, b := net.Pipe()
	select {
	case l.incoming <- b:
		return a, nil
	case <-l.closed:
		a.Close()
		b.Close()
		return nil, net.ErrClosed
	case <-ctx.Done():
		a.Close()
		b.Close()
		return nil, ctx.Err()
	}
}

func TestProxyAuthenticationAndOwnerStop(t *testing.T) {
	token := strings.Repeat("a", 64)
	listener := newMemoryListener()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan int, 1)
	var calls atomic.Int64
	dial := func(ctx context.Context, kind, address string) (net.Conn, error) {
		calls.Add(1)
		a, b := net.Pipe()
		go func() { defer b.Close(); io.Copy(b, b) }()
		return a, nil
	}
	go func() { done <- serveProxy(ctx, listener, token, dial) }()
	defer func() {
		cancel()
		select {
		case code := <-done:
			if code != 0 {
				t.Error("Owner stop must be clean")
			}
		case <-time.After(2 * time.Second):
			t.Error("Owner stop left handlers running")
		}
	}()
	open := func(auth *proxy.Auth, target string) (net.Conn, error) {
		d, e := proxy.SOCKS5("tcp", listener.Addr().String(), auth, listener)
		if e != nil {
			return nil, e
		}
		cctx, stop := context.WithTimeout(context.Background(), time.Second)
		defer stop()
		return d.(proxy.ContextDialer).DialContext(cctx, "tcp", target)
	}
	for _, auth := range []*proxy.Auth{nil, {User: "cita", Password: strings.Repeat("b", 64)}} {
		c, e := open(auth, "1.1.1.1:443")
		if c != nil {
			c.Close()
		}
		if e == nil {
			t.Fatal("Unauthenticated connection accepted")
		}
	}
	correct := &proxy.Auth{User: "cita", Password: token}
	for _, target := range []string{"127.0.0.1:80", "10.0.0.1:80", "100.100.100.100:443", "[::1]:443"} {
		c, e := open(correct, target)
		if c != nil {
			c.Close()
		}
		if e == nil {
			t.Fatal("Nonpublic or IPv6 target accepted")
		}
	}
	if calls.Load() != 0 {
		t.Fatal("Refused requests reached tunnel dialer")
	}
	c, e := open(correct, "1.1.1.1:443")
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(time.Second))
	if _, e = c.Write([]byte("ok")); e != nil {
		t.Fatal(e)
	}
	var reply [2]byte
	if _, e = io.ReadFull(c, reply[:]); e != nil || string(reply[:]) != "ok" || calls.Load() != 1 {
		t.Fatal("Authenticated relay failed")
	}
	cancel()
	c.SetReadDeadline(time.Now().Add(time.Second))
	if _, e = c.Read(reply[:]); e == nil {
		t.Fatal("Owner cancellation retained an active relay")
	}
}

func TestProxyCancellationDuringHandshake(t *testing.T) {
	listener := newMemoryListener()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan int, 1)
	go func() {
		done <- serveProxy(ctx, listener, strings.Repeat("c", 64), func(context.Context, string, string) (net.Conn, error) {
			t.Error("Partial handshake dialed a target")
			return nil, errProxy
		})
	}()
	c, e := listener.Dial("tcp", listener.Addr().String())
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	c.Write([]byte{5})
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("Partial handshake ignored owner stop")
	}
	if c, e := listener.Dial("tcp", listener.Addr().String()); e == nil {
		c.Close()
		t.Fatal("Listener survived owner stop")
	}
}
func TestProxyAddressScope(t *testing.T) {
	for _, name := range []string{"localhost", "bad..example", "-bad.example", "a/b.example", "a.example.", "a\x00.example"} {
		if safeDomain(name) {
			t.Fatal("Unsafe hostname accepted")
		}
	}
	if !safeDomain("www.cloudflare.com") {
		t.Fatal("Public hostname rejected")
	}
}

func TestProxyRejectsUdpWithoutDial(t *testing.T) {
	local, client := net.Pipe()
	defer client.Close()
	client.SetDeadline(time.Now().Add(time.Second))
	done := make(chan struct{})
	token := strings.Repeat("d", 64)
	go func() {
		defer close(done)
		defer local.Close()
		handleProxy(context.Background(), local, token, func(context.Context, string, string) (net.Conn, error) {
			t.Error("UDP request reached TCP dialer")
			return nil, errProxy
		})
	}()
	client.Write([]byte{5, 1, 2})
	var method [2]byte
	if _, e := io.ReadFull(client, method[:]); e != nil || method != [2]byte{5, 2} {
		t.Fatal("Auth negotiation failed")
	}
	auth := append([]byte{1, 4}, []byte("cita")...)
	auth = append(auth, 64)
	auth = append(auth, []byte(token)...)
	client.Write(auth)
	if _, e := io.ReadFull(client, method[:]); e != nil || method != [2]byte{1, 0} {
		t.Fatal("Authentication failed")
	}
	client.Write([]byte{5, 3, 0, 1})
	var response [10]byte
	if _, e := io.ReadFull(client, response[:]); e != nil || response[1] != 7 {
		t.Fatal("Unsupported UDP command not explicitly refused")
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("Refused command retained handler")
	}
}

func TestProxyListenerFailureCancelsPendingDial(t *testing.T) {
	listener := newMemoryListener()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	dialing := make(chan struct{})
	done := make(chan int, 1)
	go func() {
		done <- serveProxy(ctx, listener, strings.Repeat("e", 64), func(ctx context.Context, kind, address string) (net.Conn, error) {
			close(dialing)
			<-ctx.Done()
			return nil, ctx.Err()
		})
	}()
	clientDone := make(chan struct{})
	go func() {
		defer close(clientDone)
		d, _ := proxy.SOCKS5("tcp", listener.Addr().String(), &proxy.Auth{User: "cita", Password: strings.Repeat("e", 64)}, listener)
		c, e := d.Dial("tcp", "1.1.1.1:443")
		if e == nil {
			c.Close()
			t.Error("Cancelled dial unexpectedly succeeded")
		}
	}()
	select {
	case <-dialing:
	case <-time.After(time.Second):
		t.Fatal("Dial not started")
	}
	listener.Close()
	select {
	case code := <-done:
		if code != 1 {
			t.Fatal("Unexpected listener failure reported as clean stop")
		}
	case <-time.After(time.Second):
		t.Fatal("Failed listener left pending dial alive")
	}
	select {
	case <-clientDone:
	case <-time.After(time.Second):
		t.Fatal("Client retained after cancellation")
	}
}
