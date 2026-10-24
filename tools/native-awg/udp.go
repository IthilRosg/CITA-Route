package main

import (
	"crypto/rand"
	"encoding/binary"
	"net/netip"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"golang.org/x/net/dns/dnsmessage"
)

func validDNS(data []byte, id uint16) bool {
	var m dnsmessage.Message
	if m.Unpack(data) != nil || m.ID != id || !m.Response || m.Truncated || m.RCode != dnsmessage.RCodeSuccess || len(m.Questions) != 1 {
		return false
	}
	q := m.Questions[0]
	if q.Name.String() != "example.com." || q.Type != dnsmessage.TypeA || q.Class != dnsmessage.ClassINET {
		return false
	}
	for _, a := range m.Answers {
		if v, ok := a.Body.(*dnsmessage.AResource); ok {
			ip := netip.AddrFrom4(v.A)
			if ip.IsGlobalUnicast() && !ip.IsPrivate() {
				return true
			}
		}
	}
	return false
}

// Explicit DNS/UDP request, using only the tunnel stack, never Windows DNS.
func udpDNS(network *netstack.Net, server netip.Addr) bool {
	var nonce [2]byte
	if _, e := rand.Read(nonce[:]); e != nil {
		return false
	}
	id := binary.BigEndian.Uint16(nonce[:])
	m := dnsmessage.Message{Header: dnsmessage.Header{ID: id, RecursionDesired: true}, Questions: []dnsmessage.Question{{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}}}
	packet, e := m.Pack()
	if e != nil {
		return false
	}
	socket, e := network.DialUDPAddrPort(netip.AddrPort{}, netip.AddrPortFrom(server, 53))
	if e != nil {
		return false
	}
	defer socket.Close()
	if socket.SetDeadline(time.Now().Add(3*time.Second)) != nil {
		return false
	}
	if _, e = socket.Write(packet); e != nil {
		return false
	}
	data := make([]byte, 1232)
	n, e := socket.Read(data)
	return e == nil && validDNS(data[:n], id)
}
