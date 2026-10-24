package main

import (
	"context"
	"os"
	"os/exec"
	"testing"
)

func TestOwnerRestartBudgetAndStopFence(t *testing.T) {
	o := &proxyOwner{}
	ctx, cancel := context.WithCancel(context.Background())
	if !o.launchAllowed(ctx) || !o.launchAllowed(ctx) || o.launchAllowed(ctx) {
		t.Fatal("unbounded child restart")
	}
	if o.snapshot("Snapshot").Restarts != 2 {
		t.Fatal("lost budget")
	}
	cancel()
	child := &ownedProxy{cmd: &exec.Cmd{Process: &os.Process{Pid: 123}}}
	if o.publish(ctx, child) || o.child != nil || o.status.Epoch != 0 {
		t.Fatal("canceled owner published a child")
	}
	if o.launchAllowed(ctx) {
		t.Fatal("stop restarted owner")
	}
}
func TestOwnerEpochIsSeparateFromProfileGeneration(t *testing.T) {
	o := &proxyOwner{status: ownerStatus{Session: "a", Generation: 17}}
	ctx := context.Background()
	for i := 1; i <= 2; i++ {
		child := &ownedProxy{cmd: &exec.Cmd{Process: &os.Process{Pid: 100 + i}}}
		if !o.publish(ctx, child) {
			t.Fatal("publication failed")
		}
		s := o.snapshot("Snapshot")
		if s.Epoch != i || s.Session != "a" || s.Generation != 17 {
			t.Fatal("profile identity crossed restart")
		}
		o.clear()
		if o.snapshot("Snapshot").ChildPID != 0 {
			t.Fatal("dead child remains published")
		}
	}
}
