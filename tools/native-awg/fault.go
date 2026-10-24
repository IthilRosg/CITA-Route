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
	origin         time.Time
	until          atomic.Int64
	opens          atomic.Int64
	droppedSend    atomic.Int64
	droppedReceive atomic.Int64
	inboundOnly    atomic.Bool
}

func newFaultBind(bind conn.Bind) *faultBind { return &faultBind{Bind: bind, origin: time.Now()} }
func (b *faultBind) blocked() bool           { return time.Since(b.origin).Nanoseconds() < b.until.Load() }
func (b *faultBind) pause(duration time.Duration) {
	b.until.Store(time.Since(b.origin).Nanoseconds() + int64(duration))
}
func (b *faultBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	funcs, actual, e := b.Bind.Open(port)
	if e != nil {
		return funcs, actual, e
	}
	b.opens.Add(1)
	wrapped := make([]conn.ReceiveFunc, len(funcs))
	for i, receive := range funcs {
		wrapped[i] = func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
			n, e := receive(packets, sizes, eps)
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
	if b.blocked() && !b.inboundOnly.Load() {
		b.droppedSend.Add(int64(len(bufs)))
		return nil
	}
	return b.Bind.Send(bufs, ep)
}
