package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"sync/atomic"
	"testing"
)

func TestLossControlOwnerAndBudget(t *testing.T) {
	session := strings.Repeat("a", 32)
	good := lossCommand{Session: session, Generation: 9, Action: "Pause", Milliseconds: 3000}
	if !validLossCommand(good, session, 9, false) {
		t.Fatal("Authorized bounded fault rejected")
	}
	for _, change := range []func(*lossCommand){func(c *lossCommand) { c.Generation++ }, func(c *lossCommand) { c.Session = strings.Repeat("b", 32) }, func(c *lossCommand) { c.Milliseconds = 30000 }, func(c *lossCommand) { c.Action = "Restart" }} {
		c := good
		change(&c)
		if validLossCommand(c, session, 9, false) {
			t.Fatal("Invalid fault accepted")
		}
	}
	if validLossCommand(good, session, 9, true) {
		t.Fatal("Repeated fault allowed")
	}
	long := good
	long.Milliseconds = 10000
	if !validLossCommand(long, session, 9, false) {
		t.Fatal("Allowlisted long loss rejected")
	}
	for _, duration := range []int{-1, 0, 2999, 3001, 9999, 10001, 30000} {
		bad := good
		bad.Milliseconds = duration
		if validLossCommand(bad, session, 9, false) {
			t.Fatal("Unbounded duration accepted")
		}
	}
	bind := newFaultBind(&stubBind{})
	var dials atomic.Int64
	dials.Store(3)
	var input, output bytes.Buffer
	encoder := json.NewEncoder(&input)
	encoder.Encode(good)
	encoder.Encode(good)
	proxyLossControl(&input, &output, session, 9, bind, &dials)
	decoder := json.NewDecoder(&output)
	var snapshot lossSnapshot
	if decoder.Decode(&snapshot) != nil || snapshot.Stage != "Pause" || snapshot.Dials != 3 || !bind.blocked() {
		t.Fatal("Fault acknowledgement missing")
	}
	if decoder.Decode(&snapshot) == nil {
		t.Fatal("Second fault acknowledged")
	}
	// A mismatching owner cannot mutate transport or receive a snapshot.
	bind.until.Store(0)
	input.Reset()
	output.Reset()
	good.Generation++
	json.NewEncoder(&input).Encode(good)
	proxyLossControl(&input, &output, session, 9, bind, &dials)
	if bind.blocked() || output.Len() != 0 {
		t.Fatal("Stale owner affected live transport")
	}
}
func TestMigrationControlIsSingleUseAndOwned(t *testing.T) {
	session := strings.Repeat("c", 32)
	command := lossCommand{Session: session, Generation: 4, Action: "Rebind"}
	if !validLossCommand(command, session, 4, false) || validLossCommand(command, session, 5, false) || validLossCommand(command, session, 4, true) {
		t.Fatal("Migration owner/single-use boundary failed")
	}
	var input, output bytes.Buffer
	json.NewEncoder(&input).Encode(command)
	json.NewEncoder(&input).Encode(command)
	bind := newFaultBind(&stubBind{})
	bind.opens.Store(1)
	var calls, dials atomic.Int64
	proxyLossControl(&input, &output, session, 4, bind, &dials, func() error { calls.Add(1); bind.opens.Add(1); return nil })
	if calls.Load() != 1 {
		t.Fatal("Repeated rebind reached device")
	}
	var result lossSnapshot
	if json.NewDecoder(&output).Decode(&result) != nil || result.BindOpens != 2 {
		t.Fatal("Rebind acknowledgement preceded completion")
	}
}
func TestMigrationRefusesMissingOrFailedRebind(t *testing.T) {
	session := strings.Repeat("d", 32)
	c := lossCommand{Session: session, Generation: 2, Action: "Rebind"}
	var dials atomic.Int64
	for _, failed := range []bool{false, true} {
		var input, output bytes.Buffer
		json.NewEncoder(&input).Encode(c)
		bind := newFaultBind(&stubBind{})
		if failed {
			proxyLossControl(&input, &output, session, 2, bind, &dials, func() error { return errProxy })
		} else {
			proxyLossControl(&input, &output, session, 2, bind, &dials)
		}
		if output.Len() != 0 {
			t.Fatal("Unavailable/failed rebind acknowledged")
		}
	}
}

func TestShortLossDurationAllowlist(t *testing.T) {
	for _, ms := range []int{500, 2000, 3000, 10000} {
		c := lossCommand{Session: "fixture", Generation: 1, Action: "Pause", Milliseconds: ms}
		if !validLossDuration(ms) || !validLossCommand(c, "fixture", 1, false) || validLossCommand(c, "fixture", 1, true) {
			t.Fatalf("duration ownership boundary: %d", ms)
		}
	}
	for _, ms := range []int{-1, 0, 499, 501, 1999, 2001, 9999, 10001} {
		if validLossDuration(ms) {
			t.Fatalf("unapproved duration: %d", ms)
		}
	}
}
