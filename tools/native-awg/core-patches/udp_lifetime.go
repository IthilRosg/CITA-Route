package socks

import (
	"encoding/hex"
	"errors"
	"net"
	"os"
	"sync"

	M "github.com/sagernet/sing/common/metadata"
)

// Explicit owned-loopback lab opt-in only. No change to ordinary SOCKS clients.
func (c *Client) EnableCitaUDPLifetime() error {
	token, e := hex.DecodeString(c.password)
	if os.Getenv("CITA_NATIVE_UDP_LIFETIME_LAB") != "1" || c.version != Version5 || c.serverAddr.Addr.String() != "127.0.0.1" || c.serverAddr.Port == 0 || c.username != "cita" || e != nil || len(token) != 32 {
		return errors.New("invalid native UDP lifetime scope")
	}
	c.citaUDPLifetime = true
	return nil
}

type citaLifetimeConn struct {
	net.Conn
	control net.Conn
	once    sync.Once
	done    chan struct{}
}

func newCitaLifetimeConn(data, control net.Conn) *citaLifetimeConn {
	c := &citaLifetimeConn{Conn: data, control: control, done: make(chan struct{})}
	go func() { defer close(c.done); var one [1]byte; control.Read(one[:]); c.Close() }()
	return c
}
func (c *citaLifetimeConn) Close() error {
	c.once.Do(func() { c.Conn.Close(); c.control.Close() })
	return nil
}
func newCitaAssociatePacketConn(data net.Conn, address M.Socksaddr, control net.Conn) *AssociatePacketConn {
	lease := newCitaLifetimeConn(data, control)
	// One idempotent lifetime owns both resources. Closing either PacketConn
	// layer wakes the control watcher; control EOF wakes the blocked UDP reader.
	return NewAssociatePacketConn(lease, address, lease)
}
