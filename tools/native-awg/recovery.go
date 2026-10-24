package main

import (
	"context"
	"sync"
	"time"
)

// Laboratory-only, per-owner budget. Healthy probes never replenish attempts.
// This repairs the encrypted socket, never replays application data or replaces
// the AWG device/netstack. Process death still needs an external owner.
type recoveryStatus struct {
	State     string `json:"state"`
	Failures  int    `json:"failures"`
	Attempts  int    `json:"attempts"`
	Successes int    `json:"successes"`
}
type transportRecovery struct {
	mu       sync.Mutex
	status   recoveryStatus
	verified bool
	pending  bool
}

func newTransportRecovery() *transportRecovery {
	return &transportRecovery{status: recoveryStatus{State: "Starting"}}
}
func (r *transportRecovery) snapshot() recoveryStatus {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.status
}
func (r *transportRecovery) observe(healthy bool) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.status.State == "Stopped" {
		return false
	}
	if healthy {
		r.verified = true
		r.status.Failures = 0
		r.status.State = "Healthy"
		if r.pending {
			r.status.Successes++
			r.pending = false
		}
		return false
	}
	r.status.Failures++
	if !r.verified {
		r.status.State = "Unverified"
		return false
	}
	r.status.State = "Suspect"
	if r.status.Failures < 2 {
		return false
	}
	if r.status.Attempts >= 2 {
		r.status.State = "Exhausted"
		return false
	}
	r.status.Attempts++
	r.status.Failures = 0
	r.status.State = "Repairing"
	r.pending = true
	return true
}
func (r *transportRecovery) repaired(ok bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.status.State == "Stopped" {
		return
	}
	if ok {
		r.status.State = "Verifying"
	} else {
		r.status.State = "RepairFailed"
		r.pending = false
	}
}
func (r *transportRecovery) stop() {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.status.State = "Stopped"
}
func runTransportRecovery(ctx context.Context, r *transportRecovery, probe func(context.Context) bool, repair func() error) {
	defer r.stop()
	delay := time.Duration(0)
	for {
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
		check, cancel := context.WithTimeout(ctx, 1500*time.Millisecond)
		healthy := probe(check)
		cancel()
		if ctx.Err() != nil {
			return
		}
		delay = time.Second
		if r.observe(healthy) {
			if ctx.Err() != nil {
				return
			}
			r.repaired(repair() == nil)
			delay = 2 * time.Second
		}
	}
}
