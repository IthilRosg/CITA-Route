package main

import (
	"encoding/binary"
	"encoding/json"
	"net"
	"os"
	"sync"
	"time"
)

// Fixed, bounded 60-second packet train with no background TCP load.
func udpOnly(addr, token, output string) int {
	return udpOnlyWithDial(addr, token, output, net.Dial)
}

func udpOnlyWithDial(addr, token, output string, dial func(string, string) (net.Conn, error)) int {
	report := map[string]any{"scope": "UDP only, 50 packets/s, 224 bytes, 60 seconds", "complete": false}
	defer func() {
		if f, e := os.Create(output); e == nil {
			defer f.Close()
			json.NewEncoder(f).Encode(report)
		}
	}()
	u, e := dial("udp4", addr)
	if e != nil {
		report["error"] = "Dial"
		return 1
	}
	defer u.Close()
	sent, received := make([]time.Time, 3000), make([]time.Time, 3000)
	var mu sync.Mutex
	var duplicates, invalid, reordered int
	last := -1
	done := make(chan struct{})
	u.SetReadDeadline(time.Now().Add(64 * time.Second))
	go func() {
		defer close(done)
		b := make([]byte, 512)
		for {
			n, e := u.Read(b)
			if e != nil {
				return
			}
			mu.Lock()
			if !validEcho(b[:n], []byte(token)) {
				invalid++
				mu.Unlock()
				continue
			}
			id := int(binary.LittleEndian.Uint32(b[64:68]))
			if id < 0 || id >= len(received) {
				invalid++
				mu.Unlock()
				continue
			}
			if !received[id].IsZero() {
				duplicates++
			} else {
				received[id] = time.Now()
				if id < last {
					reordered++
				}
				if id > last {
					last = id
				}
			}
			mu.Unlock()
		}
	}()
	start := time.Now()
	maxDelay := 0.0
	sendFailed := false
	for i := range sent {
		due := start.Add(time.Duration(i) * 20 * time.Millisecond)
		if d := time.Until(due); d > 0 {
			time.Sleep(d)
		}
		delay := float64(time.Since(due).Microseconds()) / 1000
		if delay > maxDelay {
			maxDelay = delay
		}
		b := make([]byte, 224)
		copy(b, token)
		binary.LittleEndian.PutUint32(b[64:68], uint32(i))
		mu.Lock()
		sent[i] = time.Now()
		mu.Unlock()
		if _, e = u.Write(b); e != nil {
			mu.Lock()
			sent[i] = time.Time{}
			mu.Unlock()
			sendFailed = true
			break
		}
	}
	time.Sleep(3 * time.Second)
	u.Close()
	<-done
	stats := measureSample(sent, received, "udp-only")
	report["overall"] = stats
	report["complete"] = !sendFailed && stats.Sent == 3000
	report["sendFailed"] = sendFailed
	report["duplicates"] = duplicates
	report["reordered"] = reordered
	report["invalid"] = invalid
	report["maxPacingDelayMs"] = maxDelay
	report["startedUtc"] = start.UTC().Format(time.RFC3339Nano)
	if sendFailed || stats.Sent != 3000 {
		return 1
	}
	return 0
}
