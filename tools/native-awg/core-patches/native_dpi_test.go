package socks

import (
	"bufio"
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"io"
	"math/big"
	"net"
	"net/http"
	"os"
	"testing"
	"time"
)

type dpiCapture struct{ bytes.Buffer }
type dpiWriteCapture struct {
	dpiCapture
	writes int
}

func (c *dpiWriteCapture) Write(p []byte) (int, error) { c.writes++; return c.Buffer.Write(p) }

type dpiReplay struct {
	net.Conn
	prefix io.Reader
}

func (c *dpiReplay) Read(p []byte) (int, error)        { return c.prefix.Read(p) }
func (c *dpiCapture) Read([]byte) (int, error)         { return 0, io.EOF }
func (c *dpiCapture) Close() error                     { return nil }
func (c *dpiCapture) LocalAddr() net.Addr              { return &net.TCPAddr{} }
func (c *dpiCapture) RemoteAddr() net.Addr             { return &net.TCPAddr{} }
func (c *dpiCapture) SetDeadline(time.Time) error      { return nil }
func (c *dpiCapture) SetReadDeadline(time.Time) error  { return nil }
func (c *dpiCapture) SetWriteDeadline(time.Time) error { return nil }

func TestCitaNativePolicyDPIPartialFirstWrite(t *testing.T) {
	original := &dpiCapture{}
	_ = tls.Client(original, &tls.Config{ServerName: "www.example.com", MinVersion: tls.VersionTLS12}).Handshake()
	hello := append([]byte(nil), original.Bytes()...)
	if len(hello) < 12 {
		t.Fatal("fixture ClientHello missing")
	}
	output := &dpiCapture{}
	conn := nativeDPIConn(output, context.Background())
	for _, part := range [][]byte{hello[:2], hello[2:8], hello[8:]} {
		n, e := conn.Write(part)
		if e != nil || n != len(part) {
			t.Fatalf("partial write failed: %d %v", n, e)
		}
	}
	raw := output.Bytes()
	records := 0
	for len(raw) > 0 {
		if len(raw) < 5 || raw[0] != 22 {
			t.Fatal("invalid TLS record")
		}
		n := int(raw[3])<<8 | int(raw[4])
		if n == 0 || len(raw) < 5+n {
			t.Fatal("invalid TLS length")
		}
		records++
		raw = raw[5+n:]
	}
	if records < 2 {
		t.Fatal("split ClientHello did not receive DPI fragmentation")
	}
}
func TestCitaNativePolicyDPITcpSegmentsPreserveTLSRecord(t *testing.T) {
	t.Setenv("CITA_NATIVE_DPI_STRATEGY", "tcp")
	original := &dpiCapture{}
	_ = tls.Client(original, &tls.Config{ServerName: "www.example.com", MinVersion: tls.VersionTLS12}).Handshake()
	hello := append([]byte(nil), original.Bytes()...)
	if len(hello) < 12 {
		t.Fatal("fixture ClientHello missing")
	}
	output := &dpiWriteCapture{}
	conn := nativeDPIConn(output, context.Background())
	if n, e := conn.Write(hello); e != nil || n != len(hello) {
		t.Fatalf("TCP split failed: %v", e)
	}
	if output.writes < 2 || !bytes.Equal(output.Bytes(), hello) {
		t.Fatal("TCP writes must split without modifying the TLS stream")
	}
}
func TestCitaNativePolicyDPIRejectsInvalidAndPreservesFollowingRecord(t *testing.T) {
	bad := &dpiCapture{}
	conn := nativeDPIConn(bad, context.Background())
	if _, e := conn.Write([]byte{23, 3, 3, 0, 4, 1, 0, 0, 0}); e == nil || bad.Len() != 0 {
		t.Fatal("invalid initial record was forwarded")
	}
	original := &dpiCapture{}
	_ = tls.Client(original, &tls.Config{ServerName: "www.example.com", MinVersion: tls.VersionTLS12}).Handshake()
	hello := append([]byte(nil), original.Bytes()...)
	trailing := []byte{23, 3, 3, 0, 3, 1, 2, 3}
	output := &dpiCapture{}
	conn = nativeDPIConn(output, context.Background())
	input := append(hello, trailing...)
	if n, e := conn.Write(input); e != nil || n != len(input) {
		t.Fatalf("combined records failed: %v", e)
	}
	if !bytes.HasSuffix(output.Bytes(), trailing) {
		t.Fatal("following record was corrupted")
	}
}
func TestCitaNativePolicyDPIRejectsNoServerName(t *testing.T) {
	original := &dpiCapture{}
	_ = tls.Client(original, &tls.Config{InsecureSkipVerify: true, MinVersion: tls.VersionTLS12}).Handshake()
	output := &dpiCapture{}
	if _, e := nativeDPIConn(output, context.Background()).Write(original.Bytes()); e == nil || output.Len() != 0 {
		t.Fatal("unfragmentable ClientHello was forwarded as DPI")
	}
}

func TestCitaNativePolicyDPIFragmentsClientHello(t *testing.T) {
	capture := &dpiCapture{}
	conn := nativeDPIConn(capture, context.Background())
	client := tls.Client(conn, &tls.Config{ServerName: "www.example.com", MinVersion: tls.VersionTLS12})
	_ = client.Handshake()
	raw := capture.Bytes()
	records := 0
	for len(raw) > 0 {
		if len(raw) < 5 || raw[0] != 22 {
			t.Fatal("invalid TLS record")
		}
		n := int(raw[3])<<8 | int(raw[4])
		if n == 0 || len(raw) < 5+n {
			t.Fatal("invalid TLS length")
		}
		records++
		raw = raw[5+n:]
	}
	if records < 2 {
		t.Fatal("ClientHello was not split into multiple TLS records")
	}
}
func TestCitaNativePolicyDPIVerifiedTLS(t *testing.T) {
	public, private, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "example.com"}, DNSNames: []string{"example.com"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, BasicConstraintsValid: true}
	der, e := x509.CreateCertificate(rand.Reader, template, template, public, private)
	if e != nil {
		t.Fatal(e)
	}
	certificate := tls.Certificate{Certificate: [][]byte{der}, PrivateKey: private}
	conn, remote := net.Pipe()
	defer conn.Close()
	defer remote.Close()
	_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
	_ = remote.SetDeadline(time.Now().Add(3 * time.Second))
	done := make(chan error, 1)
	go func() {
		server := tls.Server(remote, &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12})
		done <- server.Handshake()
	}()
	pool := x509.NewCertPool()
	cert, e := x509.ParseCertificate(der)
	if e != nil {
		t.Fatal(e)
	}
	pool.AddCert(cert)
	client := tls.Client(nativeDPIConn(conn, context.Background()), &tls.Config{ServerName: "example.com", RootCAs: pool, MinVersion: tls.VersionTLS12})
	if e = client.Handshake(); e != nil {
		t.Fatalf("fragmented authenticated TLS failed: %v", e)
	}
	if e = <-done; e != nil {
		t.Fatal(e)
	}
}
func TestCitaNativePolicyDPIRequiresLabAndOwnership(t *testing.T) {
	h := &CitaNativeBinding{state: "Active", owner: "owner", session: "session", generation: 1}
	observation := nativeObservation{Domain: "example.com", Application: `C:\fixture.exe`, Address: "1.1.1.1", Port: 443}
	h.observe(observation)
	t.Setenv("CITA_NATIVE_DPI_LAB", "")
	if h.probeDirect(bindingCommand{Owner: "owner", Session: "session", Generation: 1, Action: "ProbeDPI", Observation: &observation}).Success {
		t.Fatal("DPI probe enabled outside laboratory")
	}
	t.Setenv("CITA_NATIVE_DPI_LAB", "1")
	if !nativeDPIAllowed("tcp", 443) || nativeDPIAllowed("udp", 443) || nativeDPIAllowed("tcp", 80) {
		t.Fatal("DPI laboratory must accept only TCP/443")
	}
	if h.probeDirect(bindingCommand{Owner: "wrong", Session: "session", Generation: 1, Action: "ProbeDPI", Observation: &observation}).Success {
		t.Fatal("unowned DPI probe accepted")
	}
}
func TestCitaNativePolicyDPIRejectsUntrustedCertificate(t *testing.T) {
	public, private, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(2), DNSNames: []string{"example.com"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, e := x509.CreateCertificate(rand.Reader, template, template, public, private)
	if e != nil {
		t.Fatal(e)
	}
	conn, remote := net.Pipe()
	defer conn.Close()
	defer remote.Close()
	_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
	_ = remote.SetDeadline(time.Now().Add(3 * time.Second))
	done := make(chan struct{})
	go func() {
		defer close(done)
		server := tls.Server(remote, &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: private}}})
		_ = server.Handshake()
	}()
	client := tls.Client(nativeDPIConn(conn, context.Background()), &tls.Config{ServerName: "example.com", RootCAs: x509.NewCertPool(), MinVersion: tls.VersionTLS12})
	if client.Handshake() == nil {
		t.Fatal("DPI must not bypass certificate verification")
	}
	<-done
}
func TestCitaNativePolicyDPIControlledFilter(t *testing.T) {
	public, private, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(3), DNSNames: []string{"example.com"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, e := x509.CreateCertificate(rand.Reader, template, template, public, private)
	if e != nil {
		t.Fatal(e)
	}
	cert, e := x509.ParseCertificate(der)
	if e != nil {
		t.Fatal(e)
	}
	pool := x509.NewCertPool()
	pool.AddCert(cert)
	for _, fragmented := range []bool{false, true} {
		local, remote := net.Pipe()
		_ = local.SetDeadline(time.Now().Add(3 * time.Second))
		_ = remote.SetDeadline(time.Now().Add(3 * time.Second))
		serverDone := make(chan bool, 1)
		go func() {
			defer remote.Close()
			header := make([]byte, 5)
			if _, e := io.ReadFull(remote, header); e != nil {
				serverDone <- false
				return
			}
			n := int(header[3])<<8 | int(header[4])
			if n < 4 || n > 16384 {
				serverDone <- false
				return
			}
			body := make([]byte, n)
			if _, e := io.ReadFull(remote, body); e != nil {
				serverDone <- false
				return
			}
			declared := int(body[1])<<16 | int(body[2])<<8 | int(body[3])
			if body[0] != 1 || n >= declared+4 {
				serverDone <- false
				return
			}
			prefix := append(header, body...)
			replay := &dpiReplay{Conn: remote, prefix: io.MultiReader(bytes.NewReader(prefix), remote)}
			server := tls.Server(replay, &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: private}}})
			serverDone <- server.Handshake() == nil
		}()
		var candidate net.Conn = local
		if fragmented {
			candidate = nativeDPIConn(candidate, context.Background())
		}
		client := tls.Client(candidate, &tls.Config{ServerName: "example.com", RootCAs: pool, MinVersion: tls.VersionTLS12})
		e = client.Handshake()
		_ = local.Close()
		accepted := <-serverDone
		if fragmented && (e != nil || !accepted) {
			t.Fatalf("fragmented connection not accepted: %v", e)
		}
		if !fragmented && (e == nil || accepted) {
			t.Fatal("unfragmented connection was not refused")
		}
	}
}

// Explicitly invoked only by the guest-only lab runner. This sends one
// anonymous HEAD request; it does not use or replay a user's connection.
func TestCitaNativeDPILive(t *testing.T) {
	if os.Getenv("CITA_NATIVE_DPI_LIVE_LAB") != "1" {
		t.Skip("live guest test disabled")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	conn, e := (&net.Dialer{}).DialContext(ctx, "tcp4", "1.1.1.1:443")
	if e != nil {
		t.Fatalf("dial failed: %T", e)
	}
	defer conn.Close()
	deadline, _ := ctx.Deadline()
	if e = conn.SetDeadline(deadline); e != nil {
		t.Fatal(e)
	}
	client := tls.Client(nativeDPIConn(conn, ctx), &tls.Config{ServerName: "cloudflare-dns.com", MinVersion: tls.VersionTLS12})
	if e = client.HandshakeContext(ctx); e != nil {
		t.Fatalf("authenticated TLS failed: %T", e)
	}
	if _, e = io.WriteString(client, "HEAD / HTTP/1.1\r\nHost: cloudflare-dns.com\r\nConnection: close\r\n\r\n"); e != nil {
		t.Fatalf("write failed: %T", e)
	}
	response, e := http.ReadResponse(bufio.NewReader(io.LimitReader(client, 16384)), nil)
	if e != nil {
		t.Fatalf("response failed: %T", e)
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 500 {
		t.Fatalf("HTTP status %d", response.StatusCode)
	}
}
