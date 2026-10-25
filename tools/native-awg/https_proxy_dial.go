package main

import (
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"time"
)

type controlSocksReply byte

func (controlSocksReply) Error() string { return "anonymous probe SOCKS refusal" }

var errControlIdentity = errors.New("anonymous probe identity refused")

// Fixed anonymous endpoints only. Unlike generic SOCKS wrappers, retain typed
// I/O causes so an EOF/reset is distinguishable from a proxy policy refusal.
func dialAnonymousProxy(ctx context.Context, port int, token, network, address string) (net.Conn, error) {
	if network != "tcp" && network != "tcp4" || address != "1.1.1.1:443" && address != "www.cloudflare.com:443" {
		return nil, errControlIdentity
	}
	c, e := (&net.Dialer{Timeout: 5 * time.Second}).DialContext(ctx, "tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if e != nil {
		return nil, e
	}
	closed := true
	defer func() {
		if closed {
			c.Close()
		}
	}()
	cancel := context.AfterFunc(ctx, func() { c.Close() })
	defer cancel()
	until := time.Now().Add(5 * time.Second)
	if deadline, yes := ctx.Deadline(); yes && deadline.Before(until) {
		until = deadline
	}
	if e = c.SetDeadline(until); e != nil {
		return nil, e
	}
	method := byte(0)
	if token != "" {
		method = 2
	}
	if _, e = c.Write([]byte{5, 1, method}); e != nil {
		return nil, e
	}
	var hello [2]byte
	if _, e = io.ReadFull(c, hello[:]); e != nil {
		return nil, e
	}
	if hello != [2]byte{5, method} {
		return nil, errControlIdentity
	}
	if method == 2 {
		auth := append([]byte{1, 4}, []byte("cita")...)
		auth = append(auth, 64)
		auth = append(auth, []byte(token)...)
		_, e = c.Write(auth)
		clear(auth)
		if e != nil {
			return nil, e
		}
		if _, e = io.ReadFull(c, hello[:]); e != nil {
			return nil, e
		}
		if hello != [2]byte{1, 0} {
			return nil, errControlIdentity
		}
	}
	host, _, _ := net.SplitHostPort(address)
	request := []byte{5, 1, 0}
	if ip, e := netip.ParseAddr(host); e == nil {
		request = append(request, 1)
		v := ip.As4()
		request = append(request, v[:]...)
	} else {
		request = append(request, 3, byte(len(host)))
		request = append(request, host...)
	}
	request = append(request, 1, 187)
	if _, e = c.Write(request); e != nil {
		return nil, e
	}
	var reply [4]byte
	if _, e = io.ReadFull(c, reply[:]); e != nil {
		return nil, e
	}
	if reply[0] != 5 || reply[2] != 0 {
		return nil, errControlIdentity
	}
	if reply[1] != 0 {
		return nil, controlSocksReply(reply[1])
	}
	length := 0
	switch reply[3] {
	case 1:
		length = 4
	case 4:
		length = 16
	case 3:
		var n [1]byte
		if _, e = io.ReadFull(c, n[:]); e != nil {
			return nil, e
		}
		length = int(n[0])
		if length == 0 {
			return nil, errControlIdentity
		}
	default:
		return nil, errControlIdentity
	}
	var bound [257]byte
	if _, e = io.ReadFull(c, bound[:length+2]); e != nil {
		return nil, e
	}
	if e = c.SetDeadline(time.Time{}); e != nil {
		return nil, e
	}
	closed = false
	return c, nil
}
