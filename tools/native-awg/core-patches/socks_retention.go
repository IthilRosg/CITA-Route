package socks

import (
	"context"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/common/citaretain"
	"github.com/sagernet/sing-box/option"
)

func citaLease(options option.SOCKSOutboundOptions) (*citaretain.Lease, error) {
	if options.CitaNativeRetention == nil {
		return nil, nil
	}
	opt := options.CitaNativeRetention
	return citaretain.New(options.Server, options.ServerPort, options.Version, options.Username, options.Password, opt.Session, opt.Generation, string(options.Network) == "tcp" && options.UDPOverTCP == nil && options.Detour == "" && options.BindInterface == "" && options.Inet4BindAddress == nil && options.Inet6BindAddress == nil)
}
func (h *Outbound) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage == adapter.StartStateInitialize && h.citaLease != nil {
		scope.Add(func() error { h.citaLease.Revoke(); return nil })
	}
	return nil
}
func (h *Outbound) citaContext(ctx context.Context) context.Context {
	if h.citaLease != nil {
		return citaretain.WithLease(ctx, h.citaLease)
	}
	return ctx
}
