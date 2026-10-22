package route

import (
	"encoding/binary"
	"errors"
	"fmt"
	"net/netip"
	"sync"
	"testing"
	"time"

	"github.com/sagernet/sing-box/adapter"
	C "github.com/sagernet/sing-box/constant"
	M "github.com/sagernet/sing/common/metadata"
)

func citaQUICFixture() (adapter.InboundContext, []byte, time.Time) {
	packet := make([]byte, 1250)
	packet[0] = 0xc0
	binary.BigEndian.PutUint32(packet[1:], 1)
	packet[5] = 8
	copy(packet[6:], "testcid1")
	return adapter.InboundContext{Inbound: "tun", Network: "udp", Protocol: C.ProtocolQUIC,
		Source: M.Socksaddr{Addr: netip.MustParseAddr("192.0.2.1"), Port: 10000}, Destination: M.Socksaddr{Addr: netip.MustParseAddr("203.0.113.1"), Port: 443},
		Domain: "fixture.example", ProcessInfo: &adapter.ConnectionOwner{ProcessID: 12, ProcessPath: `C:\fixture.exe`}}, packet, time.Unix(100, 0)
}

func TestCitaQUICDomainIdentityAndExpiry(t *testing.T) {
	for _, variant := range []string{"same", "cid", "version", "short", "handshake", "random-udp", "process", "pid", "source", "destination", "port", "inbound", "not-quic", "ipv6", "expiry", "clock-backwards", "new-domain"} {
		t.Run(variant, func(t *testing.T) {
			var cache citaQUICDomainCache
			metadata, packet, now := citaQUICFixture()
			cache.apply(&metadata, packet, now)
			metadata.Domain = ""
			metadata.ProcessInfo = &adapter.ConnectionOwner{ProcessID: 12, ProcessPath: `C:\fixture.exe`}
			want := false
			switch variant {
			case "same":
				want = true
			case "cid":
				packet[6]++
			case "version":
				binary.BigEndian.PutUint32(packet[1:], 2)
			case "short":
				packet = packet[:1199]
			case "handshake":
				packet[0] = 0xe0
			case "random-udp":
				packet[0] = 0
			case "process":
				metadata.ProcessInfo.ProcessPath = `C:\other.exe`
			case "pid":
				metadata.ProcessInfo.ProcessID++
			case "source":
				metadata.Source.Port++
			case "destination":
				metadata.Destination.Addr = netip.MustParseAddr("203.0.113.2")
			case "port":
				metadata.Destination.Port++
			case "inbound":
				metadata.Inbound = "other-tun"
			case "not-quic":
				metadata.Protocol = "stun"
			case "ipv6":
				metadata.Destination.Addr = netip.MustParseAddr("2001:db8::1")
			case "expiry":
				now = now.Add(citaQUICDomainTTL)
			case "clock-backwards":
				now = now.Add(-time.Second)
			case "new-domain":
				metadata.Domain = "new.example"
				cache.apply(&metadata, packet, now)
				metadata.Domain = ""
				want = true
			}
			if got := cache.apply(&metadata, packet, now); got != want {
				t.Fatalf("restored %v, want %v", got, want)
			}
			if want {
				expected := "fixture.example"
				if variant == "new-domain" {
					expected = "new.example"
				}
				if metadata.Domain != expected {
					t.Fatal("wrong restored domain")
				}
			}
		})
	}
}

func TestCitaQUICDomainBoundedAndConcurrent(t *testing.T) {
	var cache citaQUICDomainCache
	metadata, packet, now := citaQUICFixture()
	var group sync.WaitGroup
	for i := 0; i < 8; i++ {
		group.Add(1)
		go func(worker int) {
			defer group.Done()
			for j := 0; j < 300; j++ {
				value := metadata
				value.Source.Port = uint16(10000 + worker*300 + j)
				cache.apply(&value, packet, now)
			}
		}(i)
	}
	group.Wait()
	if len(cache.items) != citaQUICDomainLimit || cache.order.Len() != citaQUICDomainLimit {
		t.Fatal("cache exceeded its fixed bound")
	}
	metadata.Source.Port = 60000
	cache.apply(&metadata, packet, now.Add(citaQUICDomainTTL))
	if len(cache.items) != 1 {
		t.Fatal("expired entries retained")
	}
	metadata.Domain = ""
	cache.apply(&metadata, packet, now.Add(citaQUICDomainTTL+29*time.Second))
	metadata.Domain = ""
	if cache.apply(&metadata, packet, now.Add(2*citaQUICDomainTTL)) {
		t.Fatal("lookup renewed an expired name")
	}
}

func TestCitaQUICDomainRequiresSuccessfulObservation(t *testing.T) {
	for i := 0; i < 3; i++ {
		var cache citaQUICDomainCache
		metadata, packet, now := citaQUICFixture()
		switch i {
		case 0:
			metadata.SniffError = errors.New("incomplete")
		case 1:
			metadata.Domain = ""
		case 2:
			metadata.ProcessInfo = nil
		}
		cache.apply(&metadata, packet, now)
		if len(cache.items) != 0 {
			t.Fatal(fmt.Sprint("unconfirmed observation cached: ", i))
		}
	}
}
