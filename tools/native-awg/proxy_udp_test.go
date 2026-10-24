package main

import (
	"context"
	"io"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type testDatagram struct {
	data    []byte
	address net.Addr
}
type testPacketConn struct {
	in, out chan testDatagram
	closed  chan struct{}
	once    sync.Once
}

func newTestPacketConn() *testPacketConn {
	return &testPacketConn{in: make(chan testDatagram, 32), out: make(chan testDatagram, 32), closed: make(chan struct{})}
}
func (c *testPacketConn) ReadFrom(b []byte) (int, net.Addr, error) {
	select {
	case p := <-c.in:
		return copy(b, p.data), p.address, nil
	case <-c.closed:
		return 0, nil, net.ErrClosed
	}
}
func (c *testPacketConn) WriteTo(b []byte, a net.Addr) (int, error) {
	select {
	case c.out <- testDatagram{append([]byte(nil), b...), a}:
		return len(b), nil
	case <-c.closed:
		return 0, net.ErrClosed
	}
}
func (c *testPacketConn) Close() error { c.once.Do(func() { close(c.closed) }); return nil }
func (c *testPacketConn) LocalAddr() net.Addr {
	return &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 41000}
}
func (c *testPacketConn) SetDeadline(time.Time) error      { return nil }
func (c *testPacketConn) SetReadDeadline(time.Time) error  { return nil }
func (c *testPacketConn) SetWriteDeadline(time.Time) error { return nil }

type testDatagramConn struct {
	net.Conn
	replies chan []byte
	closed  chan struct{}
	once    sync.Once
	writes  atomic.Int64
}

func (c *testDatagramConn) Read(b []byte) (int, error) {
	select {
	case p := <-c.replies:
		return copy(b, p), nil
	case <-c.closed:
		return 0, io.EOF
	}
}
func (c *testDatagramConn) Write(b []byte) (int, error) {
	c.writes.Add(1)
	select {
	case c.replies <- append([]byte(nil), b...):
		return len(b), nil
	case <-c.closed:
		return 0, net.ErrClosed
	}
}
func (c *testDatagramConn) Close() error                { c.once.Do(func() { close(c.closed) }); return nil }
func (c *testDatagramConn) SetDeadline(time.Time) error { return nil }

func TestUDPFrameBoundary(t *testing.T) {
	target := netip.MustParseAddrPort("1.1.1.1:53")
	good := udpFrame(target, []byte("payload"))
	if got, p, ok := parseUDPFrame(good); !ok || got != target || string(p) != "payload" {
		t.Fatal("Valid datagram refused")
	}
	for _, bad := range [][]byte{good[:9], udpFrame(target, nil), udpFrame(target, make([]byte, 1233)), udpFrame(netip.MustParseAddrPort("127.0.0.1:53"), []byte{1}), udpFrame(netip.MustParseAddrPort("10.0.0.1:53"), []byte{1}), udpFrame(netip.MustParseAddrPort("100.100.100.100:53"), []byte{1}), udpFrame(netip.MustParseAddrPort("1.1.1.1:0"), []byte{1})} {
		if _, _, ok := parseUDPFrame(bad); ok {
			t.Fatal("Unsafe frame accepted")
		}
	}
	for _, position := range []int{0, 1, 2, 3} {
		bad := append([]byte(nil), good...)
		bad[position] = 4
		if _, _, ok := parseUDPFrame(bad); ok {
			t.Fatal("RSV/fragment/domain/IPv6 encoding accepted")
		}
	}
	var budget datagramBudget
	now := time.Now()
	for i := 0; i < 128; i++ {
		if !budget.allow(now) {
			t.Fatal("Burst budget incorrect")
		}
	}
	if budget.allow(now) || budget.allow(now.Add(-time.Second)) {
		t.Fatal("Budget can be bypassed")
	}
	if !budget.allow(now.Add(time.Second)) {
		t.Fatal("Budget did not refill")
	}
}
func TestUDPRelayPinFlowLimitAndTcpLifetime(t *testing.T) {
	control, peer := net.Pipe()
	defer peer.Close()
	relay := newTestPacketConn()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var calls atomic.Int64
	var sockets []*testDatagramConn
	var gate sync.Mutex
	done := make(chan struct{})
	go func() {
		defer close(done)
		relayUDP(ctx, control, relay, 40000, func(netip.AddrPort) (net.Conn, error) {
			calls.Add(1)
			socket := &testDatagramConn{replies: make(chan []byte, 16), closed: make(chan struct{})}
			gate.Lock()
			sockets = append(sockets, socket)
			gate.Unlock()
			return socket, nil
		})
	}()
	source := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40000}
	target := netip.MustParseAddrPort("1.1.1.1:53")
	send := func(frame []byte, address net.Addr) { relay.in <- testDatagram{frame, address} }
	wait := func(payload string) {
		t.Helper()
		timer := time.NewTimer(time.Second)
		defer timer.Stop()
		for {
			select {
			case p := <-relay.out:
				_, data, ok := parseUDPFrame(p.data)
				if ok && string(data) == payload {
					if p.address.(*net.UDPAddr).Port != 40000 {
						t.Fatal("Reply changed source owner")
					}
					return
				}
			case <-timer.C:
				t.Fatal("Relay reply deadline")
			}
		}
	}
	send(udpFrame(target, []byte("first")), source)
	wait("first")
	send(udpFrame(target, []byte("rogue")), &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40001})
	send(udpFrame(netip.MustParseAddrPort("10.0.0.1:53"), []byte("private")), source)
	fragmented := udpFrame(target, []byte("fragment"))
	fragmented[2] = 1
	send(fragmented, source)
	send(udpFrame(target, []byte("barrier")), source)
	wait("barrier")
	gate.Lock()
	first := sockets[0]
	gate.Unlock()
	if calls.Load() != 1 || first.writes.Load() != 2 {
		t.Fatal("Invalid/source-changed packet reached tunnel")
	}
	for i := 0; i < 8; i++ {
		send(udpFrame(netip.AddrPortFrom(netip.AddrFrom4([4]byte{9, 9, 9, byte(i + 1)}), 53), []byte("flow")), source)
	}
	send(udpFrame(target, []byte("limit-barrier")), source)
	wait("limit-barrier")
	if calls.Load() != 8 {
		t.Fatal("Flow cap bypassed")
	}
	peer.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("TCP EOF did not close UDP association")
	}
	gate.Lock()
	defer gate.Unlock()
	for _, socket := range sockets {
		select {
		case <-socket.closed:
		default:
			t.Fatal("Owned tunnel UDP socket survived EOF")
		}
	}
	select {
	case <-relay.closed:
	default:
		t.Fatal("Local relay survived EOF")
	}
}
