package main

import (
	"encoding/json"
	"io"
)

// Offline preflight only. Does not create a device, listener or network socket.
func validateProfile(input io.Reader, output io.Writer) int {
	decoder := json.NewDecoder(io.LimitReader(input, 131073))
	decoder.DisallowUnknownFields()
	var spec proxyStart
	if decoder.Decode(&spec) != nil || !validHex(spec.Session, 16) || !validHex(spec.Token, 32) || spec.Generation == 0 {
		json.NewEncoder(output).Encode(map[string]string{"stage": "ConfigurationRejected"})
		return 1
	}
	_, e := parseConfig(spec.Config)
	spec.Config = ""
	if e != nil {
		json.NewEncoder(output).Encode(map[string]string{"stage": "ConfigurationRejected"})
		return 1
	}
	json.NewEncoder(output).Encode(proxyReady{Stage: "ConfigurationValidated", Session: spec.Session, Generation: spec.Generation})
	return 0
}
