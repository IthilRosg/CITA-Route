package socks

import (
	M "github.com/sagernet/sing/common/metadata"
	"io"
	"net"
	"net/netip"
	"sync"
)

// Used only by the explicitly enabled isolated CITA native UDP lab process.
// Drop before ServerPacketConn can replace its remembered sender or strip FRAG.
type citaUDPGuard struct {
	net.PacketConn
	gate   sync.Mutex
	source netip.Addr
	port   uint16
}

func newCitaUDPGuard(base net.PacketConn, peer net.Addr, hint M.Socksaddr) (net.PacketConn, error) {
	tcp, ok := peer.(*net.TCPAddr)
	if !ok || !tcp.IP.IsLoopback() {
		return nil, io.ErrUnexpectedEOF
	}
	local, ok := base.LocalAddr().(*net.UDPAddr)
	if !ok || !local.IP.IsLoopback() {
		return nil, io.ErrUnexpectedEOF
	}
	ip, ok := netip.AddrFromSlice(tcp.IP)
	if !ok || !ip.Unmap().Is4() {
		return nil, io.ErrUnexpectedEOF
	}
	port := uint16(0)
	if hint.Addr.IsLoopback() || hint.Addr.IsUnspecified() {
		port = hint.Port
	}
	return &citaUDPGuard{PacketConn: base, source: ip.Unmap(), port: port}, nil
}
func (c *citaUDPGuard) accept(packet []byte, from net.Addr) bool {
	address, ok := from.(*net.UDPAddr)
	if !ok || address.Port < 1 {
		return false
	}
	ip, ok := netip.AddrFromSlice(address.IP)
	if !ok || ip.Unmap() != c.source {
		return false
	}
	if len(packet) <= 10 || len(packet) > 1242 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 || packet[3] != 1 || packet[8] == 0 && packet[9] == 0 {
		return false
	}
	target := netip.AddrFrom4([4]byte{packet[4], packet[5], packet[6], packet[7]})
	if !target.IsGlobalUnicast() || target.IsPrivate() || target.IsLoopback() || target.IsLinkLocalUnicast() {
		return false
	}
	for _, prefix := range []string{"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4"} {
		if netip.MustParsePrefix(prefix).Contains(target) {
			return false
		}
	}
	c.gate.Lock()
	defer c.gate.Unlock()
	if c.port == 0 {
		c.port = uint16(address.Port)
	}
	return int(c.port) == address.Port
}
func (c *citaUDPGuard) ReadFrom(packet []byte) (int, net.Addr, error) {
	for rejected := 0; rejected < 128; rejected++ {
		n, from, e := c.PacketConn.ReadFrom(packet)
		if e != nil {
			return n, from, e
		}
		if c.accept(packet[:n], from) {
			return n, from, nil
		}
	}
	return 0, nil, io.ErrNoProgress
}
func (c *citaUDPGuard) WriteTo(packet []byte, to net.Addr) (int, error) {
	address, ok := to.(*net.UDPAddr)
	if !ok {
		return 0, io.ErrUnexpectedEOF
	}
	ip, ok := netip.AddrFromSlice(address.IP)
	if !ok || ip.Unmap() != c.source {
		return 0, io.ErrUnexpectedEOF
	}
	c.gate.Lock()
	valid := c.port != 0 && int(c.port) == address.Port
	c.gate.Unlock()
	if !valid {
		return 0, io.ErrUnexpectedEOF
	}
	return c.PacketConn.WriteTo(packet, to)
}
