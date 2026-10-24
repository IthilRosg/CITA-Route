// Experimental CITA native-AWG lease. Not part of production core builds.
package citaretain

import (
	"context"
	"encoding/hex"
	"errors"
	"io"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
)

type Lease struct {
	endpoint    string
	session     string
	generation  uint64
	revoked     atomic.Bool
	gate        sync.Mutex
	connections map[*retainedConn]struct{}
}
type leaseKey struct{}
type resetKey struct{}
type retainedConn struct {
	net.Conn
	lease      *Lease
	once       sync.Once
	closeError error
}

func New(server string, port uint16, version, username, password, session string, generation uint64, tcpOnly bool) (*Lease, error) {
	valid := func(value string, size int) bool { b, e := hex.DecodeString(value); return e == nil && len(b) == size }
	if server != "127.0.0.1" || port == 0 || version != "5" || username != "cita" || !valid(password, 32) || !valid(session, 16) || generation == 0 || !tcpOnly {
		return nil, errors.New("invalid native transport lease")
	}
	return &Lease{endpoint: net.JoinHostPort(server, strconv.Itoa(int(port))), session: session, generation: generation, connections: make(map[*retainedConn]struct{})}, nil
}
func (l *Lease) Revoke() {
	if l == nil {
		return
	}
	l.revoked.Store(true)
	l.gate.Lock()
	connections := make([]*retainedConn, 0, len(l.connections))
	for c := range l.connections {
		connections = append(connections, c)
	}
	l.gate.Unlock()
	for _, c := range connections {
		c.Close()
	}
}
func (c *retainedConn) Close() error {
	c.once.Do(func() {
		c.lease.gate.Lock()
		delete(c.lease.connections, c)
		c.lease.gate.Unlock()
		c.closeError = c.Conn.Close()
	})
	return c.closeError
}
func WithLease(ctx context.Context, l *Lease) context.Context {
	return context.WithValue(ctx, leaseKey{}, l)
}
func Wrap(ctx context.Context, destination string, c net.Conn) net.Conn {
	l, _ := ctx.Value(leaseKey{}).(*Lease)
	if l == nil || l.endpoint != destination {
		return c
	}
	if l.revoked.Load() {
		c.Close()
		return c
	}
	remote, ok := c.RemoteAddr().(*net.TCPAddr)
	if !ok || remote.IP.String() != "127.0.0.1" || remote.Port == 0 || remote.String() != l.endpoint {
		return c
	}
	local, ok := c.LocalAddr().(*net.TCPAddr)
	if !ok || !local.IP.IsLoopback() {
		return c
	}
	l.gate.Lock()
	if l.revoked.Load() {
		l.gate.Unlock()
		c.Close()
		return c
	}
	retained := &retainedConn{Conn: c, lease: l}
	l.connections[retained] = struct{}{}
	l.gate.Unlock()
	return retained
}
func Preserve(c io.Closer) bool { r, ok := c.(*retainedConn); return ok && !r.lease.revoked.Load() }

// Only the default-interface-update call is marked. Explicit reset, shutdown,
// OOM reset and suspend retain their original CloseAll behavior.
func InterfaceReset(ctx context.Context) context.Context {
	return context.WithValue(ctx, resetKey{}, true)
}
func IsInterfaceReset(ctx context.Context) bool { v, _ := ctx.Value(resetKey{}).(bool); return v }
