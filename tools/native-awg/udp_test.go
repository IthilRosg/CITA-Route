package main

import (
	"golang.org/x/net/dns/dnsmessage"
	"testing"
)

func TestDnsEvidence(t *testing.T) {
	m := dnsmessage.Message{Header: dnsmessage.Header{ID: 42, Response: true}, Questions: []dnsmessage.Question{{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}}, Answers: []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: dnsmessage.MustNewName("example.com."), Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}, Body: &dnsmessage.AResource{A: [4]byte{93, 184, 215, 14}}}}}
	data, e := m.Pack()
	if e != nil || !validDNS(data, 42) || validDNS(data, 43) || validDNS([]byte{1}, 42) {
		t.Fatal("DNS identity validation failed")
	}
	m.Truncated = true
	data, _ = m.Pack()
	if validDNS(data, 42) {
		t.Fatal("Truncated UDP reply cannot certify completion")
	}
}
