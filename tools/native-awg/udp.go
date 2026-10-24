package main

import (
	"net/netip"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"golang.org/x/net/dns/dnsmessage"
)

func validDNS(data []byte, id uint16) bool {
	return dnsEvidenceFailure(data, id) == ""
}

func dnsEvidenceFailure(data []byte, id uint16) string {
	var m dnsmessage.Message
	if m.Unpack(data) != nil {
		return "MalformedDNS"
	}
	if m.ID != id {
		return "StaleDNSID"
	}
	if !m.Response {
		return "NotDNSResponse"
	}
	if m.Truncated {
		return "TruncatedDNS"
	}
	if m.RCode != dnsmessage.RCodeSuccess {
		return "DNSRcode"
	}
	if len(m.Questions) != 1 {
		return "DNSQuestions"
	}
	q := m.Questions[0]
	if q.Name.String() != "example.com." || q.Type != dnsmessage.TypeA || q.Class != dnsmessage.ClassINET {
		return "DNSQuestionMismatch"
	}
	for _, a := range m.Answers {
		if v, ok := a.Body.(*dnsmessage.AResource); ok {
			ip := netip.AddrFrom4(v.A)
			if ip.IsGlobalUnicast() && !ip.IsPrivate() {
				return ""
			}
		}
	}
	return "DNSNoPublicAnswer"
}

// Explicit DNS/UDP request, using only the tunnel stack, never Windows DNS.
func udpDNS(network *netstack.Net, server netip.Addr) bool {
	return udpDNSDeadline(network, server, 3*time.Second)
}
func udpDNSDeadline(network *netstack.Net, server netip.Addr, timeout time.Duration) bool {
	socket, e := network.DialUDPAddrPort(netip.AddrPort{}, netip.AddrPortFrom(server, 53))
	if e != nil {
		return false
	}
	defer socket.Close()
	ok, _ := exchangeDNS(socket, netip.AddrPortFrom(server, 53), false, timeout, nil)
	return ok
}
