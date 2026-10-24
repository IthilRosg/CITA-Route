package main

import (
	"bytes"
	"crypto/tls"
	"encoding/json"
	"net"
	"testing"
	"time"
)

func testMaterial(t *testing.T) spec {
	t.Helper()
	var b bytes.Buffer
	if prepare(&b) != nil {
		t.Fatal("certificate generation")
	}
	var s spec
	if json.Unmarshal(b.Bytes(), &s) != nil {
		t.Fatal("certificate envelope")
	}
	return s
}
func TestMutualTLSRequiresPrivateFixtureCredential(t *testing.T) {
	s := testMaterial(t)
	for _, withClient := range []bool{true, false} {
		a, b := net.Pipe()
		a.SetDeadline(time.Now().Add(time.Second))
		b.SetDeadline(time.Now().Add(time.Second))
		serverConf, e := material(s, true)
		if e != nil {
			t.Fatal("server material")
		}
		clientConf, e := material(s, false)
		if e != nil {
			t.Fatal("client material")
		}
		if !withClient {
			clientConf.Certificates = nil
		}
		server, client := tls.Server(a, serverConf), tls.Client(b, clientConf)
		done := make(chan error, 1)
		go func() { done <- server.Handshake() }()
		_ = client.Handshake()
		e = <-done
		a.Close()
		b.Close()
		if withClient && e != nil || !withClient && e == nil {
			t.Fatal("mutual TLS authorization mismatch")
		}
	}
}
func TestTransferEvidenceRejectsTruncationAndCorruption(t *testing.T) {
	good := payload()
	if !readStream("fixture", bytes.NewReader(good), 1, time.Now()).Success {
		t.Fatal("valid payload")
	}
	if readStream("fixture", bytes.NewReader(good[:len(good)-1]), 1, time.Now()).Success {
		t.Fatal("truncated accepted")
	}
	good[99] ^= 1
	if readStream("fixture", bytes.NewReader(good), 1, time.Now()).Success {
		t.Fatal("corruption accepted")
	}
}
func TestPacketAdapterRejectsUnownedTargetAndOversize(t *testing.T) {
	p := packets{target: &net.UDPAddr{IP: net.IPv4(203, 0, 113, 1), Port: 49197}}
	if _, e := p.WriteTo(make([]byte, 1233), p.target); e == nil {
		t.Fatal("oversize accepted")
	}
	if _, e := p.WriteTo([]byte{1}, &net.UDPAddr{IP: net.IPv4(203, 0, 113, 2), Port: 49197}); e == nil {
		t.Fatal("another target accepted")
	}
}
