package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"net/http"
	"net/http/httptrace"
	"net/netip"
	"strings"
	"sync/atomic"
	"time"
)

type retentionResult struct {
	Success             bool   `json:"success"`
	FaultMilliseconds   int64  `json:"faultMilliseconds"`
	RequestMilliseconds int64  `json:"requestMilliseconds"`
	TCPDialsBefore      int64  `json:"tcpDialsBefore"`
	TCPDialsAfter       int64  `json:"tcpDialsAfter"`
	BindOpensBefore     int64  `json:"bindOpensBefore"`
	BindOpensAfter      int64  `json:"bindOpensAfter"`
	ReusedTCP           bool   `json:"reusedTcp"`
	DroppedOutgoing     int64  `json:"droppedOutgoing"`
	DroppedIncoming     int64  `json:"droppedIncoming"`
	SameExit            bool   `json:"sameExit"`
	LossMode            string `json:"lossMode"`
}

func traceHash(body []byte) string {
	found := ""
	for _, line := range strings.Split(string(body), "\n") {
		if !strings.HasPrefix(line, "ip=") {
			continue
		}
		if found != "" {
			return ""
		}
		ip, e := netip.ParseAddr(strings.TrimSpace(strings.TrimPrefix(line, "ip=")))
		if e != nil || !ip.IsGlobalUnicast() || ip.IsPrivate() {
			return ""
		}
		hash := sha256.Sum256([]byte(ip.String()))
		found = hex.EncodeToString(hash[:])
	}
	return found
}
func retainTCP(ctx context.Context, client *http.Client, bind *faultBind, dials *atomic.Int64, expected string) retentionResult {
	duration := 3 * time.Second
	r := retentionResult{FaultMilliseconds: duration.Milliseconds(), TCPDialsBefore: dials.Load(), BindOpensBefore: bind.opens.Load()}
	r.LossMode = "BothDirections"
	if bind.inboundOnly.Load() {
		r.LossMode = "IncomingOnly"
	}
	bind.pause(duration)
	began := time.Now()
	trace := &httptrace.ClientTrace{GotConn: func(info httptrace.GotConnInfo) { r.ReusedTCP = info.Reused }}
	req, e := http.NewRequestWithContext(httptrace.WithClientTrace(ctx, trace), http.MethodGet, "https://www.cloudflare.com/cdn-cgi/trace", nil)
	if e != nil {
		return r
	}
	response, e := client.Do(req)
	if e == nil {
		body, readError := io.ReadAll(io.LimitReader(response.Body, 8193))
		response.Body.Close()
		r.SameExit = readError == nil && response.StatusCode == 200 && len(body) <= 8192 && traceHash(body) == expected
	}
	r.RequestMilliseconds = time.Since(began).Milliseconds()
	r.TCPDialsAfter = dials.Load()
	r.BindOpensAfter = bind.opens.Load()
	r.DroppedOutgoing = bind.droppedSend.Load()
	r.DroppedIncoming = bind.droppedReceive.Load()
	dropped := r.DroppedOutgoing > 0
	if bind.inboundOnly.Load() {
		dropped = r.DroppedIncoming > 0
	}
	r.Success = r.SameExit && r.ReusedTCP && r.TCPDialsBefore == 1 && r.TCPDialsAfter == 1 && r.BindOpensBefore == 1 && r.BindOpensAfter == 1 && dropped && r.RequestMilliseconds >= r.FaultMilliseconds
	return r
}
