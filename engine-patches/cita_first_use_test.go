package route

import (
 "bufio"
 "context"
 "encoding/json"
 "fmt"
 "net"
 "os"
 "testing"
 "time"
)

func TestCitaFirstUseAuthentication(t *testing.T) {
 secret:="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
 for _,test:=range []struct{name,route string;badMac,badNonce bool;want string}{
  {"verified","vpn",false,false,"vpn"},{"forged","vpn",true,false,""},
  {"replay","vpn",false,true,""},{"unknown","foreign",false,false,""},
 } {
  t.Run(test.name,func(t *testing.T){
   listener,err:=net.Listen("tcp","127.0.0.1:0");if err!=nil{t.Fatal(err)};defer listener.Close()
   done:=make(chan struct{});go func(){defer close(done);conn,err:=listener.Accept();if err!=nil{return};defer conn.Close()
    conn.SetDeadline(time.Now().Add(time.Second));var request citaFirstUseRequest
    if json.NewDecoder(bufio.NewReader(conn)).Decode(&request)!=nil{return}
    expected:=citaGateSign(secret,fmt.Sprintf("1\n%s\n%d\n%s\n%s",request.Nonce,request.RouterPid,request.Domain,request.Application))
    if request.Mac!=expected{return}
    reply:=citaFirstUseReply{Version:1,Nonce:request.Nonce,RouterPid:request.RouterPid,Route:test.route,Epoch:"fixture"}
    if test.badNonce{reply.Nonce="old"}
    reply.Mac=citaGateSign(secret,fmt.Sprintf("1\n%s\n%d\n%s\n%s",reply.Nonce,reply.RouterPid,reply.Route,reply.Epoch))
    if test.badMac{reply.Mac="forged"};json.NewEncoder(conn).Encode(reply)
   }()
   got:=citaGateExchange(context.Background(),listener.Addr().String(),secret,citaFirstUseRequest{RouterPid:os.Getpid(),Domain:"fixture.invalid",Application:"C:\\Browser\\chrome.exe"})
   if got!=test.want{t.Fatalf("got %q want %q",got,test.want)};<-done
  })
 }
}

func TestCitaFirstUseCancellation(t *testing.T){
 listener,err:=net.Listen("tcp","127.0.0.1:0");if err!=nil{t.Fatal(err)};defer listener.Close()
 done:=make(chan struct{});go func(){defer close(done);conn,err:=listener.Accept();if err!=nil{return};defer conn.Close();conn.SetReadDeadline(time.Now().Add(time.Second));bufio.NewReader(conn).ReadBytes('\n');var data [1]byte;conn.Read(data[:])}()
 ctx,cancel:=context.WithTimeout(context.Background(),50*time.Millisecond);defer cancel();started:=time.Now()
 result:=citaGateExchange(ctx,listener.Addr().String(),"fixture",citaFirstUseRequest{RouterPid:1})
 if result!=""||time.Since(started)>500*time.Millisecond{t.Fatal("cancellation must retain bounded fallback")};<-done
}
