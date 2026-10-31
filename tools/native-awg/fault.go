package main

import (
	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"sync/atomic"
	"time"
)

// Lab-only loss injection below encryption. Does not close/rebind the UDP
// socket or replace the AWG device/netstack. Monotonic time bounds the fault.
type faultBind struct {
	conn.Bind
	origin           time.Time
	until            atomic.Int64
	opens            atomic.Int64
	droppedSend      atomic.Int64
	droppedReceive   atomic.Int64
	inboundOnly      atomic.Bool
	port             atomic.Int64
	udpDials         atomic.Int64
	blackholePort    atomic.Int64
	recovery         *transportRecovery
	ownedControl     bool
	sendAttempts     atomic.Int64
	sendAccepted     atomic.Int64
	sendErrors       atomic.Int64
	receiveDatagrams atomic.Int64
	receiveErrors    atomic.Int64
}

func newFaultBind(bind conn.Bind) *faultBind { return &faultBind{Bind: bind, origin: time.Now()} }
func (b *faultBind) blocked() bool {
	return time.Since(b.origin).Nanoseconds() < b.until.Load() || b.blackholePort.Load() != 0 && b.blackholePort.Load() == b.port.Load()
}
func (b *faultBind) pause(duration time.Duration) {
	b.until.Store(time.Since(b.origin).Nanoseconds() + int64(duration))
}
func (b *faultBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	funcs, actual, e := b.Bind.Open(port)
	if e != nil {
		return funcs, actual, e
	}
	b.opens.Add(1)
	b.port.Store(int64(actual))
	wrapped := make([]conn.ReceiveFunc, len(funcs))
	for i, receive := range funcs {
		wrapped[i] = func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
			n, e := receive(packets, sizes, eps)
			if e != nil {
				b.receiveErrors.Add(1)
			}
			for j := 0; j < n && j < len(sizes); j++ {
				if sizes[j] > 0 {
					b.receiveDatagrams.Add(1)
				}
			}
			if b.blocked() {
				for j := 0; j < n; j++ {
					if sizes[j] > 0 {
						b.droppedReceive.Add(1)
						sizes[j] = 0
					}
				}
			}
			return n, e
		}
	}
	return wrapped, actual, nil
}
func (b *faultBind) Send(bufs [][]byte, ep conn.Endpoint) error {
	b.sendAttempts.Add(int64(len(bufs)))
	if b.blocked() && !b.inboundOnly.Load() {
		b.droppedSend.Add(int64(len(bufs)))
		return nil
	}
	e := b.Bind.Send(bufs, ep)
	if e != nil {
		b.sendErrors.Add(1)
	} else {
		b.sendAccepted.Add(int64(len(bufs)))
	}
	return e
}
