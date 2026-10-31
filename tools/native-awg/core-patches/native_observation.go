package socks

import (
 "bufio"
 "context"
 "crypto/tls"
 "crypto/x509"
 "errors"
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
// The limit covers headers plus the sampled body. Common responses can exceed
// 12 KiB of headers; keep a hard cap to prevent unbounded probe allocations.
const nativeProbeResponseLimit int64 = 65536
func nativeProbeReadResponse(r io.Reader)(*http.Response,error){return http.ReadResponse(bufio.NewReader(io.LimitReader(r,nativeProbeResponseLimit)),nil)}
// These fixed categories deliberately exclude error strings, addresses and URLs.
// DNS is not assessed here: the probe connects to the already observed IP.
func nativeProbeFailure(stage string,err error) string {
 if err==nil{return stage+"-failed"}
 var cert *tls.CertificateVerificationError
 var unknown x509.UnknownAuthorityError
 var invalid x509.CertificateInvalidError
 var host x509.HostnameError
 if errors.As(err,&cert)||errors.As(err,&unknown)||errors.As(err,&invalid)||errors.As(err,&host){return "tls-certificate"}
 var netErr net.Error
 if errors.Is(err,context.DeadlineExceeded)||(errors.As(err,&netErr)&&netErr.Timeout()){return stage+"-timeout"}
 return stage+"-failed"
}
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
 if c.Action=="ProbeDPI"&&!nativeDPIAllowed("tcp",443){return bindingReply{Stage:c.Action}}
 h.mu.Lock();r=bindingReply{Stage:c.Action,Owner:h.owner,Session:c.Session,Generation:c.Generation,State:h.state,CurrentGeneration:h.generation}
 if c.Owner!=h.owner||c.Session!=h.session||c.Generation!=h.generation||h.state!="Active"||c.Observation==nil||c.Port!=0||c.Token!=""||c.Policy!=nil||c.Scope!=nil||c.Revision!=0 {h.mu.Unlock();return}
 o,ok:=normalizeObservation(*c.Observation);if !ok{h.mu.Unlock();return};_,seen:=h.observations[observationKey(o)];var lease *nativeDirectLease;if seen{lease=h.acquireDirectLocked(nativeDirectScope{Address:o.Address,Network:"tcp",Port:o.Port})};h.mu.Unlock();if lease==nil{return};defer lease.release();if h.outbounds==nil{return}
 outbound,ok:=h.outbounds.Outbound("native-direct");if !ok||outbound.Type()!="direct"{return};r.Success=true
 r.ProbeResult="dial-failed"
 ctx,cancel:=context.WithTimeout(h.ctx,2500*time.Millisecond);defer cancel()
 conn,e:=outbound.DialContext(ctx,"tcp",M.ParseSocksaddr(fmt.Sprintf("%s:443",o.Address)));if e!=nil{r.ProbeResult=nativeProbeFailure("dial",e);return};defer conn.Close();if e=nativeProbeSetDeadline(ctx,conn);e!=nil{r.ProbeResult=nativeProbeFailure("deadline",e);return}
 if c.Action=="ProbeDPI"{conn=nativeDPIConn(conn,ctx)}
 secure:=tls.Client(conn,&tls.Config{ServerName:o.Domain,MinVersion:tls.VersionTLS12});if e=secure.HandshakeContext(ctx);e!=nil{r.ProbeResult=nativeProbeFailure("tls",e);return}
 if _,e=io.WriteString(secure,"GET / HTTP/1.1\r\nHost: "+o.Domain+"\r\nUser-Agent: CITA-Route-Connectivity/1\r\nConnection: close\r\n\r\n");e!=nil{r.ProbeResult=nativeProbeFailure("http-write",e);return}
 response,e:=nativeProbeReadResponse(secure);if e!=nil{r.ProbeResult=nativeProbeFailure("http-header",e);return};defer response.Body.Close();if response.StatusCode<200||response.StatusCode>=400{r.ProbeResult="http-refused";return}
 // A TLS handshake or response header alone cannot prove a usable body path.
 b:=make([]byte,1024);n,e:=io.ReadFull(response.Body,b);r.ProbeReachable=n>0&&(e==nil||e==io.EOF||e==io.ErrUnexpectedEOF);if r.ProbeReachable{r.ProbeResult="reachable"}else if e!=nil&&e!=io.EOF&&e!=io.ErrUnexpectedEOF{r.ProbeResult=nativeProbeFailure("http-body",e)}else{r.ProbeResult="http-body-empty"};return
}
