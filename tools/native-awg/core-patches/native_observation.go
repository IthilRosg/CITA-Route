package socks

import (
 "bufio"
 "context"
 "crypto/tls"
 "fmt"
 "io"
 "net"
 "net/http"
 "net/netip"
 "time"
 M "github.com/sagernet/sing/common/metadata"
)

// No URL, cookies, application payload or credentials are captured or replayed.
// Unknown traffic keeps the VPN path while an anonymous probe is evaluated.
type nativeObservation struct { Domain,Application,Address string; Port uint16 }
// Dial, TLS and body reads share one absolute probe deadline. Giving the
// socket a fresh timeout after Dial can overrun the controller's reply budget.
func nativeProbeSetDeadline(ctx context.Context,conn net.Conn) error {
 deadline,ok:=ctx.Deadline();if !ok{return fmt.Errorf("probe requires a bounded context")};return conn.SetDeadline(deadline)
}
func nativePublic(ip netip.Addr) bool {
 if !ip.Is4()||!ip.IsGlobalUnicast()||ip.IsPrivate()||ip.IsLoopback()||ip.IsLinkLocalUnicast()||nativeFakeIPPrefix.Contains(ip){return false}
 for _,prefix:=range []string{"0.0.0.0/8","100.64.0.0/10","224.0.0.0/4","240.0.0.0/4"}{if netip.MustParsePrefix(prefix).Contains(ip){return false}}
 return true
}
func observationKey(o nativeObservation) string {return nativePolicyKey(o.Domain,o.Application,"tcp",o.Port)+"\x00"+o.Address}
func normalizeObservation(o nativeObservation)(nativeObservation,bool){
 d,ok:=nativeDomain(o.Domain);if !ok||o.Port!=443{return nativeObservation{},false};a,ok:=nativeApplication(o.Application);if !ok{return nativeObservation{},false};ip,e:=netip.ParseAddr(o.Address);if e!=nil||!nativePublic(ip)||ip.String()!=o.Address{return nativeObservation{},false};o.Domain=d;o.Application=a;return o,true
}
func (h *CitaNativeBinding) observe(o nativeObservation){
 o,ok:=normalizeObservation(o);if !ok{return};h.mu.Lock();defer h.mu.Unlock();if h.state!="Active"{return}
 if h.observations==nil{h.observations=make(map[string]nativeObservation);h.observationOrder=nil}
 key:=observationKey(o)
 // Fixed-size LRU window. Refresh existing entries even at capacity; evicting
 // probe evidence never revokes a route or closes an admitted application flow.
 for i,k:=range h.observationOrder{if k==key{h.observationOrder=append(h.observationOrder[:i],h.observationOrder[i+1:]...);break}}
 if _,exists:=h.observations[key];!exists&&len(h.observations)>=128{delete(h.observations,h.observationOrder[0]);h.observationOrder=h.observationOrder[1:]}
 h.observations[key]=o;h.observationOrder=append(h.observationOrder,key)
}
func(h *CitaNativeBinding) probeDirect(c bindingCommand)(r bindingReply){
 h.mu.Lock();r=bindingReply{Stage:c.Action,Owner:h.owner,Session:c.Session,Generation:c.Generation,State:h.state,CurrentGeneration:h.generation}
 if c.Owner!=h.owner||c.Session!=h.session||c.Generation!=h.generation||h.state!="Active"||c.Observation==nil||c.Port!=0||c.Token!=""||c.Policy!=nil||c.Scope!=nil||c.Revision!=0 {h.mu.Unlock();return}
 o,ok:=normalizeObservation(*c.Observation);if !ok{h.mu.Unlock();return};_,seen:=h.observations[observationKey(o)];var lease *nativeDirectLease;if seen{lease=h.acquireDirectLocked(nativeDirectScope{Address:o.Address,Network:"tcp",Port:o.Port})};h.mu.Unlock();if lease==nil{return};defer lease.release();if h.outbounds==nil{return}
 outbound,ok:=h.outbounds.Outbound("native-direct");if !ok||outbound.Type()!="direct"{return};r.Success=true
 ctx,cancel:=context.WithTimeout(h.ctx,2500*time.Millisecond);defer cancel()
 conn,e:=outbound.DialContext(ctx,"tcp",M.ParseSocksaddr(fmt.Sprintf("%s:443",o.Address)));if e!=nil{return};defer conn.Close();if nativeProbeSetDeadline(ctx,conn)!=nil{return}
 secure:=tls.Client(conn,&tls.Config{ServerName:o.Domain,MinVersion:tls.VersionTLS12});if secure.HandshakeContext(ctx)!=nil{return}
 if _,e=io.WriteString(secure,"GET / HTTP/1.1\r\nHost: "+o.Domain+"\r\nUser-Agent: CITA-Route-Connectivity/1\r\nConnection: close\r\n\r\n");e!=nil{return}
 response,e:=http.ReadResponse(bufio.NewReader(io.LimitReader(secure,12288)),nil);if e!=nil{return};defer response.Body.Close();if response.StatusCode<200||response.StatusCode>=400{return}
 // A TLS handshake or response header alone cannot prove a usable body path.
 b:=make([]byte,1024);n,e:=io.ReadFull(response.Body,b);r.ProbeReachable=n>0&&(e==nil||e==io.EOF||e==io.ErrUnexpectedEOF);return
}
