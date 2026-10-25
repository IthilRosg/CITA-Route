package main

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"io"
	"math/big"
	"net"
	"testing"
	"time"
)

func TestAnonymousHttpsDistinguishesPeerCloseAndCertificateFailure(t *testing.T) {
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(7), Subject: pkix.Name{CommonName: "isolated-fixture.invalid"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, IPAddresses: []net.IP{net.IPv4(1, 1, 1, 1)}}
	der, e := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if e != nil {
		t.Fatal(e)
	}
	for _, mode := range []string{"close", "untrusted-certificate"} {
		t.Run(mode, func(t *testing.T) {
			listener, e := net.Listen("tcp4", "127.0.0.1:0")
			if e != nil {
				t.Fatal(e)
			}
			defer listener.Close()
			finished := make(chan struct{})
			go func() {
				defer close(finished)
				c, e := listener.Accept()
				if e != nil {
					return
				}
				defer c.Close()
				c.SetDeadline(time.Now().Add(3 * time.Second))
				var greeting [3]byte
				if _, e = io.ReadFull(c, greeting[:]); e != nil {
					return
				}
				c.Write([]byte{5, 0})
				var request [10]byte
				if _, e = io.ReadFull(c, request[:]); e != nil {
					return
				}
				c.Write([]byte{5, 0, 0, 1, 127, 0, 0, 1, 0, 1})
				if mode == "untrusted-certificate" {
					server := tls.Server(c, &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}, MinVersion: tls.VersionTLS12})
					server.Handshake()
				}
			}()
			spec, _ := json.Marshal(map[string]any{"Port": listener.Addr().(*net.TCPAddr).Port, "LiteralIPv4": true})
			var output bytes.Buffer
			began := time.Now()
			code := proxyClient(bytes.NewReader(spec), &output)
			var r result
			if json.Unmarshal(output.Bytes(), &r) != nil || code != 1 || r.Success || r.Stage != "ProxyConnectFailed" || time.Since(began) > 5*time.Second {
				t.Fatal("HTTPS refusal was not bounded and classified")
			}
			if mode == "untrusted-certificate" && r.FailureCode != "CertificateRejected" {
				t.Fatalf("certificate refusal misclassified: %s", r.FailureCode)
			}
			if mode == "close" && r.FailureCode != "Closed" && r.FailureCode != "ConnectionReset" && r.FailureCode != "Truncated" {
				t.Fatalf("peer close misclassified: %s phase=%s types=%v errno=%d", r.FailureCode, r.FailurePhase, r.FailureLayers, r.FailureErrno)
			}
			listener.Close()
			<-finished
		})
	}
}
