package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

type sample struct {
	Phase        string  `json:"phase"`
	Sent         int     `json:"sent"`
	Received     int     `json:"received"`
	Late         int     `json:"lateOver250Ms"`
	MaxRTT       float64 `json:"maxRttMs"`
	P95          float64 `json:"p95RttMs"`
	P99          float64 `json:"p99RttMs"`
	MaxGap       float64 `json:"maxReceiveGapMs"`
	MaxLossBurst int     `json:"maxConsecutiveMissing"`
	StreamBytes  int64   `json:"streamBytes"`
}

type streamResult struct {
	phase        int
	bytes        int64
	received     int64
	ack          bool
	reason       string
	startDelayMs int64
	ackWaitMs    int64
}

func validEcho(packet []byte, token []byte) bool {
	return len(packet) == 224 && len(token) == 64 && bytes.Equal(packet[:64], token)
}

func readFailureReason(err error) string {
	if err == nil {
		return ""
	}
	if err == io.EOF {
		return "EOF"
	}
	if err == io.ErrUnexpectedEOF {
		return "Truncated"
	}
	if ne, ok := err.(net.Error); ok && ne.Timeout() {
		return "Timeout"
	}
	return "Network"
}

// Missing means no echo by the end of the observation, not one-way loss.
func measureSample(sent, recv []time.Time, phase string) sample {
	s := sample{Phase: phase}
	var rtts []float64
	var arrivals []time.Time
	burst := 0
	for i := range sent {
		if sent[i].IsZero() {
			burst = 0
			continue
		}
		s.Sent++
		if recv[i].IsZero() {
			burst++
			if burst > s.MaxLossBurst {
				s.MaxLossBurst = burst
			}
			continue
		}
		burst = 0
		s.Received++
		rtt := float64(recv[i].Sub(sent[i]).Microseconds()) / 1000
		rtts = append(rtts, rtt)
		arrivals = append(arrivals, recv[i])
		if rtt > 250 {
			s.Late++
		}
		if rtt > s.MaxRTT {
			s.MaxRTT = rtt
		}
	}
	sort.Float64s(rtts)
	if len(rtts) > 0 {
		s.P95 = rtts[(len(rtts)-1)*95/100]
		s.P99 = rtts[(len(rtts)-1)*99/100]
	}
	sort.Slice(arrivals, func(i, j int) bool { return arrivals[i].Before(arrivals[j]) })
	for i := 1; i < len(arrivals); i++ {
		gap := float64(arrivals[i].Sub(arrivals[i-1]).Microseconds()) / 1000
		if gap > s.MaxGap {
			s.MaxGap = gap
		}
	}
	return s
}

// Lab threshold only: round-trip echo, not Discord or one-way packet loss.
func evaluateUDP(sent, recv []time.Time) (bool, []bool) {
	if len(sent) != 8000 || len(recv) != len(sent) {
		return false, nil
	}
	result := make([]bool, 4)
	all := true
	for phase := range result {
		s := measureSample(sent[phase*2000:(phase+1)*2000], recv[phase*2000:(phase+1)*2000], "")
		result[phase] = s.Sent == 2000 && s.Received*100 >= s.Sent*95
		all = all && result[phase]
	}
	return all, result
}

func smoke(addr string, token string, output string) int {
	result := map[string]any{"success": false, "stage": "Dial"}
	defer func() {
		if f, e := os.Create(output); e == nil {
			defer f.Close()
			_ = json.NewEncoder(f).Encode(result)
		}
	}()
	c, e := net.DialTimeout("tcp4", addr, 6*time.Second)
	if e != nil {
		return 1
	}
	defer c.Close()
	_ = c.SetDeadline(time.Now().Add(12 * time.Second))
	result["stage"] = "AuthWrite"
	if _, e = c.Write(append([]byte(token), 'U', '0')); e != nil {
		return 1
	}
	result["stage"] = "PayloadWrite"
	payload := make([]byte, 262144)
	frame := make([]byte, 4+len(payload))
	binary.BigEndian.PutUint32(frame, uint32(len(payload)))
	copy(frame[4:], payload)
	written, e := io.Copy(c, bytes.NewReader(frame))
	n := written - 4
	if n < 0 {
		n = 0
	}
	result["sentBytes"] = n
	if e != nil || n != int64(len(payload)) {
		return 1
	}
	result["stage"] = "EndWrite"
	if _, e = c.Write([]byte{0, 0, 0, 0}); e != nil {
		return 1
	}
	result["stage"] = "AckRead"
	var reply [8]byte
	if _, e = io.ReadFull(c, reply[:]); e != nil {
		result["reason"] = readFailureReason(e)
		return 1
	}
	got := binary.BigEndian.Uint64(reply[:])
	result["receivedBytes"] = got
	if got != uint64(n) {
		result["stage"] = "AckMismatch"
		return 1
	}
	result["stage"] = "AckOk"
	result["success"] = true
	return 0
}

// Isolated TCP load: no UDP sender or preceding load phases.
func dialProxyTarget(addr string, proxyPort int, proxyToken string) (net.Conn, error) {
	host, portText, err := net.SplitHostPort(addr)
	if err != nil {
		return nil, err
	}
	ip := net.ParseIP(host).To4()
	port, err := strconv.Atoi(portText)
	if ip == nil || err != nil || port < 1 || port > 65535 || proxyPort < 1 || proxyPort > 65535 || len(proxyToken) != 64 {
		return nil, fmt.Errorf("invalid proxy target")
	}
	c, err := net.DialTimeout("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(proxyPort)), 6*time.Second)
	if err != nil {
		return nil, err
	}
	fail := func() (net.Conn, error) { c.Close(); return nil, fmt.Errorf("proxy handshake failed") }
	_ = c.SetDeadline(time.Now().Add(10 * time.Second))
	var reply [10]byte
	if _, err = c.Write([]byte{5, 1, 2}); err != nil {
		return fail()
	}
	if _, err = io.ReadFull(c, reply[:2]); err != nil || reply[0] != 5 || reply[1] != 2 {
		return fail()
	}
	auth := append([]byte{1, 4, 'c', 'i', 't', 'a', 64}, []byte(proxyToken)...)
	if _, err = c.Write(auth); err != nil {
		return fail()
	}
	if _, err = io.ReadFull(c, reply[:2]); err != nil || reply[0] != 1 || reply[1] != 0 {
		return fail()
	}
	req := append([]byte{5, 1, 0, 1}, ip...)
	req = append(req, byte(port>>8), byte(port))
	if _, err = c.Write(req); err != nil {
		return fail()
	}
	if _, err = io.ReadFull(c, reply[:]); err != nil || reply[0] != 5 || reply[1] != 0 || reply[3] != 1 {
		return fail()
	}
	_ = c.SetDeadline(time.Time{})
	return c, nil
}

func tcpOnly(addr, token, output string, proxyPort int, proxyToken string) int {
	result := map[string]any{"success": false, "stage": "Dial", "scope": "isolated 8 Mbps TCP upload; no UDP traffic"}
	defer func() {
		if f, err := os.Create(output); err == nil {
			defer f.Close()
			_ = json.NewEncoder(f).Encode(result)
		}
	}()
	var c net.Conn
	var err error
	if proxyPort != 0 {
		c, err = dialProxyTarget(addr, proxyPort, proxyToken)
		result["scope"] = "isolated 8 Mbps TCP upload through lab AWG proxy; no system TUN or UDP traffic"
	} else {
		c, err = net.DialTimeout("tcp4", addr, 6*time.Second)
	}
	if err != nil {
		return 1
	}
	defer c.Close()
	start := time.Now()
	_ = c.SetWriteDeadline(start.Add(39 * time.Second))
	result["stage"] = "AuthWrite"
	if _, err = c.Write(append([]byte(token), 'U', '3')); err != nil {
		return 1
	}
	block := make([]byte, 20004)
	binary.BigEndian.PutUint32(block, 20000)
	ticker := time.NewTicker(20 * time.Millisecond)
	defer ticker.Stop()
	until := time.NewTimer(time.Until(start.Add(38 * time.Second)))
	defer until.Stop()
	var sent int64
	var maxWriteMs int64
	var slowWrites int
	result["stage"] = "PayloadWrite"
	for {
		select {
		case <-until.C:
			result["sentBytes"] = sent
			result["maxWriteMs"] = maxWriteMs
			result["slowWritesOver20Ms"] = slowWrites
			result["stage"] = "EndWrite"
			if _, err = c.Write([]byte{0, 0, 0, 0}); err != nil {
				result["reason"] = readFailureReason(err)
				return 1
			}
			result["stage"] = "AckRead"
			ackStart := time.Now()
			_ = c.SetReadDeadline(ackStart.Add(15 * time.Second))
			var ack [8]byte
			_, err = io.ReadFull(c, ack[:])
			result["ackWaitMs"] = time.Since(ackStart).Milliseconds()
			if err != nil {
				result["reason"] = readFailureReason(err)
				return 1
			}
			received := int64(binary.BigEndian.Uint64(ack[:]))
			result["receivedBytes"] = received
			if received < 0 || received > sent {
				result["stage"] = "AckBounds"
				return 1
			}
			result["loadConfirmed"] = received*10 >= int64(8*1000000*38/8)*8
			result["success"] = result["loadConfirmed"]
			result["stage"] = "AckOk"
			if result["success"] == true {
				return 0
			}
			return 1
		case <-ticker.C:
			before := time.Now()
			n, writeErr := c.Write(block)
			duration := time.Since(before).Milliseconds()
			if duration > maxWriteMs {
				maxWriteMs = duration
			}
			if duration > 20 {
				slowWrites++
			}
			if writeErr != nil || n != len(block) {
				result["sentBytes"] = sent
				result["maxWriteMs"] = maxWriteMs
				result["slowWritesOver20Ms"] = slowWrites
				if writeErr != nil {
					result["reason"] = readFailureReason(writeErr)
				} else {
					result["reason"] = "ShortWrite"
				}
				return 1
			}
			sent += int64(n - 4)
		}
	}
}

func main() {
	if len(os.Args) == 3 && os.Args[1] == "--udp-only-proxy" {
		os.Exit(proxyMeasurement(os.Stdin, os.Args[2]))
	}
	if len(os.Args) != 3 && !(len(os.Args) == 4 && (os.Args[3] == "--smoke" || os.Args[3] == "--tcp-only" || os.Args[3] == "--proxy-tcp-only" || os.Args[3] == "--udp-only")) {
		os.Exit(2)
	}
	meta, e := os.ReadFile(os.Args[1])
	if e != nil {
		os.Exit(3)
	}
	parts := strings.Split(strings.TrimSpace(string(meta)), "|")
	if (len(parts) != 2 && !(len(parts) == 4 && len(os.Args) == 4 && os.Args[3] == "--proxy-tcp-only")) || len(parts[1]) != 64 {
		os.Exit(4)
	}
	addr := net.JoinHostPort(parts[0], "49214")
	if len(os.Args) == 4 {
		if os.Args[3] == "--udp-only" {
			os.Exit(udpOnly(addr, parts[1], os.Args[2]))
		}
		if os.Args[3] == "--proxy-tcp-only" {
			if len(parts) != 4 {
				os.Exit(4)
			}
			port, err := strconv.Atoi(parts[2])
			if err != nil || port < 1 || port > 65535 {
				os.Exit(4)
			}
			os.Exit(tcpOnly(addr, parts[1], os.Args[2], port, parts[3]))
		}
		if os.Args[3] == "--tcp-only" {
			os.Exit(tcpOnly(addr, parts[1], os.Args[2], 0, ""))
		}
		os.Exit(smoke(addr, parts[1], os.Args[2]))
	}
	u, e := net.Dial("udp4", addr)
	if e != nil {
		os.Exit(5)
	}
	defer u.Close()
	barrier := make([]byte, 224)
	copy(barrier, []byte(parts[1]))
	binary.LittleEndian.PutUint32(barrier[64:68], ^uint32(0))
	limit := time.Now().Add(50 * time.Second)
	ready := false
	for time.Now().Before(limit) {
		u.SetDeadline(time.Now().Add(500 * time.Millisecond))
		if _, e = u.Write(barrier); e != nil {
			break
		}
		buf := make([]byte, 256)
		n, e := u.Read(buf)
		if e == nil && validEcho(buf[:n], []byte(parts[1])) && binary.LittleEndian.Uint32(buf[64:68]) == ^uint32(0) {
			ready = true
			break
		}
	}
	if !ready {
		os.Exit(8)
	}
	u.SetDeadline(time.Time{})
	var mu sync.Mutex
	sent := make([]time.Time, 8000)
	recv := make([]time.Time, 8000)
	begin := time.Now()
	done := make(chan struct{})
	go func() {
		defer close(done)
		buf := make([]byte, 256)
		for {
			n, e := u.Read(buf)
			if e != nil {
				return
			}
			if !validEcho(buf[:n], []byte(parts[1])) {
				continue
			}
			id := int(binary.LittleEndian.Uint32(buf[64:68]))
			if id < 0 || id >= len(recv) {
				continue
			}
			mu.Lock()
			if recv[id].IsZero() {
				recv[id] = time.Now()
			}
			mu.Unlock()
		}
	}()
	streams := make(chan streamResult, 3)
	for phase := 1; phase <= 3; phase++ {
		go func(phase int) {
			phaseStart := begin.Add(time.Duration(phase) * 40 * time.Second)
			wait := time.Until(phaseStart)
			if wait > 0 {
				time.Sleep(wait)
			}
			startDelay := time.Since(phaseStart).Milliseconds()
			if startDelay > 2000 {
				streams <- streamResult{phase: phase, startDelayMs: startDelay, reason: "PhaseStartLate"}
				return
			}
			func() {
				c, e := net.DialTimeout("tcp4", addr, 5*time.Second)
				if e != nil {
					streams <- streamResult{phase: phase, reason: "Dial"}
					return
				}
				defer c.Close()
				c.SetWriteDeadline(phaseStart.Add(39 * time.Second))
				mode := byte('U')
				if _, e = c.Write(append([]byte(parts[1]), mode, byte('0'+phase))); e != nil {
					streams <- streamResult{phase: phase, reason: "AuthWrite"}
					return
				}
				var total int64
				if phase == 0 {
					total, _ = io.Copy(io.Discard, c)
				} else {
					block := make([]byte, 4+[]int{0, 5000, 10000, 20000}[phase])
					binary.BigEndian.PutUint32(block, uint32(len(block)-4))
					ticker := time.NewTicker(20 * time.Millisecond)
					defer ticker.Stop()
					until := time.After(time.Until(phaseStart.Add(38 * time.Second)))
					for {
						select {
						case <-until:
							result := streamResult{phase: phase, bytes: total, reason: "EndWrite", startDelayMs: startDelay}
							if _, err := c.Write([]byte{0, 0, 0, 0}); err == nil {
								result.reason = "AckRead"
								ackStart := time.Now()
								_ = c.SetReadDeadline(ackStart.Add(12 * time.Second))
								var reply [8]byte
								if _, err := io.ReadFull(c, reply[:]); err == nil {
									result.received = int64(binary.BigEndian.Uint64(reply[:]))
									result.ack = result.received >= 0 && result.received <= total
									if result.ack {
										result.reason = "AckOk"
									} else {
										result.reason = "AckBounds"
									}
								} else {
									result.reason = "AckRead" + readFailureReason(err)
								}
								result.ackWaitMs = time.Since(ackStart).Milliseconds()
							}
							streams <- result
							return
						case <-ticker.C:
							n, e := c.Write(block)
							if e == nil && n == len(block) {
								total += int64(n - 4)
							}
							if e != nil || n != len(block) {
								streams <- streamResult{phase: phase, bytes: total, reason: "PayloadWrite"}
								return
							}
						}
					}
				}
				streams <- streamResult{phase: phase, bytes: total}
			}()
		}(phase)
	}
	token := []byte(parts[1])
	sendFailed := false
	var maxPacingDelayMs float64
	var delayedSends int
	for i := range sent {
		due := begin.Add(time.Duration(i) * 20 * time.Millisecond)
		if wait := time.Until(due); wait > 0 {
			time.Sleep(wait)
		}
		delayMs := float64(time.Since(due).Microseconds()) / 1000
		if delayMs > maxPacingDelayMs {
			maxPacingDelayMs = delayMs
		}
		if delayMs > 20 {
			delayedSends++
		}
		packet := make([]byte, 224)
		copy(packet, token)
		binary.LittleEndian.PutUint32(packet[64:68], uint32(i))
		mu.Lock()
		sent[i] = time.Now()
		mu.Unlock()
		if _, e = u.Write(packet); e != nil {
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
	result := []sample{}
	for part := 0; part < 16; part++ {
		start, end := part*500, (part+1)*500
		s := measureSample(sent[start:end], recv[start:end], []string{"baseline", "upload2Mbps", "upload4Mbps", "upload8Mbps"}[part/4])
		result = append(result, s)
	}
	complete := true
	var bytesByPhase [4]int64
	var receivedByPhase [4]int64
	var ackByPhase [4]bool
	var reasonByPhase [4]string
	var startDelayByPhase [4]int64
	var ackWaitByPhase [4]int64
	timer := time.NewTimer(15 * time.Second)
	defer timer.Stop()
	for n := 0; n < 3; n++ {
		select {
		case r := <-streams:
			if r.phase < 1 || r.phase > 3 {
				complete = false
				continue
			}
			bytesByPhase[r.phase] = r.bytes
			receivedByPhase[r.phase] = r.received
			ackByPhase[r.phase] = r.ack
			reasonByPhase[r.phase] = r.reason
			startDelayByPhase[r.phase] = r.startDelayMs
			ackWaitByPhase[r.phase] = r.ackWaitMs
			for i := r.phase * 4; i < r.phase*4+4; i++ {
				result[i].StreamBytes = r.bytes
			}
		case <-timer.C:
			complete = false
			n = 3
		}
	}
	requested := [4]int64{0, 2, 4, 8}
	loadValid := make([]bool, 4)
	loadValid[0] = true
	for phase := 1; phase <= 3; phase++ {
		expected := requested[phase] * 1000000 * 38 / 8
		loadValid[phase] = ackByPhase[phase] && receivedByPhase[phase]*10 >= expected*8
	}
	measurementComplete := complete && !sendFailed && measureSample(sent, recv, "overall").Sent == 8000
	loadConfirmed := complete && loadValid[1] && loadValid[2] && loadValid[3]
	udpQuality, udpQualityByPhase := evaluateUDP(sent, recv)
	success := measurementComplete && loadConfirmed && udpQuality
	f, e := os.Create(os.Args[2])
	if e != nil {
		os.Exit(6)
	}
	defer f.Close()
	if e = json.NewEncoder(f).Encode(map[string]any{"success": success, "measurementComplete": measurementComplete, "loadConfirmed": loadConfirmed, "udpQualityPassed": udpQuality, "udpQualityCriteria": "phase reply ratio only; bursts and real call quality not assessed", "udpQualityByPhase": udpQualityByPhase, "udpMinimumReplyPercent": 95, "complete": complete, "sendFailed": sendFailed, "loadValid": loadValid, "bytesSentByPhase": bytesByPhase, "bytesReceivedByPhase": receivedByPhase, "ackByPhase": ackByPhase, "streamReasonByPhase": reasonByPhase, "phaseStartDelayMs": startDelayByPhase, "ackWaitMs": ackWaitByPhase, "durationSeconds": 160, "maxPacingDelayMs": maxPacingDelayMs, "delayedSendsOver20Ms": delayedSends, "overall": measureSample(sent, recv, "overall"), "samples": result}); e != nil {
		fmt.Fprintln(os.Stderr, "write failed")
		os.Exit(7)
	}
	if !success {
		os.Exit(9)
	}
}
