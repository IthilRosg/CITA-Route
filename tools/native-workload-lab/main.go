package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"math/big"
	"net"
	"os"
	"sort"
	"sync"
	"time"

	quic "github.com/sagernet/quic-go"
	"golang.org/x/net/proxy"
)

type spec struct {
	CertPEM, KeyPEM, Address, Token string
	Port                            int
	OsTun                           bool
}

var failureStage = "Input"

func material(s spec, server bool) (*tls.Config, error) {
	cert, e := tls.X509KeyPair([]byte(s.CertPEM), []byte(s.KeyPEM))
	if e != nil {
		return nil, e
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM([]byte(s.CertPEM)) {
		return nil, fmt.Errorf("certificate")
	}
	c := &tls.Config{MinVersion: tls.VersionTLS13, Certificates: []tls.Certificate{cert}, RootCAs: pool, ServerName: "cita-workload.invalid", NextProtos: []string{"cita-workload/1"}}
	if server {
		c.ClientCAs = pool
		c.ClientAuth = tls.RequireAndVerifyClientCert
	}
	return c, nil
}
func prepare(output io.Writer) error {
	k, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		return e
	}
	serial, e := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	if e != nil {
		return e
	}
	c := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: "CITA isolated workload"}, DNSNames: []string{"cita-workload.invalid"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(30 * time.Minute), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth, x509.ExtKeyUsageClientAuth}}
	der, e := x509.CreateCertificate(rand.Reader, c, c, &k.PublicKey, k)
	if e != nil {
		return e
	}
	key, e := x509.MarshalPKCS8PrivateKey(k)
	if e != nil {
		return e
	}
	return json.NewEncoder(output).Encode(spec{CertPEM: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})), KeyPEM: string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: key}))})
}
func payload() []byte {
	b := make([]byte, 32768)
	for i := range b {
		b[i] = byte(i)
	}
	return b
}
func stream(w io.Writer, blocks int, pace time.Duration) error {
	b := payload()
	for i := 0; i < blocks; i++ {
		if _, e := w.Write(b); e != nil {
			return e
		}
		if pace > 0 {
			time.Sleep(pace)
		}
	}
	return nil
}
func serve(s spec) error {
	tlsConf, e := material(s, true)
	if e != nil {
		return e
	}
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Minute)
	defer cancel()
	tcp, e := tls.Listen("tcp4", s.Address, tlsConf)
	if e != nil {
		return e
	}
	defer tcp.Close()
	udp, e := quic.ListenAddr(s.Address, tlsConf, &quic.Config{EnableDatagrams: true, InitialPacketSize: 1200, DisablePathMTUDiscovery: true, MaxIdleTimeout: 20 * time.Second, MaxIncomingStreams: 4})
	if e != nil {
		return e
	}
	defer udp.Close()
	go func() { <-ctx.Done(); tcp.Close(); udp.Close() }()
	slots := make(chan struct{}, 8)
	go func() {
		for {
			c, e := tcp.Accept()
			if e != nil {
				return
			}
			select {
			case slots <- struct{}{}:
				go func() {
					defer func() { <-slots }()
					defer c.Close()
					c.SetDeadline(time.Now().Add(45 * time.Second))
					var command [1]byte
					if _, e := io.ReadFull(c, command[:]); e != nil {
						return
					}
					if command[0] == 1 {
						stream(c, 256, 0)
					} else if command[0] == 2 {
						stream(c, 64, 50*time.Millisecond)
					}
				}()
			default:
				c.Close()
			}
		}
	}()
	fmt.Println(`{"stage":"Ready"}`)
	for {
		c, e := udp.Accept(ctx)
		if e != nil {
			return nil
		}
		select {
		case slots <- struct{}{}:
			go func() {
				defer func() { <-slots }()
				defer c.CloseWithError(0, "")
				var wg sync.WaitGroup
				wg.Add(1)
				go func() {
					defer wg.Done()
					for {
						p, e := c.ReceiveDatagram(c.Context())
						if e != nil {
							return
						}
						if len(p) <= 512 {
							c.SendDatagram(p)
						}
					}
				}()
				for {
					st, e := c.AcceptStream(ctx)
					if e != nil {
						break
					}
					go func() {
						defer st.Close()
						st.SetDeadline(time.Now().Add(30 * time.Second))
						var command [1]byte
						if _, e := io.ReadFull(st, command[:]); e == nil && command[0] == 1 {
							stream(st, 128, 0)
						}
					}()
				}
				wg.Wait()
			}()
		default:
			c.CloseWithError(1, "capacity")
		}
	}
}

type sample struct {
	Name           string `json:"name"`
	Success        bool   `json:"success"`
	Bytes          int64  `json:"bytes"`
	Milliseconds   int64  `json:"milliseconds"`
	SHA256         string `json:"sha256,omitempty"`
	Sent, Received int
	P50, P95, Max  float64
}

func readStream(name string, r io.Reader, blocks int, began time.Time) sample {
	h := sha256.New()
	n, e := io.Copy(h, io.LimitReader(r, int64(blocks*32768+1)))
	expected := sha256.New()
	for i := 0; i < blocks; i++ {
		expected.Write(payload())
	}
	digest := hex.EncodeToString(h.Sum(nil))
	return sample{Name: name, Success: e == nil && n == int64(blocks*32768) && digest == hex.EncodeToString(expected.Sum(nil)), Bytes: n, Milliseconds: time.Since(began).Milliseconds(), SHA256: digest}
}
func run(s spec) ([]sample, error) {
	failureStage = "TlsMaterial"
	tlsConf, e := material(s, false)
	if e != nil {
		return nil, e
	}
	ctx, cancel := context.WithTimeout(context.Background(), 40*time.Second)
	defer cancel()
	addr, e := net.ResolveUDPAddr("udp4", s.Address)
	if e != nil {
		return nil, e
	}
	dial, e := proxy.SOCKS5("tcp", net.JoinHostPort("127.0.0.1", fmt.Sprint(s.Port)), &proxy.Auth{User: "cita", Password: s.Token}, &net.Dialer{Timeout: 3 * time.Second})
	if e != nil {
		return nil, e
	}
	var cd proxy.ContextDialer = dial.(proxy.ContextDialer)
	failureStage = "UdpAssociation"
	var packet net.PacketConn
	var control net.Conn
	if s.OsTun {
		if !addr.IP.IsGlobalUnicast() || addr.IP.IsPrivate() {
			return nil, fmt.Errorf("test destination")
		}
		cd = &net.Dialer{Timeout: 3 * time.Second}
		packet, e = net.ListenPacket("udp4", "0.0.0.0:0")
	} else {
		packet, control, e = association(ctx, s.Port, s.Token, addr)
	}
	if e != nil {
		return nil, e
	}
	if control != nil {
		defer control.Close()
	}
	defer packet.Close()
	failureStage = "FixtureTcpDial"
	probe, e := cd.DialContext(ctx, "tcp", s.Address)
	if e != nil {
		return nil, e
	}
	probe.SetDeadline(time.Now().Add(5 * time.Second))
	secure := tls.Client(probe, tlsConf.Clone())
	failureStage = "FixtureTlsHandshake"
	e = secure.HandshakeContext(ctx)
	secure.Close()
	if e != nil {
		return nil, e
	}
	failureStage = "QuicHandshake"
	q, e := quic.Dial(ctx, packet, addr, tlsConf, &quic.Config{EnableDatagrams: true, InitialPacketSize: 1200, DisablePathMTUDiscovery: true, MaxIdleTimeout: 15 * time.Second})
	if e != nil {
		return nil, e
	}
	defer q.CloseWithError(0, "")
	failureStage = "ConcurrentWorkload"
	results := make(chan sample, 4)
	for _, work := range []struct {
		name    string
		command byte
		blocks  int
	}{{"tls-download", 1, 256}, {"tls-paced-video", 2, 64}} {
		go func() {
			began := time.Now()
			r := sample{Name: work.name}
			defer func() { results <- r }()
			c, e := cd.DialContext(ctx, "tcp", s.Address)
			if e != nil {
				return
			}
			defer c.Close()
			c.SetDeadline(time.Now().Add(35 * time.Second))
			t := tls.Client(c, tlsConf.Clone())
			defer t.Close()
			if e = t.HandshakeContext(ctx); e != nil {
				return
			}
			if _, e = t.Write([]byte{work.command}); e != nil {
				return
			}
			r = readStream(work.name, t, work.blocks, began)
		}()
	}
	go func() {
		began := time.Now()
		r := sample{Name: "quic-stream"}
		defer func() { results <- r }()
		st, e := q.OpenStreamSync(ctx)
		if e != nil {
			return
		}
		defer st.Close()
		st.SetDeadline(time.Now().Add(35 * time.Second))
		if _, e = st.Write([]byte{1}); e != nil {
			return
		}
		r = readStream("quic-stream", st, 128, began)
	}()
	go func() {
		r := sample{Name: "quic-game-voice-cadence"}
		defer func() { results <- r }()
		began := time.Now()
		const count = 500
		sentAt := make([]time.Time, count)
		seen := make([]bool, count)
		times := make([]float64, 0, count)
		var gate sync.Mutex
		receive, done := context.WithTimeout(ctx, 13*time.Second)
		defer done()
		finished := make(chan struct{})
		go func() {
			defer close(finished)
			for {
				body, e := q.ReceiveDatagram(receive)
				if e != nil {
					return
				}
				if len(body) != 160 {
					continue
				}
				id := int(binary.BigEndian.Uint32(body[:4]))
				if id < 0 || id >= count {
					continue
				}
				gate.Lock()
				if !seen[id] && !sentAt[id].IsZero() {
					seen[id] = true
					times = append(times, float64(time.Since(sentAt[id]).Microseconds())/1000)
				}
				received := len(times)
				gate.Unlock()
				if received == count {
					return
				}
			}
		}()
		for i := 0; i < count; i++ {
			due := began.Add(time.Duration(i) * 20 * time.Millisecond)
			if wait := time.Until(due); wait > 0 {
				time.Sleep(wait)
			}
			body := make([]byte, 160)
			binary.BigEndian.PutUint32(body[:4], uint32(i))
			gate.Lock()
			sentAt[i] = time.Now()
			gate.Unlock()
			if e := q.SendDatagram(body); e != nil {
				break
			}
			r.Sent++
		}
		<-finished
		gate.Lock()
		r.Received = len(times)
		sort.Float64s(times)
		if len(times) > 0 {
			r.P50 = times[len(times)/2]
			r.P95 = times[(len(times)-1)*95/100]
			r.Max = times[len(times)-1]
		}
		gate.Unlock()
		r.Milliseconds = time.Since(began).Milliseconds()
		r.Success = r.Sent == count && r.Received >= count*99/100
	}()
	out := make([]sample, 0, 4)
	for i := 0; i < 4; i++ {
		out = append(out, <-results)
	}
	return out, nil
}
func main() {
	if len(os.Args) == 2 && os.Args[1] == "--prepare" {
		if prepare(os.Stdout) != nil {
			os.Exit(1)
		}
		return
	}
	var s spec
	d := json.NewDecoder(io.LimitReader(os.Stdin, 32768))
	d.DisallowUnknownFields()
	if d.Decode(&s) != nil {
		os.Exit(2)
	}
	if len(os.Args) == 2 && os.Args[1] == "--serve" {
		if serve(s) != nil {
			fmt.Println(`{"stage":"Failed"}`)
			os.Exit(1)
		}
		return
	}
	samples, e := run(s)
	success := e == nil && len(samples) == 4
	for _, r := range samples {
		success = success && r.Success
	}
	json.NewEncoder(os.Stdout).Encode(struct {
		Success   bool     `json:"success"`
		Samples   []sample `json:"samples"`
		Stage     string   `json:"stage"`
		ErrorType string   `json:"errorType"`
	}{success, samples, failureStage, fmt.Sprintf("%T", e)})
	if !success {
		os.Exit(1)
	}
}
