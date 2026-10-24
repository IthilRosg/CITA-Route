package main

import (
	"encoding/binary"
	"net"
	"net/netip"
	"time"
)

type dnsExchangeEvidence struct {
	ID             uint16 `json:"id"`
	Retries        int    `json:"retries"`
	StaleResponses int    `json:"staleResponses"`
	Milliseconds   int64  `json:"milliseconds"`
}

// DNS is idempotent and UDP is lossy. One retransmission shares the original
// deadline and transaction ID. This never replays an application TCP/UDP flow.
func exchangeDNS(socket net.Conn, target netip.AddrPort, framed bool, timeout time.Duration, evidence *dnsExchangeEvidence) (ok bool, reason string) {
	if evidence == nil {
		evidence = &dnsExchangeEvidence{}
	}
	*evidence = dnsExchangeEvidence{}
	began := time.Now()
	defer func() { evidence.Milliseconds = time.Since(began).Milliseconds() }()
	payload, id, e := dnsQuery()
	evidence.ID = id
	if e != nil {
		return false, "QueryCreation"
	}
	if framed {
		payload = udpFrame(target, payload)
	}
	until := began.Add(timeout)
	firstWait := timeout / 2
	if firstWait > 750*time.Millisecond {
		firstWait = 750 * time.Millisecond
	}
	if timeout <= 0 {
		return false, "Deadline"
	}
	if socket.SetWriteDeadline(until) != nil {
		return false, "Deadline"
	}
	send := func() bool { n, e := socket.Write(payload); return e == nil && n == len(payload) }
	if !send() {
		return false, "WriteError"
	}
	if socket.SetReadDeadline(began.Add(firstWait)) != nil {
		return false, "Deadline"
	}
	buffer := make([]byte, udpPayloadLimit+11)
	for discarded := 0; discarded < 32; {
		n, e := socket.Read(buffer)
		if e != nil {
			if v, yes := e.(net.Error); yes && v.Timeout() {
				if evidence.Retries == 0 && time.Now().Before(until) {
					evidence.Retries++
					if !send() {
						return false, "WriteError"
					}
					if socket.SetReadDeadline(until) != nil {
						return false, "Deadline"
					}
					continue
				}
				return false, "ReadTimeout"
			}
			return false, "ReadError"
		}
		data := buffer[:n]
		if framed {
			reply, body, valid := parseUDPFrame(data)
			if !valid || reply != target {
				return false, "InvalidFrame"
			}
			data = body
		} else if n > udpPayloadLimit {
			return false, "Oversize"
		}
		if len(data) >= 2 && binary.BigEndian.Uint16(data[:2]) != id {
			evidence.StaleResponses++
			discarded++
			continue
		}
		reason := dnsEvidenceFailure(data, id)
		return reason == "", reason
	}
	return false, "TooManyStaleDNSResponses"
}
