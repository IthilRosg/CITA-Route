package main

import (
	"context"
	"encoding/json"
	"golang.org/x/net/proxy"
	"io"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"time"
)

// The parent is the only recovery writer. Child commands contain ownership
// identity, not credentials; stdin itself is the inherited private lease.
func (c *ownedProxy) call(ctx context.Context, spec proxyStart, action string) (lossSnapshot, error) {
	c.ioMu.Lock()
	defer c.ioMu.Unlock()
	if ctx.Err() != nil {
		return lossSnapshot{}, errProxy
	}
	if json.NewEncoder(c.input).Encode(lossCommand{Session: spec.Session, Generation: spec.Generation, Action: action}) != nil {
		return lossSnapshot{}, errProxy
	}
	type reply struct {
		value lossSnapshot
		err   error
	}
	result := make(chan reply, 1)
	go func() { var v lossSnapshot; e := c.decoder.Decode(&v); result <- reply{v, e} }()
	timer := time.NewTimer(2 * time.Second)
	defer timer.Stop()
	var r reply
	select {
	case r = <-result:
	case <-ctx.Done():
		c.read.Close()
		c.cmd.Process.Kill()
		<-result
		return lossSnapshot{}, errProxy
	case <-timer.C:
		c.read.Close()
		c.cmd.Process.Kill()
		<-result
		return lossSnapshot{}, errProxy
	}
	if r.err != nil || r.value.Stage != action || r.value.Session != spec.Session || r.value.Generation != spec.Generation {
		return lossSnapshot{}, errProxy
	}
	return r.value, nil
}
func (o *proxyOwner) transportSnapshot(ctx context.Context, spec proxyStart) {
	o.mu.Lock()
	child := o.child
	o.mu.Unlock()
	if child == nil {
		return
	}
	status, e := child.call(ctx, spec, "Snapshot")
	if e != nil {
		return
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.child == child {
		o.status.Transport = &status
	}
}
type ownerProbeResult struct{ HTTPS bool `json:"https"`; UDP bool `json:"udp"`; CompletedUnixMilliseconds int64 `json:"completedUnixMilliseconds"` }
func probeOwned(ctx context.Context, child *ownedProxy, token string, dns netip.Addr) ownerProbeResult {
	if !publicTarget(dns) {
		dns = netip.MustParseAddr("1.1.1.1")
	}
 type outcome struct{https bool;ok bool};results:=make(chan outcome,2)
	go func() {
		d, e := proxy.SOCKS5("tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(child.port)), &proxy.Auth{User: "cita", Password: token}, &net.Dialer{Timeout: 1500 * time.Millisecond})
		if e != nil {
			results <- outcome{https:true}
			return
		}
		dial, ok := d.(proxy.ContextDialer)
		if !ok {
			results <- outcome{https:true}
			return
		}
		transport := &http.Transport{Proxy: nil, DialContext: dial.DialContext, DisableKeepAlives: true, MaxResponseHeaderBytes: 16384, TLSHandshakeTimeout: time.Second, ResponseHeaderTimeout: time.Second}
		defer transport.CloseIdleConnections()
		client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
		request, _ := http.NewRequestWithContext(ctx, "GET", "https://1.1.1.1/cdn-cgi/trace", nil)
		response, e := client.Do(request)
		if e != nil {
			results <- outcome{https:true}
			return
		}
		defer response.Body.Close()
		data, e := io.ReadAll(io.LimitReader(response.Body, 8193))
		results <- outcome{https:true,ok:e == nil && response.StatusCode == 200 && len(data) <= 8192 && traceHash(data) != ""}
	}()
	go func() {
		control, socket, e := openUDPAssociation(ctx, child.port, token)
		if e != nil {
			results <- outcome{}
			return
		}
		defer control.Close()
		defer socket.Close()
		stop := context.AfterFunc(ctx, func() { control.Close(); socket.Close() })
		defer stop()
		results <- outcome{ok:queryThroughAssociation(socket, netip.AddrPortFrom(dns, 53), 1500*time.Millisecond)}
	}()
 result:=ownerProbeResult{};for i:=0;i<2;i++{r:=<-results;if r.https{result.HTTPS=r.ok}else{result.UDP=r.ok}};result.CompletedUnixMilliseconds=time.Now().UnixMilli();return result
}
// A partial protocol failure is visible, but is not evidence that replacing the
// encrypted socket will help. Keep the original two-total-failure repair gate.
func(o *proxyOwner)decideProbe(ctx context.Context,child *ownedProxy,result ownerProbeResult)bool{
 repair:=o.decideRepair(ctx,child,result.HTTPS||result.UDP);o.mu.Lock();defer o.mu.Unlock()
 if ctx.Err()!=nil||o.child!=child{return false};copy:=result;o.status.HealthProbe=&copy
 if result.HTTPS&&!result.UDP{o.status.Health="UdpDegraded"};if !result.HTTPS&&result.UDP{o.status.Health="HttpsDegraded"};return repair
}
func (o *proxyOwner) decideRepair(ctx context.Context, child *ownedProxy, healthy bool) bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	if ctx.Err() != nil || o.child != child {
		return false
	}
	if healthy {
		o.verified = true
		o.failures = 0
		o.status.Health = "Reachable"
		return false
	}
	o.failures++
	if !o.verified {
		o.status.Health = "Unverified"
		return false
	}
	o.status.Health = "Suspect"
	if o.failures < 2 {
		return false
	}
	allowed := false
	if persistentControl() {
		allowed = o.repairBudget.acquire(time.Now(), "socket")
	} else {
		allowed = o.status.BudgetUsed < 3 && o.status.SocketRepairs < 2
	}
	if !allowed {
		o.status.Health = "Exhausted"
		return false
	}
	o.status.SocketRepairs++
	o.status.BudgetUsed++
	o.failures = 0
	o.status.Health = "Repairing"
	return true
}
func (o *proxyOwner) healthLoop(ctx context.Context, spec proxyStart) {
	config, e := parseConfig(spec.Config)
	if e != nil {
		return
	}
	delay := time.Duration(0)
	for {
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
		delay = time.Second
		o.mu.Lock()
		child := o.child
		o.mu.Unlock()
		if child == nil {
			continue
		}
		check, cancel := context.WithTimeout(ctx, 1500*time.Millisecond)
		healthy := probeOwned(check, child, spec.Token, config.dns[0])
		cancel()
		if o.decideProbe(ctx, child, healthy) {
			_, err := child.call(ctx, spec, "Rebind")
			o.mu.Lock()
			if o.child == child && ctx.Err() == nil {
				if err == nil {
					o.status.Health = "Verifying"
				} else {
					o.status.Health = "RepairFailed"
				}
			}
			o.mu.Unlock()
			delay = 2 * time.Second
		}
	}
}
