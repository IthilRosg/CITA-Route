package transport

import (
	"context"
	"errors"
	"testing"
	"time"

	mDNS "github.com/miekg/dns"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/dns"
)

type citaTestDNS struct {
	dns.TransportAdapter
	calls int
	reply func(context.Context, *mDNS.Msg) (*mDNS.Msg, error)
}

func (t *citaTestDNS) Start(adapter.StartStage, *adapter.Scope) error { return nil }
func (t *citaTestDNS) Close() error                                   { return nil }
func (t *citaTestDNS) Reset()                                         {}
func (t *citaTestDNS) Exchange(ctx context.Context, m *mDNS.Msg) (*mDNS.Msg, error) {
	t.calls++
	return t.reply(ctx, m)
}
func (t *citaTestDNS) ExchangeAsync(ctx context.Context, m *mDNS.Msg, cb func(*mDNS.Msg, error)) {
	cb(t.Exchange(ctx, m))
}

func TestCitaFallback(t *testing.T) {
	request := new(mDNS.Msg)
	request.SetQuestion("fixture.invalid.", mDNS.TypeA)
	response := func(rcode int) func(context.Context, *mDNS.Msg) (*mDNS.Msg, error) {
		return func(_ context.Context, m *mDNS.Msg) (*mDNS.Msg, error) {
			r := new(mDNS.Msg)
			r.SetReply(m)
			r.Rcode = rcode
			return r, nil
		}
	}
	for _, rcode := range []int{mDNS.RcodeSuccess, mDNS.RcodeNameError} {
		first := &citaTestDNS{reply: response(rcode)}
		second := &citaTestDNS{reply: response(mDNS.RcodeSuccess)}
		result, err := exchangeCitaFallback(context.Background(), request, time.Second, []adapter.DNSTransport{first, second})
		if err != nil || result.Rcode != rcode || first.calls != 1 || second.calls != 0 {
			t.Fatal("valid response must not query backup", rcode, err)
		}
	}
	for _, rcode := range []int{mDNS.RcodeServerFailure, mDNS.RcodeRefused} {
		first := &citaTestDNS{reply: response(rcode)}
		second := &citaTestDNS{reply: response(mDNS.RcodeSuccess)}
		result, err := exchangeCitaFallback(context.Background(), request, time.Second, []adapter.DNSTransport{first, second})
		if err != nil || result.Rcode != mDNS.RcodeSuccess || second.calls != 1 {
			t.Fatal("temporary DNS failure must use backup", rcode, err)
		}
	}
	first := &citaTestDNS{reply: func(ctx context.Context, _ *mDNS.Msg) (*mDNS.Msg, error) { <-ctx.Done(); return nil, ctx.Err() }}
	second := &citaTestDNS{reply: response(mDNS.RcodeSuccess)}
	result, err := exchangeCitaFallback(context.Background(), request, 20*time.Millisecond, []adapter.DNSTransport{first, second})
	if err != nil || result == nil || second.calls != 1 {
		t.Fatal("attempt deadline did not release backup", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	second.calls = 0
	_, err = exchangeCitaFallback(ctx, request, time.Second, []adapter.DNSTransport{first, second})
	if !errors.Is(err, context.Canceled) || second.calls != 0 {
		t.Fatal("caller Stop must not launch backup", err)
	}
	first = &citaTestDNS{reply: func(_ context.Context, m *mDNS.Msg) (*mDNS.Msg, error) {
		m.Question[0].Name = "changed.invalid."
		return nil, errors.New("fixture")
	}}
	second = &citaTestDNS{reply: func(_ context.Context, m *mDNS.Msg) (*mDNS.Msg, error) {
		if m.Question[0].Name != request.Question[0].Name {
			t.Fatal("child mutated subsequent query")
		}
		return response(mDNS.RcodeSuccess)(context.Background(), m)
	}}
	if _, err = exchangeCitaFallback(context.Background(), request, time.Second, []adapter.DNSTransport{first, second}); err != nil {
		t.Fatal(err)
	}
}
