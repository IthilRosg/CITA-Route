package main

import (
	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"testing"
	"time"
)

type stubBind struct {
	conn.Bind
	sent int
}

func (b *stubBind) Send(bufs [][]byte, ep conn.Endpoint) error { b.sent++; return nil }
func (b *stubBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	return []conn.ReceiveFunc{func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) { sizes[0] = 12; return 1, nil }}, port, nil
}
func TestBoundedTransportLoss(t *testing.T) {
	underlying := &stubBind{}
	bind := newFaultBind(underlying)
	receives, _, e := bind.Open(7)
	if e != nil {
		t.Fatal(e)
	}
	sizes := make([]int, 1)
	bind.pause(time.Second)
	if bind.Send([][]byte{{1}}, nil) != nil || underlying.sent != 0 || bind.droppedSend.Load() != 1 {
		t.Fatal("Fault must drop below the encrypted transport without forwarding")
	}
	n, e := receives[0](nil, sizes, nil)
	if e != nil || n != 1 || sizes[0] != 0 || bind.droppedReceive.Load() != 1 {
		t.Fatal("Fault must suppress incoming datagrams")
	}
	bind.until.Store(0)
	if bind.Send([][]byte{{2}}, nil) != nil || underlying.sent != 1 {
		t.Fatal("Expired fault must resume original bind")
	}
	n, e = receives[0](nil, sizes, nil)
	if e != nil || n != 1 || sizes[0] != 12 || bind.opens.Load() != 1 {
		t.Fatal("Transport must resume without socket reopen")
	}
	bind.inboundOnly.Store(true)
	bind.pause(time.Second)
	if bind.Send([][]byte{{3}}, nil) != nil || underlying.sent != 2 || bind.droppedSend.Load() != 1 {
		t.Fatal("Incoming-only loss must preserve outgoing traffic")
	}
	n, e = receives[0](nil, sizes, nil)
	if e != nil || n != 1 || sizes[0] != 0 || bind.droppedReceive.Load() != 2 {
		t.Fatal("Incoming-only loss must suppress responses")
	}
}
