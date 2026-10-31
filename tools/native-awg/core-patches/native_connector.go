package socks

import (
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"strings"
	"sync"
	"time"
)

var errNativeConnector = errors.New("private Zapret connector unavailable")

type nativeConnectorBoundConn struct{ *nativeBindingConn }

func (c *nativeConnectorBoundConn) CloseWrite() error {
	if connection, ok := c.Conn.(*nativeConnectorConn); ok {
		return connection.CloseWrite()
	}
	return errNativeConnector
}

type nativeConnectorConfig struct {
	Pipe, Epoch, Capability string
	ServerPid               uint32
}

func (c nativeConnectorConfig) valid() bool {
	return strings.HasPrefix(c.Pipe, "Cita.CoreConnector.") && len(c.Pipe) == 51 && validBindingHex(strings.TrimPrefix(c.Pipe, "Cita.CoreConnector."), 16) &&
		validBindingHex(c.Epoch, 16) && validBindingHex(c.Capability, 32) && c.ServerPid > 0
}

type nativeConnectorRequest struct {
	Epoch, Capability, Nonce     string
	IssuedUnixMilliseconds       int64
	Domain, Application, Address string
	Port                         uint16
	Network                      string
}

func nativeConnectorHandshake(ctx context.Context, conn net.Conn, config nativeConnectorConfig, domain, application, address string, port uint16) (net.Conn, error) {
	fail := func() (net.Conn, error) { conn.Close(); return nil, errNativeConnector }
	d, ok := nativeDomain(domain)
	if !ok {
		return fail()
	}
	app, ok := nativeApplication(application)
	if !ok || app == "" || !config.valid() || port != 443 {
		return fail()
	}
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		return fail()
	}
	request := nativeConnectorRequest{config.Epoch, config.Capability, hex.EncodeToString(nonce), time.Now().UnixMilli(), d, app, address, port, "tcp"}
	payload, err := json.Marshal(request)
	if err != nil || len(payload) > 4096 {
		return fail()
	}
	deadline := time.Now().Add(5 * time.Second)
	if limit, ok := ctx.Deadline(); ok && limit.Before(deadline) {
		deadline = limit
	}
	if conn.SetDeadline(deadline) != nil {
		return fail()
	}
	stop := context.AfterFunc(ctx, func() { conn.Close() })
	header := make([]byte, 4)
	binary.BigEndian.PutUint32(header, uint32(len(payload)))
	if nativeConnectorWrite(conn, header) != nil || nativeConnectorWrite(conn, payload) != nil {
		stop()
		return fail()
	}
	ack := make([]byte, 1)
	if _, err = io.ReadFull(conn, ack); err != nil || ack[0] != 0 {
		stop()
		return fail()
	}
	// No cancellation callback may race detachment and return a closed socket
	// as a successful dial. An in-progress callback makes this attempt fail.
	if !stop() || ctx.Err() != nil {
		return fail()
	}
	if conn.SetDeadline(time.Time{}) != nil {
		return fail()
	}
	return &nativeConnectorConn{Conn: conn}, nil
}
func nativeConnectorWrite(w io.Writer, p []byte) error {
	for len(p) > 0 {
		n, e := w.Write(p)
		if e != nil {
			return e
		}
		if n <= 0 || n > len(p) {
			return io.ErrShortWrite
		}
		p = p[n:]
	}
	return nil
}

// Application TLS bytes stay encrypted. Length zero is a directional EOF,
// not a request to close both halves of the proxied TCP connection.
type nativeConnectorConn struct {
	net.Conn
	readMu, writeMu                      sync.Mutex
	remaining                            uint32
	readClosed, writeClosed, writeFailed bool
}

func (c *nativeConnectorConn) Read(p []byte) (int, error) {
	c.readMu.Lock()
	defer c.readMu.Unlock()
	if len(p) == 0 {
		return 0, nil
	}
	if c.readClosed {
		return 0, io.EOF
	}
	if c.remaining == 0 {
		header := make([]byte, 4)
		if _, e := io.ReadFull(c.Conn, header); e != nil {
			return 0, e
		}
		c.remaining = binary.BigEndian.Uint32(header)
		if c.remaining > 16384 {
			c.Conn.Close()
			return 0, errNativeConnector
		}
		if c.remaining == 0 {
			c.readClosed = true
			return 0, io.EOF
		}
	}
	if uint32(len(p)) > c.remaining {
		p = p[:c.remaining]
	}
	n, e := c.Conn.Read(p)
	c.remaining -= uint32(n)
	if e == io.EOF && c.remaining != 0 {
		return n, io.ErrUnexpectedEOF
	}
	return n, e
}
func (c *nativeConnectorConn) Write(p []byte) (total int, err error) {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	if c.writeClosed || c.writeFailed {
		return 0, io.ErrClosedPipe
	}
	for len(p) > 0 {
		count := len(p)
		if count > 16384 {
			count = 16384
		}
		header := make([]byte, 4)
		binary.BigEndian.PutUint32(header, uint32(count))
		if e := nativeConnectorWrite(c.Conn, header); e != nil {
			c.writeFailed = true
			c.Conn.Close()
			return total, e
		}
		if e := nativeConnectorWrite(c.Conn, p[:count]); e != nil {
			c.writeFailed = true
			c.Conn.Close()
			return total, e
		}
		total += count
		p = p[count:]
	}
	return total, nil
}
func (c *nativeConnectorConn) CloseWrite() error {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	if c.writeFailed {
		return io.ErrClosedPipe
	}
	if c.writeClosed {
		return nil
	}
	c.writeClosed = true
	if err := nativeConnectorWrite(c.Conn, make([]byte, 4)); err != nil {
		c.writeFailed = true
		c.Conn.Close()
		return err
	}
	return nil
}
