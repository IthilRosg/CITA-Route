package socks

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"strings"
	"testing"
	"time"
)

func TestCitaNativePolicyConnectorFrames(t *testing.T) {
	cfg := nativeConnectorConfig{"Cita.CoreConnector." + strings.Repeat("a", 32), strings.Repeat("b", 32), strings.Repeat("c", 64), 42}
	if !cfg.valid() {
		t.Fatal("valid connector refused")
	}
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	data := bytes.Repeat([]byte{1, 2, 3, 4, 5}, 10001)
	result := make(chan error, 1)
	go func() {
		header := make([]byte, 4)
		if _, e := io.ReadFull(server, header); e != nil {
			result <- e
			return
		}
		size := binary.BigEndian.Uint32(header)
		if size < 1 || size > 4096 {
			result <- errNativeConnector
			return
		}
		payload := make([]byte, size)
		if _, e := io.ReadFull(server, payload); e != nil {
			result <- e
			return
		}
		var r nativeConnectorRequest
		if json.Unmarshal(payload, &r) != nil || r.Epoch != cfg.Epoch || r.Capability != cfg.Capability || r.Port != 443 || r.Network != "tcp" || r.Application != `c:\test.exe` {
			result <- errNativeConnector
			return
		}
		if _, e := server.Write([]byte{0}); e != nil {
			result <- e
			return
		}
		frames := &nativeConnectorConn{Conn: server}
		received, e := io.ReadAll(frames)
		if e != nil || !bytes.Equal(received, data) {
			result <- errNativeConnector
			return
		}
		if e = nativeConnectorWrite(frames, data); e == nil {
			e = frames.CloseWrite()
		}
		result <- e
	}()
	connection, e := nativeConnectorHandshake(ctx, client, cfg, "example.org", `C:\test.exe`, "8.8.8.8", 443)
	if e != nil {
		t.Fatal(e)
	}
	cancel() // setup cancellation must be detached after a successful dial.
	if _, e = connection.Write(data); e != nil {
		t.Fatal(e)
	}
	if e = connection.(*nativeConnectorConn).CloseWrite(); e != nil {
		t.Fatal(e)
	}
	received, e := io.ReadAll(connection)
	if e != nil || !bytes.Equal(received, data) {
		t.Fatal("reverse response lost after half-close", e)
	}
	if e = <-result; e != nil {
		t.Fatal(e)
	}
	if _, e = connection.Write([]byte{1}); e == nil {
		t.Fatal("write after directional EOF admitted")
	}
}
func TestCitaNativePolicyConnectorMalformed(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	go func() { header := make([]byte, 4); binary.BigEndian.PutUint32(header, 16385); server.Write(header) }()
	frames := &nativeConnectorConn{Conn: client}
	if _, e := frames.Read(make([]byte, 1)); e == nil {
		t.Fatal("unbounded data frame admitted")
	}
}
func TestCitaNativePolicyConnectorOwnership(t *testing.T) {
	owner := strings.Repeat("a", 32)
	session := strings.Repeat("b", 32)
	h := &CitaNativeBinding{owner: owner, session: session, generation: 2, state: "Active"}
	cfg := &nativeConnectorConfig{"Cita.CoreConnector." + strings.Repeat("c", 32), strings.Repeat("d", 32), strings.Repeat("e", 64), 1234}
	command := bindingCommand{Owner: owner, Session: session, Generation: 2, Action: "ZapretConnector", Connector: cfg}
	if !h.apply(command).Success || h.connector == nil {
		t.Fatal("private connector control refused")
	}
	cfg.Pipe = "changed"
	if !h.connector.valid() {
		t.Fatal("caller changed installed connector snapshot")
	}
	command.Generation = 1
	if h.apply(command).Success {
		t.Fatal("old generation connector accepted")
	}
	command.Generation = 2
	command.Owner = "foreign"
	if h.apply(command).Success {
		t.Fatal("foreign controller accepted")
	}
	h.closeActive()
	if h.connector != nil {
		t.Fatal("closed core retained connector capability")
	}
}
