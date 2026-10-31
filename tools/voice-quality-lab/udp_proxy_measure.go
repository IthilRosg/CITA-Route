package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"time"
)

type proxyPacketConn struct {
	*packets
	control net.Conn
}

func (c *proxyPacketConn) Read(b []byte) (int, error)  { n, _, e := c.ReadFrom(b); return n, e }
func (c *proxyPacketConn) Write(b []byte) (int, error) { return c.WriteTo(b, c.target) }
func (c *proxyPacketConn) RemoteAddr() net.Addr        { return c.target }
func (c *proxyPacketConn) Close() error                { c.control.Close(); return c.packets.Close() }

func proxyMeasurement(input io.Reader, output string) int {
	var s struct {
		Port                              int
		ProxyToken, ReceiverToken, Target string
	}
	d := json.NewDecoder(io.LimitReader(input, 4096))
	d.DisallowUnknownFields()
	if d.Decode(&s) != nil || s.Port < 1 || s.Port > 65535 || (s.Target != "138.124.250.203:49214" && s.Target != "143.20.37.241:49214") {
		return 2
	}
	for _, token := range []string{s.ProxyToken, s.ReceiverToken} {
		b, e := hex.DecodeString(token)
		if e != nil || len(b) != 32 {
			return 2
		}
	}
	code := udpOnlyWithDial(s.Target, s.ReceiverToken, output, func(_, addr string) (net.Conn, error) {
		target, e := net.ResolveUDPAddr("udp4", addr)
		if e != nil {
			return nil, e
		}
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		udp, control, e := association(ctx, s.Port, s.ProxyToken, target)
		if e != nil {
			return nil, fmt.Errorf("authenticated association failed")
		}
		return &proxyPacketConn{udp, control}, nil
	})
	// A completed measurement is not a quality pass or proof of an installed TUN.
	if b, e := os.ReadFile(output); e == nil {
		var r map[string]any
		if json.Unmarshal(b, &r) == nil {
			r["transport"] = "authenticated SOCKS UDP to native owner; no OS UDP fallback"
			b, _ = json.Marshal(r)
			os.WriteFile(output, b, 0600)
		}
	}
	return code
}
