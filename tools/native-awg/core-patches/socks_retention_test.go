package socks

import (
	"github.com/sagernet/sing-box/option"
	"strings"
	"testing"
)

func TestCitaRetentionExplicitSocksOptions(t *testing.T) {
	var options option.SOCKSOutboundOptions
	if l, e := citaLease(options); e != nil || l != nil {
		t.Fatal("Default SOCKS must retain old behavior")
	}
	options.Server = "127.0.0.1"
	options.ServerPort = 20888
	options.Version = "5"
	options.Username = "cita"
	options.Password = strings.Repeat("a", 64)
	options.Network = "tcp"
	options.CitaNativeRetention = &option.CitaNativeRetentionOptions{Session: strings.Repeat("b", 32), Generation: 7}
	if _, e := citaLease(options); e != nil {
		t.Fatal(e)
	}
	options.Detour = "other"
	if _, e := citaLease(options); e == nil {
		t.Fatal("Detour must not obtain lease")
	}
	options.Detour = ""
	options.Network = "udp"
	if _, e := citaLease(options); e == nil {
		t.Fatal("UDP must not obtain TCP retention")
	}
}
