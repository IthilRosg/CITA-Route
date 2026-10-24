package socks

import (
	"context"
	"io"
	"net"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type bindingCountCloser struct{ count atomic.Int32 }

func (c *bindingCountCloser) Close() error { c.count.Add(1); return nil }
func testBinding() *nativeBinding {
	ctx, cancel := context.WithCancel(context.Background())
	return &nativeBinding{ctx: ctx, cancel: cancel, flows: make(map[io.Closer]bool)}
}
func TestCitaRetentionBindingGenerationAndScope(t *testing.T) {
	owner := strings.Repeat("a", 32)
	h := &CitaNativeBinding{owner: owner, state: "Closed", seen: map[string]bool{}, tokens: map[[32]byte]bool{}, makeBinding: func(bindingCommand) (*nativeBinding, error) { return testBinding(), nil }}
	cmd := bindingCommand{Owner: owner, Action: "Quarantine", Session: strings.Repeat("b", 32), Generation: 1}
	if !h.apply(cmd).Success {
		t.Fatal("quarantine rejected")
	}
	cmd.Action = "Activate"
	cmd.Port = 12345
	cmd.Token = strings.Repeat("c", 64)
	if !h.apply(cmd).Success {
		t.Fatal("activation rejected")
	}
	old := h.active
	tcp, udp := &bindingCountCloser{}, &bindingCountCloser{}
	old.register(tcp, false)
	old.register(udp, true)
	next := bindingCommand{Owner: owner, Action: "Quarantine", Session: strings.Repeat("d", 32), Generation: 2}
	ack := h.apply(next)
	if !ack.Success || ack.TCPClosed != 1 || ack.UDPClosed != 1 || h.active != nil || tcp.count.Load() != 1 || udp.count.Load() != 1 {
		t.Fatal("targeted revocation failed")
	}
	if h.apply(cmd).Success {
		t.Fatal("old activation crossed quarantine")
	}
	next.Action = "Activate"
	next.Port = 12346
	next.Token = strings.Repeat("e", 64)
	if !h.apply(next).Success {
		t.Fatal("second activation")
	}
	current := h.active
	cmd.Action = "Close"
	cmd.Token = ""
	cmd.Port = 0
	if h.apply(cmd).Success || h.active != current {
		t.Fatal("old Stop changed new binding")
	}
	next.Action = "Close"
	next.Port = 0
	next.Token = ""
	if !h.apply(next).Success || h.active != nil {
		t.Fatal("close failed")
	}
	next.Action = "Activate"
	next.Port = 12346
	next.Token = strings.Repeat("f", 64)
	if h.apply(next).Success {
		t.Fatal("closed generation resurrected")
	}
}
func TestCitaRetentionBindingInflightAndDirectIsolation(t *testing.T) {
	b := testBinding()
	b.revoke()
	local, peer := net.Pipe()
	defer peer.Close()
	c := &nativeBindingConn{Conn: local, b: b}
	done := make(chan bool, 1)
	go func() { done <- b.register(c, false) }()
	select {
	case admitted := <-done:
		if admitted {
			t.Fatal("late dial admitted")
		}
	case <-time.After(time.Second):
		t.Fatal("revoked registration deadlocked")
	}
	direct, other := net.Pipe()
	defer direct.Close()
	defer other.Close()
	go other.Write([]byte{1})
	direct.SetReadDeadline(time.Now().Add(time.Second))
	var one [1]byte
	if _, e := direct.Read(one[:]); e != nil {
		t.Fatal("unrelated direct connection closed")
	}
}
