package socks
import("testing";"time";"encoding/json";"encoding/base64";"strings";"net/netip")
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
