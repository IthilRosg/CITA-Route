package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"
)

func TestProfilePreflightNoConfigurationEcho(t *testing.T) {
	for _, config := range []string{fixture(), "private-secret-input"} {
		var in, out bytes.Buffer
		json.NewEncoder(&in).Encode(proxyStart{Config: config, Session: strings.Repeat("a", 32), Token: strings.Repeat("b", 64), Generation: 4})
		code := validateProfile(&in, &out)
		if (code == 0) != (config == fixture()) || strings.Contains(out.String(), config) || strings.Contains(out.String(), strings.Repeat("b", 64)) {
			t.Fatal("preflight result or secret boundary")
		}
	}
}
