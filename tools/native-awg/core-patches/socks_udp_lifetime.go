package socks

import (
	"errors"
	"github.com/sagernet/sing-box/option"
	socksclient "github.com/sagernet/sing/protocol/socks"
)

func citaConfigureUDPLifetime(client *socksclient.Client, options option.SOCKSOutboundOptions) error {
	if !options.CitaNativeUDPLifetime {
		return nil
	}
	if string(options.Network) != "udp" || options.UDPOverTCP != nil || options.Detour != "" || options.BindInterface != "" || options.Inet4BindAddress != nil || options.Inet6BindAddress != nil || options.CitaNativeRetention != nil {
		return errors.New("invalid native UDP outbound scope")
	}
	return client.EnableCitaUDPLifetime()
}
