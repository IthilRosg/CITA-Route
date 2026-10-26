package socks

import("testing";"time";"net";"sync")
func scopeFixture()(*CitaNativeBinding,nativeDirectScope,bindingCommand){
 h:=&CitaNativeBinding{owner:"owner",session:"session",generation:1,state:"Active"}
 s:=nativeDirectScope{Address:"1.1.1.1",Network:"tcp",Port:443}
 return h,s,bindingCommand{Owner:"owner",Session:"session",Generation:1,Scope:&s,Action:"EnableDirect"}
}
func TestCitaNativePolicyScopeRetirementBarrier(t *testing.T){
 h,s,c:=scopeFixture();if !h.apply(c).Success{t.Fatal("enable failed")}
 h.mu.Lock();pending:=h.acquireDirectLocked(s);h.mu.Unlock();if pending==nil{t.Fatal("dial was not reserved")}
 c.Action="RetireDirect";if h.apply(c).ScopeRetired{t.Fatal("in-flight dial lost permission")}
 a,b:=net.Pipe();conn:=&nativeDirectConn{Conn:a,lease:pending};_ = b.Close()
 var one [1]byte;_,_=conn.Read(one[:]);if h.apply(c).ScopeRetired{t.Fatal("read EOF released a still-open socket")}
 h.policy.rules=map[string]nativePolicyRule{"shared":{Route:"direct",Network:"tcp",Port:443,Addresses:[]string{s.Address},deadline:time.Now().Add(time.Minute)}}
 _=conn.Close();_=conn.Close();if h.apply(c).ScopeRetired{t.Fatal("another domain policy still needs scope")}
 h.policy.rules=nil;if !h.apply(c).ScopeRetired{t.Fatal("idle scope did not retire")}
 h.mu.Lock();late:=h.acquireDirectLocked(s);h.mu.Unlock();if late!=nil{t.Fatal("dial admitted after retirement acknowledgement")}
 if !h.apply(c).ScopeRetired{t.Fatal("retirement retry not idempotent")}
 c.Action="EnableDirect";if !h.apply(c).Success{t.Fatal("could not re-enable exact scope")}
}
func TestCitaNativePolicyScopeConcurrentReservations(t *testing.T){
 h,s,c:=scopeFixture();h.apply(c);var ready sync.WaitGroup;ready.Add(32);release:=make(chan struct{});done:=make(chan struct{},32)
 for i:=0;i<32;i++{go func(){h.mu.Lock();lease:=h.acquireDirectLocked(s);h.mu.Unlock();ready.Done();<-release;lease.release();done<-struct{}{}}()}
 ready.Wait();c.Action="RetireDirect";if h.apply(c).ScopeRetired{t.Fatal("pending concurrent dials were not counted")};close(release);for i:=0;i<32;i++{<-done};if !h.apply(c).ScopeRetired{t.Fatal("closed reservations leaked")}
}
func TestCitaNativePolicyScopeBudgetAndIdentity(t *testing.T){
 h,s,c:=scopeFixture()
 for i:=0;i<256;i++{s.Port=uint16(1024+i);c.Scope=&s;if !h.apply(c).Success{t.Fatal("budget refused early")}}
 s.Port=2048;if h.apply(c).Success{t.Fatal("unbounded scope admissions")}
 s.Port=1024;c.Action="RetireDirect";c.Generation=2;if h.apply(c).Success{t.Fatal("stale generation accepted")};c.Generation=1;c.Revision=1;if h.apply(c).Success{t.Fatal("stale policy accepted")};c.Revision=0
 c.Owner="other";if h.apply(c).Success{t.Fatal("foreign owner accepted")};c.Owner="owner"
 if !h.apply(c).ScopeRetired{t.Fatal("valid idle scope refused")}
 for k:=range h.directEnabled{delete(h.directEnabled,k)}
 for i:=0;i<10000;i++{s.Port=uint16(1024+i);c.Action="EnableDirect";if !h.apply(c).Success{t.Fatal("rotation exhausted budget")};c.Action="RetireDirect";if !h.apply(c).ScopeRetired||len(h.directEnabled)!=0||len(h.directUses)!=0{t.Fatal("retirement retained historical scope state")}}
}
