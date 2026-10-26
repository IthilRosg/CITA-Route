package socks
import("testing";"fmt";"context";"net";"time")
func TestCitaNativePolicyProbeSharedDeadline(t *testing.T){
 a,b:=net.Pipe();defer a.Close();defer b.Close()
 if nativeProbeSetDeadline(context.Background(),a)==nil{t.Fatal("unbounded probe accepted")}
 // Dial/TLS have already used the whole budget. Reading must time out now,
 // rather than gaining another socket timeout after the expired context.
 ctx,cancel:=context.WithDeadline(context.Background(),time.Now().Add(-time.Millisecond));defer cancel()
 if e:=nativeProbeSetDeadline(ctx,a);e!=nil{t.Fatal(e)}
 safety:=time.AfterFunc(250*time.Millisecond,func(){a.Close()});defer safety.Stop()
 _,e:=a.Read(make([]byte,1));timeout,ok:=e.(net.Error);if !ok||!timeout.Timeout(){t.Fatalf("read escaped the absolute deadline: %v",e)}
}
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
func TestCitaNativePolicyObservationRotation(t *testing.T){
 h:=&CitaNativeBinding{state:"Active"}
 observation:=func(i int)nativeObservation{return nativeObservation{Domain:fmt.Sprintf("h%d.example.com",i),Address:"1.1.1.1",Port:443}}
 for i:=0;i<128;i++{h.observe(observation(i))}
 h.observe(observation(0));h.observe(observation(128))
 if _,ok:=h.observations[observationKey(observation(0))];!ok{t.Fatal("recently refreshed observation evicted")}
 if _,ok:=h.observations[observationKey(observation(1))];ok{t.Fatal("oldest observation retained at capacity")}
 if _,ok:=h.observations[observationKey(observation(128))];!ok{t.Fatal("new destination ignored after capacity")}
 for i:=129;i<10000;i++{h.observe(observation(i));if len(h.observations)!=128||len(h.observationOrder)!=128{t.Fatal("rotation grew beyond budget")}}
 for i:=9872;i<10000;i++{if _,ok:=h.observations[observationKey(observation(i))];!ok{t.Fatal("latest observation missing")}}
 // Rotated evidence must no longer authorize an independent Direct probe.
 stale:=observation(0);h.owner="owner";h.session="session";h.generation=1
 if h.probeDirect(bindingCommand{Owner:"owner",Action:"ProbeDirect",Session:"session",Generation:1,Observation:&stale}).Success{t.Fatal("evicted observation authorized probe")}
 h.observations=nil;h.observe(observation(7));if len(h.observationOrder)!=1{t.Fatal("new session inherited old observation order")}
}
