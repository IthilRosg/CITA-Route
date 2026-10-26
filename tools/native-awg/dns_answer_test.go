package main
import "testing"
func TestValidatedIPv4DnsAnswer(t *testing.T){
 for _,ips:=range [][]string{{"1.1.1.1"},{"2606:4700:4700::1111","1.1.1.1"},{"1.1.1.1","2606:4700:4700::1111"}}{ip,e:=validatedIPv4Answer(ips);if e!=nil||ip!="1.1.1.1"{t.Fatal("valid IPv4 lost in mixed answer")}}
 for _,ips:=range [][]string{nil,{"::1"},{"2606:4700:4700::1111"},{"127.0.0.1","1.1.1.1"},{"1.1.1.1","10.0.0.1"},{"198.18.0.1"},{"bad"},{"::ffff:1.1.1.1"}}{if _,e:=validatedIPv4Answer(ips);e==nil{t.Fatal("invalid or absent public IPv4 accepted")}}
}
