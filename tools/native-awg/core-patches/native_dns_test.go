package socks
import("testing";"net/netip"; M "github.com/sagernet/sing/common/metadata")
func TestCitaNativePolicyFakeIPDestination(t *testing.T){
 for _,ip:=range []string{"198.18.0.0","198.18.255.255","198.19.255.255"}{
  source:=M.Socksaddr{Addr:netip.MustParseAddr(ip),Port:443};next,recovered,e:=nativeVpnDestination("tcp",source,"Example.COM.");if e!=nil||!recovered||next.Fqdn!="example.com"||next.Addr.IsValid()||next.Port!=443{t.Fatal("fake target did not become a VPN hostname")}
  for _,domain:=range []string{"","127.0.0.1","*.example.com","x\r\nHost: other","https://example.com"}{if _,_,e=nativeVpnDestination("tcp",source,domain);e==nil{t.Fatal("unverified fake target accepted")}}
  if _,_,e=nativeVpnDestination("udp",source,"example.com");e==nil{t.Fatal("unsupported fake UDP guessed a domain")}
  if _,ok:=normalizeObservation(nativeObservation{Domain:"example.com",Address:ip,Port:443});ok{t.Fatal("fake target entered Direct calibration")}
 }
 for _,ip:=range []string{"1.1.1.1","198.17.255.255","198.20.0.0"}{source:=M.Socksaddr{Addr:netip.MustParseAddr(ip),Port:443};next,recovered,e:=nativeVpnDestination("tcp",source,"other.example");if e!=nil||recovered||next!=source{t.Fatal("real destination overridden by SNI")}}
}
