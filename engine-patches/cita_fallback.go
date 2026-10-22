// CITA routing core modification, 2026. Distributed with corresponding source.
package transport

import (
	"context"
	"errors"
	"fmt"
	"time"

	mDNS "github.com/miekg/dns"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/dns"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing/common/json/badoption"
	"github.com/sagernet/sing/service"
)

type CitaFallbackOptions struct {
	Servers []string           `json:"servers"`
	Timeout badoption.Duration `json:"timeout,omitempty"`
}

func RegisterCitaFallback(registry *dns.TransportRegistry) {
	dns.RegisterTransport[CitaFallbackOptions](registry, "cita-fallback", newCitaFallback)
}

type citaFallback struct {
	dns.TransportAdapter
	manager  adapter.DNSTransportManager
	names    []string
	children []adapter.DNSTransport
	timeout  time.Duration
}

func newCitaFallback(ctx context.Context, _ log.ContextLogger, tag string, options CitaFallbackOptions) (adapter.DNSTransport, error) {
	if len(options.Servers) != 2 || options.Servers[0] == "" || options.Servers[1] == "" || options.Servers[0] == options.Servers[1] || options.Servers[0] == tag || options.Servers[1] == tag {
		return nil, errors.New("cita-fallback requires two distinct child servers")
	}
	timeout := time.Duration(options.Timeout)
	if timeout == 0 {
		timeout = 1500 * time.Millisecond
	}
	if timeout < 50*time.Millisecond || timeout > 3*time.Second {
		return nil, errors.New("invalid cita-fallback attempt timeout")
	}
	manager := service.FromContext[adapter.DNSTransportManager](ctx)
	if manager == nil {
		return nil, errors.New("missing DNS transport manager")
	}
	names := append([]string(nil), options.Servers...)
	return &citaFallback{TransportAdapter: dns.NewTransportAdapter("cita-fallback", tag, names), manager: manager, names: names, timeout: timeout}, nil
}

func (t *citaFallback) Start(stage adapter.StartStage, _ *adapter.Scope) error {
	if stage != adapter.StartStateStart {
		return nil
	}
	for _, name := range t.names {
		child, found := t.manager.Transport(name)
		if !found {
			return fmt.Errorf("DNS fallback child not found: %s", name)
		}
		t.children = append(t.children, child)
	}
	return nil
}

// Children are owned, started, reset and closed by the transport manager.
func (t *citaFallback) Close() error { return nil }
func (t *citaFallback) Reset()       {}
func (t *citaFallback) Exchange(ctx context.Context, message *mDNS.Msg) (*mDNS.Msg, error) {
	return exchangeCitaFallback(ctx, message, t.timeout, t.children)
}
func (t *citaFallback) ExchangeAsync(ctx context.Context, message *mDNS.Msg, callback func(*mDNS.Msg, error)) {
	request := message.Copy()
	go func() { callback(t.Exchange(ctx, request)) }()
}

func exchangeCitaFallback(ctx context.Context, message *mDNS.Msg, timeout time.Duration, children []adapter.DNSTransport) (*mDNS.Msg, error) {
	var lastResponse *mDNS.Msg
	var lastError error
	for _, child := range children {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		attempt, cancel := context.WithTimeout(ctx, timeout)
		response, err := child.Exchange(attempt, message.Copy())
		cancel()
		if parentError := ctx.Err(); parentError != nil {
			return nil, parentError
		}
		if err == nil && response != nil {
			// A valid NXDOMAIN/NODATA is authoritative for this attempt. Do not ask
			// another provider merely to turn a negative answer into a positive one.
			if response.Rcode != mDNS.RcodeServerFailure && response.Rcode != mDNS.RcodeRefused {
				return response, nil
			}
			lastResponse = response
		} else if err == nil {
			err = errors.New("empty DNS fallback response")
		}
		if err != nil {
			lastError = err
		}
	}
	if lastResponse != nil {
		return lastResponse, nil
	}
	if lastError == nil {
		lastError = errors.New("no DNS fallback child available")
	}
	return nil, lastError
}
