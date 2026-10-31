package socks

import (
	"context"
	"errors"
	"io"
	"net"
	"os"
	"sync"

	tf "github.com/sagernet/sing-box/common/tlsfragment"
)

// Experimental lab primitive only. The production policy rejects DPI until
// scoped probe, publication and connection-retirement acceptance all pass.
func nativeDPIConn(conn net.Conn, ctx context.Context) net.Conn {
	return &nativeDPIBufferedConn{Conn: conn, ctx: ctx}
}
func nativeDPIAllowed(network string, port uint16) bool {
	return network == "tcp" && port == 443 && os.Getenv("CITA_NATIVE_DPI_LAB") == "1"
}

// Application writes need not align with TLS records. Buffer exactly the
// first record before asking the existing fragmenter to process ClientHello.
type nativeDPIBufferedConn struct {
	net.Conn
	ctx     context.Context
	mu      sync.Mutex
	pending []byte
	ready   bool
	failed  bool
}

func (c *nativeDPIBufferedConn) Write(p []byte) (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.failed {
		return 0, errors.New("DPI initial TLS record refused")
	}
	if c.ready {
		return c.Conn.Write(p)
	}
	if len(c.pending)+len(p) > 65536 {
		c.failed = true
		return 0, errors.New("DPI initial write exceeds budget")
	}
	old := len(c.pending)
	c.pending = append(c.pending, p...)
	if len(c.pending) < 5 {
		return len(p), nil
	}
	size := int(c.pending[3])<<8 | int(c.pending[4])
	if c.pending[0] != 22 || size < 4 || size > 16384 {
		c.failed = true
		c.pending = nil
		return 0, errors.New("DPI initial TLS record invalid")
	}
	end := 5 + size
	if len(c.pending) < end {
		return len(p), nil
	}
	first, rest := c.pending[:end], c.pending[end:]
	if first[5] != 1 {
		c.failed = true
		c.pending = nil
		return 0, errors.New("DPI requires ClientHello")
	}
	// If SNI is absent or the ClientHello spans multiple records, this version
	// cannot confirm a meaningful split. Never silently report plain TLS as DPI.
	if tf.IndexTLSServerName(first) == nil {
		c.failed = true
		c.pending = nil
		return 0, errors.New("DPI cannot fragment this ClientHello")
	}
	// Packet splitting is a separate lab-only strategy. It keeps the TLS record
	// intact while splitting writes; OS/driver coalescing still requires VM proof.
	packetSplit := os.Getenv("CITA_NATIVE_DPI_STRATEGY") == "tcp"
	fragmented := tf.NewConn(c.Conn, c.ctx, packetSplit, !packetSplit, 0)
	n, e := fragmented.Write(first)
	if e == nil && n != len(first) {
		e = io.ErrShortWrite
	}
	if e == nil && len(rest) > 0 {
		n, e = c.Conn.Write(rest)
		if e == nil && n != len(rest) {
			e = io.ErrShortWrite
		}
	}
	c.pending = nil
	if e != nil {
		c.failed = true
		return max(0, min(len(p), end-old)), e
	}
	c.ready = true
	return len(p), nil
}
