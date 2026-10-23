//go:build windows

package bufio

import (
    "net"
    "testing"
    "time"

    "github.com/sagernet/sing/common/buf"
    N "github.com/sagernet/sing/common/network"
)

// A timed-out RawConn.Read must not carry the previous readiness indication
// into the next call. Otherwise the next Recv/Recvfrom blocks inside RawRead
// and neither its deadline nor FD.Close can wake that synchronous syscall.
func TestCitaWindowsReadDeadlineRetry(t *testing.T) {
    for _, name := range []string{"udp", "tcp", "tcp-vector"} {
        t.Run(name, func(t *testing.T) {
            var connection net.Conn
            var rescue func()
            var read func() error
            if name == "udp" {
                socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127,0,0,1)})
                if err != nil { t.Fatal(err) }
                connection = socket
                peer, err := net.DialUDP("udp4", nil, socket.LocalAddr().(*net.UDPAddr))
                if err != nil { t.Fatal(err) }
                defer peer.Close()
                rescue = func(){ _, _ = peer.Write([]byte{1}) }
                waiter, ok := createSyscallPacketReadWaiter(socket)
                if !ok { t.Fatal("expected the Windows UDP waiter") }
                waiter.InitializeReadWaiter(N.ReadWaitOptions{})
                read = func()error{ b, _, err := waiter.WaitReadPacket(); if b != nil { b.Release() }; return err }
            } else {
                listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127,0,0,1)})
                if err != nil { t.Fatal(err) }
                defer listener.Close()
                peer, err := net.DialTCP("tcp4", nil, listener.Addr().(*net.TCPAddr))
                if err != nil { t.Fatal(err) }
                defer peer.Close()
                socket, err := listener.AcceptTCP()
                if err != nil { t.Fatal(err) }
                connection = socket
                rescue = func(){ _, _ = peer.Write([]byte{1}) }
                if name == "tcp-vector" {
                    waiter, ok := createVectorisedSyscallReadWaiter(socket)
                    if !ok { t.Fatal("expected the Windows vector TCP waiter") }
                    waiter.InitializeReadWaiter(N.ReadWaitOptions{BatchSize:2})
                    read = func()error{ buffers, e := waiter.WaitReadBuffers(); for _, b := range buffers { b.Release() }; return e }
                } else {
                    waiter, ok := createSyscallReadWaiter(socket)
                    if !ok { t.Fatal("expected the Windows TCP waiter") }
                    waiter.InitializeReadWaiter(N.ReadWaitOptions{})
                    read = func()error{ var b *buf.Buffer; b, err = waiter.WaitReadBuffer(); if b != nil { b.Release() }; return err }
                }
            }
            defer connection.Close()
            for attempt:=0;attempt<3;attempt++ {
                if err:=connection.SetReadDeadline(time.Now().Add(30*time.Millisecond));err!=nil { t.Fatal(err) }
                done:=make(chan error,1);go func(){done<-read()}()
                select {
                case err:=<-done:
                    timeout,ok:=err.(net.Error)
                    if !ok || !timeout.Timeout(){ t.Fatalf("attempt %d expected timeout, got %v",attempt,err) }
                case <-time.After(400*time.Millisecond):
                    // Release the old implementation's synchronous syscall, so
                    // even a failing regression test leaves no blocked close.
                    rescue()
                    select {case <-done:case <-time.After(time.Second):t.Fatal("rescue failed")}
                    t.Fatalf("attempt %d: retry ignored its deadline and blocked a Windows socket",attempt)
                }
            }
        })
    }
}
