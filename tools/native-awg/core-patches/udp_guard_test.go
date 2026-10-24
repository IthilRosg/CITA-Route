package socks

import (
	M "github.com/sagernet/sing/common/metadata"
	"net"
	"net/netip"
	"testing"
)

type citaGuardBase struct{ net.PacketConn }

func (c *citaGuardBase) LocalAddr() net.Addr {
	return &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 42000}
}
func TestCitaUDPGuardSourceAndFragment(t *testing.T) {
	base := &citaGuardBase{}
	peer := &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40001}
	hint := M.SocksaddrFrom(netip.MustParseAddr("127.0.0.1"), 40000)
	wrapper, e := newCitaUDPGuard(base, peer, hint)
	if e != nil {
		t.Fatal(e)
	}
	guard := wrapper.(*citaUDPGuard)
	frame := []byte{0, 0, 0, 1, 1, 1, 1, 1, 0, 53, 1}
	source := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40000}
	if guard.accept(frame, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40002}) {
		t.Fatal("Different sender accepted before first valid packet")
	}
	if !guard.accept(frame, source) {
		t.Fatal("Declared local sender refused")
	}
	for _, position := range []int{0, 1, 2, 3} {
		bad := append([]byte(nil), frame...)
		bad[position] = 4
		if guard.accept(bad, source) {
			t.Fatal("Malformed/fragmented/domain frame accepted")
		}
	}
	bad := append([]byte(nil), frame...)
	bad[4] = 10
	if guard.accept(bad, source) {
		t.Fatal("Private destination accepted")
	}
	if guard.accept(frame, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 2), Port: 40000}) {
		t.Fatal("Different source IP accepted")
	}
	fallback, e := newCitaUDPGuard(base, peer, M.SocksaddrFrom(netip.MustParseAddr("1.1.1.1"), 53))
	if e != nil {
		t.Fatal(e)
	}
	if !fallback.(*citaUDPGuard).accept(frame, source) || fallback.(*citaUDPGuard).accept(frame, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 40003}) {
		t.Fatal("First-source pin changed")
	}
	if _, e := newCitaUDPGuard(base, &net.TCPAddr{IP: net.IPv4(1, 1, 1, 1)}, hint); e == nil {
		t.Fatal("Guard enabled on nonloopback TCP")
	}
}
