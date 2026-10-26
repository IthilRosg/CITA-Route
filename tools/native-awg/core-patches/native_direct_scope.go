package socks

import (
 "net"
 "net/netip"
 "strconv"
 "sync"
 "time"
)

// Admission and retirement share the policy mutex. Reserve BEFORE dialing;
// release on socket close, never merely on a read EOF or a policy TTL expiry.
type nativeDirectScope struct { Address,Network string; Port uint16 }
func(s nativeDirectScope) key()string{return s.Network+"\x00"+s.Address+"\x00"+strconv.Itoa(int(s.Port))}
func(s nativeDirectScope) valid()bool{ip,e:=netip.ParseAddr(s.Address);return e==nil&&nativePublic(ip)&&ip.String()==s.Address&&s.Port!=0&&(s.Network=="tcp"||s.Network=="udp")}
type nativeDirectLease struct{ h *CitaNativeBinding; key string; once sync.Once }
func(l *nativeDirectLease) release(){if l==nil{return};l.once.Do(func(){l.h.mu.Lock();defer l.h.mu.Unlock();if l.h.directUses[l.key]<=1{delete(l.h.directUses,l.key)}else{l.h.directUses[l.key]--}})}
func(h *CitaNativeBinding) acquireDirectLocked(s nativeDirectScope)*nativeDirectLease{
 key:=s.key();if !h.directEnabled[key]{return nil};if h.directUses==nil{h.directUses=map[string]uint64{}};h.directUses[key]++;return &nativeDirectLease{h:h,key:key}
}
func(h *CitaNativeBinding) scopeReferencedLocked(s nativeDirectScope,now time.Time)bool{
 for _,r:=range h.policy.rules{if (r.Route=="direct"||r.Route=="dpi")&&r.Network==s.Network&&r.Port==s.Port&&now.Before(r.deadline){for _,ip:=range r.Addresses{if ip==s.Address{return true}}}};return false
}
func(h *CitaNativeBinding) directScopeLocked(c bindingCommand)(r bindingReply){
 r=bindingReply{Stage:c.Action,Owner:h.owner,Session:c.Session,Generation:c.Generation}
 if c.Owner!=h.owner||c.Session!=h.session||c.Generation!=h.generation||h.state!="Active"||c.Scope==nil||!c.Scope.valid()||c.Revision!=h.policy.revision||c.Port!=0||c.Token!=""||c.Policy!=nil||c.Observation!=nil{return}
 r.Scope=c.Scope;r.PolicyRevision=h.policy.revision;key:=c.Scope.key()
 switch c.Action{
 case "EnableDirect":
  if !h.directEnabled[key]&&len(h.directEnabled)>=256{return};if h.directEnabled==nil{h.directEnabled=map[string]bool{}};h.directEnabled[key]=true;r.Success=true
 case "RetireDirect":
  r.Success=true;if h.directUses[key]!=0||h.scopeReferencedLocked(*c.Scope,time.Now()){return};delete(h.directEnabled,key);r.ScopeRetired=true
 }
 return
}
type nativeDirectConn struct{net.Conn;lease *nativeDirectLease}
func(c *nativeDirectConn) Close()error{e:=c.Conn.Close();c.lease.release();return e}
func(c *nativeDirectConn) ReadFrom(p []byte)(int,net.Addr,error){if pc,ok:=c.Conn.(net.PacketConn);ok{return pc.ReadFrom(p)};return 0,nil,errNativePolicy}
func(c *nativeDirectConn) WriteTo(p []byte,a net.Addr)(int,error){if pc,ok:=c.Conn.(net.PacketConn);ok{return pc.WriteTo(p,a)};return 0,errNativePolicy}
type nativeDirectPacket struct{net.PacketConn;lease *nativeDirectLease}
func(c *nativeDirectPacket) Close()error{e:=c.PacketConn.Close();c.lease.release();return e}
