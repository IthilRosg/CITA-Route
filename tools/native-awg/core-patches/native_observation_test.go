package socks
import("testing";"fmt")
func TestCitaNativePolicyObservationScope(t *testing.T){
 h:=&CitaNativeBinding{state:"Active"};good:=nativeObservation{Domain:"example.com",Address:"1.1.1.1",Port:443};h.observe(good);if len(h.observations)!=1{t.Fatal("missing observation")}
 for _,address:=range []string{"127.0.0.1","10.0.0.1","100.100.100.100","0.1.2.3","240.1.2.3","::1"}{bad:=good;bad.Address=address;h.observe(bad)};if len(h.observations)!=1{t.Fatal("private or reserved scope accepted")}
 bad:=good;bad.Domain="example.com\r\nCookie: secret";h.observe(bad);bad=good;bad.Port=8443;h.observe(bad);if len(h.observations)!=1{t.Fatal("unsafe probe target")}
 for i:=0;i<200;i++{good.Domain=fmt.Sprintf("h%d.example.com",i);h.observe(good)};if len(h.observations)!=128{t.Fatal("unbounded observations")}
 h.state="Closed";h.observations=nil;h.observe(good);if len(h.observations)!=0{t.Fatal("closed controller observed traffic")}
 r:=h.probeDirect(bindingCommand{Action:"ProbeDirect",Observation:&good});if r.Success{t.Fatal("unowned probe accepted")}
}
func TestCitaNativePolicyFailureInvalidatesOnlyAffectedDomain(t *testing.T){
 h:=&CitaNativeBinding{policy:nativePolicyTable{rules:map[string]nativePolicyRule{
 nativePolicyKey("example.com","","tcp",443):{Domain:"example.com",Route:"direct"},
 nativePolicyKey("other.example","","tcp",443):{Domain:"other.example",Route:"direct"},
 }}}
 previous:=h.policy.rules;h.invalidatePolicy("EXAMPLE.COM","","tcp",443)
 if len(h.policy.rules)!=1||len(previous)!=2{t.Fatal("invalidation must preserve unrelated routes and immutable published tables")}
}
