package citaretain

import (
	"context"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
)

type addressConn struct {
	net.Conn
	remote, local *net.TCPAddr
}

func (c *addressConn) RemoteAddr() net.Addr { return c.remote }
func (c *addressConn) LocalAddr() net.Addr  { return c.local }
func TestCitaRetentionLeaseBoundary(t *testing.T) {
	makeLease := func(server string, port uint16, version, user, password, session string, generation uint64, tcp bool) (*Lease, error) {
		return New(server, port, version, user, password, session, generation, tcp)
	}
	token := strings.Repeat("a", 64)
	session := strings.Repeat("b", 32)
	l, e := makeLease("127.0.0.1", 20888, "5", "cita", token, session, 7, true)
	if e != nil {
		t.Fatal(e)
	}
	for _, test := range []struct {
		server                        string
		port                          uint16
		version, user, token, session string
		generation                    uint64
		tcp                           bool
	}{
		{"localhost", 20888, "5", "cita", token, session, 7, true}, {"1.1.1.1", 20888, "5", "cita", token, session, 7, true},
		{"127.0.0.1", 0, "5", "cita", token, session, 7, true}, {"127.0.0.1", 20888, "4", "cita", token, session, 7, true},
		{"127.0.0.1", 20888, "5", "user", token, session, 7, true}, {"127.0.0.1", 20888, "5", "cita", "short", session, 7, true},
		{"127.0.0.1", 20888, "5", "cita", token, "short", 7, true}, {"127.0.0.1", 20888, "5", "cita", token, session, 0, true},
		{"127.0.0.1", 20888, "5", "cita", token, session, 7, false},
	} {
		if _, e := makeLease(test.server, test.port, test.version, test.user, test.token, test.session, test.generation, test.tcp); e == nil {
			t.Fatal("Unsafe lease accepted")
		}
	}
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	c := &addressConn{a, &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 20888}, &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 49152}}
	ctx := WithLease(context.Background(), l)
	if !Preserve(Wrap(ctx, "127.0.0.1:20888", c)) {
		t.Fatal("Scoped local connection not retained")
	}
	if Preserve(Wrap(context.Background(), "127.0.0.1:20888", c)) || Preserve(Wrap(ctx, "127.0.0.1:20889", c)) {
		t.Fatal("Unowned connection retained")
	}
	c.remote = &net.TCPAddr{IP: net.IPv4(1, 1, 1, 1), Port: 20888}
	if Preserve(Wrap(ctx, "127.0.0.1:20888", c)) {
		t.Fatal("Physical socket retained")
	}
	c.remote = &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 20888}
	retained := Wrap(ctx, "127.0.0.1:20888", c)
	l.Revoke()
	if Preserve(retained) || Preserve(Wrap(ctx, "127.0.0.1:20888", c)) {
		t.Fatal("Revoked generation retained")
	}
	if IsInterfaceReset(context.Background()) || !IsInterfaceReset(InterfaceReset(context.Background())) {
		t.Fatal("Reset scope incorrect")
	}
}

type countConn struct {
	net.Conn
	closed atomic.Int64
}

func (c *countConn) Close() error { c.closed.Add(1); return nil }
func TestCitaRetentionRevocationClosesOnlyItsGeneration(t *testing.T) {
	makeLease := func(generation uint64) *Lease {
		l, e := New("127.0.0.1", 20888, "5", "cita", strings.Repeat("a", 64), strings.Repeat("b", 32), generation, true)
		if e != nil {
			t.Fatal(e)
		}
		return l
	}
	old := makeLease(7)
	next := makeLease(8)
	wrap := func(l *Lease) (net.Conn, *countConn) {
		raw := &countConn{}
		c := &addressConn{raw, &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 20888}, &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 49152}}
		return Wrap(WithLease(context.Background(), l), "127.0.0.1:20888", c), raw
	}
	a, rawOld := wrap(old)
	b, rawNext := wrap(next)
	defer b.Close()
	var workers sync.WaitGroup
	for i := 0; i < 8; i++ {
		workers.Add(1)
		go func() { defer workers.Done(); old.Revoke(); a.Close() }()
	}
	workers.Wait()
	if rawOld.closed.Load() != 1 || rawNext.closed.Load() != 0 || Preserve(a) || !Preserve(b) {
		t.Fatal("Revocation crossed generation or failed to close exactly once")
	}
	old.gate.Lock()
	remaining := len(old.connections)
	old.gate.Unlock()
	if remaining != 0 {
		t.Fatal("Revoked lease retained connection references")
	}
}
func TestCitaRetentionRegisterDuringRevoke(t *testing.T) {
	l, e := New("127.0.0.1", 20888, "5", "cita", strings.Repeat("a", 64), strings.Repeat("b", 32), 7, true)
	if e != nil {
		t.Fatal(e)
	}
	ctx := WithLease(context.Background(), l)
	raw := make([]*countConn, 32)
	var workers sync.WaitGroup
	for i := range raw {
		raw[i] = &countConn{}
		workers.Add(1)
		go func(i int) {
			defer workers.Done()
			c := &addressConn{raw[i], &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 20888}, &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 49152}}
			Wrap(ctx, "127.0.0.1:20888", c)
		}(i)
	}
	l.Revoke()
	workers.Wait()
	l.Revoke()
	for _, c := range raw {
		if c.closed.Load() != 1 {
			t.Fatal("Pending/stale dial survived revocation or closed twice")
		}
	}
}
