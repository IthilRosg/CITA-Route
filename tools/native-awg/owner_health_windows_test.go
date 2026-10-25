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

func TestPersistentOwnerUsesRollingSharedBudget(t *testing.T) {
	t.Setenv("CITA_NATIVE_PERSISTENT_CONTROL", "1")
	ctx := context.Background()
	child := &ownedProxy{}
	o := &proxyOwner{coordinated: true, child: child}
	for i := 0; i < 2; i++ {
		o.decideRepair(ctx, child, true)
		o.decideRepair(ctx, child, false)
		if !o.decideRepair(ctx, child, false) {
			t.Fatal("missing socket allowance")
		}
	}
	if !o.launchAllowed(ctx) || o.launchAllowed(ctx) {
		t.Fatal("shared burst cap")
	}
	// Age attempts without sleeping or resetting lifetime diagnostic counters.
	for i := range o.repairBudget.events {
		o.repairBudget.events[i].at = o.repairBudget.events[i].at.Add(-5 * time.Minute)
	}
	if !o.launchAllowed(ctx) || o.status.Restarts != 2 {
		t.Fatal("persistent owner did not age its allowance")
	}
	canceled, cancel := context.WithCancel(ctx)
	cancel()
	before := len(o.repairBudget.events)
	if o.launchAllowed(canceled) || len(o.repairBudget.events) != before {
		t.Fatal("Stop consumed repair or relaunched")
	}
}

func TestCoordinatedBudgetSurvivesChildReplacement(t *testing.T) {
	ctx := context.Background()
	first, second := &ownedProxy{}, &ownedProxy{}
	o := &proxyOwner{coordinated: true, child: first}
	o.decideRepair(ctx, first, true)
	if o.decideRepair(ctx, first, false) || !o.decideRepair(ctx, first, false) {
		t.Fatal("confirmation gate")
	}
	if !o.launchAllowed(ctx) {
		t.Fatal("restart permit missing")
	}
	o.mu.Lock()
	o.child = second
	o.verified = false
	o.failures = 0
	o.mu.Unlock()
	if o.decideRepair(ctx, first, false) || o.status.BudgetUsed != 2 {
		t.Fatal("stale epoch used budget")
	}
	if o.decideRepair(ctx, second, false) {
		t.Fatal("unverified child repaired")
	}
	o.decideRepair(ctx, second, true)
	o.decideRepair(ctx, second, false)
	if !o.decideRepair(ctx, second, false) || o.status.BudgetUsed != 3 || o.status.SocketRepairs != 2 || o.status.Restarts != 1 {
		t.Fatal("separate budgets reset after crash")
	}
	if o.launchAllowed(ctx) {
		t.Fatal("fourth operation accepted")
	}
	o.decideRepair(ctx, second, true)
	for i := 0; i < 10; i++ {
		if o.decideRepair(ctx, second, false) {
			t.Fatal("health replenished budget")
		}
	}
}
func TestCoordinatedStopDeniesRepair(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	child := &ownedProxy{}
	o := &proxyOwner{coordinated: true, child: child}
	o.decideRepair(ctx, child, true)
	o.decideRepair(ctx, child, false)
	cancel()
	if o.decideRepair(ctx, child, false) || o.status.BudgetUsed != 0 || o.launchAllowed(ctx) {
		t.Fatal("stop spent or relaunched")
	}
}
func TestOwnedChildHasLocalRepairCap(t *testing.T) {
	session := strings.Repeat("a", 32)
	var input, output bytes.Buffer
	var dials atomic.Int64
	for i := 0; i < 3; i++ {
		json.NewEncoder(&input).Encode(lossCommand{Session: session, Generation: 1, Action: "Rebind"})
	}
	bind := newFaultBind(&stubBind{})
	bind.ownedControl = true
	calls := 0
	proxyLossControl(&input, &output, session, 1, bind, &dials, func() error { calls++; return nil })
	if calls != 2 {
		t.Fatal("child repair cap escaped")
	}
}
