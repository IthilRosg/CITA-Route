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
type nativePolicyDocument struct { Epoch string; Rules []nativePolicyRule; ForcedIntentRevision uint64 }
type nativePolicyRule struct {
 Domain,Application,Network,Route string
 Port uint16
 Addresses []string
 ExpiresUnixMilliseconds int64
 Forced bool `json:",omitempty"`
 CalibrationId string `json:",omitempty"`
 deadline time.Time
}
type nativePolicyTable struct { revision uint64; digest,epoch string; forcedRevision uint64; rules map[string]nativePolicyRule }
const nativeRetiredCalibrationLimit=1024
// Monotonic tombstone expiry is safe only while wall time does not regress:
// otherwise an expired, already-pruned proof could appear valid again.
func(h *CitaNativeBinding) observeCalibrationClockLocked(now time.Time)bool{
 ms:=now.UnixMilli()
 if ms<h.calibrationWallHighWater{
  for key,r:=range h.policy.rules{if r.CalibrationId!=""&&!r.Forced{delete(h.policy.rules,key)}}
  return false
 }
 h.calibrationWallHighWater=ms
 return true
}
func(h *CitaNativeBinding) retireCalibrationLocked(rule nativePolicyRule,now time.Time){
 h.observeCalibrationClockLocked(now)
 if rule.CalibrationId==""{return}
 for id,deadline:=range h.retiredCalibrations{if !now.Before(deadline){delete(h.retiredCalibrations,id)}}
 if h.calibrationOverflow{return}
 if h.retiredCalibrations==nil{h.retiredCalibrations=make(map[string]time.Time)}
 if _,exists:=h.retiredCalibrations[rule.CalibrationId];!exists&&len(h.retiredCalibrations)>=nativeRetiredCalibrationLimit{
  h.calibrationOverflow=true
  for key,r:=range h.policy.rules{if r.CalibrationId!=""&&!r.Forced{delete(h.policy.rules,key)}}
  return
 }
 h.retiredCalibrations[rule.CalibrationId]=rule.deadline
}
func(h *CitaNativeBinding) acceptsCalibrationsLocked(table nativePolicyTable,now time.Time)bool{
 clockOK:=h.observeCalibrationClockLocked(now)
 for id,deadline:=range h.retiredCalibrations{if !now.Before(deadline){delete(h.retiredCalibrations,id)}}
 for _,rule:=range table.rules{if rule.CalibrationId!=""{if h.calibrationOverflow||!clockOK{return false};if _,retired:=h.retiredCalibrations[rule.CalibrationId];retired{return false}}}
 return true
}
func nativeUpdateHasCalibration(update *nativePolicyUpdate)bool{
 if update==nil||len(update.Document)>87384{return true}
 data,e:=base64.StdEncoding.Strict().DecodeString(update.Document);if e!=nil||len(data)>65536{return true}
 var document nativePolicyDocument;if json.Unmarshal(data,&document)!=nil{return true}
 for _,rule:=range document.Rules{if rule.CalibrationId!=""{return true}}
 return false
}
func(h *CitaNativeBinding) invalidatePolicy(domain string,processApp string,selectedApp string,network string,port uint16,ip netip.Addr,expectedRevision uint64) bool{
 d,ok:=nativeDomain(domain);if !ok{return true};app,ok:=nativeApplication(processApp);if !ok{return true};scope,ok:=nativeApplication(selectedApp);if !ok||scope!=""&&scope!=app{return true};h.mu.Lock();defer h.mu.Unlock()
 // A failed dial from an older policy must neither delete nor bypass a new policy.
 if h.policy.revision!=expectedRevision{return true}
 key:=nativePolicyKey(d,scope,network,port);if rule,found:=h.policy.rules[key];found&&(rule.Route=="direct"||rule.Route=="dpi"||rule.Route=="zapret"){if rule.Forced{return true};h.retireCalibrationLocked(rule,time.Now())}
 next:=make(map[string]nativePolicyRule,len(h.policy.rules));for k,v:=range h.policy.rules{next[k]=v}
 if rule,found:=next[key];found&&(rule.Route=="direct"||rule.Route=="dpi"||rule.Route=="zapret"){delete(next,key)}
 h.policy.rules=next
 rule,selected:=h.policy.selectRule(d,app,network,port,ip,time.Now());return selected&&rule.Route=="blocked"
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
 table:=nativePolicyTable{revision:update.Revision,digest:digest,epoch:document.Epoch,forcedRevision:document.ForcedIntentRevision,rules:make(map[string]nativePolicyRule)};calibrationIds:=make(map[string]bool)
 for _,rule:=range document.Rules {
  domain,ok:=nativeDomain(rule.Domain);if !ok{return nativePolicyTable{},errNativePolicy};app,ok:=nativeApplication(rule.Application);if !ok||rule.Port==0||rule.Network!="tcp"&&rule.Network!="udp"{return nativePolicyTable{},errNativePolicy}
  lifetime:=time.Duration(rule.ExpiresUnixMilliseconds-now.UnixMilli())*time.Millisecond;if rule.ExpiresUnixMilliseconds<=now.UnixMilli()||rule.ExpiresUnixMilliseconds>now.Add(10*time.Minute).UnixMilli(){return nativePolicyTable{},errNativePolicy}
  rule.Domain=domain;rule.Application=app;rule.deadline=now.Add(lifetime)
  if rule.CalibrationId!=""&&(!validBindingHex(rule.CalibrationId,16)||rule.CalibrationId!=strings.ToLower(rule.CalibrationId)||app==""||rule.Forced||(rule.Route!="direct"&&rule.Route!="dpi"&&rule.Route!="zapret")){return nativePolicyTable{},errNativePolicy}
  if rule.CalibrationId!=""{if calibrationIds[rule.CalibrationId]{return nativePolicyTable{},errNativePolicy};calibrationIds[rule.CalibrationId]=true}
  switch rule.Route {
  case "direct","dpi","zapret":
   if rule.Route=="zapret"&&(rule.Network!="tcp"||rule.Port!=443||app==""){return nativePolicyTable{},errNativePolicy}
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
 for _,application:=range []string{a,""}{if r,found:=p.rules[nativePolicyKey(d,application,network,port)];found{
  if !now.Before(r.deadline){if r.Forced{r.Route="blocked";return r,true};continue}
  if r.Route=="direct"||r.Route=="dpi"||r.Route=="zapret" {
   match:=false;if ip.IsValid()&&ip.Is4(){for _,v:=range r.Addresses{if v==ip.String(){match=true;break}}}
   if !match {
    // An address-pinned exception is inapplicable; never let that mismatch
    // bypass a still-live domain-wide explicit block. Other wildcard paths
    // must not be inferred from an unverified address.
    if r.Forced{r.Route="blocked";return r,true}
    if application!="" {if block,exists:=p.rules[nativePolicyKey(d,"",network,port)];exists&&block.Route=="blocked"&&(block.Forced||now.Before(block.deadline)){return block,true}}
    return nativePolicyRule{},false
   }
  }
  return r,true
 }};return nativePolicyRule{},false
}
