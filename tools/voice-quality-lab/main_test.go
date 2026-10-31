package main

import (
	"bytes"
	"io"
	"net"
	"os"
	"testing"
	"time"
)

func TestProxyConnectAuthentication(t *testing.T) {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	result := make(chan error, 1)
	token := string(bytes.Repeat([]byte{'a'}, 64))
	go func() {
		c, err := listener.Accept()
		if err != nil {
			result <- err
			return
		}
		defer c.Close()
		buf := make([]byte, 3)
		if _, err = io.ReadFull(c, buf); err != nil {
			result <- err
			return
		}
		if !bytes.Equal(buf, []byte{5, 1, 2}) {
			result <- io.ErrUnexpectedEOF
			return
		}
		c.Write([]byte{5, 2})
		buf = make([]byte, 71)
		if _, err = io.ReadFull(c, buf); err != nil {
			result <- err
			return
		}
		if !bytes.Equal(buf[7:], []byte(token)) {
			result <- io.ErrUnexpectedEOF
			return
		}
		c.Write([]byte{1, 0})
		buf = make([]byte, 10)
		if _, err = io.ReadFull(c, buf); err != nil {
			result <- err
			return
		}
		if !bytes.Equal(buf[:4], []byte{5, 1, 0, 1}) {
			result <- io.ErrUnexpectedEOF
			return
		}
		c.Write([]byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0})
		result <- nil
	}()
	c, err := dialProxyTarget("203.0.113.9:49214", listener.Addr().(*net.TCPAddr).Port, token)
	if err != nil {
		t.Fatal(err)
	}
	c.Close()
	if err = <-result; err != nil {
		t.Fatal(err)
	}
	if _, err = dialProxyTarget("203.0.113.9:49214", listener.Addr().(*net.TCPAddr).Port, "bad"); err == nil {
		t.Fatal("short credential accepted")
	}
}

func TestReadFailureReasons(t *testing.T) {
	if readFailureReason(io.EOF) != "EOF" || readFailureReason(io.ErrUnexpectedEOF) != "Truncated" || readFailureReason(os.ErrDeadlineExceeded) != "Timeout" || readFailureReason(nil) != "" {
		t.Fatal("incorrect read failure classification")
	}
}

func TestEchoIdentity(t *testing.T) {
	token := bytes.Repeat([]byte{'a'}, 64)
	packet := make([]byte, 224)
	copy(packet, token)
	if !validEcho(packet, token) {
		t.Fatal("valid response rejected")
	}
	packet[0] = 'b'
	if validEcho(packet, token) {
		t.Fatal("wrong token accepted")
	}
	if validEcho(packet[:63], token) || validEcho(packet, token[:63]) {
		t.Fatal("invalid length accepted")
	}
}

func TestSampleLossAndReorderedArrivals(t *testing.T) {
	t0 := time.Unix(100, 0)
	sent := make([]time.Time, 6)
	recv := make([]time.Time, 6)
	for i := range sent {
		sent[i] = t0.Add(time.Duration(i) * 20 * time.Millisecond)
	}
	recv[0] = t0.Add(100 * time.Millisecond)
	recv[1] = t0.Add(50 * time.Millisecond)
	recv[4] = t0.Add(300 * time.Millisecond)
	s := measureSample(sent, recv, "test")
	if s.Sent != 6 || s.Received != 3 || s.MaxLossBurst != 2 || s.MaxGap != 200 || s.MaxRTT != 220 {
		t.Fatalf("unexpected sample: %+v", s)
	}
}

func TestOverallRetainsBurstAcrossSampleBoundary(t *testing.T) {
	t0 := time.Unix(100, 0)
	sent, recv := make([]time.Time, 1000), make([]time.Time, 1000)
	for i := range sent {
		sent[i] = t0.Add(time.Duration(i) * 20 * time.Millisecond)
		recv[i] = sent[i].Add(time.Millisecond)
	}
	for i := 498; i < 503; i++ {
		recv[i] = time.Time{}
	}
	if got := measureSample(sent, recv, "overall").MaxLossBurst; got != 5 {
		t.Fatalf("cross-boundary burst: got %d, want 5", got)
	}
}

func TestUDPQualityRejectsIncompleteAndBadPhases(t *testing.T) {
	t0 := time.Unix(100, 0)
	sent, recv := make([]time.Time, 8000), make([]time.Time, 8000)
	for i := range sent {
		sent[i] = t0.Add(time.Duration(i) * time.Millisecond)
		recv[i] = sent[i].Add(time.Millisecond)
	}
	if ok, _ := evaluateUDP(sent, recv); !ok {
		t.Fatal("complete replies refused")
	}
	for i := 2000; i < 2101; i++ {
		recv[i] = time.Time{}
	}
	if ok, phases := evaluateUDP(sent, recv); ok || phases[1] || !phases[0] {
		t.Fatal("loss in one phase was accepted")
	}
	if ok, _ := evaluateUDP(sent[:7999], recv[:7999]); ok {
		t.Fatal("incomplete run accepted")
	}
	sent[0] = time.Time{}
	if ok, _ := evaluateUDP(sent, recv); ok {
		t.Fatal("missing send accepted")
	}
}

// A loopback control validates the packet transport and sequence accounting.
// It does not estimate Internet-path loss or voice quality.
func TestUDPControlledLoopback(t *testing.T) {
	server, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	client, err := net.DialUDP("udp4", nil, server.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	done := make(chan error, 1)
	go func() {
		buf := make([]byte, 32)
		for i := 0; i < 20; i++ {
			server.SetReadDeadline(time.Now().Add(2 * time.Second))
			n, addr, e := server.ReadFromUDP(buf)
			if e != nil {
				done <- e
				return
			}
			if n != 1 || int(buf[0]) != i {
				done <- io.ErrUnexpectedEOF
				return
			}
			// Deterministically omit replies 4,5,6 and 19.
			if i >= 4 && i <= 6 || i == 19 {
				continue
			}
			if _, e = server.WriteToUDP(buf[:n], addr); e != nil {
				done <- e
				return
			}
		}
		done <- nil
	}()
	sent, received := make([]time.Time, 20), make([]time.Time, 20)
	for i := 0; i < 20; i++ {
		sent[i] = time.Now()
		if _, e := client.Write([]byte{byte(i)}); e != nil {
			t.Fatal(e)
		}
		if i >= 4 && i <= 6 || i == 19 {
			continue
		}
		client.SetReadDeadline(time.Now().Add(2 * time.Second))
		buf := make([]byte, 32)
		n, e := client.Read(buf)
		if e != nil {
			t.Fatal(e)
		}
		if n != 1 || int(buf[0]) != i {
			t.Fatal("wrong sequence echoed")
		}
		received[i] = time.Now()
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	result := measureSample(sent, received, "controlled loopback")
	if result.Sent != 20 || result.Received != 16 || result.MaxLossBurst != 3 {
		t.Fatalf("controlled omissions miscounted: %+v", result)
	}
}
