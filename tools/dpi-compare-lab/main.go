// VM test client: reproduce the native anonymous HTTPS root probe over a
// caller-pinned IPv4 address. No cookies, redirects, application payload or
// unverified TLS; fixed allowlist prevents turning this into a generic scanner.
package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"os"
	"strings"
	"syscall"
	"time"
)

type result struct {
	Domain          string `json:"domain"`
	Reachable       bool   `json:"reachable"`
	Stage           string `json:"stage"`
	Status          int    `json:"status,omitempty"`
	BodyBytes       int    `json:"bodyBytes"`
	Milliseconds    int64  `json:"milliseconds"`
	ErrorKind       string `json:"errorKind,omitempty"`
	Variant         string `json:"variant"`
	Protocol        string `json:"protocol,omitempty"`
	HeaderBytesRead int    `json:"headerBytesRead"`
}

type countReader struct {
	r io.Reader
	n int
}

func (c *countReader) Read(p []byte) (int, error) { n, e := c.r.Read(p); c.n += n; return n, e }

func errorKind(e error) string {
	switch {
	case e == nil:
		return ""
	case errors.Is(e, io.EOF):
		return "eof"
	case errors.Is(e, io.ErrUnexpectedEOF):
		return "unexpected-eof"
	case errors.Is(e, syscall.ECONNRESET):
		return "connection-reset"
	case errors.Is(e, context.DeadlineExceeded):
		return "timeout"
	case strings.HasPrefix(e.Error(), "malformed HTTP"):
		return "malformed-http"
	default:
		return "other"
	}
}

func run(domain, address, variant string) (r result) {
	r.Domain = domain
	r.Variant = variant
	if variant != "default" && variant != "alpn" && variant != "tls12" && variant != "both" && variant != "accept" && variant != "keepalive" && variant != "headers" && variant != "larger" {
		r.Stage = "variant-refused"
		return
	}
	started := time.Now()
	defer func() { r.Milliseconds = time.Since(started).Milliseconds() }()
	switch domain {
	case "www.youtube.com", "www.wikipedia.org", "telegram.org":
	default:
		r.Stage = "domain-refused"
		return
	}
	ip, e := netip.ParseAddr(address)
	if e != nil || !ip.Is4() || !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
		r.Stage = "address-refused"
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2500*time.Millisecond)
	defer cancel()
	r.Stage = "dial"
	c, e := (&net.Dialer{}).DialContext(ctx, "tcp4", net.JoinHostPort(ip.String(), "443"))
	if e != nil {
		return
	}
	defer c.Close()
	deadline, _ := ctx.Deadline()
	if c.SetDeadline(deadline) != nil {
		return
	}
	r.Stage = "tls"
	config := &tls.Config{ServerName: domain, MinVersion: tls.VersionTLS12}
	if variant == "alpn" || variant == "both" {
		config.NextProtos = []string{"http/1.1"}
	}
	if variant == "tls12" || variant == "both" {
		config.MaxVersion = tls.VersionTLS12
	}
	secure := tls.Client(c, config)
	if secure.HandshakeContext(ctx) != nil {
		return
	}
	r.Protocol = secure.ConnectionState().NegotiatedProtocol
	r.Stage = "write"
	request := "GET / HTTP/1.1\r\nHost: " + domain + "\r\nUser-Agent: CITA-Route-Connectivity/1\r\n"
	if variant == "accept" || variant == "headers" {
		request += "Accept: */*\r\n"
	}
	if variant != "keepalive" && variant != "headers" {
		request += "Connection: close\r\n"
	}
	request += "\r\n"
	if _, e = io.WriteString(secure, request); e != nil {
		return
	}
	r.Stage = "header"
	limit := int64(12288)
	if variant == "larger" {
		limit = 65536
	}
	reader := &countReader{r: io.LimitReader(secure, limit)}
	resp, e := http.ReadResponse(bufio.NewReader(reader), nil)
	r.HeaderBytesRead = reader.n
	if e != nil {
		r.ErrorKind = errorKind(e)
		return
	}
	defer resp.Body.Close()
	r.Status = resp.StatusCode
	if resp.StatusCode < 200 || resp.StatusCode >= 400 {
		r.Stage = "http-refused"
		return
	}
	r.Stage = "body"
	buf := make([]byte, 1024)
	n, e := io.ReadFull(resp.Body, buf)
	r.BodyBytes = n
	r.Reachable = n > 0 && (e == nil || e == io.EOF || e == io.ErrUnexpectedEOF)
	if r.Reachable {
		r.Stage = "reachable"
	}
	return
}
func main() {
	if len(os.Args) < 3 || len(os.Args) > 4 {
		fmt.Fprintln(os.Stderr, "test arguments required")
		os.Exit(2)
	}
	variant := "default"
	if len(os.Args) == 4 {
		variant = os.Args[3]
	}
	if e := json.NewEncoder(os.Stdout).Encode(run(os.Args[1], os.Args[2], variant)); e != nil {
		os.Exit(1)
	}
}
