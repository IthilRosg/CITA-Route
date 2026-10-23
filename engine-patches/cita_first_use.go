package route

import (
 "bufio"
 "context"
 "crypto/hmac"
 "crypto/rand"
 "crypto/sha256"
 "encoding/hex"
 "encoding/json"
 "fmt"
 "io"
 "net"
 "os"
 "path/filepath"
 "strings"
 "time"

 "github.com/sagernet/sing-box/adapter"
)

// Explicitly opted-in fallback TCP flows only. No packet or HTTP data is read,
// duplicated or replayed. A failure simply retains the existing default route.
type citaFirstUseRequest struct {
 Version int `json:"Version"`
 Nonce string `json:"Nonce"`
 RouterPid int `json:"RouterPid"`
 Domain string `json:"Domain"`
 Application string `json:"Application"`
 Mac string `json:"Mac"`
}
type citaFirstUseReply struct {
 Version int
 Nonce string
 RouterPid int
 Route string
 Epoch string
 Mac string
}
var citaFirstUseSlots=make(chan struct{},32)
func citaGateSign(secret,text string) string {
 h:=hmac.New(sha256.New,[]byte(secret));h.Write([]byte(text));return strings.ToUpper(hex.EncodeToString(h.Sum(nil)))
}
func citaGateEligible(m *adapter.InboundContext) bool {
 if m.Destination.Port!=443 || m.IPVersion==6 || m.ProcessInfo==nil || m.ProcessInfo.ProcessPath=="" ||
  (m.Inbound!="system-tun" && m.Inbound!="local-test") {return false}
 domain:=m.Domain;if domain=="" {domain=m.Destination.Fqdn}
 if len(domain)>253 || !strings.Contains(domain,".") || net.ParseIP(domain)!=nil || strings.ContainsAny(domain,"\r\n\x00 /\\:") {return false}
 app:=strings.ToLower(filepath.Base(m.ProcessInfo.ProcessPath))
 for _,excluded:=range []string{"routepilot.agent.exe","smart-router.exe","vpn-bridge.exe","tailscaled.exe","tailscale.exe","tailscale-ipn.exe","wiresock-connect-service.exe","wiresock-connect-app-service.exe"} {if app==excluded{return false}}
 return true
}
func citaGateExchange(ctx context.Context,address,secret string,request citaFirstUseRequest) string {
 request.Version=1
 nonce:=make([]byte,16);if _,err:=rand.Read(nonce);err!=nil{return ""};request.Nonce=hex.EncodeToString(nonce)
 request.Mac=citaGateSign(secret,fmt.Sprintf("1\n%s\n%d\n%s\n%s",request.Nonce,request.RouterPid,request.Domain,request.Application))
 ctx,cancel:=context.WithTimeout(ctx,2200*time.Millisecond);defer cancel()
 conn,err:=(&net.Dialer{Timeout:100*time.Millisecond}).DialContext(ctx,"tcp",address);if err!=nil{return ""};defer conn.Close()
 deadline,_:=ctx.Deadline();conn.SetDeadline(deadline)
 done:=context.AfterFunc(ctx,func(){conn.Close()});defer done()
 if err=json.NewEncoder(conn).Encode(request);err!=nil{return ""}
 reader:=bufio.NewReader(io.LimitReader(conn,4096));line,err:=reader.ReadBytes('\n');if err!=nil{return ""}
 var reply citaFirstUseReply
 if json.Unmarshal(line,&reply)!=nil || reply.Version!=1 || reply.Nonce!=request.Nonce || reply.RouterPid!=request.RouterPid || len(reply.Epoch)>256 {return ""}
 signature:=citaGateSign(secret,fmt.Sprintf("1\n%s\n%d\n%s\n%s",reply.Nonce,reply.RouterPid,reply.Route,reply.Epoch))
 if !hmac.Equal([]byte(reply.Mac),[]byte(signature)){return ""}
 if reply.Route!="direct" && reply.Route!="dpi" && reply.Route!="vpn" {return ""}
 return reply.Route
}
func (r *Router) citaFirstUse(ctx context.Context,m *adapter.InboundContext) string {
 root:=os.Getenv("CITA_FIRST_USE_ROOT");if root=="" || !citaGateEligible(m){return ""}
 select {case citaFirstUseSlots<-struct{}{}:defer func(){<-citaFirstUseSlots}();default:return ""}
 // Existing protected local controller credential, never logged or sent on wire.
 key,err:=os.ReadFile(filepath.Join(root,"private","controller-token.txt"));if err!=nil{return ""}
 secret:=strings.TrimSpace(string(key));if len(secret)!=64{return ""}
 domain:=m.Domain;if domain==""{domain=m.Destination.Fqdn}
 return citaGateExchange(ctx,"127.0.0.1:20833",secret,citaFirstUseRequest{RouterPid:os.Getpid(),Domain:strings.ToLower(domain),Application:m.ProcessInfo.ProcessPath})
}
