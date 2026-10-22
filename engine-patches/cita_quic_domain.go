package route

import (
	"container/list"
	"encoding/binary"
	"sync"
	"time"

	"github.com/sagernet/sing-box/adapter"
	C "github.com/sagernet/sing-box/constant"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
)

// Rejection closes the UDP association. A retransmitted tail of a fragmented
// Initial can then be sniffed without its earlier CRYPTO frames/SNI. Retain only
// that flow's already observed domain, never a routing decision or packet body.
// Every retry still evaluates the current rules, including manual exceptions.
const citaQUICDomainLimit = 1024
const citaQUICDomainTTL = 30 * time.Second

type citaQUICDomainKey struct {
	inbound, application, cid string
	pid                       uint32
	source, destination       M.Socksaddr
}
type citaQUICDomainEntry struct {
	key     citaQUICDomainKey
	domain  string
	created time.Time
}
type citaQUICDomainCache struct {
	mu    sync.Mutex
	items map[citaQUICDomainKey]*list.Element
	order list.List
}

func citaQUICInitialID(packet []byte) (string, bool) {
	if len(packet) < 1200 || packet[0]&0xc0 != 0xc0 {
		return "", false
	}
	version := binary.BigEndian.Uint32(packet[1:5])
	kind := (packet[0] >> 4) & 3
	if (version != 1 || kind != 0) && (version != 0x6b3343cf || kind != 1) {
		return "", false
	}
	n := int(packet[5])
	if n == 0 || n > 20 || len(packet) < 7+n || packet[6+n] > 20 || len(packet) < 7+n+int(packet[6+n]) {
		return "", false
	}
	// Version separates v1/v2 even if their connection IDs happen to coincide.
	return string(packet[1 : 6+n]), true
}

func (c *citaQUICDomainCache) apply(metadata *adapter.InboundContext, packet []byte, now time.Time) bool {
	if metadata.Network != N.NetworkUDP || metadata.Protocol != C.ProtocolQUIC ||
		!metadata.Source.IsIPv4() || !metadata.Destination.IsIPv4() || metadata.Destination.Port != 443 ||
		metadata.ProcessInfo == nil || metadata.ProcessInfo.ProcessID == 0 || metadata.ProcessInfo.ProcessPath == "" {
		return false
	}
	cid, valid := citaQUICInitialID(packet)
	if !valid {
		return false
	}
	key := citaQUICDomainKey{metadata.Inbound, metadata.ProcessInfo.ProcessPath, cid, metadata.ProcessInfo.ProcessID, metadata.Source, metadata.Destination}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.items == nil {
		c.items = make(map[citaQUICDomainKey]*list.Element)
	}
	// Creation order, not use order: a flood cannot indefinitely renew a name.
	for first := c.order.Front(); first != nil; first = c.order.Front() {
		entry := first.Value.(citaQUICDomainEntry)
		if now.Before(entry.created) || now.Sub(entry.created) < citaQUICDomainTTL {
			break
		}
		delete(c.items, entry.key)
		c.order.Remove(first)
	}
	if metadata.Domain != "" {
		if metadata.SniffError != nil || !M.IsDomainName(metadata.Domain) || len(metadata.Domain) > 253 {
			return false
		}
		if old := c.items[key]; old != nil {
			entry := old.Value.(citaQUICDomainEntry)
			if entry.domain == metadata.Domain {
				return false
			}
			delete(c.items, key)
			c.order.Remove(old)
		}
		for len(c.items) >= citaQUICDomainLimit {
			first := c.order.Front()
			delete(c.items, first.Value.(citaQUICDomainEntry).key)
			c.order.Remove(first)
		}
		c.items[key] = c.order.PushBack(citaQUICDomainEntry{key, metadata.Domain, now})
		return false
	}
	if found := c.items[key]; found != nil {
		entry := found.Value.(citaQUICDomainEntry)
		if now.Before(entry.created) || now.Sub(entry.created) >= citaQUICDomainTTL {
			return false
		}
		metadata.Domain = entry.domain
		return true
	}
	return false
}

func (r *Router) citaQUICDomain(metadata *adapter.InboundContext, packets []*N.PacketBuffer) {
	now := time.Now()
	for _, packet := range packets {
		if r.citaQUICDomains.apply(metadata, packet.Buffer.Bytes(), now) {
			return
		}
	}
}
