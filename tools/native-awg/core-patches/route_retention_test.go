package route

import (
	"context"
	"github.com/sagernet/sing-box/common/citaretain"
	"net"
	"strings"
	"sync/atomic"
	"testing"
)

type citaCountConn struct {
	net.Conn
	closed atomic.Int64
}

func (c *citaCountConn) Close() error { c.closed.Add(1); return nil }
func (c *citaCountConn) RemoteAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 20888}
}
func (c *citaCountConn) LocalAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 49152}
}
func TestCitaRetentionSelectiveResetAndShutdown(t *testing.T) {
	m := &ConnectionManager{}
	lease, e := citaretain.New("127.0.0.1", 20888, "5", "cita", strings.Repeat("a", 64), strings.Repeat("b", 32), 7, true)
	if e != nil {
		t.Fatal(e)
	}
	direct := &citaCountConn{}
	native := &citaCountConn{}
	directTracked := m.TrackConn(direct)
	nativeTracked := m.TrackConn(citaretain.Wrap(citaretain.WithLease(context.Background(), lease), "127.0.0.1:20888", native))
	m.CloseForInterfaceChange()
	if direct.closed.Load() != 1 || native.closed.Load() != 0 || m.Count() != 1 {
		t.Fatal("Selective reset failed")
	}
	m.CloseForInterfaceChange()
	if native.closed.Load() != 0 || m.Count() != 1 {
		t.Fatal("Repeated update lost native connection")
	}
	m.CloseAll()
	if native.closed.Load() != 1 || m.Count() != 0 {
		t.Fatal("Explicit stop failed to close native connection")
	}
	directTracked.Close()
	nativeTracked.Close()
	next := &citaCountConn{}
	m.TrackConn(citaretain.Wrap(citaretain.WithLease(context.Background(), lease), "127.0.0.1:20888", next))
	lease.Revoke()
	if next.closed.Load() != 1 {
		t.Fatal("Revocation did not immediately close owned socket")
	}
	m.CloseForInterfaceChange()
	if next.closed.Load() != 1 || m.Count() != 0 {
		t.Fatal("Revoked lease survived interface reset")
	}
}
