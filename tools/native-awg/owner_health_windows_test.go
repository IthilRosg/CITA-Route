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

func TestOwnerProtocolDegradationDoesNotRepairHealthySocket(t *testing.T){
 for _,test:=range []struct{https,udp bool;state string}{{true,true,"Reachable"},{true,false,"UdpDegraded"},{false,true,"HttpsDegraded"}}{
  child:=&ownedProxy{};o:=&proxyOwner{coordinated:true,child:child}
  evidence:=ownerProbeResult{HTTPS:test.https,UDP:test.udp,CompletedUnixMilliseconds:123}
  for i:=0;i<10;i++{if o.decideProbe(context.Background(),child,evidence){t.Fatal("partial protocol failure rebound working transport")}}
  state:=o.snapshot("Snapshot");if state.Health!=test.state||state.SocketRepairs!=0||state.BudgetUsed!=0||state.HealthProbe==nil||state.HealthProbe.HTTPS!=test.https||state.HealthProbe.UDP!=test.udp{t.Fatal("partial protocol health hidden or spent budget")}
 }
 child:=&ownedProxy{};o:=&proxyOwner{coordinated:true,child:child};ctx:=context.Background()
 o.decideProbe(ctx,child,ownerProbeResult{HTTPS:true,UDP:true})
 if o.decideProbe(ctx,child,ownerProbeResult{})||!o.decideProbe(ctx,child,ownerProbeResult{}){t.Fatal("two-total-failure repair gate changed")}
 old:=o.snapshot("Snapshot");o.decideProbe(ctx,child,ownerProbeResult{HTTPS:true});if old.HealthProbe.HTTPS{t.Fatal("old snapshot mutated by next probe")}
 before:=o.snapshot("Snapshot");if o.decideProbe(ctx,&ownedProxy{},ownerProbeResult{})||o.snapshot("Snapshot").BudgetUsed!=before.BudgetUsed{t.Fatal("stale child changed repair state")}
 canceled,cancel:=context.WithCancel(ctx);cancel();if o.decideProbe(canceled,child,ownerProbeResult{}){t.Fatal("Stop repaired transport")};o.state("Stopped");if o.snapshot("Snapshot").HealthProbe!=nil{t.Fatal("stopped owner retained live probe")}
}

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

func TestSocketRepairRetrySpacing(t *testing.T) {
 for _, persistent:=range []string{"", "1"} { t.Run("persistent="+persistent,func(t *testing.T){
  t.Setenv("CITA_NATIVE_PERSISTENT_CONTROL", persistent)
  child:=&ownedProxy{};o:=&proxyOwner{coordinated:true,child:child};ctx:=context.Background();at:=time.Unix(1000,0)
  o.decideRepairAt(ctx,child,true,at)
  if o.decideRepairAt(ctx,child,false,at)||!o.decideRepairAt(ctx,child,false,at){t.Fatal("first repair delayed")}
  for i:=1;i<80;i++ {if o.decideRepairAt(ctx,child,false,at.Add(time.Duration(i)*100*time.Millisecond)){t.Fatal("continuous outage repeated repair too early")}}
  if o.status.BudgetUsed!=1||o.status.SocketRepairs!=1{t.Fatal("deferred attempts spent budget")}
  if !o.decideRepairAt(ctx,child,false,at.Add(8*time.Second)){t.Fatal("bounded retry never resumed")}
  o.decideRepairAt(ctx,child,true,at.Add(8100*time.Millisecond))
  if o.decideRepairAt(ctx,child,false,at.Add(8200*time.Millisecond)){t.Fatal("new failure skipped confirmation")}
  allowed:=o.decideRepairAt(ctx,child,false,at.Add(8300*time.Millisecond))
  if allowed {t.Fatal("health reset shared socket cap")}
  fresh:=&proxyOwner{coordinated:true,child:child}
  fresh.decideRepairAt(ctx,child,true,at);fresh.decideRepairAt(ctx,child,false,at);fresh.decideRepairAt(ctx,child,false,at)
  fresh.decideRepairAt(ctx,child,true,at.Add(time.Second))
  fresh.decideRepairAt(ctx,child,false,at.Add(1100*time.Millisecond))
  if !fresh.decideRepairAt(ctx,child,false,at.Add(1200*time.Millisecond)){t.Fatal("new outage inherited retry delay")}
  canceled,cancel:=context.WithCancel(ctx);cancel()
  before:=o.status.BudgetUsed
  if o.decideRepairAt(canceled,child,false,at.Add(time.Minute))||o.decideRepairAt(ctx,&ownedProxy{},false,at.Add(time.Minute))||o.status.BudgetUsed!=before{t.Fatal("Stop or stale child spent budget")}
 }) }
}
func TestRetryWindowClearsOnChildReplacement(t *testing.T) {
 o:=&proxyOwner{coordinated:true,child:&ownedProxy{},nextSocketRepair:time.Now().Add(time.Hour)}
 o.clear()
 if !o.nextSocketRepair.IsZero(){t.Fatal("retired child retained retry window")}
}
