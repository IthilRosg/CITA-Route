package socks

import (
 "errors"
 "net/netip"
 M "github.com/sagernet/sing/common/metadata"
)

var nativeFakeIPPrefix=netip.MustParsePrefix("198.18.0.0/15")
var errNativeFakeIP=errors.New("native fake-ip requires a valid hostname on TCP")

// A foreign Fake-IP table cannot be trusted as a destination. Only the hostname
// already carried by this connection may be resolved by its captured VPN owner.
// No OS resolver, fixed external resolver, payload replay or Direct exception.
func nativeVpnDestination(network string,destination M.Socksaddr,domain string)(M.Socksaddr,bool,error){
 if !nativeFakeIPPrefix.Contains(destination.Addr){return destination,false,nil}
 name,ok:=nativeDomain(domain);if !ok||network!="tcp"||destination.Port==0{return M.Socksaddr{},false,errNativeFakeIP}
 return M.Socksaddr{Fqdn:name,Port:destination.Port},true,nil
}
