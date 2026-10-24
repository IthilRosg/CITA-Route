package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"golang.org/x/net/proxy"
	"io"
	"net"
	"net/http"
	"net/http/httptrace"
	"net/netip"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

// Fixed anonymous HTTPS acceptance client. Secret input stays off command lines.
func proxyClient(input io.Reader, output io.Writer) int {
	var spec struct {
		Port        int
		Token       string
		LiteralIPv4 bool
		ExportExit  bool
	}
	decoder := json.NewDecoder(io.LimitReader(input, 2048))
	decoder.DisallowUnknownFields()
	r := result{Schema: 1, Stage: "ProxyHttps", Engine: "amneziawg-go/v3@v3.1.20260828"}
	defer func() { json.NewEncoder(output).Encode(r) }()
	if decoder.Decode(&spec) != nil || spec.Port < 1 || spec.Port > 65535 {
		return 1
	}
	var auth *proxy.Auth
	if spec.Token != "" {
		if !validHex(spec.Token, 32) {
			return 1
		}
		auth = &proxy.Auth{User: "cita", Password: spec.Token}
	}
	dialer, e := proxy.SOCKS5("tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(spec.Port)), auth, &net.Dialer{Timeout: 5 * time.Second})
	if e != nil {
		return 1
	}
	contextual, ok := dialer.(proxy.ContextDialer)
	if !ok {
		return 1
	}
	transport := &http.Transport{DialContext: contextual.DialContext, TLSHandshakeTimeout: 7 * time.Second, MaxResponseHeaderBytes: 16384, DisableKeepAlives: true}
	defer transport.CloseIdleConnections()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	client := http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	url := "https://www.cloudflare.com/cdn-cgi/trace"
	if spec.LiteralIPv4 {
		url = "https://1.1.1.1/cdn-cgi/trace"
	}
	request, e := http.NewRequestWithContext(ctx, "GET", url, nil)
	if e != nil {
		return 1
	}
	began := time.Now()
	var phase atomic.Int32
	request = request.WithContext(httptrace.WithClientTrace(request.Context(), &httptrace.ClientTrace{
		TLSHandshakeStart: func() { phase.Store(1) },
		TLSHandshakeDone: func(_ tls.ConnectionState, e error) {
			if e == nil {
				phase.Store(2)
			}
		},
		GotFirstResponseByte: func() { phase.Store(3) },
	}))
	response, e := client.Do(request)
	r.Milliseconds = time.Since(began).Milliseconds()
	if e != nil {
		r.Stage = "ProxyConnectFailed"
		r.FailurePhase = []string{"ProxyDial", "TlsHandshake", "ResponseHeaders", "ResponseRead"}[phase.Load()]
		return 1
	}
	defer response.Body.Close()
	r.HTTPStatus = response.StatusCode
	r.Stage = "ProxyHttpResponse"
	body, e := io.ReadAll(io.LimitReader(response.Body, 8193))
	if e != nil || len(body) > 8192 || response.StatusCode != 200 {
		r.FailurePhase = "ResponseBodyOrStatus"
		return 1
	}
	r.EgressHash = traceHash(body)
	if spec.ExportExit && r.EgressHash != "" {
		for _, line := range strings.Split(string(body), "\n") {
			if strings.HasPrefix(line, "ip=") {
				ip, e := netip.ParseAddr(strings.TrimSpace(strings.TrimPrefix(line, "ip=")))
				if e == nil && ip.Is4() && publicTarget(ip) {
					r.EgressIPv4 = ip.String()
				}
			}
		}
	}
	r.Success = r.EgressHash != ""
	if !r.Success {
		return 1
	}
	r.Stage = "VerifiedProxyHttps"
	return 0
}
