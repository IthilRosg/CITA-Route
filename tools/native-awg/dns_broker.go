package main

import (
 "context"
 "crypto/rand"
 "encoding/binary"
 "io"
 "net"
 "net/netip"
 "sort"
 "strings"
 "sync"
 "time"
 "golang.org/x/net/dns/dnsmessage"
)

type dnsValue struct{ ip string; until time.Time }
type dnsFlight struct{done chan struct{};cancel context.CancelFunc;waiters int;ip string;err error;epoch uint64}
type dnsBroker struct{
 mu sync.Mutex;root context.Context;lookup func(context.Context,string)(string,time.Duration,error)
 identity func()string;stamp string;epoch uint64;cache map[string]dnsValue;pending map[string]*dnsFlight
}
func newDnsBroker(ctx context.Context,identity func()string,lookup func(context.Context,string)(string,time.Duration,error))*dnsBroker{
 return &dnsBroker{root:ctx,identity:identity,lookup:lookup,stamp:identity(),cache:map[string]dnsValue{},pending:map[string]*dnsFlight{}}
}
func localDNSName(name string)bool{
 if !strings.Contains(name,"."){return true};for _,suffix:=range []string{"local","localhost","internal","home.arpa","ts.net","in-addr.arpa","ip6.arpa"}{if name==suffix||strings.HasSuffix(name,"."+suffix){return true}};return false
}
func dnsInterfaceIdentity()string{
 interfaces,e:=net.Interfaces();if e!=nil{return "unavailable"};var values []string
 for _,nic:=range interfaces{if nic.Flags&net.FlagUp==0||nic.Flags&net.FlagLoopback!=0{continue};addresses,e:=nic.Addrs();if e!=nil{return "unavailable"};for _,address:=range addresses{values=append(values,nic.Name+"|"+address.String())}};sort.Strings(values);return strings.Join(values,";")
}
func(b *dnsBroker)syncEpoch(){
 stamp:=b.identity();if stamp==b.stamp{return};b.stamp=stamp;b.epoch++;b.cache=map[string]dnsValue{}
 for _,flight:=range b.pending{flight.cancel()};b.pending=map[string]*dnsFlight{}
}
func(b *dnsBroker)watch(){
 ticker:=time.NewTicker(time.Second);defer ticker.Stop()
 for{select{case <-b.root.Done():b.mu.Lock();for _,flight:=range b.pending{flight.cancel()};b.cache=map[string]dnsValue{};b.mu.Unlock();return
 case <-ticker.C:b.mu.Lock();if len(b.pending)>0||len(b.cache)>0{b.syncEpoch()};b.mu.Unlock()}}
}
func(b *dnsBroker)resolve(ctx context.Context,name string)(string,error){
 name=strings.TrimSuffix(strings.ToLower(name),".");if !safeDomain(name)||localDNSName(name){return "",errProxy}
 if e:=ctx.Err();e!=nil{return "",e};b.mu.Lock();b.syncEpoch()
 if b.root.Err()!=nil{b.mu.Unlock();return "",b.root.Err()};if b.stamp=="unavailable"{b.mu.Unlock();return "",errProxy}
 if value,ok:=b.cache[name];ok&&time.Now().Before(value.until){b.mu.Unlock();return value.ip,nil}
 flight:=b.pending[name]
 if flight==nil{
  if len(b.pending)>=32{b.mu.Unlock();return "",errProxy};query,cancel:=context.WithTimeout(b.root,3*time.Second)
  flight=&dnsFlight{done:make(chan struct{}),cancel:cancel,epoch:b.epoch};b.pending[name]=flight
  go func(f *dnsFlight){
   began:=time.Now();ip,ttl,e:=b.lookup(query,name);cancel();b.mu.Lock();defer b.mu.Unlock();b.syncEpoch()
   if b.epoch!=f.epoch||b.root.Err()!=nil||b.pending[name]!=f{e=errProxy}
   if e==nil{if ttl>60*time.Second{ttl=60*time.Second};until:=began.Add(ttl);if ttl>0&&time.Now().Before(until){for key,value:=range b.cache{if !time.Now().Before(value.until){delete(b.cache,key)}};if len(b.cache)>=256{var oldest string;var at time.Time;for key,value:=range b.cache{if oldest==""||value.until.Before(at){oldest=key;at=value.until}};delete(b.cache,oldest)};b.cache[name]=dnsValue{ip,until}}}
   f.ip=ip;f.err=e;if b.pending[name]==f{delete(b.pending,name)};close(f.done)
  }(flight)
 }
 flight.waiters++;b.mu.Unlock()
 defer func(){b.mu.Lock();defer b.mu.Unlock();flight.waiters--;if flight.waiters==0&&b.pending[name]==flight{delete(b.pending,name);flight.cancel()}}()
 select{case <-ctx.Done():return "",ctx.Err();case <-b.root.Done():return "",b.root.Err();case <-flight.done:
  b.mu.Lock();b.syncEpoch();stale:=flight.epoch!=b.epoch;b.mu.Unlock();if stale{return "",errProxy};return flight.ip,flight.err}
}

// Profile DNS, TCP framing, one question, no host resolver or public fallback.
// TTL is the minimum across the verified CNAME chain and selected A records.
func queryProfileDNS(ctx context.Context,dial func(context.Context,string,string)(net.Conn,error),server netip.Addr,name string)(string,time.Duration,error){
 var random [2]byte;if _,e:=rand.Read(random[:]);e!=nil{return "",0,e};id:=binary.BigEndian.Uint16(random[:]);qname,e:=dnsmessage.NewName(name+".");if e!=nil{return "",0,e}
 question:=dnsmessage.Question{Name:qname,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET}
 message:=dnsmessage.Message{Header:dnsmessage.Header{ID:id,RecursionDesired:true},Questions:[]dnsmessage.Question{question}};packet,e:=message.Pack();if e!=nil{return "",0,e}
 conn,e:=dial(ctx,"tcp",net.JoinHostPort(server.String(),"53"));if e!=nil{return "",0,e};defer conn.Close();cancel:=context.AfterFunc(ctx,func(){conn.Close()});defer cancel()
 deadline,ok:=ctx.Deadline();if !ok{return "",0,errProxy};if e=conn.SetDeadline(deadline);e!=nil{return "",0,e}
 frame:=make([]byte,2+len(packet));binary.BigEndian.PutUint16(frame,uint16(len(packet)));copy(frame[2:],packet);if _,e=io.Copy(conn,strings.NewReader(string(frame)));e!=nil{return "",0,e}
 var prefix [2]byte;if _,e=io.ReadFull(conn,prefix[:]);e!=nil{return "",0,e};size:=int(binary.BigEndian.Uint16(prefix[:]));if size<12||size>16384{return "",0,errProxy};answer:=make([]byte,size);if _,e=io.ReadFull(conn,answer);e!=nil{return "",0,e}
 return parseProfileDNS(answer,id,question)
}
func parseProfileDNS(packet []byte,id uint16,question dnsmessage.Question)(string,time.Duration,error){
 var response dnsmessage.Message;if response.Unpack(packet)!=nil||response.ID!=id||!response.Response||response.OpCode!=0||response.Truncated||response.RCode!=dnsmessage.RCodeSuccess||len(response.Questions)!=1||response.Questions[0]!=question||len(response.Answers)>64{return "",0,errProxy}
 current:=strings.ToLower(question.Name.String());ttl:=uint32(60);seen:=map[string]bool{}
 for depth:=0;depth<9;depth++{
  plain:=strings.TrimSuffix(current,".");if !safeDomain(plain)||localDNSName(plain)||seen[current]{return "",0,errProxy};seen[current]=true;next:="";selected:=""
  for _,record:=range response.Answers{if strings.ToLower(record.Header.Name.String())!=current||record.Header.Class!=dnsmessage.ClassINET{continue}
   switch value:=record.Body.(type){case *dnsmessage.CNAMEResource:target:=strings.ToLower(value.CNAME.String());if next!=""&&next!=target{return "",0,errProxy};next=target;if record.Header.TTL<ttl{ttl=record.Header.TTL}
   case *dnsmessage.AResource:ip:=netip.AddrFrom4(value.A);if !publicTarget(ip){return "",0,errProxy};if selected==""{selected=ip.String()};if record.Header.TTL<ttl{ttl=record.Header.TTL}}
  };if next!=""&&selected!=""{return "",0,errProxy};if selected!=""{return selected,time.Duration(ttl)*time.Second,nil};if next==""{return "",0,errProxy};current=next
 };return "",0,errProxy
}
