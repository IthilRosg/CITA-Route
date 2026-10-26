package socks

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
 "net/netip"
	"os"
	"strconv"
	"sync"
	"sync/atomic"
 "time"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/common/citaretain"
	"github.com/sagernet/sing-box/common/dialer"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	M "github.com/sagernet/sing/common/metadata"
	S "github.com/sagernet/sing/protocol/socks"
 "github.com/sagernet/sing/service"
)

var nativeBindingClaim atomic.Bool
var errNativeBinding = errors.New("native binding unavailable")

type bindingCommand struct {
	Owner, Action, Session, Token string
	Generation                    uint64
	Port                          uint16
 Policy *nativePolicyUpdate
 Observation *nativeObservation
 Scope *nativeDirectScope
 Revision uint64
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
 PolicyRevision uint64 `json:"policyRevision"`
 PolicyDigest string `json:"policyDigest,omitempty"`
 PolicyProtocol int `json:"policyProtocol,omitempty"`
 PolicyDirectDials uint64 `json:"policyDirectDials"`
 PolicyBlockedDials uint64 `json:"policyBlockedDials"`
 Observations []nativeObservation `json:"observations,omitempty"`
 ProbeReachable bool `json:"probeReachable,omitempty"`
 DirectScopeProtocol int `json:"directScopeProtocol,omitempty"`
 NativeDnsProtocol int `json:"nativeDnsProtocol,omitempty"`
 FakeIpVpnDials uint64 `json:"fakeIpVpnDials"`
 FakeIpRefused uint64 `json:"fakeIpRefused"`
 Scope *nativeDirectScope `json:"scope,omitempty"`
 ScopeRetired bool `json:"scopeRetired,omitempty"`
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
 policy nativePolicyTable
 policyDirectDials atomic.Uint64
 policyBlockedDials atomic.Uint64
 fakeIpVpnDials atomic.Uint64
 fakeIpRefused atomic.Uint64
 outbounds adapter.OutboundManager
 observations map[string]nativeObservation
 observationOrder []string
 directEnabled map[string]bool
 directUses map[string]uint64
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
 h.outbounds=service.FromContext[adapter.OutboundManager](ctx)
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
 if c.Action=="ProbeDirect" {return h.probeDirect(c)}
	h.mu.Lock()
	defer h.mu.Unlock()
	if c.Action=="EnableDirect"||c.Action=="RetireDirect"{r=h.directScopeLocked(c);r.State=h.state;r.CurrentGeneration=h.generation;r.PolicyDirectDials=h.policyDirectDials.Load();r.PolicyBlockedDials=h.policyBlockedDials.Load();return}
	if c.Scope!=nil||c.Revision!=0{return bindingReply{Stage:c.Action,Owner:h.owner,Session:c.Session,Generation:c.Generation,State:h.state,CurrentGeneration:h.generation}}
	r = bindingReply{Stage: c.Action, Owner: h.owner, Session: c.Session, Generation: c.Generation}
	defer func() { r.State = h.state; r.CurrentGeneration = h.generation;r.PolicyRevision=h.policy.revision;r.PolicyDigest=h.policy.digest;r.PolicyDirectDials=h.policyDirectDials.Load();r.PolicyBlockedDials=h.policyBlockedDials.Load();r.NativeDnsProtocol=1;r.FakeIpVpnDials=h.fakeIpVpnDials.Load();r.FakeIpRefused=h.fakeIpRefused.Load() }()
	if c.Owner != h.owner {
		return
	}
	switch c.Action {
 case "Policy":
  if c.Generation!=h.generation||c.Session!=h.session||c.Port!=0||c.Token!=""||h.state!="Active" {return}
  next,e:=prepareNativePolicy(c.Policy,h.policy,time.Now());if e!=nil{return}
  for _,rule:=range next.rules {if rule.Route=="direct"||rule.Route=="dpi" {if h.outbounds==nil{return};outbound,ok:=h.outbounds.Outbound("native-"+rule.Route);if !ok||outbound.Type()!="direct"{return};for _,ip:=range rule.Addresses{if !h.directEnabled[(nativeDirectScope{Address:ip,Network:rule.Network,Port:rule.Port}).key()]{return}}}}
  h.policy=next;r.Success=true
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
  h.policy=nativePolicyTable{}
  h.observations=nil
  h.observationOrder=nil
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
  if r.Success {for _,observation:=range h.observations {r.Observations=append(r.Observations,observation)}}
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
			if encoder.Encode(bindingReply{Stage: "BindingControlReady", Owner: h.owner, Success: true, State: "Closed",PolicyProtocol:1,DirectScopeProtocol:1}) != nil {
				return
			}
			legacy := json.NewDecoder(io.LimitReader(os.Stdin, 131072))
			legacy.DisallowUnknownFields()
			var decoder messageDecoder = legacy
			persistent := os.Getenv("CITA_NATIVE_PERSISTENT_CONTROL") == "1"
			if persistent {
				decoder = newBoundedFrames(os.Stdin, 131072)
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
func(h *CitaNativeBinding) policyRoute(domain string,process *adapter.ConnectionOwner,network string,destination M.Socksaddr)(string,netip.Addr,bool,*nativeDirectLease){
 if domain==""&&destination.IsFqdn(){domain=destination.Fqdn};application:="";if process!=nil{application=process.ProcessPath}
 h.mu.Lock();defer h.mu.Unlock()
 rule,selected:=h.policy.selectRule(domain,application,network,destination.Port,destination.Addr,time.Now());if !selected||h.state!="Active"{return "",netip.Addr{},false,nil}
 var lease *nativeDirectLease
 if rule.Route=="direct"||rule.Route=="dpi"{lease=h.acquireDirectLocked(nativeDirectScope{Address:destination.Addr.String(),Network:network,Port:destination.Port});if lease==nil{return "",netip.Addr{},false,nil}}
 return rule.Route,destination.Addr,true,lease
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
 if network=="tcp" {domain:=metadata.Domain;if domain==""&&destination.IsFqdn(){domain=destination.Fqdn};app:="";if metadata.ProcessInfo!=nil{app=metadata.ProcessInfo.ProcessPath};h.observe(nativeObservation{Domain:domain,Application:app,Address:destination.Addr.String(),Port:destination.Port})}
 if route,ip,selected,lease:=h.policyRoute(metadata.Domain,metadata.ProcessInfo,network,destination);selected {
  if route=="blocked" {h.policyBlockedDials.Add(1);return nil,errNativePolicy}
  if route=="direct"||route=="dpi" {outbound,ok:=h.outbounds.Outbound("native-"+route);if !ok{lease.release();return nil,errNativePolicy};destination.Addr=ip;destination.Fqdn="";conn,err:=outbound.DialContext(ctx,network,destination);if err==nil{h.policyDirectDials.Add(1);return &nativeDirectConn{Conn:conn,lease:lease},nil};lease.release();domain:=metadata.Domain;app:="";if metadata.ProcessInfo!=nil{app=metadata.ProcessInfo.ProcessPath};h.invalidatePolicy(domain,app,network,destination.Port)}
 }
	b, e := h.capture()
	if e != nil {
		return nil, e
	}
	child, done := b.dialContext(ctx, network == "tcp")
	defer done()
	name:=metadata.Domain;if name==""&&destination.IsFqdn(){name=destination.Fqdn}
	destination,fakeRecovered,e:=nativeVpnDestination(network,destination,name);if e!=nil{h.fakeIpRefused.Add(1);return nil,e}
	conn, e := b.client.DialContext(child, network, destination)
	if e != nil {
		return nil, e
	}
	if fakeRecovered{h.fakeIpVpnDials.Add(1)}
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
 if route,ip,selected,lease:=h.policyRoute(metadata.Domain,metadata.ProcessInfo,"udp",destination);selected {
  if route=="blocked" {h.policyBlockedDials.Add(1);return nil,errNativePolicy}
  if route=="direct"||route=="dpi" {outbound,ok:=h.outbounds.Outbound("native-"+route);if !ok{lease.release();return nil,errNativePolicy};destination.Addr=ip;destination.Fqdn="";conn,err:=outbound.ListenPacket(ctx,destination);if err!=nil{lease.release();return nil,err};h.policyDirectDials.Add(1);return &nativeDirectPacket{PacketConn:conn,lease:lease},nil}
 }
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
