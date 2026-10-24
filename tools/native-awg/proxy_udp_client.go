package main

import (
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/json"
	"golang.org/x/net/dns/dnsmessage"
	"io"
	"net"
	"net/netip"
	"strconv"
	"time"
)

type udpClientResult struct {
	Success              bool   `json:"success"`
	Stage                string `json:"stage"`
	Queries              int    `json:"queries"`
	AssociationReused    bool   `json:"associationReused"`
	WrongSourceNoReply   bool   `json:"wrongSourceNoReply"`
	InvalidFramesNoReply bool   `json:"invalidFramesNoReply"`
	NoReplyAfterClose    bool   `json:"noReplyAfterClose"`
	HeldOwnerStop        bool   `json:"heldOwnerStop"`
}

func openUDPAssociation(ctx context.Context, port int, token string) (net.Conn, *net.UDPConn, error) {
	udp, e := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if e != nil {
		return nil, nil, e
	}
	control, e := (&net.Dialer{}).DialContext(ctx, "tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if e != nil {
		udp.Close()
		return nil, nil, e
	}
	fail := func() (net.Conn, *net.UDPConn, error) { control.Close(); udp.Close(); return nil, nil, errProxy }
	control.SetDeadline(time.Now().Add(3 * time.Second))
	method := byte(0)
	if token != "" {
		method = 2
	}
	if _, e = control.Write([]byte{5, 1, method}); e != nil {
		return fail()
	}
	var response [2]byte
	if _, e = io.ReadFull(control, response[:]); e != nil || response != [2]byte{5, method} {
		return fail()
	}
	if method == 2 {
		request := append([]byte{1, 4}, []byte("cita")...)
		request = append(request, 64)
		request = append(request, []byte(token)...)
		if _, e = control.Write(request); e != nil {
			return fail()
		}
		clear(request)
		if _, e = io.ReadFull(control, response[:]); e != nil || response != [2]byte{1, 0} {
			return fail()
		}
	}
	local := udp.LocalAddr().(*net.UDPAddr)
	if _, e = control.Write([]byte{5, 3, 0, 1, 127, 0, 0, 1, byte(local.Port >> 8), byte(local.Port)}); e != nil {
		return fail()
	}
	var bound [10]byte
	if _, e = io.ReadFull(control, bound[:]); e != nil || bound[0] != 5 || bound[1] != 0 || bound[2] != 0 || bound[3] != 1 {
		return fail()
	}
	relay := netip.AddrPortFrom(netip.AddrFrom4([4]byte{bound[4], bound[5], bound[6], bound[7]}), binary.BigEndian.Uint16(bound[8:]))
	if !relay.Addr().IsLoopback() || relay.Port() == 0 {
		return fail()
	}
	// A connected UDP client rejects replies from every other loopback relay.
	udp.Close()
	udp, e = net.DialUDP("udp4", local, net.UDPAddrFromAddrPort(relay))
	if e != nil {
		control.Close()
		return nil, nil, e
	}
	control.SetDeadline(time.Time{})
	return control, udp, nil
}
func dnsQuery() ([]byte, uint16, error) {
	var nonce [2]byte
	if _, e := rand.Read(nonce[:]); e != nil {
		return nil, 0, e
	}
	id := binary.BigEndian.Uint16(nonce[:])
	m := dnsmessage.Message{Header: dnsmessage.Header{ID: id, RecursionDesired: true}, Questions: []dnsmessage.Question{{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}}}
	packet, e := m.Pack()
	return packet, id, e
}
func queryThroughAssociation(socket *net.UDPConn, target netip.AddrPort, timeout time.Duration) bool {
	payload, id, e := dnsQuery()
	if e != nil {
		return false
	}
	socket.SetDeadline(time.Now().Add(timeout))
	if _, e = socket.Write(udpFrame(target, payload)); e != nil {
		return false
	}
	buffer := make([]byte, udpPayloadLimit+11)
	n, e := socket.Read(buffer)
	if e != nil {
		return false
	}
	reply, data, ok := parseUDPFrame(buffer[:n])
	return ok && reply == target && validDNS(data, id)
}
func proxyUDPClient(input io.Reader, output io.Writer) int {
	var spec struct {
		Port   int
		Token  string
		Server string
		Hold   bool
	}
	r := udpClientResult{Stage: "UdpAssociation"}
	defer func() { json.NewEncoder(output).Encode(r) }()
	decoder := json.NewDecoder(io.LimitReader(input, 4096))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&spec) != nil || spec.Port < 1 || spec.Port > 65535 || (spec.Token != "" && !validHex(spec.Token, 32)) {
		return 1
	}
	server, e := netip.ParseAddr(spec.Server)
	if e != nil || !publicTarget(server) {
		return 1
	}
	target := netip.AddrPortFrom(server, 53)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	control, socket, e := openUDPAssociation(ctx, spec.Port, spec.Token)
	if e != nil {
		return 1
	}
	defer control.Close()
	defer socket.Close()
	for i := 0; i < 3; i++ {
		if !queryThroughAssociation(socket, target, 3*time.Second) {
			return 1
		}
		r.Queries++
	}
	r.AssociationReused = true
	rogue, e := net.DialUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)}, socket.RemoteAddr().(*net.UDPAddr))
	if e != nil {
		return 1
	}
	r.WrongSourceNoReply = !queryThroughAssociation(rogue, target, 250*time.Millisecond)
	rogue.Close()
	payload, _, e := dnsQuery()
	if e != nil {
		return 1
	}
	bad := udpFrame(target, payload)
	bad[2] = 1
	socket.SetDeadline(time.Now().Add(250 * time.Millisecond))
	socket.Write(bad)
	buffer := make([]byte, 1400)
	_, e = socket.Read(buffer)
	r.InvalidFramesNoReply = e != nil
	if spec.Hold {
		if json.NewEncoder(output).Encode(struct {
			Stage string `json:"stage"`
		}{"UdpClientReady"}) != nil {
			return 1
		}
		var command struct{ Continue bool }
		if decoder.Decode(&command) != nil || !command.Continue {
			return 1
		}
		r.HeldOwnerStop = true
	} else {
		control.Close()
		time.Sleep(150 * time.Millisecond)
	}
	r.NoReplyAfterClose = !queryThroughAssociation(socket, target, 700*time.Millisecond)
	r.Success = r.Queries == 3 && r.AssociationReused && r.WrongSourceNoReply && r.InvalidFramesNoReply && r.NoReplyAfterClose
	if !r.Success {
		return 1
	}
	r.Stage = "VerifiedUdpAssociationLifecycle"
	return 0
}
