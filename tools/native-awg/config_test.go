package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

func fixture() string {
	key := base64.StdEncoding.EncodeToString(make([]byte, 32))
	return "[Interface]\nPrivateKey=" + key + "\nAddress=10.1.0.2/32\nDNS=1.1.1.1\nMTU=1400\nS1=0\nS2=0\n[Peer]\nPublicKey=" + key + "\nEndpoint=203.0.113.1:3080\nAllowedIPs=0.0.0.0/0\nPersistentKeepalive=25\n"
}

func TestEngineErrorsDoNotExposeConfiguration(t *testing.T) {
	input := strings.Replace(fixture(), "S1=0", "H1=secret-engine-input", 1)
	r := probe(strings.NewReader(input))
	encoded, e := json.Marshal(r)
	if e != nil || r.Success || r.Stage != "EngineConfiguration" || strings.Contains(string(encoded), "secret-engine-input") {
		t.Fatal("Engine refusal must publish only its fixed stage")
	}
}
func TestConfigBoundary(t *testing.T) {
	c, e := parseConfig(fixture())
	if e != nil || len(c.address) != 1 || len(c.dns) != 1 || !strings.Contains(c.ipc, "s1=0\n") {
		t.Fatal("supported fixture rejected")
	}
	for _, text := range []string{
		fixture() + "[Peer]\n", strings.Replace(fixture(), "203.0.113.1:3080", "example.com:3080", 1),
		strings.Replace(fixture(), "203.0.113.1:3080", "127.0.0.1:3080", 1),
		strings.Replace(fixture(), "DNS=1.1.1.1", "DNS=localhost", 1),
		strings.Replace(fixture(), "MTU=1400", "MTU=9000", 1),
		strings.Replace(fixture(), "S1=0", "PostUp=arbitrary-command", 1),
		strings.Replace(fixture(), "S1=0", "#@ws:AllowedApps=anything", 1),
		strings.Replace(fixture(), "S1=0", "S1=0\nS1=2", 1), fixture() + "Unknown=secret-value\n",
		strings.Replace(fixture(), "AllowedIPs=0.0.0.0/0", "AllowedIPs=10.0.0.0/8", 1),
	} {
		if _, e := parseConfig(text); e == nil || strings.Contains(e.Error(), "secret-value") {
			t.Fatal("unsafe/unsupported input accepted or exposed")
		}
	}
}
