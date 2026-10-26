package main
import("fmt";"context";"sync/atomic";"testing";"time";"golang.org/x/net/dns/dnsmessage")
func TestDNSBrokerCoalescingAndEpoch(t *testing.T){
 root,stop:=context.WithCancel(context.Background());defer stop();var calls atomic.Int32;var epoch atomic.Int32
 entered:=make(chan struct{},3);release:=make(chan struct{});b:=newDnsBroker(root,func()string{if epoch.Load()==0{return "a"};return "b"},func(ctx context.Context,_ string)(string,time.Duration,error){calls.Add(1);entered<-struct{}{};select{case <-release:return "1.1.1.1",time.Minute,nil;case <-ctx.Done():return "",0,ctx.Err()}})
 first,cancel:=context.WithCancel(root);done:=make(chan error,2);go func(){_,e:=b.resolve(first,"example.com");done<-e}();<-entered
 go func(){_,e:=b.resolve(root,"example.com");done<-e}();deadline:=time.Now().Add(time.Second);for{b.mu.Lock();waiters:=b.pending["example.com"].waiters;b.mu.Unlock();if waiters==2{break};if time.Now().After(deadline){t.Fatal("second waiter missing")};time.Sleep(time.Millisecond)}
 cancel();if <-done==nil{t.Fatal("cancel ignored")};close(release);if <-done!=nil||calls.Load()!=1{t.Fatal("one waiter canceled shared lookup")}
 if _,e:=b.resolve(root,"EXAMPLE.COM.");e!=nil||calls.Load()!=1{t.Fatal("cache normalization missed")};epoch.Store(1);if _,e:=b.resolve(root,"example.com");e!=nil||calls.Load()!=2{t.Fatal("new network reused old cache")}
 for _,name:=range []string{"server","host.local","device.tail.ts.net","home.arpa","x.internal"}{if _,e:=b.resolve(root,name);e==nil{t.Fatal("private namespace escaped to VPN DNS")}}
 stop();if _,e:=b.resolve(context.Background(),"example.com");e==nil{t.Fatal("old profile cache survived cancellation")}
}
func TestDNSBrokerZeroTTLAndLateResponse(t *testing.T){
 var calls atomic.Int32;b:=newDnsBroker(context.Background(),func()string{return "a"},func(context.Context,string)(string,time.Duration,error){calls.Add(1);return "1.1.1.1",0,nil});for i:=0;i<2;i++{if _,e:=b.resolve(context.Background(),"example.com");e!=nil{t.Fatal(e)}};if calls.Load()!=2{t.Fatal("zero TTL cached")}
 var epoch atomic.Int32;entered:=make(chan struct{});release:=make(chan struct{});b=newDnsBroker(context.Background(),func()string{if epoch.Load()==0{return "a"};return "b"},func(context.Context,string)(string,time.Duration,error){close(entered);<-release;return "1.1.1.1",time.Minute,nil});done:=make(chan error,1);go func(){_,e:=b.resolve(context.Background(),"example.com");done<-e}();<-entered;epoch.Store(1);close(release);if <-done==nil{t.Fatal("late network answer accepted")};if len(b.cache)!=0{t.Fatal("late answer cached")}
}
func TestDNSProfileAnswerValidation(t *testing.T){
 name,_:=dnsmessage.NewName("example.com.");q:=dnsmessage.Question{Name:name,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET};reply:=dnsmessage.Message{Header:dnsmessage.Header{ID:7,Response:true},Questions:[]dnsmessage.Question{q},Answers:[]dnsmessage.Resource{{Header:dnsmessage.ResourceHeader{Name:name,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET,TTL:5},Body:&dnsmessage.AResource{A:[4]byte{1,1,1,1}}}}}
 packed,_:=reply.Pack();ip,ttl,e:=parseProfileDNS(packed,7,q);if e!=nil||ip!="1.1.1.1"||ttl!=5*time.Second{t.Fatal("A/TTL failed")};if _,_,e=parseProfileDNS(packed,8,q);e==nil{t.Fatal("wrong transaction accepted")}
 reply.Answers[0].Header.TTL=0;packed,_=reply.Pack();_,ttl,e=parseProfileDNS(packed,7,q);if e!=nil||ttl!=0{t.Fatal("zero TTL not honored")};reply.Answers[0].Body=&dnsmessage.AResource{A:[4]byte{198,18,0,1}};packed,_=reply.Pack();if _,_,e=parseProfileDNS(packed,7,q);e==nil{t.Fatal("Fake-IP DNS answer admitted")}
 reply.Truncated=true;packed,_=reply.Pack();if _,_,e=parseProfileDNS(packed,7,q);e==nil{t.Fatal("truncated answer admitted")}
}
func TestDNSBrokerCapacityExpiryAndLastWaiter(t *testing.T){
 b:=newDnsBroker(context.Background(),func()string{return "a"},func(context.Context,string)(string,time.Duration,error){return "1.1.1.1",time.Hour,nil})
 for i:=0;i<300;i++{name:=fmt.Sprintf("h%d.example",i);if _,e:=b.resolve(context.Background(),name);e!=nil{t.Fatal(e)}};if len(b.cache)!=256{t.Fatal("cache unbounded")}
 b.mu.Lock();b.cache["expired.example"]=dnsValue{"8.8.8.8",time.Now().Add(-time.Second)};b.mu.Unlock();ip,e:=b.resolve(context.Background(),"expired.example");if e!=nil||ip!="1.1.1.1"{t.Fatal("expired answer used")}
 entered:=make(chan struct{});cancelled:=make(chan struct{});b=newDnsBroker(context.Background(),func()string{return "a"},func(ctx context.Context,_ string)(string,time.Duration,error){close(entered);<-ctx.Done();close(cancelled);return "",0,ctx.Err()})
 ctx,cancel:=context.WithCancel(context.Background());done:=make(chan error,1);go func(){_,e:=b.resolve(ctx,"example.com");done<-e}();<-entered;cancel();if <-done==nil{t.Fatal("last waiter cancel failed")};select{case <-cancelled:case <-time.After(time.Second):t.Fatal("unneeded query kept running")}
}
func TestDNSProfileCnameTTL(t *testing.T){
 n,_:=dnsmessage.NewName("example.com.");alias,_:=dnsmessage.NewName("cdn.example.com.");q:=dnsmessage.Question{Name:n,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET};r:=dnsmessage.Message{Header:dnsmessage.Header{ID:9,Response:true},Questions:[]dnsmessage.Question{q},Answers:[]dnsmessage.Resource{{Header:dnsmessage.ResourceHeader{Name:n,Type:dnsmessage.TypeCNAME,Class:dnsmessage.ClassINET,TTL:2},Body:&dnsmessage.CNAMEResource{CNAME:alias}},{Header:dnsmessage.ResourceHeader{Name:alias,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET,TTL:20},Body:&dnsmessage.AResource{A:[4]byte{1,1,1,1}}}}};p,_:=r.Pack();_,ttl,e:=parseProfileDNS(p,9,q);if e!=nil||ttl!=2*time.Second{t.Fatal("CNAME minimum TTL lost")}
 r.Answers[0].Body=&dnsmessage.CNAMEResource{CNAME:n};p,_=r.Pack();if _,_,e=parseProfileDNS(p,9,q);e==nil{t.Fatal("CNAME loop accepted")}
}
