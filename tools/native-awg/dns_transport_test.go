package main
import("context";"encoding/binary";"errors";"io";"net";"net/netip";"sync";"testing";"time";"golang.org/x/net/dns/dnsmessage")
func dnsFixtureDial(mode string,calls *[]string,mu *sync.Mutex)func(context.Context,string,string)(net.Conn,error){
 return func(_ context.Context,network,address string)(net.Conn,error){
  mu.Lock();*calls=append(*calls,network+"|"+address);mu.Unlock()
  if mode=="refused"||mode=="tcp-only"&&network=="udp"{return nil,io.EOF}
  client,server:=net.Pipe();go func(){defer server.Close();_ = server.SetDeadline(time.Now().Add(time.Second));buffer:=make([]byte,512);var packet []byte
   if network=="tcp"{var prefix [2]byte;if _,e:=io.ReadFull(server,prefix[:]);e!=nil{return};size:=int(binary.BigEndian.Uint16(prefix[:]));if size>512{return};packet=make([]byte,size);if _,e:=io.ReadFull(server,packet);e!=nil{return}}else{n,e:=server.Read(buffer);if e!=nil{return};packet=buffer[:n]}
   var q dnsmessage.Message;if q.Unpack(packet)!=nil||len(q.Questions)!=1{return};r:=dnsmessage.Message{Header:dnsmessage.Header{ID:q.ID,Response:true},Questions:q.Questions,Answers:[]dnsmessage.Resource{{Header:dnsmessage.ResourceHeader{Name:q.Questions[0].Name,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET,TTL:17},Body:&dnsmessage.AResource{A:[4]byte{1,1,1,1}}}}}
   if mode=="truncated"&&network=="udp"{r.Truncated=true;r.Answers=nil};if mode=="private"{r.Answers[0].Body=&dnsmessage.AResource{A:[4]byte{10,0,0,1}}};if mode=="nxdomain"{r.RCode=dnsmessage.RCodeNameError;r.Answers=nil};if mode=="servfail"{r.RCode=dnsmessage.RCodeServerFailure;r.Answers=nil};if mode=="denied"{r.RCode=dnsmessage.RCodeRefused;r.Answers=nil}
   if mode=="partial-tc"&&network=="udp"{r.Truncated=true};answer,_:=r.Pack();if mode=="partial-tc"&&network=="udp"{answer=answer[:len(answer)-3]};if network=="tcp"{framed:=make([]byte,len(answer)+2);binary.BigEndian.PutUint16(framed,uint16(len(answer)));copy(framed[2:],answer);answer=framed};_,_=server.Write(answer)
  }();return client,nil
 }
}
func TestDNSProfileTransportFallback(t *testing.T){
 for _,mode:=range []string{"udp-only","tcp-only","truncated","partial-tc","private","nxdomain"}{
  var calls []string;var mu sync.Mutex;ctx,cancel:=context.WithTimeout(context.Background(),time.Second);ip,ttl,e:=queryProfileDNS(ctx,dnsFixtureDial(mode,&calls,&mu),netip.MustParseAddr("1.1.1.1"),"example.com");cancel()
  if mode=="private"||mode=="nxdomain"{if !errors.Is(e,errProxy)||len(calls)!=1{t.Fatal("negative/unsafe answer retried")};continue}
  if e!=nil||ip!="1.1.1.1"||ttl!=17*time.Second{t.Fatalf("%s failed: %v",mode,e)};expected:=1;if mode!="udp-only"{expected=2};if len(calls)!=expected{t.Fatal("wrong DNS transport budget")}
 }
}
func TestDNSProfileServerBudget(t *testing.T){
 var calls []string;var mu sync.Mutex;servers:=[]netip.Addr{netip.MustParseAddr("1.1.1.1"),netip.MustParseAddr("8.8.8.8")};good:=dnsFixtureDial("udp-only",&calls,&mu);bad:=dnsFixtureDial("refused",&calls,&mu)
 dial:=func(ctx context.Context,network,address string)(net.Conn,error){if address=="1.1.1.1:53"{return bad(ctx,network,address)};return good(ctx,network,address)}
 ctx,cancel:=context.WithTimeout(context.Background(),time.Second);defer cancel();if ip,_,e:=queryProfileServers(ctx,dial,servers,"example.com");e!=nil||ip!="1.1.1.1"||len(calls)!=3{t.Fatal("profile secondary resolver not used")}
 for _,mode:=range []string{"servfail","denied"}{calls=nil;unavailable:=dnsFixtureDial(mode,&calls,&mu);route:=func(ctx context.Context,network,address string)(net.Conn,error){if address=="1.1.1.1:53"{return unavailable(ctx,network,address)};return good(ctx,network,address)};if ip,_,e:=queryProfileServers(ctx,route,servers,"example.com");e!=nil||ip!="1.1.1.1"||len(calls)!=3{t.Fatal("unavailable server response prevented fallback")}}
 calls=nil;if _,_,e:=queryProfileServers(ctx,bad,append(servers,servers...),"example.com");e==nil||len(calls)!=6{t.Fatal("more than three profile servers attempted")}
 canceled,stop:=context.WithCancel(context.Background());stop();calls=nil;if _,_,e:=queryProfileServers(canceled,bad,servers,"example.com");e==nil||len(calls)!=0{t.Fatal("cancelled profile contacted DNS")}
}
