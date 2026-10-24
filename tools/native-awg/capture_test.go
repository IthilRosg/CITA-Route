package main

import (
	"encoding/binary"
	"testing"
)

func captureBlock(kind uint32, body []byte) []byte {
	n := (len(body) + 3) &^ 3
	out := make([]byte, n+12)
	binary.LittleEndian.PutUint32(out, kind)
	binary.LittleEndian.PutUint32(out[4:], uint32(len(out)))
	copy(out[8:], body)
	binary.LittleEndian.PutUint32(out[len(out)-4:], uint32(len(out)))
	return out
}
func captureFixture(protocol byte, port uint16) []byte {
	return captureFixtureTo(protocol, port, [4]byte{203, 0, 113, 1})
}
func captureFixtureTo(protocol byte, port uint16, destination [4]byte) []byte {
	sh := make([]byte, 16)
	binary.LittleEndian.PutUint32(sh, 0x1a2b3c4d)
	binary.LittleEndian.PutUint16(sh[4:], 1)
	data := captureBlock(0x0a0d0d0a, sh)
	idb := make([]byte, 8)
	binary.LittleEndian.PutUint16(idb, 1)
	data = append(data, captureBlock(1, idb)...)
	packet := make([]byte, 14+20+8)
	binary.BigEndian.PutUint16(packet[12:], 0x0800)
	packet[14] = 0x45
	packet[23] = protocol
	copy(packet[26:], []byte{10, 0, 0, 2})
	copy(packet[30:], destination[:])
	binary.BigEndian.PutUint16(packet[36:], port)
	body := make([]byte, 20+len(packet))
	binary.LittleEndian.PutUint32(body[12:], uint32(len(packet)))
	binary.LittleEndian.PutUint32(body[16:], uint32(len(packet)))
	copy(body[20:], packet)
	return append(data, captureBlock(6, body)...)
}
func TestCaptureProtectedDestinationScope(t *testing.T) {
	s := captureSpec{Local: "10.0.0.2", Endpoint: "203.0.113.1", Port: 3080, Protected: []string{"1.1.1.1"}}
	for _, test := range []struct {
		protocol byte
		port     uint16
		target   [4]byte
		tcp, udp int
	}{
		{6, 443, [4]byte{1, 1, 1, 1}, 1, 0}, {17, 53, [4]byte{1, 1, 1, 1}, 0, 1}, {17, 3080, [4]byte{203, 0, 113, 1}, 0, 0}, {6, 443, [4]byte{8, 8, 8, 8}, 0, 0},
	} {
		r, e := analyzeCapture(captureFixtureTo(test.protocol, test.port, test.target), s)
		if e != nil || !r.Success || r.ProtectedTCP != test.tcp || r.ProtectedUDP != test.udp {
			t.Fatal("Protected destination classification failed")
		}
		if test.target == [4]byte{8, 8, 8, 8} && r.OtherTCP != 1 {
			t.Fatal("Out-of-scope traffic must remain reported")
		}
	}
	for _, addresses := range [][]string{{"10.0.0.1"}, {"::1"}, {"1.1.1.1", "1.1.1.1"}} {
		s.Protected = addresses
		if _, e := analyzeCapture(captureFixture(17, 3080), s); e == nil {
			t.Fatal("Unsafe/ambiguous protected set accepted")
		}
	}
}
func TestCaptureEvidence(t *testing.T) {
	spec := captureSpec{Local: "10.0.0.2", Endpoint: "203.0.113.1", Port: 3080}
	for _, test := range []struct {
		protocol         byte
		port             uint16
		tunnel, tcp, udp int
	}{{17, 3080, 1, 0, 0}, {6, 443, 0, 1, 0}, {17, 53, 0, 0, 1}} {
		r, e := analyzeCapture(captureFixture(test.protocol, test.port), spec)
		if e != nil || !r.Success || r.Packets != 1 || r.TunnelUDP != test.tunnel || r.OtherTCP != test.tcp || r.OtherUDP != test.udp {
			t.Fatal("Capture classification failed")
		}
	}
	data := captureFixture(17, 3080)
	for _, bad := range [][]byte{nil, data[:7], data[:len(data)-1], append([]byte{1}, data...)} {
		if r, e := analyzeCapture(bad, spec); e == nil && r.Success {
			t.Fatal("Malformed capture cannot certify a boundary")
		}
	}
	binary.LittleEndian.PutUint16(data[36:], 101) // unsupported raw link type in IDB
	if r, e := analyzeCapture(data, spec); e == nil && r.Success {
		t.Fatal("Unsupported capture link cannot certify a boundary")
	}
}
