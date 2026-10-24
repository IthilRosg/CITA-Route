package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"time"
)

// One fixed destination and authenticated association. Never an OS UDP fallback.
type packets struct {
	udp    *net.UDPConn
	target *net.UDPAddr
}

func (p *packets) Close() error                       { return p.udp.Close() }
func (p *packets) LocalAddr() net.Addr                { return p.udp.LocalAddr() }
func (p *packets) SetDeadline(t time.Time) error      { return p.udp.SetDeadline(t) }
func (p *packets) SetReadDeadline(t time.Time) error  { return p.udp.SetReadDeadline(t) }
func (p *packets) SetWriteDeadline(t time.Time) error { return p.udp.SetWriteDeadline(t) }
func (p *packets) WriteTo(b []byte, a net.Addr) (int, error) {
	if len(b) > 1232 || a.String() != p.target.String() {
		return 0, fmt.Errorf("destination refused")
	}
	packet := make([]byte, 10+len(b))
	packet[3] = 1
	copy(packet[4:8], p.target.IP.To4())
	binary.BigEndian.PutUint16(packet[8:10], uint16(p.target.Port))
	copy(packet[10:], b)
	n, e := p.udp.Write(packet)
	if e != nil {
		return 0, e
	}
	if n != len(packet) {
		return 0, io.ErrShortWrite
	}
	return len(b), nil
}
func (p *packets) ReadFrom(b []byte) (int, net.Addr, error) {
	packet := make([]byte, 1243)
	for i := 0; i < 32; i++ {
		n, e := p.udp.Read(packet)
		if e != nil {
			return 0, nil, e
		}
		if n <= 10 || n > 1242 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 || packet[3] != 1 || !net.IP(packet[4:8]).Equal(p.target.IP) || int(binary.BigEndian.Uint16(packet[8:10])) != p.target.Port {
			continue
		}
		if n-10 > len(b) {
			return 0, nil, io.ErrShortBuffer
		}
		return copy(b, packet[10:n]), p.target, nil
	}
	return 0, nil, fmt.Errorf("invalid relay frames")
}
func association(ctx context.Context, port int, token string, target *net.UDPAddr) (*packets, net.Conn, error) {
	if len(token) != 64 || target.IP.To4() == nil {
		return nil, nil, fmt.Errorf("identity")
	}
	c, e := (&net.Dialer{}).DialContext(ctx, "tcp4", net.JoinHostPort("127.0.0.1", fmt.Sprint(port)))
	if e != nil {
		return nil, nil, e
	}
	fail := func() (*packets, net.Conn, error) { c.Close(); return nil, nil, fmt.Errorf("association") }
	c.SetDeadline(time.Now().Add(3 * time.Second))
	var reply [2]byte
	if _, e = c.Write([]byte{5, 1, 2}); e != nil {
		return fail()
	}
	if _, e = io.ReadFull(c, reply[:]); e != nil || reply != [2]byte{5, 2} {
		return fail()
	}
	auth := append([]byte{1, 4}, []byte("cita")...)
	auth = append(auth, 64)
	auth = append(auth, []byte(token)...)
	_, e = c.Write(auth)
	clear(auth)
	if e != nil {
		return fail()
	}
	if _, e = io.ReadFull(c, reply[:]); e != nil || reply != [2]byte{1, 0} {
		return fail()
	}
	if _, e = c.Write([]byte{5, 3, 0, 1, 0, 0, 0, 0, 0, 0}); e != nil {
		return fail()
	}
	var bound [10]byte
	if _, e = io.ReadFull(c, bound[:]); e != nil || bound[0] != 5 || bound[1] != 0 || bound[2] != 0 || bound[3] != 1 {
		return fail()
	}
	relay := &net.UDPAddr{IP: net.IP(bound[4:8]), Port: int(binary.BigEndian.Uint16(bound[8:10]))}
	if !relay.IP.IsLoopback() || relay.Port == 0 {
		return fail()
	}
	udp, e := net.DialUDP("udp4", nil, relay)
	if e != nil {
		return fail()
	}
	c.SetDeadline(time.Time{})
	return &packets{udp, target}, c, nil
}
