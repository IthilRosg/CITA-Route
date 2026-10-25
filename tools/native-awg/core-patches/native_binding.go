package socks

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"os"
	"strconv"
	"sync"
	"sync/atomic"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/common/citaretain"
	"github.com/sagernet/sing-box/common/dialer"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	M "github.com/sagernet/sing/common/metadata"
	S "github.com/sagernet/sing/protocol/socks"
)

var nativeBindingClaim atomic.Bool
var errNativeBinding = errors.New("native binding unavailable")

type bindingCommand struct {
	Owner, Action, Session, Token string
	Generation                    uint64
	Port                          uint16
}
type bindingReply struct {
	Stage             string `json:"stage"`
	Owner             string `json:"owner"`
	Session           string `json:"session"`
	Generation        uint64 `json:"generation"`
	Success           bool   `json:"success"`
	State             string `json:"state"`
	CurrentGeneration uint64 `json:"currentGeneration"`
	TCPClosed         int    `json:"tcpClosed"`
	UDPClosed         int    `json:"udpClosed"`
}
type nativeBinding struct {
	mu      sync.Mutex
	revoked bool
	client  *S.Client
	lease   *citaretain.Lease
	ctx     context.Context
	cancel  context.CancelFunc
	flows   map[io.Closer]bool // true = packet association
}

func (b *nativeBinding) register(c io.Closer, udp bool) bool {
	b.mu.Lock()
	if b.revoked {
		b.mu.Unlock()
		c.Close()
		return false
	}
	b.flows[c] = udp
	b.mu.Unlock()
	return true
}
func (b *nativeBinding) remove(c io.Closer) { b.mu.Lock(); delete(b.flows, c); b.mu.Unlock() }
func (b *nativeBinding) revoke() (tcp, udp int) {
	b.mu.Lock()
	b.revoked = true
	b.cancel()
	flows := make(map[io.Closer]bool, len(b.flows))
	for c, p := range b.flows {
		flows[c] = p
	}
	b.mu.Unlock()
	for c, p := range flows {
		if p {
			udp++
		} else {
			tcp++
		}
		c.Close()
	}
	b.lease.Revoke()
	return
}

type nativeBindingConn struct {
	net.Conn
	b    *nativeBinding
	once sync.Once
}

func (c *nativeBindingConn) Close() error {
	c.once.Do(func() { c.Conn.Close(); c.b.remove(c) })
	return nil
}

type nativeBindingPacket struct {
	net.PacketConn
	b    *nativeBinding
	once sync.Once
}

func (c *nativeBindingPacket) Close() error {
	c.once.Do(func() { c.PacketConn.Close(); c.b.remove(c) })
	return nil
}
func (c *nativeBindingPacket) Read(p []byte) (int, error)  { return c.PacketConn.(net.Conn).Read(p) }
func (c *nativeBindingPacket) Write(p []byte) (int, error) { return c.PacketConn.(net.Conn).Write(p) }
func (c *nativeBindingPacket) RemoteAddr() net.Addr        { return c.PacketConn.(net.Conn).RemoteAddr() }

type CitaNativeBinding struct {
	outbound.Adapter
	ctx            context.Context
	owner          string
	mu             sync.Mutex
	state, session string
	generation     uint64
	active         *nativeBinding
	seen           map[string]bool
	tokens         map[[32]byte]bool
	makeBinding    func(bindingCommand) (*nativeBinding, error)
	once           sync.Once
	closeOnce      sync.Once
}

func validBindingHex(s string, n int) bool {
	b, e := hex.DecodeString(s)
	return e == nil && len(b) == n
}
func NewCitaNativeBinding(ctx context.Context, _ adapter.Router, _ log.ContextLogger, tag string, opt option.CitaNativeBindingOptions) (adapter.Outbound, error) {
	if os.Getenv("CITA_NATIVE_BINDING_LAB") != "1" || os.Getenv("CITA_NATIVE_UDP_LIFETIME_LAB") != "1" || !validBindingHex(opt.Owner, 16) || !nativeBindingClaim.CompareAndSwap(false, true) {
		return nil, errNativeBinding
	}
	h := &CitaNativeBinding{Adapter: outbound.NewAdapter("cita_native_lab", tag, []string{"tcp", "udp"}, nil), ctx: ctx, owner: opt.Owner, state: "Closed", seen: make(map[string]bool), tokens: make(map[[32]byte]bool)}
	h.makeBinding = h.create
	return h, nil
}
func (h *CitaNativeBinding) create(c bindingCommand) (*nativeBinding, error) {
	base, e := dialer.New(h.ctx, option.DialerOptions{}, false)
	if e != nil {
		return nil, errNativeBinding
	}
	lease, e := citaretain.New("127.0.0.1", c.Port, "5", "cita", c.Token, c.Session, c.Generation, true)
	if e != nil {
		return nil, errNativeBinding
	}
	client := S.NewClient(base, M.ParseSocksaddr(net.JoinHostPort("127.0.0.1", strconv.Itoa(int(c.Port)))), S.Version5, "cita", c.Token)
	if client.EnableCitaUDPLifetime() != nil {
		return nil, errNativeBinding
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &nativeBinding{client: client, lease: lease, ctx: ctx, cancel: cancel, flows: make(map[io.Closer]bool)}, nil
}
func (h *CitaNativeBinding) apply(c bindingCommand) (r bindingReply) {
	h.mu.Lock()
	defer h.mu.Unlock()
	r = bindingReply{Stage: c.Action, Owner: h.owner, Session: c.Session, Generation: c.Generation}
	defer func() { r.State = h.state; r.CurrentGeneration = h.generation }()
	if c.Owner != h.owner {
		return
	}
	switch c.Action {
	case "Quarantine":
		if !validBindingHex(c.Session, 16) || c.Generation <= h.generation || h.seen[c.Session] || len(h.seen) >= 1024 || c.Port != 0 || c.Token != "" {
			return
		}
		old := h.active
		h.active = nil
		h.generation = c.Generation
		h.session = c.Session
		h.seen[c.Session] = true
		h.state = "Quarantined"
		if old != nil {
			r.TCPClosed, r.UDPClosed = old.revoke()
		}
		r.Success = true
	case "Activate":
		if c.Generation != h.generation || c.Session != h.session || h.state != "Quarantined" || c.Port == 0 || !validBindingHex(c.Token, 32) {
			return
		}
		hash := sha256.Sum256([]byte(c.Token))
		if h.tokens[hash] {
			return
		}
		b, e := h.makeBinding(c)
		if e != nil {
			return
		}
		h.active = b
		h.tokens[hash] = true
		h.state = "Active"
		r.Success = true
	case "Close":
		if c.Generation != h.generation || c.Session != h.session || c.Port != 0 || c.Token != "" {
			return
		}
		old := h.active
		h.active = nil
		h.state = "Closed"
		if old != nil {
			r.TCPClosed, r.UDPClosed = old.revoke()
		}
		r.Success = true
	case "Snapshot":
		r.Success = c.Generation == h.generation && c.Session == h.session && c.Port == 0 && c.Token == ""
	}
	return
}
func (h *CitaNativeBinding) closeActive() {
	h.mu.Lock()
	b := h.active
	h.active = nil
	h.state = "Closed"
	h.mu.Unlock()
	if b != nil {
		b.revoke()
	}
}
func (h *CitaNativeBinding) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateInitialize {
		return nil
	}
	info, e := os.Stdin.Stat()
	if e != nil || info.Mode()&os.ModeNamedPipe == 0 {
		return errNativeBinding
	}
	scope.Add(h.Close)
	h.once.Do(func() {
		go func() {
			defer h.closeActive()
			encoder := json.NewEncoder(os.Stdout)
			if encoder.Encode(bindingReply{Stage: "BindingControlReady", Owner: h.owner, Success: true, State: "Closed"}) != nil {
				return
			}
			legacy := json.NewDecoder(io.LimitReader(os.Stdin, 131072))
			legacy.DisallowUnknownFields()
			var decoder messageDecoder = legacy
			persistent := os.Getenv("CITA_NATIVE_PERSISTENT_CONTROL") == "1"
			if persistent {
				decoder = newBoundedFrames(os.Stdin, 2048)
			}
			for i := 0; persistent || i < 64; i++ {
				var c bindingCommand
				if decoder.Decode(&c) != nil {
					return
				}
				if encoder.Encode(h.apply(c)) != nil {
					return
				}
			}
		}()
	})
	return nil
}
func (h *CitaNativeBinding) Close() error {
	h.closeOnce.Do(func() { h.closeActive(); os.Stdin.Close(); nativeBindingClaim.Store(false) })
	return nil
}
func (h *CitaNativeBinding) capture() (*nativeBinding, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.active == nil {
		return nil, errNativeBinding
	}
	return h.active, nil
}
func (b *nativeBinding) dialContext(ctx context.Context, lease bool) (context.Context, func()) {
	child, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(b.ctx, cancel)
	if lease {
		child = citaretain.WithLease(child, b.lease)
	}
	return child, func() { stop(); cancel() }
}
func (h *CitaNativeBinding) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	b, e := h.capture()
	if e != nil {
		return nil, e
	}
	child, done := b.dialContext(ctx, network == "tcp")
	defer done()
	conn, e := b.client.DialContext(child, network, destination)
	if e != nil {
		return nil, e
	}
	if network == "udp" {
		p := &nativeBindingPacket{PacketConn: conn.(net.PacketConn), b: b}
		if !b.register(p, true) {
			return nil, errNativeBinding
		}
		return p, nil
	}
	c := &nativeBindingConn{Conn: conn, b: b}
	if !b.register(c, false) {
		return nil, errNativeBinding
	}
	return c, nil
}
func (h *CitaNativeBinding) ListenPacket(ctx context.Context, destination M.Socksaddr) (net.PacketConn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	b, e := h.capture()
	if e != nil {
		return nil, e
	}
	child, done := b.dialContext(ctx, false)
	defer done()
	conn, e := b.client.ListenPacket(child, destination)
	if e != nil {
		return nil, e
	}
	p := &nativeBindingPacket{PacketConn: conn, b: b}
	if !b.register(p, true) {
		return nil, errNativeBinding
	}
	return p, nil
}
