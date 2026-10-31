package socks
import("testing";"time";"encoding/json";"encoding/base64";"strings";"net/netip";"sync";"bytes";"context";"errors";"fmt";"github.com/sagernet/sing-box/adapter";M "github.com/sagernet/sing/common/metadata")
func policyUpdate(revision uint64,epoch string,rules []nativePolicyRule,proof string)*nativePolicyUpdate{b,_:=json.Marshal(nativePolicyDocument{Epoch:epoch,Rules:rules});return &nativePolicyUpdate{Revision:revision,Document:base64.StdEncoding.EncodeToString(b),GuardDigest:proof}}
func TestCitaNativePolicyAtomicAndScoped(t *testing.T){
 now:=time.Now();epoch:=strings.Repeat("a",32);rule:=nativePolicyRule{Domain:"Example.COM.",Application:`C:\Program Files\Browser\browser.exe`,Port:443,Network:"tcp",Route:"direct",Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:now.Add(time.Minute).UnixMilli()}
 update:=policyUpdate(1,epoch,[]nativePolicyRule{rule},strings.Repeat("b",64));p,e:=prepareNativePolicy(update,nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
 if r,ok:=p.selectRule("example.com",strings.ToLower(rule.Application),"tcp",443,netip.MustParseAddr("1.1.1.1"),now);!ok||r.Route!="direct"{t.Fatal("scoped rule missing")}
 for _,q:=range []struct{domain,app,network,ip string;port uint16}{{"other.example",rule.Application,"tcp","1.1.1.1",443},{"example.com",`C:\other.exe`,"tcp","1.1.1.1",443},{"example.com",rule.Application,"udp","1.1.1.1",443},{"example.com",rule.Application,"tcp","1.0.0.1",443},{"example.com",rule.Application,"tcp","1.1.1.1",80}}{if _,ok:=p.selectRule(q.domain,q.app,q.network,q.port,netip.MustParseAddr(q.ip),now);ok{t.Fatal("scope widened")}}
 if _,ok:=p.selectRule("example.com",rule.Application,"tcp",443,netip.MustParseAddr("1.1.1.1"),now.Add(2*time.Minute));ok{t.Fatal("expired rule selected")}
 if _,e:=prepareNativePolicy(update,p,now);e!=nil{t.Fatal("same generation retry refused")};bad:=policyUpdate(1,epoch,nil,"");if _,e:=prepareNativePolicy(bad,p,now);e==nil{t.Fatal("same generation changed")}
 next,e:=prepareNativePolicy(policyUpdate(2,epoch,nil,""),p,now);if e!=nil||len(next.rules)!=0||len(p.rules)!=1{t.Fatal("atomic replacement mutated retained policy")}
}
func TestCitaNativePolicyRejectsUnprotectedDirect(t *testing.T){
 now:=time.Now();r:=nativePolicyRule{Domain:"example.com",Network:"tcp",Port:443,Route:"direct",Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:now.Add(time.Minute).UnixMilli()};epoch:=strings.Repeat("a",32)
 if _,e:=prepareNativePolicy(policyUpdate(1,epoch,[]nativePolicyRule{r},""),nativePolicyTable{},now);e==nil{t.Fatal("no guard proof")}
 for _,mutate:=range []func(*nativePolicyRule){func(r *nativePolicyRule){r.Domain="*.example.com"},func(r *nativePolicyRule){r.Application="browser.exe"},func(r *nativePolicyRule){r.Addresses=[]string{"127.0.0.1"}},func(r *nativePolicyRule){r.ExpiresUnixMilliseconds=now.Add(time.Hour).UnixMilli()},func(r *nativePolicyRule){r.Network="all"},func(r *nativePolicyRule){r.Port=0}}{bad:=r;mutate(&bad);if _,e:=prepareNativePolicy(policyUpdate(1,epoch,[]nativePolicyRule{bad},strings.Repeat("b",64)),nativePolicyTable{},now);e==nil{t.Fatal("unsafe policy accepted")}}
}
func TestCitaNativePolicyDoesNotRetireLiveBinding(t *testing.T){
 now:=time.Now();b:=testBinding();flow:=&bindingCountCloser{};b.register(flow,false);h:=&CitaNativeBinding{owner:strings.Repeat("a",32),session:strings.Repeat("b",32),generation:7,state:"Active",active:b}
 r:=nativePolicyRule{Domain:"example.com",Network:"tcp",Port:443,Route:"blocked",ExpiresUnixMilliseconds:now.Add(time.Minute).UnixMilli()}
 command:=bindingCommand{Owner:h.owner,Action:"Policy",Session:h.session,Generation:7,Policy:policyUpdate(1,strings.Repeat("c",32),[]nativePolicyRule{r},"")}
 reply:=h.apply(command);if !reply.Success||reply.PolicyRevision!=1||h.active!=b||flow.count.Load()!=0{t.Fatal("policy update interrupted an admitted flow")}
 command.Generation=6;if h.apply(command).Success{t.Fatal("stale session policy accepted")}
 if nativePolicyKey("example.com","","tcp",55301)==nativePolicyKey("example.com","","tcp",55302){t.Fatal("high ports alias")}
}
func TestCitaNativePolicyAddressMismatchPreservesWildcardBlock(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;expiry:=now.Add(time.Minute).UnixMilli()
 for _,route:=range []string{"direct","dpi","zapret"}{
  scoped:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:route,Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:expiry}
  block:=nativePolicyRule{Domain:"example.com",Network:"tcp",Port:443,Route:"blocked",ExpiresUnixMilliseconds:expiry}
  p,e:=prepareNativePolicy(policyUpdate(1,strings.Repeat("a",32),[]nativePolicyRule{scoped,block},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
  for _,q:=range []struct{app,ip,want string;port uint16;at time.Time}{{app,"1.1.1.1",route,443,now},{app,"1.0.0.1","blocked",443,now},{`C:\other.exe`,"1.0.0.1","blocked",443,now},{app,"1.0.0.1","",8443,now},{app,"1.0.0.1","",443,now.Add(2*time.Minute)}}{
   got,ok:=p.selectRule("example.com",q.app,"tcp",q.port,netip.MustParseAddr(q.ip),q.at);if ok!=(q.want!="")||ok&&got.Route!=q.want{t.Fatalf("%s: wanted %s, got %s selected=%v",route,q.want,got.Route,ok)}
  }
  got,ok:=p.selectRule("example.com",app,"tcp",443,netip.Addr{},now);if !ok||got.Route!="blocked"{t.Fatalf("%s: invalid address bypassed block",route)}
 }
}
func TestCitaNativePolicyFailedDirectPreservesAndHonorsBlock(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;expiry:=now.Add(time.Minute).UnixMilli()
 direct:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:"direct",Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:expiry}
 block:=nativePolicyRule{Domain:"example.com",Network:"tcp",Port:443,Route:"blocked",ExpiresUnixMilliseconds:expiry}
 p,e:=prepareNativePolicy(policyUpdate(1,strings.Repeat("a",32),[]nativePolicyRule{direct,block},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
 h:=&CitaNativeBinding{policy:p};if !h.invalidatePolicy("example.com",app,app,"tcp",443,netip.MustParseAddr("1.1.1.1"),p.revision){t.Fatal("failed direct ignored explicit block")}
 if len(h.policy.rules)!=1||h.policy.rules[nativePolicyKey("example.com","","tcp",443)].Route!="blocked"{t.Fatal("failed direct removed protected block")}
 if h.invalidatePolicy("example.com",app,app,"tcp",8443,netip.MustParseAddr("1.1.1.1"),p.revision){t.Fatal("block leaked to unrelated port")}
 h.policy=p;h.policy.revision++;if !h.invalidatePolicy("example.com",app,app,"tcp",443,netip.MustParseAddr("1.1.1.1"),p.revision){t.Fatal("stale failed dial was allowed to fall through")};if len(h.policy.rules)!=2{t.Fatal("stale dial deleted newer policy")}
}
func TestCitaNativePolicyMissingDirectLeasePreservesBlock(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;expiry:=now.Add(time.Minute).UnixMilli()
 for _,network:=range []string{"tcp","udp"}{
  direct:=nativePolicyRule{Domain:"example.com",Application:app,Network:network,Port:443,Route:"direct",Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:expiry}
  block:=nativePolicyRule{Domain:"example.com",Network:network,Port:443,Route:"blocked",ExpiresUnixMilliseconds:expiry}
  p,e:=prepareNativePolicy(policyUpdate(1,strings.Repeat("a",32),[]nativePolicyRule{direct,block},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
  h:=&CitaNativeBinding{policy:p,state:"Active"};dest:=M.Socksaddr{Addr:netip.MustParseAddr("1.1.1.1"),Port:443};route,_,selected,lease,_,_:=h.policyRoute("example.com",&adapter.ConnectionOwner{ProcessPath:app},network,dest)
  if !selected||route!="blocked"||lease!=nil{t.Fatalf("%s: unavailable direct lease bypassed block",network)}
 }
}
func TestCitaNativePolicyFailureRetiresOnlySelectedScope(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;other:=`C:\Other\browser.exe`;expiry:=now.Add(time.Minute).UnixMilli();address:=netip.MustParseAddr("1.1.1.1")
 rule:=func(application string)nativePolicyRule{return nativePolicyRule{Domain:"example.com",Application:application,Network:"tcp",Port:443,Route:"direct",Addresses:[]string{address.String()},ExpiresUnixMilliseconds:expiry}}
 p,e:=prepareNativePolicy(policyUpdate(1,strings.Repeat("a",32),[]nativePolicyRule{rule(app),rule(other),rule("")},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
 h:=&CitaNativeBinding{policy:p};if h.invalidatePolicy("example.com",app,app,"tcp",443,address,p.revision){t.Fatal("unexpected block")}
 if len(h.policy.rules)!=2{t.Fatal("app-specific failure retired unrelated rule")};if _,ok:=h.policy.rules[nativePolicyKey("example.com","","tcp",443)];!ok{t.Fatal("wildcard rule retired by app-specific failure")};if _,ok:=h.policy.rules[nativePolicyKey("example.com",strings.ToLower(other),"tcp",443)];!ok{t.Fatal("other application rule retired")}
 if h.invalidatePolicy("example.com",app,app,"tcp",443,address,p.revision)||len(h.policy.rules)!=2{t.Fatal("second same-revision failure retired another scope")}
 h.policy=p;var wait sync.WaitGroup;wait.Add(2);for i:=0;i<2;i++{go func(){defer wait.Done();h.invalidatePolicy("example.com",app,app,"tcp",443,address,p.revision)}()};wait.Wait();if len(h.policy.rules)!=2{t.Fatal("concurrent same-revision failures retired another scope")}
 h.policy=p;if h.invalidatePolicy("example.com",app,"","tcp",443,address,p.revision){t.Fatal("unexpected block")};if len(h.policy.rules)!=2{t.Fatal("wildcard failure retired application rules")}
 if !h.invalidatePolicy("example.com",app,other,"tcp",443,address,p.revision){t.Fatal("foreign scope must fail closed")}
}
func TestCitaNativePolicyForcedNeverFallsBack(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;address:=netip.MustParseAddr("1.1.1.1");expiry:=now.Add(time.Minute).UnixMilli()
 for _,route:=range []string{"direct","dpi","zapret"}{
  r:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:route,Addresses:[]string{address.String()},ExpiresUnixMilliseconds:expiry,Forced:true}
  p,e:=prepareNativePolicy(policyUpdate(1,strings.Repeat("a",32),[]nativePolicyRule{r},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
  for _,q:=range []struct{ip netip.Addr;at time.Time}{{netip.MustParseAddr("1.0.0.1"),now},{netip.Addr{},now},{address,now.Add(2*time.Minute)}}{got,ok:=p.selectRule("example.com",app,"tcp",443,q.ip,q.at);if !ok||got.Route!="blocked"{t.Fatalf("%s: forced rule fell back on mismatch or expiry",route)}}
  h:=&CitaNativeBinding{policy:p,state:"Active"};if route!="zapret"{dest:=M.Socksaddr{Addr:address,Port:443};got,_,ok,lease,_,_:=h.policyRoute("example.com",&adapter.ConnectionOwner{ProcessPath:app},"tcp",dest);if !ok||got!="blocked"||lease!=nil{t.Fatalf("%s: forced rule fell back on missing lease",route)};if !h.invalidatePolicy("example.com",app,app,"tcp",443,address,p.revision)||len(h.policy.rules)!=1{t.Fatalf("%s: forced rule removed after failure",route)}}
 }
}
func TestCitaNativePolicyFailedZapretNewDial(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;address:=netip.MustParseAddr("1.1.1.1");dest:=M.Socksaddr{Addr:address,Port:443}
 rule:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:"zapret",Addresses:[]string{address.String()},ExpiresUnixMilliseconds:now.Add(time.Minute).UnixMilli()}
 makePolicy:=func(revision uint64,r nativePolicyRule)nativePolicyTable{p,e:=prepareNativePolicy(policyUpdate(revision,strings.Repeat("a",32),[]nativePolicyRule{r},strings.Repeat("b",64)),nativePolicyTable{},now);if e!=nil{t.Fatal(e)};return p}
 metadata:=&adapter.InboundContext{Domain:"example.com",ProcessInfo:&adapter.ConnectionOwner{ProcessPath:app}};ctx:=adapter.WithContext(context.Background(),metadata)
 existing:=&bindingCountCloser{};binding:=testBinding();binding.register(existing,false)
 h:=&CitaNativeBinding{policy:makePolicy(1,rule),state:"Active",active:binding}
 if _,e:=h.DialContext(ctx,"tcp",dest);e==nil{t.Fatal("unconfigured Zapret connector unexpectedly accepted a dial")}
 if existing.count.Load()!=0{t.Fatal("failed new dial closed an existing flow")}
 if _,_,selected,_,_,_:=h.policyRoute("example.com",metadata.ProcessInfo,"tcp",dest);selected{t.Fatal("failed Zapret rule still selected")}
 // With the binding intentionally absent, the next connection must reach the
 // ordinary VPN branch, which reports errNativeBinding before any network I/O.
 h.active=nil
 if _,e:=h.DialContext(ctx,"tcp",dest);!errors.Is(e,errNativeBinding){t.Fatalf("next connection did not use VPN branch: %v",e)}
 newer:=makePolicy(2,rule);h.policy=newer
 if !h.invalidatePolicy("example.com",app,app,"tcp",443,address,1)||len(h.policy.rules)!=1{t.Fatal("stale failure removed a newer policy")}
 rule.Forced=true;h.policy=makePolicy(3,rule);h.active=binding
 if _,e:=h.DialContext(ctx,"tcp",dest);e==nil{t.Fatal("forced Zapret failure was ignored")}
 if got,_,selected,_,_,_:=h.policyRoute("example.com",metadata.ProcessInfo,"tcp",dest);!selected||got!="zapret"{t.Fatal("forced Zapret was weakened")}
}
func TestCitaNativePolicyRetiredCalibrationCannotResurrect(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;addr:=netip.MustParseAddr("1.1.1.1");id:=strings.Repeat("a",32)
 rule:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:"zapret",Addresses:[]string{addr.String()},ExpiresUnixMilliseconds:now.Add(time.Minute).UnixMilli(),CalibrationId:id}
 epoch:=strings.Repeat("b",32);proof:=strings.Repeat("c",64)
 initial:=policyUpdate(1,epoch,[]nativePolicyRule{rule},proof);p,e:=prepareNativePolicy(initial,nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
 b:=testBinding();existing:=&bindingCountCloser{};b.register(existing,false)
 h:=&CitaNativeBinding{policy:p,state:"Active",active:b,owner:strings.Repeat("d",32),session:strings.Repeat("e",32),generation:1}
 ctx:=adapter.WithContext(context.Background(),&adapter.InboundContext{Domain:rule.Domain,ProcessInfo:&adapter.ConnectionOwner{ProcessPath:app}})
 if _,e=h.DialContext(ctx,"tcp",M.Socksaddr{Addr:addr,Port:443});e==nil{t.Fatal("failed dial unexpectedly succeeded")}
 if existing.count.Load()!=0{t.Fatal("failed dial retired an existing flow")}
 h.connector=&nativeConnectorConfig{Pipe:"Cita.CoreConnector."+strings.Repeat("1",32),Epoch:strings.Repeat("2",32),Capability:strings.Repeat("3",64),ServerPid:1}
 apply:=func(update *nativePolicyUpdate)bool{return h.apply(bindingCommand{Owner:h.owner,Action:"Policy",Session:h.session,Generation:h.generation,Policy:update}).Success}
 // A same-revision idempotent replay must never rebuild the deleted map.
 if !apply(initial)||len(h.policy.rules)!=0{t.Fatal("idempotent replay resurrected failed rule")}
 other:=rule;other.Domain="other.example";other.CalibrationId=strings.Repeat("4",32)
 if apply(policyUpdate(2,epoch,[]nativePolicyRule{rule,other},proof))||len(h.policy.rules)!=0{t.Fatal("new revision resurrected retired proof")}
 if !apply(policyUpdate(2,epoch,nil,"")){t.Fatal("empty reset refused")}
 if apply(policyUpdate(3,strings.Repeat("f",32),[]nativePolicyRule{rule},proof)){t.Fatal("new epoch resurrected retired proof")}
 fresh:=rule;fresh.CalibrationId=strings.Repeat("5",32)
 if !apply(policyUpdate(3,epoch,[]nativePolicyRule{fresh},proof)){t.Fatal("fresh proof refused")}
 if !h.invalidatePolicy(rule.Domain,app,app,"tcp",443,addr,1){t.Fatal("stale failure did not fail closed")}
 if _,ok:=h.policy.rules[nativePolicyKey(rule.Domain,strings.ToLower(app),"tcp",443)];!ok{t.Fatal("stale failure removed fresh proof")}
}
func TestCitaNativePolicyRetiredCalibrationBounds(t *testing.T){
 now:=time.Now();h:=&CitaNativeBinding{retiredCalibrations:map[string]time.Time{"expired":now.Add(-time.Second)}}
 h.retireCalibrationLocked(nativePolicyRule{CalibrationId:strings.Repeat("a",32),deadline:now.Add(time.Minute)},now)
 if _,ok:=h.retiredCalibrations["expired"];ok{t.Fatal("expired tombstone retained")}
 forced:=nativePolicyRule{Domain:"forced.example",Route:"blocked",Forced:true};manual:=nativePolicyRule{Domain:"manual.example",Route:"vpn"};auto:=nativePolicyRule{Domain:"auto.example",Route:"zapret",CalibrationId:strings.Repeat("b",32)}
 h.policy.rules=map[string]nativePolicyRule{"forced":forced,"manual":manual,"auto":auto}
 h.retiredCalibrations=make(map[string]time.Time,nativeRetiredCalibrationLimit)
 for i:=0;i<nativeRetiredCalibrationLimit;i++{h.retiredCalibrations[fmt.Sprintf("%032x",i)]=now.Add(time.Minute)}
 h.retireCalibrationLocked(nativePolicyRule{CalibrationId:strings.Repeat("f",32),deadline:now.Add(time.Minute)},now)
 if !h.calibrationOverflow||len(h.policy.rules)!=2||h.policy.rules["forced"].Route!="blocked"||h.policy.rules["manual"].Route!="vpn"{t.Fatal("overflow weakened manual policy or retained automatic rule")}
 if h.acceptsCalibrationsLocked(nativePolicyTable{rules:map[string]nativePolicyRule{"auto":auto}},now){t.Fatal("overflow accepted automatic rule")}
 if !h.acceptsCalibrationsLocked(nativePolicyTable{rules:map[string]nativePolicyRule{"forced":forced}},now.Add(2*time.Minute)){t.Fatal("overflow refused explicit rule")}
 if !h.calibrationOverflow{t.Fatal("overflow must remain sticky after expiry")}
}
func TestCitaNativePolicyClockRollbackRetainsExpirySafety(t *testing.T){
 now:=time.Now();app:=`C:\Browser\browser.exe`;id:=strings.Repeat("a",32);expires:=now.Add(time.Minute)
 rule:=nativePolicyRule{Domain:"example.com",Application:app,Network:"tcp",Port:443,Route:"zapret",Addresses:[]string{"1.1.1.1"},ExpiresUnixMilliseconds:expires.UnixMilli(),CalibrationId:id}
 epoch:=strings.Repeat("b",32);proof:=strings.Repeat("c",64)
 old:=policyUpdate(1,epoch,[]nativePolicyRule{rule},proof);p,e:=prepareNativePolicy(old,nativePolicyTable{},now);if e!=nil{t.Fatal(e)}
 h:=&CitaNativeBinding{policy:p};h.invalidatePolicy(rule.Domain,app,app,"tcp",443,netip.MustParseAddr("1.1.1.1"),1)
 later:=now.Add(2*time.Minute);if !h.acceptsCalibrationsLocked(nativePolicyTable{},later)||len(h.retiredCalibrations)!=0{t.Fatal("expired proof was not pruned")}
 back:=now.Add(30*time.Second);h.policy.rules["manual"]=nativePolicyRule{Route:"vpn"};h.policy.rules["forced"]=nativePolicyRule{Route:"blocked",Forced:true};h.policy.rules["auto"]=nativePolicyRule{Route:"zapret",CalibrationId:strings.Repeat("d",32)}
 clockOK:=h.observeCalibrationClockLocked(back);if clockOK||len(h.policy.rules)!=2||h.policy.rules["forced"].Route!="blocked"||h.policy.rules["manual"].Route!="vpn"{t.Fatal("clock rollback did not withdraw only automatic rules")}
 oldRevision:=policyUpdate(2,epoch,[]nativePolicyRule{rule},proof);candidate,e:=prepareNativePolicy(oldRevision,h.policy,back);if e!=nil{t.Fatal(e)}
 if h.acceptsCalibrationsLocked(candidate,back)||!nativeUpdateHasCalibration(oldRevision){t.Fatal("clock rollback accepted expired proof")}
 empty:=policyUpdate(2,epoch,nil,"");if nativeUpdateHasCalibration(empty){t.Fatal("empty reset incorrectly classified")};candidate,e=prepareNativePolicy(empty,h.policy,back);if e!=nil||!h.acceptsCalibrationsLocked(candidate,back){t.Fatal("empty reset refused during clock rollback")}
 h.policy=candidate;if _,e=prepareNativePolicy(oldRevision,h.policy,later);e==nil{t.Fatal("expired old proof accepted after clock caught up")};fresh:=rule;fresh.CalibrationId=strings.Repeat("f",32);fresh.ExpiresUnixMilliseconds=later.Add(time.Minute).UnixMilli()
 candidate,e=prepareNativePolicy(policyUpdate(3,epoch,[]nativePolicyRule{fresh},proof),h.policy,later);if e!=nil||!h.acceptsCalibrationsLocked(candidate,later){t.Fatal("fresh proof refused after clock caught up")}
 wildcard:=fresh;wildcard.Application="";if _,e=prepareNativePolicy(policyUpdate(4,epoch,[]nativePolicyRule{wildcard},proof),candidate,later);e==nil{t.Fatal("automatic wildcard application accepted")}
}
func TestCitaNativePolicyLegacyDecoderRejectsForced(t *testing.T){
 type legacyRule struct{Domain,Application,Network,Route string;Port uint16;Addresses []string;ExpiresUnixMilliseconds int64}
 for _,forced:=range []bool{false,true}{
  payload,e:=json.Marshal(nativePolicyRule{Domain:"example.com",Forced:forced});if e!=nil{t.Fatal(e)}
  decoder:=json.NewDecoder(bytes.NewReader(payload));decoder.DisallowUnknownFields();var legacy legacyRule;e=decoder.Decode(&legacy)
  if (e!=nil)!=forced{t.Fatalf("legacy decoder forced=%v, error=%v",forced,e)}
 }
}
