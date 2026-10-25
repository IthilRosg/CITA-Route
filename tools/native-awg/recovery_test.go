package main

import (
	"bytes"
	"context"
	"encoding/json"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestPersistentTransportAgesBudgetWithoutProbeReset(t *testing.T) {
	t.Setenv("CITA_NATIVE_PERSISTENT_CONTROL", "1")
	r := newTransportRecovery()
	for i := 0; i < 2; i++ {
		r.observe(true)
		r.observe(false)
		if !r.observe(false) {
			t.Fatal("missing bounded attempt")
		}
	}
	r.observe(true)
	r.observe(false)
	if r.observe(false) {
		t.Fatal("healthy probe reset allowance")
	}
	for i := range r.repairBudget.events {
		r.repairBudget.events[i].at = r.repairBudget.events[i].at.Add(-5 * time.Minute)
	}
	if !r.observe(false) || r.status.Attempts != 3 {
		t.Fatal("expired attempts prevented recovery")
	}
	r.stop()
	if r.observe(false) {
		t.Fatal("stopped recovery resurrected")
	}
}

func TestBlackholeRequiresRecoveryOwner(t *testing.T) {
	session := strings.Repeat("d", 32)
	command := lossCommand{Session: session, Generation: 7, Action: "Blackhole"}
	for _, enabled := range []bool{false, true} {
		bind := newFaultBind(&stubBind{})
		bind.port.Store(12345)
		if enabled {
			bind.recovery = newTransportRecovery()
		}
		var in, out bytes.Buffer
		var dials atomic.Int64
		json.NewEncoder(&in).Encode(command)
		proxyLossControl(&in, &out, session, 7, bind, &dials)
		if bind.blocked() != enabled || (out.Len() > 0) != enabled {
			t.Fatal("fault available outside explicit recovery lab")
		}
		bind.port.Store(12346)
		if bind.blocked() {
			t.Fatal("fault must apply only to original encrypted port")
		}
	}
}

func TestAutomaticRecoveryRefusesCompetingRebind(t *testing.T) {
	session := strings.Repeat("e", 32)
	var in, out bytes.Buffer
	json.NewEncoder(&in).Encode(lossCommand{Session: session, Generation: 1, Action: "Rebind"})
	bind := newFaultBind(&stubBind{})
	bind.recovery = newTransportRecovery()
	var dials atomic.Int64
	called := false
	proxyLossControl(&in, &out, session, 1, bind, &dials, func() error { called = true; return nil })
	if called || out.Len() != 0 {
		t.Fatal("competing repair writer accepted")
	}
}

func TestRecoveryBudgetAndConfirmation(t *testing.T) {
	r := newTransportRecovery()
	for i := 0; i < 10; i++ {
		if r.observe(false) {
			t.Fatal("unverified startup must not repair")
		}
	}
	r.observe(true)
	if r.observe(false) {
		t.Fatal("single failure")
	}
	r.observe(true)
	if r.observe(false) || !r.observe(false) {
		t.Fatal("two failures must repair")
	}
	r.repaired(true)
	if r.snapshot().State != "Verifying" || r.snapshot().Successes != 0 {
		t.Fatal("socket reopen is not recovery proof")
	}
	r.observe(true)
	if r.snapshot().Successes != 1 {
		t.Fatal("missing successful probe")
	}
	r.observe(false)
	if !r.observe(false) {
		t.Fatal("second bounded attempt")
	}
	r.repaired(false)
	if r.snapshot().State != "RepairFailed" {
		t.Fatal("repair error hidden")
	}
	r.observe(true)
	for i := 0; i < 20; i++ {
		if r.observe(false) {
			t.Fatal("budget replenished")
		}
	}
	if r.snapshot().State != "Exhausted" || r.snapshot().Attempts != 2 {
		t.Fatal("unbounded repair")
	}
	r.stop()
	if r.observe(true) || r.observe(false) || r.snapshot().State != "Stopped" {
		t.Fatal("stop resurrected")
	}
}
func TestRecoveryCancelDuringProbe(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	r := newTransportRecovery()
	r.observe(true)
	r.observe(false)
	repairs := 0
	runTransportRecovery(ctx, r, func(context.Context) bool { cancel(); return false }, func() error { repairs++; return nil })
	if repairs != 0 || r.snapshot().State != "Stopped" {
		t.Fatal("canceled owner repaired")
	}
}
func TestRecoverySessionsIndependent(t *testing.T) {
	a, b := newTransportRecovery(), newTransportRecovery()
	a.observe(true)
	a.observe(false)
	a.observe(false)
	if b.snapshot().Attempts != 0 || b.snapshot().State != "Starting" {
		t.Fatal("state crosses owner boundary")
	}
}
