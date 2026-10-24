package socks

import (
	M "github.com/sagernet/sing/common/metadata"
	"net"
	"strings"
	"testing"
	"time"
)

func TestCitaUDPLifetimeControlEOF(t *testing.T) {
	data, peer := net.Pipe()
	defer peer.Close()
	control, owner := net.Pipe()
	defer owner.Close()
	lease := newCitaLifetimeConn(data, control)
	defer lease.Close()
	result := make(chan error, 1)
	go func() { var b [1]byte; _, e := lease.Read(b[:]); result <- e }()
	owner.Close()
	select {
	case e := <-result:
		if e == nil {
			t.Fatal("dead control left UDP reader alive")
		}
	case <-time.After(time.Second):
		t.Fatal("UDP not invalidated")
	}
	select {
	case <-lease.done:
	case <-time.After(time.Second):
		t.Fatal("watcher leak")
	}
}
func TestCitaUDPLifetimeCloseAndIsolation(t *testing.T) {
	data, peer := net.Pipe()
	defer peer.Close()
	control, owner := net.Pipe()
	defer owner.Close()
	independent, other := net.Pipe()
	defer independent.Close()
	defer other.Close()
	lease := newCitaLifetimeConn(data, control)
	for i := 0; i < 3; i++ {
		if lease.Close() != nil {
			t.Fatal("close not idempotent")
		}
	}
	select {
	case <-lease.done:
	case <-time.After(time.Second):
		t.Fatal("watcher survived explicit close")
	}
	go func() { other.Write([]byte{7}) }()
	independent.SetReadDeadline(time.Now().Add(time.Second))
	var b [1]byte
	if n, e := independent.Read(b[:]); e != nil || n != 1 || b[0] != 7 {
		t.Fatal("unrelated connection closed")
	}
}
func TestCitaUDPLifetimeScope(t *testing.T) {
	t.Setenv("CITA_NATIVE_UDP_LIFETIME_LAB", "1")
	valid := func() *Client {
		return &Client{version: Version5, serverAddr: M.ParseSocksaddr("127.0.0.1:20888"), username: "cita", password: strings.Repeat("a", 64)}
	}
	if e := valid().EnableCitaUDPLifetime(); e != nil {
		t.Fatal(e)
	}
	for _, change := range []func(*Client){func(c *Client) { c.version = Version4 }, func(c *Client) { c.serverAddr = M.ParseSocksaddr("1.1.1.1:20888") }, func(c *Client) { c.username = "other" }, func(c *Client) { c.password = "bad" }} {
		c := valid()
		change(c)
		if c.EnableCitaUDPLifetime() == nil || c.citaUDPLifetime {
			t.Fatal("scope escape")
		}
	}
	t.Setenv("CITA_NATIVE_UDP_LIFETIME_LAB", "")
	if valid().EnableCitaUDPLifetime() == nil {
		t.Fatal("implicit enable")
	}
}
