package main
import("context";"crypto/rand";"encoding/binary";"errors";"net";"net/netip";"time";"golang.org/x/net/dns/dnsmessage")

func queryProfileDNS(ctx context.Context,dial func(context.Context,string,string)(net.Conn,error),server netip.Addr,name string)(string,time.Duration,error){
 deadline,ok:=ctx.Deadline();if !ok{return "",0,errProxy};remaining:=time.Until(deadline);if remaining<=0{return "",0,context.DeadlineExceeded}
 udpWait:=remaining/3;if udpWait>800*time.Millisecond{udpWait=800*time.Millisecond};udp,cancel:=context.WithTimeout(ctx,udpWait)
 ip,ttl,e:=queryProfileDNSUDP(udp,dial,server,name);cancel();if e==nil||errors.Is(e,errProxy){return ip,ttl,e}
 if ctx.Err()!=nil{return "",0,ctx.Err()};return queryProfileDNSTCP(ctx,dial,server,name)
}
var errDNSTruncated=errors.New("bounded DNS TCP fallback required")
var errDNSServerUnavailable=errors.New("profile DNS server unavailable")
func queryProfileDNSUDP(ctx context.Context,dial func(context.Context,string,string)(net.Conn,error),server netip.Addr,name string)(string,time.Duration,error){
 var random [2]byte;if _,e:=rand.Read(random[:]);e!=nil{return "",0,e};id:=binary.BigEndian.Uint16(random[:]);qname,e:=dnsmessage.NewName(name+".");if e!=nil{return "",0,e}
 question:=dnsmessage.Question{Name:qname,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET};message:=dnsmessage.Message{Header:dnsmessage.Header{ID:id,RecursionDesired:true},Questions:[]dnsmessage.Question{question}};packet,e:=message.Pack();if e!=nil{return "",0,e}
 conn,e:=dial(ctx,"udp",net.JoinHostPort(server.String(),"53"));if e!=nil{return "",0,e};defer conn.Close();cancel:=context.AfterFunc(ctx,func(){conn.Close()});defer cancel();deadline,ok:=ctx.Deadline();if !ok{return "",0,errProxy};if e=conn.SetDeadline(deadline);e!=nil{return "",0,e}
 if n,e:=conn.Write(packet);e!=nil{return "",0,e}else if n!=len(packet){return "",0,errProxy}
 buffer:=make([]byte,513)
 for discarded:=0;discarded<4;discarded++{
  n,e:=conn.Read(buffer);if e!=nil{return "",0,e};if n>512{return "",0,errProxy};var parser dnsmessage.Parser;reply,e:=parser.Start(buffer[:n]);if e!=nil{return "",0,errProxy};questions,e:=parser.AllQuestions();if e!=nil{return "",0,errProxy}
  if reply.ID!=id||len(questions)!=1||questions[0]!=question{continue}
  if !reply.Response||reply.OpCode!=0{return "",0,errProxy};if reply.Truncated{return "",0,errDNSTruncated}
  return parseProfileDNS(buffer[:n],id,question)
 };return "",0,errProxy
}
func queryProfileServers(ctx context.Context,dial func(context.Context,string,string)(net.Conn,error),servers []netip.Addr,name string)(string,time.Duration,error){
 if len(servers)==0{return "",0,errProxy};if len(servers)>3{servers=servers[:3]};deadline,ok:=ctx.Deadline();if !ok{return "",0,errProxy}
 var last error=errProxy
 for i,server:=range servers{
  if ctx.Err()!=nil{return "",0,ctx.Err()};part:=time.Until(deadline)/time.Duration(len(servers)-i);attempt,cancel:=context.WithTimeout(ctx,part)
  ip,ttl,e:=queryProfileDNS(attempt,dial,server,name);cancel();if e==nil||errors.Is(e,errProxy){return ip,ttl,e};last=e
 };return "",0,last
}
