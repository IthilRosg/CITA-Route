package main

import (
	"context"
	"encoding/json"
	"golang.org/x/net/proxy"
	"io"
	"net"
	"net/http"
	"net/http/httptrace"
	"strconv"
	"sync/atomic"
	"time"
)

type lossCommand struct {
	Session      string
	Generation   uint64
	Action       string
	Milliseconds int
	IncomingOnly bool
}
type lossSnapshot struct {
	Stage           string `json:"stage"`
	Session         string `json:"session"`
	Generation      uint64 `json:"generation"`
	Dials           int64  `json:"dials"`
	BindOpens       int64  `json:"bindOpens"`
	DroppedOutgoing int64  `json:"droppedOutgoing"`
	DroppedIncoming int64  `json:"droppedIncoming"`
	OuterPort       int64  `json:"outerPort"`
	UdpDials        int64  `json:"udpDials"`
}

func validLossCommand(c lossCommand, session string, generation uint64, used bool) bool {
	if c.Session != session || c.Generation != generation {
		return false
	}
	if c.Action == "Snapshot" {
		return c.Milliseconds == 0 && !c.IncomingOnly
	}
	if c.Action == "Rebind" {
		return !used && c.Milliseconds == 0 && !c.IncomingOnly
	}
	return c.Action == "Pause" && !used && (c.Milliseconds == 3000 || c.Milliseconds == 10000)
}

// This control channel exists ONLY in the explicit VM loss-lab launch mode.
// Bounded commands, one fault, allowlisted durations, matching owner identity; no TCP
// management listener and no such commands on the ordinary proxy launch path.
func proxyLossControl(input io.Reader, output io.Writer, session string, generation uint64, bind *faultBind, dials *atomic.Int64, rebind ...func() error) {
	decoder := json.NewDecoder(io.LimitReader(input, 4096))
	decoder.DisallowUnknownFields()
	used := false
	for count := 0; count < 8; count++ {
		var c lossCommand
		if decoder.Decode(&c) != nil || !validLossCommand(c, session, generation, used) {
			return
		}
		if c.Action == "Pause" {
			used = true
			bind.inboundOnly.Store(c.IncomingOnly)
			bind.pause(time.Duration(c.Milliseconds) * time.Millisecond)
		}
		r := lossSnapshot{Stage: c.Action, Session: session, Generation: generation, Dials: dials.Load(), BindOpens: bind.opens.Load(), DroppedOutgoing: bind.droppedSend.Load(), DroppedIncoming: bind.droppedReceive.Load()}
		if c.Action == "Rebind" {
			if len(rebind) != 1 {
				return
			}
			used = true
			if rebind[0]() != nil {
				return
			}
			r.BindOpens = bind.opens.Load()
		}
		r.OuterPort = bind.port.Load()
		r.UdpDials = bind.udpDials.Load()
		if json.NewEncoder(output).Encode(r) != nil {
			return
		}
	}
}

type chainRetention struct {
	Success             bool   `json:"success"`
	Stage               string `json:"stage"`
	EgressHash          string `json:"egressHash,omitempty"`
	DialsBefore         int64  `json:"dialsBefore"`
	DialsAfter          int64  `json:"dialsAfter"`
	ReusedTCP           bool   `json:"reusedTcp"`
	SameExit            bool   `json:"sameExit"`
	RequestMilliseconds int64  `json:"requestMilliseconds"`
}

func proxyRetentionClient(input io.Reader, output io.Writer) int {
	r := chainRetention{Stage: "ChainRetention"}
	defer func() { json.NewEncoder(output).Encode(r) }()
	decoder := json.NewDecoder(io.LimitReader(input, 2048))
	decoder.DisallowUnknownFields()
	var spec struct {
		Port              int
		FaultMilliseconds int
		Token             string
		HoldForFinal      bool
	}
	if decoder.Decode(&spec) != nil || spec.Port < 1 || spec.Port > 65535 || (spec.FaultMilliseconds != 3000 && spec.FaultMilliseconds != 10000) {
		return 1
	}
	var auth *proxy.Auth
	if spec.Token != "" {
		if !validHex(spec.Token, 32) {
			return 1
		}
		auth = &proxy.Auth{User: "cita", Password: spec.Token}
	}
	d, e := proxy.SOCKS5("tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(spec.Port)), auth, &net.Dialer{Timeout: 5 * time.Second})
	if e != nil {
		return 1
	}
	dial, ok := d.(proxy.ContextDialer)
	if !ok {
		return 1
	}
	var calls atomic.Int64
	transport := &http.Transport{DialContext: func(ctx context.Context, kind, address string) (net.Conn, error) {
		calls.Add(1)
		return dial.DialContext(ctx, kind, address)
	}, ForceAttemptHTTP2: false, MaxConnsPerHost: 1, TLSHandshakeTimeout: 7 * time.Second, ResponseHeaderTimeout: 25 * time.Second, MaxResponseHeaderBytes: 16384}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	get := func(ctx context.Context) string {
		req, e := http.NewRequestWithContext(ctx, "GET", "https://www.cloudflare.com/cdn-cgi/trace", nil)
		if e != nil {
			return ""
		}
		resp, e := client.Do(req)
		if e != nil {
			return ""
		}
		defer resp.Body.Close()
		body, e := io.ReadAll(io.LimitReader(resp.Body, 8193))
		if e != nil || len(body) > 8192 || resp.StatusCode != 200 {
			return ""
		}
		return traceHash(body)
	}
	r.EgressHash = get(ctx)
	r.DialsBefore = calls.Load()
	if r.EgressHash == "" || r.DialsBefore != 1 {
		return 1
	}
	if json.NewEncoder(output).Encode(struct {
		Stage string `json:"stage"`
	}{"RetentionClientReady"}) != nil {
		return 1
	}
	var command struct{ Continue bool }
	if decoder.Decode(&command) != nil || !command.Continue {
		return 1
	}
	var reused atomic.Bool
	trace := &httptrace.ClientTrace{GotConn: func(info httptrace.GotConnInfo) { reused.Store(info.Reused) }}
	began := time.Now()
	after := get(httptrace.WithClientTrace(ctx, trace))
	r.RequestMilliseconds = time.Since(began).Milliseconds()
	r.SameExit = after != "" && after == r.EgressHash
	r.ReusedTCP = reused.Load()
	r.DialsAfter = calls.Load()
	r.Success = r.SameExit && r.ReusedTCP && r.DialsBefore == 1 && r.DialsAfter == 1
	if spec.HoldForFinal {
		if json.NewEncoder(output).Encode(struct {
			Stage string `json:"stage"`
		}{"RetentionAfterLoss"}) != nil {
			return 1
		}
		var final struct{ Continue bool }
		if decoder.Decode(&final) != nil || !final.Continue {
			return 1
		}
		reused.Store(false)
		finalHash := get(httptrace.WithClientTrace(ctx, trace))
		r.SameExit = r.SameExit && finalHash != "" && finalHash == r.EgressHash
		r.ReusedTCP = r.ReusedTCP && reused.Load()
		r.DialsAfter = calls.Load()
		r.Success = r.Success && r.SameExit && r.ReusedTCP && r.DialsAfter == 1
	}
	if r.Success {
		r.Stage = "RetainedTcpThroughCore"
		if spec.Token != "" {
			r.Stage = "RetainedTcpDirectProxy"
		}
		return 0
	}
	return 1
}
