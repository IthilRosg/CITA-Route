package main

import (
	"encoding/binary"
	"golang.org/x/net/dns/dnsmessage"
	"net"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"
)

func TestBoundedDNSRetransmissionAndStaleReply(t *testing.T) {
	for _, mode := range []string{"drop-first", "stale-first", "silent", "malformed"} {
		t.Run(mode, func(t *testing.T) {
			server, e := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
			if e != nil {
				t.Fatal(e)
			}
			defer server.Close()
			var count atomic.Int32
			done := make(chan struct{})
			go func() {
				defer close(done)
				buffer := make([]byte, 1232)
				for {
					n, from, e := server.ReadFromUDP(buffer)
					if e != nil {
						return
					}
					received := count.Add(1)
					if mode == "silent" || mode == "drop-first" && received == 1 {
						continue
					}
					if n < 2 {
						continue
					}
					id := binary.BigEndian.Uint16(buffer[:2])
					m := dnsmessage.Message{Header: dnsmessage.Header{ID: id, Response: true}, Questions: []dnsmessage.Question{{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}}, Answers: []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}, Body: &dnsmessage.AResource{A: [4]byte{93, 184, 215, 14}}}}}
					if mode == "stale-first" {
						m.ID = id + 1
						data, _ := m.Pack()
						server.WriteToUDP(data, from)
						m.ID = id
					}
					data, _ := m.Pack()
					if mode == "malformed" {
						data = data[:3]
					}
					server.WriteToUDP(data, from)
				}
			}()
			client, e := net.DialUDP("udp4", nil, server.LocalAddr().(*net.UDPAddr))
			if e != nil {
				t.Fatal(e)
			}
			defer client.Close()
			var evidence dnsExchangeEvidence
			began := time.Now()
			ok, reason := exchangeDNS(client, netip.AddrPort{}, false, 180*time.Millisecond, &evidence)
			if time.Since(began) > 500*time.Millisecond || evidence.Retries > 1 {
				t.Fatal("DNS retry exceeded its budget")
			}
			switch mode {
			case "drop-first":
				if !ok || evidence.Retries != 1 || count.Load() != 2 {
					t.Fatalf("lost probe: ok=%v reason=%s count=%d retries=%d",ok,reason,count.Load(),evidence.Retries)
				}
			case "stale-first":
				if !ok || evidence.StaleResponses != 1 || evidence.Retries != 0 {
					t.Fatalf("stale probe: ok=%v reason=%s count=%d stale=%d",ok,reason,count.Load(),evidence.StaleResponses)
				}
			case "silent":
				if ok || reason != "ReadTimeout" || evidence.Retries != 1 {
					t.Fatal("persistent failure concealed")
				}
			case "malformed":
				if ok || reason != "MalformedDNS" {
					t.Fatalf("malformed probe: ok=%v reason=%s count=%d",ok,reason,count.Load())
				}
			}
			server.Close()
			<-done
		})
	}
}
