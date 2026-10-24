package socks

import (
	"github.com/sagernet/sing-box/option"
	M "github.com/sagernet/sing/common/metadata"
	client "github.com/sagernet/sing/protocol/socks"
	"strings"
	"testing"
)

func TestCitaRetentionUDPLifetimeOptions(t *testing.T) {
	if citaConfigureUDPLifetime(nil, option.SOCKSOutboundOptions{}) != nil {
		t.Fatal("default behavior changed")
	}
	t.Setenv("CITA_NATIVE_UDP_LIFETIME_LAB", "1")
	c := client.NewClient(nil, M.ParseSocksaddr("127.0.0.1:20888"), client.Version5, "cita", strings.Repeat("a", 64))
	var o option.SOCKSOutboundOptions
	o.CitaNativeUDPLifetime = true
	o.Network = "udp"
	if e := citaConfigureUDPLifetime(c, o); e != nil {
		t.Fatal(e)
	}
	for _, change := range []func(*option.SOCKSOutboundOptions){func(o *option.SOCKSOutboundOptions) { o.Network = "tcp" }, func(o *option.SOCKSOutboundOptions) { o.Detour = "other" }, func(o *option.SOCKSOutboundOptions) { o.BindInterface = "nic" }, func(o *option.SOCKSOutboundOptions) { o.CitaNativeRetention = &option.CitaNativeRetentionOptions{} }} {
		bad := o
		change(&bad)
		if citaConfigureUDPLifetime(c, bad) == nil {
			t.Fatal("scope escape")
		}
	}
}
