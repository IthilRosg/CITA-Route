package main

import (
	"testing"
	"time"
)

func TestPersistentRepairWindow(t *testing.T) {
	var w repairWindow
	start := time.Now()
	for i := 0; i < 4; i++ {
		now := start.Add(time.Duration(i) * 5 * time.Minute)
		if !w.acquire(now, "socket") || !w.acquire(now, "socket") || w.acquire(now, "socket") || !w.acquire(now, "child") || w.acquire(now, "child") {
			t.Fatal("burst/category allowance")
		}
	}
	if w.acquire(start.Add(59*time.Minute), "socket") {
		t.Fatal("hourly cap")
	}
	if !w.acquire(start.Add(time.Hour), "socket") {
		t.Fatal("old attempts did not expire")
	}
	if w.acquire(start, "socket") {
		t.Fatal("backwards clock")
	}
	if len(w.events) > 12 {
		t.Fatal("unbounded history")
	}
}
