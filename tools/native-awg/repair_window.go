package main

import "time"

// Callers hold their controller mutex. Attempts age out by monotonic duration;
// healthy replies never replenish a crash-loop allowance.
type repairEvent struct {
	at   time.Time
	kind string
}
type repairWindow struct {
	events []repairEvent
	last   time.Time
}

func (w *repairWindow) acquire(now time.Time, kind string) bool {
	if !w.last.IsZero() && now.Before(w.last) {
		return false
	}
	w.last = now
	kept := w.events[:0]
	for _, e := range w.events {
		if now.Sub(e.at) < time.Hour {
			kept = append(kept, e)
		}
	}
	w.events = kept
	recent, same := 0, 0
	for _, e := range kept {
		if now.Sub(e.at) < 5*time.Minute {
			recent++
			if e.kind == kind {
				same++
			}
		}
	}
	if len(kept) >= 12 || recent >= 3 || same >= 2 {
		return false
	}
	w.events = append(w.events, repairEvent{now, kind})
	return true
}
