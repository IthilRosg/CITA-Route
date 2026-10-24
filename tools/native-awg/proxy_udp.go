package main

import (
	"context"
	"encoding/binary"
	"io"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"time"
)

const udpPayloadLimit = 1232
const udpFlowLimit = 8
const udpDatagramBurst = 256
const udpDatagramsPerSecond = 4096

var udpBudgetDrops atomic.Int64

// A child-wide bound prevents opening more associations from multiplying load.
var udpAggregateBudget = datagramBudget{rate: 8192, burst: 512}

type udpTunnelDial func(netip.AddrPort) (net.Conn, error)
type udpFlow struct{ socket net.Conn }
type datagramBudget struct {
	gate        sync.Mutex
	tokens      float64
	updated     time.Time
	rate, burst float64
}

func (b *datagramBudget) allow(now time.Time) bool {
	b.gate.Lock()
	defer b.gate.Unlock()
	if b.rate == 0 {
		b.rate = udpDatagramsPerSecond
	}
	if b.burst == 0 {
		b.burst = udpDatagramBurst
	}
	if b.updated.IsZero() {
		b.tokens = b.burst
		b.updated = now
	}
	if now.After(b.updated) {
		b.tokens += now.Sub(b.updated).Seconds() * b.rate
		if b.tokens > b.burst {
			b.tokens = b.burst
		}
		b.updated = now
	}
	if b.tokens < 1 {
		udpBudgetDrops.Add(1)
		return false
	}
	b.tokens--
	return true
}
func parseUDPFrame(packet []byte) (netip.AddrPort, []byte, bool) {
	if len(packet) <= 10 || len(packet) > 10+udpPayloadLimit || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 || packet[3] != 1 {
		return netip.AddrPort{}, nil, false
	}
	ip := netip.AddrFrom4([4]byte{packet[4], packet[5], packet[6], packet[7]})
	port := binary.BigEndian.Uint16(packet[8:10])
	if !publicTarget(ip) || port == 0 {
		return netip.AddrPort{}, nil, false
	}
	return netip.AddrPortFrom(ip, port), packet[10:], true
}
func udpFrame(target netip.AddrPort, payload []byte) []byte {
	packet := make([]byte, 10+len(payload))
	packet[3] = 1
	ip := target.Addr().As4()
	copy(packet[4:8], ip[:])
	binary.BigEndian.PutUint16(packet[8:10], target.Port())
	copy(packet[10:], payload)
	return packet
}
func handleUDPAssociation(ctx context.Context, control net.Conn, kind byte, dial udpTunnelDial) {
	if kind != 1 {
		control.Write([]byte{5, 8, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	var hint [6]byte
	if _, e := io.ReadFull(control, hint[:]); e != nil {
		return
	}
	ip := netip.AddrFrom4([4]byte{hint[0], hint[1], hint[2], hint[3]})
	port := binary.BigEndian.Uint16(hint[4:])
	peer, ok := control.RemoteAddr().(*net.TCPAddr)
	if !ok || !peer.IP.IsLoopback() {
		return
	}
	// Some clients send the destination rather than their local UDP address.
	// For those clients, pin the first VALID loopback source tuple. Never rebind.
	if !ip.IsUnspecified() && !ip.IsLoopback() && !publicTarget(ip) {
		control.Write([]byte{5, 2, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	if publicTarget(ip) {
		port = 0
	}
	relay, e := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if e != nil {
		control.Write([]byte{5, 1, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	defer relay.Close()
	address := relay.LocalAddr().(*net.UDPAddr)
	reply := []byte{5, 0, 0, 1, 127, 0, 0, 1, byte(address.Port >> 8), byte(address.Port)}
	if _, e = control.Write(reply); e != nil {
		return
	}
	if control.SetDeadline(time.Time{}) != nil {
		return
	}
	relayUDP(ctx, control, relay, port, dial)
}

// Each authenticated TCP control owns one ephemeral loopback relay and at most
// eight connected in-tunnel UDP flows. No OS WAN UDP/DNS socket is ever used.
func relayUDP(parent context.Context, control net.Conn, relay net.PacketConn, sourcePort uint16, dial udpTunnelDial) {
	ctx, cancel := context.WithCancel(parent)
	flows := map[netip.AddrPort]*udpFlow{}
	var gate sync.Mutex
	var readers sync.WaitGroup
	var budget datagramBudget
	done := make(chan struct{})
	go func() {
		defer close(done)
		<-ctx.Done()
		relay.Close()
		control.Close()
		gate.Lock()
		for _, flow := range flows {
			flow.socket.Close()
		}
		gate.Unlock()
	}()
	watched := make(chan struct{})
	go func() { defer close(watched); var b [1]byte; control.Read(b[:]); cancel() }()
	defer func() { cancel(); <-done; <-watched; readers.Wait() }()
	packet := make([]byte, udpPayloadLimit+11)
	invalid := 0
	for {
		if relay.SetReadDeadline(time.Now().Add(60*time.Second)) != nil {
			return
		}
		n, from, e := relay.ReadFrom(packet)
		if e != nil {
			return
		}
		if ctx.Err() != nil {
			return
		}
		if !budget.allow(time.Now()) {
			invalid++
			if invalid >= 128 {
				return
			}
			continue
		}
		source, ok := from.(*net.UDPAddr)
		if !ok || source.IP.To4() == nil || !source.IP.IsLoopback() || (sourcePort != 0 && source.Port != int(sourcePort)) {
			invalid++
			if invalid >= 128 {
				return
			}
			continue
		}
		target, payload, valid := parseUDPFrame(packet[:n])
		if !valid {
			invalid++
			if invalid >= 128 {
				return
			}
			continue
		}
		if sourcePort == 0 {
			sourcePort = uint16(source.Port)
		}
		if !udpAggregateBudget.allow(time.Now()) {
			continue
		}
		// Copy before a reader goroutine can outlive the next ReadFrom iteration.
		client := &net.UDPAddr{IP: append(net.IP(nil), source.IP...), Port: source.Port}
		gate.Lock()
		flow := flows[target]
		if flow == nil {
			if len(flows) >= udpFlowLimit {
				gate.Unlock()
				continue
			}
			socket, err := dial(target)
			if err != nil {
				gate.Unlock()
				continue
			}
			if ctx.Err() != nil {
				gate.Unlock()
				socket.Close()
				return
			}
			flow = &udpFlow{socket: socket}
			flows[target] = flow
			readers.Add(1)
			go func(target netip.AddrPort, flow *udpFlow, client *net.UDPAddr) {
				defer readers.Done()
				defer flow.socket.Close()
				defer func() {
					gate.Lock()
					if flows[target] == flow {
						delete(flows, target)
					}
					gate.Unlock()
				}()
				data := make([]byte, udpPayloadLimit+1)
				for {
					n, e := flow.socket.Read(data)
					if e != nil {
						return
					}
					if n == 0 || n > udpPayloadLimit || ctx.Err() != nil {
						if ctx.Err() != nil {
							return
						}
						continue
					}
					if !budget.allow(time.Now()) || !udpAggregateBudget.allow(time.Now()) {
						continue
					}
					if relay.SetWriteDeadline(time.Now().Add(time.Second)) != nil {
						return
					}
					if _, e = relay.WriteTo(udpFrame(target, data[:n]), client); e != nil {
						return
					}
				}
			}(target, flow, client)
		}
		gate.Unlock()
		if flow.socket.SetDeadline(time.Now().Add(30*time.Second)) != nil {
			flow.socket.Close()
			continue
		}
		if _, e = flow.socket.Write(payload); e != nil {
			flow.socket.Close()
		}
	}
}
