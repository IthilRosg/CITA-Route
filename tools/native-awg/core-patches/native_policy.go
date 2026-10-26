package socks

import (
 "bytes"
 "crypto/sha256"
 "encoding/base64"
 "encoding/hex"
 "encoding/json"
 "errors"
 "io"
 "net/netip"
 "strings"
 "strconv"
 "time"
)

var errNativePolicy=errors.New("native policy refused")
type nativePolicyUpdate struct { Revision uint64; Document,GuardDigest string }
type nativePolicyDocument struct { Epoch string; Rules []nativePolicyRule }
type nativePolicyRule struct {
 Domain,Application,Network,Route string
 Port uint16
 Addresses []string
 ExpiresUnixMilliseconds int64
 deadline time.Time
}
type nativePolicyTable struct { revision uint64; digest,epoch string; rules map[string]nativePolicyRule }
func(h *CitaNativeBinding) invalidatePolicy(domain string,processApp string,network string,port uint16){
 d,ok:=nativeDomain(domain);if !ok{return};app,ok:=nativeApplication(processApp);if !ok{return};h.mu.Lock();defer h.mu.Unlock()
 next:=make(map[string]nativePolicyRule,len(h.policy.rules));for k,v:=range h.policy.rules{next[k]=v};delete(next,nativePolicyKey(d,app,network,port));delete(next,nativePolicyKey(d,"",network,port));h.policy.rules=next
}
func nativeDomain(value string)(string,bool){
 s:=strings.TrimSuffix(strings.ToLower(value),".");if len(s)<1||len(s)>253{return "",false};if _,e:=netip.ParseAddr(s);e==nil{return "",false}
 for _,label:=range strings.Split(s,"."){if len(label)<1||len(label)>63||label[0]=='-'||label[len(label)-1]=='-'{return "",false};for _,c:=range label {if c!='-'&&(c<'a'||c>'z')&&(c<'0'||c>'9'){return "",false}}};return s,true
}
func nativeApplication(s string)(string,bool){
 if s==""{return "",true};if len(s)>512||len(s)<3||s[1]!=':'||s[2]!='\\'||!((s[0]>='A'&&s[0]<='Z')||(s[0]>='a'&&s[0]<='z')){return "",false};for _,c:=range s{if c<32||c==127{return "",false}};return strings.ToLower(s),true
}
func nativePolicyKey(domain,app,network string,port uint16) string {return domain+"\x00"+app+"\x00"+network+"\x00"+strconv.Itoa(int(port))}
func prepareNativePolicy(update *nativePolicyUpdate,previous nativePolicyTable,now time.Time)(nativePolicyTable,error){
 if update==nil||update.Revision==0||update.Revision<previous.revision||len(update.Document)>87384{return nativePolicyTable{},errNativePolicy}
 data,e:=base64.StdEncoding.Strict().DecodeString(update.Document);if e!=nil||len(data)>65536{return nativePolicyTable{},errNativePolicy};sum:=sha256.Sum256(data);digest:=hex.EncodeToString(sum[:])
 if update.Revision==previous.revision {if digest==previous.digest{return previous,nil};return nativePolicyTable{},errNativePolicy}
 var document nativePolicyDocument;decoder:=json.NewDecoder(bytes.NewReader(data));decoder.DisallowUnknownFields();if decoder.Decode(&document)!=nil||decoder.Decode(new(any))!=io.EOF||!validBindingHex(document.Epoch,16)||len(document.Rules)>128{return nativePolicyTable{},errNativePolicy}
 table:=nativePolicyTable{revision:update.Revision,digest:digest,epoch:document.Epoch,rules:make(map[string]nativePolicyRule)}
 for _,rule:=range document.Rules {
  domain,ok:=nativeDomain(rule.Domain);if !ok{return nativePolicyTable{},errNativePolicy};app,ok:=nativeApplication(rule.Application);if !ok||rule.Port==0||rule.Network!="tcp"&&rule.Network!="udp"{return nativePolicyTable{},errNativePolicy}
  lifetime:=time.Duration(rule.ExpiresUnixMilliseconds-now.UnixMilli())*time.Millisecond;if rule.ExpiresUnixMilliseconds<=now.UnixMilli()||rule.ExpiresUnixMilliseconds>now.Add(10*time.Minute).UnixMilli(){return nativePolicyTable{},errNativePolicy}
  rule.Domain=domain;rule.Application=app;rule.deadline=now.Add(lifetime)
  switch rule.Route {
  case "direct","dpi":
   if !validBindingHex(update.GuardDigest,32)||len(rule.Addresses)==0||len(rule.Addresses)>4{return nativePolicyTable{},errNativePolicy};seen:=map[string]bool{}
   for _,ip:=range rule.Addresses {a,e:=netip.ParseAddr(ip);if e!=nil||!nativePublic(a)||a.String()!=ip||seen[ip]{return nativePolicyTable{},errNativePolicy};seen[ip]=true}
  case "vpn","blocked":if len(rule.Addresses)!=0{return nativePolicyTable{},errNativePolicy}
  default:return nativePolicyTable{},errNativePolicy
  }
  key:=nativePolicyKey(domain,app,rule.Network,rule.Port);if _,found:=table.rules[key];found{return nativePolicyTable{},errNativePolicy};table.rules[key]=rule
 }
 return table,nil
}
func (p nativePolicyTable) selectRule(domain,app,network string,port uint16,ip netip.Addr,now time.Time)(nativePolicyRule,bool){
 d,ok:=nativeDomain(domain);if !ok{return nativePolicyRule{},false};a,ok:=nativeApplication(app);if !ok{return nativePolicyRule{},false}
 for _,application:=range []string{a,""}{if r,found:=p.rules[nativePolicyKey(d,application,network,port)];found&&now.Before(r.deadline){
  if r.Route=="direct"||r.Route=="dpi" {if !ip.IsValid()||!ip.Is4(){return nativePolicyRule{},false};match:=false;for _,v:=range r.Addresses{if v==ip.String(){match=true;break}};if !match{return nativePolicyRule{},false}}
  return r,true
 }};return nativePolicyRule{},false
}
