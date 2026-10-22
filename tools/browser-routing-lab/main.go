//go:build windows

// Owned VMware acceptance fixture. Never part of the installed client.
package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	stdjson "encoding/json"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math/big"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/sagernet/quic-go/http3"
	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	"github.com/sagernet/sing/common/json"
	"golang.org/x/sys/windows/registry"
)

const name = "web.cita-route.test"
const target = "203.0.113.231/32"

func checkedRoot() (string, error) {
	if len(os.Args) != 2 {
		return "", fmt.Errorf("one owned lab directory required")
	}
	root, err := filepath.Abs(os.Args[1])
	if err != nil {
		return "", err
	}
	if !strings.HasPrefix(strings.ToLower(root), `c:\citalab\browsertun\`) {
		return "", fmt.Errorf("only the authorized guest lab path is allowed")
	}
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, `HARDWARE\DESCRIPTION\System\BIOS`, registry.QUERY_VALUE)
	if err != nil {
		return "", err
	}
	defer key.Close()
	maker, _, _ := key.GetStringValue("SystemManufacturer")
	if !strings.Contains(maker, "VMware") {
		return "", fmt.Errorf("VMware guest required")
	}
	return root, nil
}

func validateConfig(data []byte) error {
	var cfg map[string]any
	if err := stdjson.Unmarshal(data, &cfg); err != nil {
		return err
	}
	ins, ok := cfg["inbounds"].([]any)
	if !ok {
		return fmt.Errorf("missing scoped topology")
	}
	tuns := 0
	for _, raw := range ins {
		in := raw.(map[string]any)
		if in["type"] != "tun" {
			if in["listen"] != "127.0.0.1" {
				return fmt.Errorf("non-loopback proxy refused")
			}
			continue
		}
		tuns++
		routes, _ := in["route_address"].([]any)
		if len(routes) != 1 || routes[0] != target || in["strict_route"] != false || in["dns_mode"] != "disabled" || in["interface_name"] != "Cita-WebLab" {
			return fmt.Errorf("global routing or DNS change refused")
		}
	}
	if tuns != 1 {
		return fmt.Errorf("exactly one scoped TUN required")
	}
	return nil
}

func run() error {
	root, err := checkedRoot()
	if err != nil {
		return err
	}
	data, err := os.ReadFile(filepath.Join(root, "router.json"))
	if err != nil {
		return err
	}
	if err = validateConfig(data); err != nil {
		return err
	}
	private, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: name}, DNSNames: []string{name}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(3 * time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &private.PublicKey, private)
	if err != nil {
		return err
	}
	cert := tls.Certificate{Certificate: [][]byte{der}, PrivateKey: private}
	public, _ := x509.MarshalPKIXPublicKey(&private.PublicKey)
	spki := sha256.Sum256(public)
	tlsConfig := &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12}
	picture := image.NewNRGBA(image.Rect(0, 0, 256, 256))
	pixels := make([]byte, 256*256*3)
	rand.Read(pixels)
	for y := 0; y < 256; y++ {
		for x := 0; x < 256; x++ {
			at := (y*256 + x) * 3
			picture.SetNRGBA(x, y, color.NRGBA{pixels[at], pixels[at+1], pixels[at+2], 255})
		}
	}
	var imageData bytes.Buffer
	png.Encode(&imageData, picture)
	fileData := make([]byte, 256*1024)
	rand.Read(fileData)
	imageHash := sha256.Sum256(imageData.Bytes())
	fileHash := sha256.Sum256(fileData)
	var fault atomic.Bool
	var mu sync.Mutex
	events := make([]map[string]any, 0, 64)
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Alt-Svc", `h3=":443"; ma=120`)
		body := []byte("<!doctype html><title>CITA owned routing fixture</title><body>ready</body>")
		kind := "page"
		if r.URL.Path == "/image.png" {
			body = imageData.Bytes()
			kind = "image"
			w.Header().Set("Content-Type", "image/png")
		}
		if r.URL.Path == "/file.bin" {
			body = fileData
			kind = "file"
			w.Header().Set("Content-Type", "application/octet-stream")
		}
		if r.URL.Path == "/ping" {
			body = []byte("OK")
			kind = "ping"
		}
		broken := fault.Load() && r.ProtoMajor == 3 && kind != "page" && kind != "ping"
		mu.Lock()
		events = append(events, map[string]any{"utc": time.Now().UTC(), "protocol": r.Proto, "kind": kind, "broken": broken})
		if len(events) > 128 {
			events = events[len(events)-128:]
		}
		mu.Unlock()
		w.Header().Set("Content-Length", fmt.Sprint(len(body)))
		w.WriteHeader(200)
		if broken {
			w.Write(body[:24*1024])
			if flusher, ok := w.(http.Flusher); ok {
				flusher.Flush()
			}
			panic(http.ErrAbortHandler)
		}
		w.Write(body)
	})
	tcpListener, err := net.Listen("tcp", "127.0.0.1:443")
	if err != nil {
		return err
	}
	tcpServer := &http.Server{Handler: handler, TLSConfig: tlsConfig.Clone()}
	defer tcpServer.Close()
	go tcpServer.ServeTLS(tcpListener, "", "")
	var h3 *http3.Server
	var h3Lock sync.Mutex
	startH3 := func() {
		h3Lock.Lock()
		defer h3Lock.Unlock()
		if h3 == nil {
			h3 = &http3.Server{Addr: "127.0.0.1:443", TLSConfig: tlsConfig.Clone(), Handler: handler}
			server := h3
			go server.ListenAndServe()
		}
	}
	stopH3 := func() {
		h3Lock.Lock()
		defer h3Lock.Unlock()
		if h3 != nil {
			h3.Close()
			h3 = nil
		}
	}
	startH3()
	defer stopH3()
	echo, err := net.ListenPacket("udp", "127.0.0.1:4444")
	if err != nil {
		return err
	}
	defer echo.Close()
	go func() {
		buffer := make([]byte, 2048)
		for {
			n, addr, err := echo.ReadFrom(buffer)
			if err != nil {
				return
			}
			echo.WriteTo(buffer[:n], addr)
		}
	}()
	ctx := include.Context(context.Background())
	opts, err := json.UnmarshalExtendedContext[option.Options](ctx, data)
	if err != nil {
		return err
	}
	instance, err := box.New(box.Options{Options: opts, Context: ctx})
	if err != nil {
		return err
	}
	defer instance.Close()
	if err = instance.Start(); err != nil {
		return err
	}
	control, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return err
	}
	nonce := make([]byte, 24)
	rand.Read(nonce)
	prefix := "/" + base64.RawURLEncoding.EncodeToString(nonce)
	stopped := make(chan struct{})
	var stopOnce sync.Once
	mux := http.NewServeMux()
	reply := func(w http.ResponseWriter, v any) {
		w.Header().Set("Content-Type", "application/json")
		stdjson.NewEncoder(w).Encode(v)
	}
	mux.HandleFunc(prefix+"/status", func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		reply(w, map[string]any{"events": events, "fault": fault.Load()})
	})
	mux.HandleFunc(prefix+"/fault", func(w http.ResponseWriter, r *http.Request) { fault.Store(true); reply(w, map[string]bool{"ok": true}) })
	mux.HandleFunc(prefix+"/healthy", func(w http.ResponseWriter, r *http.Request) {
		fault.Store(false)
		startH3()
		reply(w, map[string]bool{"ok": true})
	})
	mux.HandleFunc(prefix+"/close-quic", func(w http.ResponseWriter, r *http.Request) { stopH3(); reply(w, map[string]bool{"ok": true}) })
	mux.HandleFunc(prefix+"/shutdown", func(w http.ResponseWriter, r *http.Request) {
		reply(w, map[string]bool{"ok": true})
		stopOnce.Do(func() { close(stopped) })
	})
	api := &http.Server{Handler: mux, ReadHeaderTimeout: time.Second}
	defer api.Close()
	go api.Serve(control)
	ready := map[string]any{"control": "http://" + control.Addr().String() + prefix, "spki": base64.StdEncoding.EncodeToString(spki[:]), "imageBytes": imageData.Len(), "fileBytes": len(fileData), "imageSha256": fmt.Sprintf("%x", imageHash), "fileSha256": fmt.Sprintf("%x", fileHash), "pid": os.Getpid()}
	encoded, _ := stdjson.Marshal(ready)
	readyPath := filepath.Join(root, "ready.json")
	if err = os.WriteFile(readyPath+".tmp", encoded, 0600); err != nil {
		return err
	}
	if err = os.Rename(readyPath+".tmp", readyPath); err != nil {
		return err
	}
	select {
	case <-stopped:
	case <-time.After(8 * time.Minute):
	}
	return nil
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
