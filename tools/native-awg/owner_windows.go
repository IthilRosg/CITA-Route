package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"os"
	"os/exec"
	"strconv"
	"sync"
	"syscall"
	"time"
)

// Explicit laboratory owner, not the installed Windows service. Only this
// executable can be its child. Configuration and credentials travel over stdin.
type ownedProxy struct {
	cmd     *exec.Cmd
	input   io.WriteCloser
	done    chan error
	port    int
	read    io.ReadCloser
	decoder messageDecoder
	ioMu    sync.Mutex
}
type ownerStatus struct {
	Stage         string        `json:"stage"`
	Session       string        `json:"session"`
	Generation    uint64        `json:"generation"`
	State         string        `json:"state"`
	Epoch         int           `json:"epoch"`
	Restarts      int           `json:"restarts"`
	ChildPID      int           `json:"childPid"`
	Health        string        `json:"health,omitempty"`
	SocketRepairs int           `json:"socketRepairs"`
	BudgetUsed    int           `json:"budgetUsed"`
	Transport     *lossSnapshot `json:"transport,omitempty"`
}
type proxyOwner struct {
	mu           sync.Mutex
	status       ownerStatus
	child        *ownedProxy
	relays       map[net.Conn]struct{}
	coordinated  bool
	verified     bool
	failures     int
	repairBudget repairWindow
}

func (o *proxyOwner) snapshot(action string) ownerStatus {
	o.mu.Lock()
	defer o.mu.Unlock()
	s := o.status
	s.Stage = action
	return s
}
func (o *proxyOwner) clear() {
	o.mu.Lock()
	defer o.mu.Unlock()
	o.child = nil
	o.status.ChildPID = 0
	if o.coordinated {
		o.status.Health = "Unavailable"
		o.verified = false
		o.failures = 0
		o.status.Transport = nil
	}
	for c := range o.relays {
		c.Close()
	}
}
func (o *proxyOwner) launchAllowed(ctx context.Context) bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	if ctx.Err() != nil {
		return false
	}
	if persistentControl() {
		if !o.repairBudget.acquire(time.Now(), "child") {
			return false
		}
	} else if o.status.Restarts >= 2 || o.coordinated && o.status.BudgetUsed >= 3 {
		return false
	}
	o.status.Restarts++
	if o.coordinated {
		o.status.BudgetUsed++
	}
	o.status.State = "Restarting"
	return true
}
func (o *proxyOwner) publish(ctx context.Context, child *ownedProxy) bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	if ctx.Err() != nil {
		return false
	}
	o.child = child
	o.status.Epoch++
	o.status.ChildPID = child.cmd.Process.Pid
	o.status.State = "RunningUnverified"
	if o.coordinated {
		o.status.Health = "Starting"
		o.verified = false
		o.failures = 0
		o.status.Transport = nil
	}
	return true
}
func (o *proxyOwner) state(state string) {
	o.mu.Lock()
	defer o.mu.Unlock()
	o.status.State = state
	if o.coordinated && (state == "Stopped" || state == "Exhausted") {
		o.status.Health = state
	}
}
func startOwnedProxy(ctx context.Context, spec proxyStart) (*ownedProxy, error) {
	return startOwnedProxyMode(ctx, spec, false)
}
func startOwnedProxyMode(ctx context.Context, spec proxyStart, coordinated bool) (*ownedProxy, error) {
	image, e := os.Executable()
	if e != nil {
		return nil, errProxy
	}
	mode := "--serve-proxy-udp-lab"
	if coordinated {
		mode = "--serve-owned-transport-lab"
	}
	cmd := exec.CommandContext(ctx, image, mode)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	input, e := cmd.StdinPipe()
	if e != nil {
		return nil, errProxy
	}
	output, e := cmd.StdoutPipe()
	if e != nil {
		input.Close()
		return nil, errProxy
	}
	cmd.Stderr = io.Discard // Never persist upstream exceptions containing config.
	if cmd.Start() != nil {
		input.Close()
		return nil, errProxy
	}
	child := &ownedProxy{cmd: cmd, input: input, done: make(chan error, 1), read: output, decoder: json.NewDecoder(io.LimitReader(output, 65536))}
	if coordinated && os.Getenv("CITA_NATIVE_PERSISTENT_CONTROL") == "1" {
		child.decoder = newBoundedFrames(output, 8192)
	}
	ready := make(chan proxyReady, 1)
	go func() {
		var r proxyReady
		child.decoder.Decode(&r)
		ready <- r
		if !coordinated {
			io.Copy(io.Discard, output)
		}
	}()
	go func() { child.done <- cmd.Wait() }()
	stop := func() { input.Close(); cmd.Process.Kill(); <-child.done }
	if json.NewEncoder(input).Encode(spec) != nil {
		stop()
		return nil, errProxy
	}
	timer := time.NewTimer(6 * time.Second)
	defer timer.Stop()
	select {
	case r := <-ready:
		if r.Stage != "ListeningUnverified" || r.Session != spec.Session || r.Generation != spec.Generation || r.Port < 1 || r.Port > 65535 {
			stop()
			return nil, errProxy
		}
		child.port = r.Port
		return child, nil
	case <-ctx.Done():
		stop()
		return nil, errProxy
	case <-timer.C:
		stop()
		return nil, errProxy
	}
}
func (o *proxyOwner) monitor(ctx context.Context, spec proxyStart, child *ownedProxy) {
	for {
		select {
		case <-ctx.Done():
			child.input.Close()
			child.cmd.Process.Kill()
			<-child.done
			o.clear()
			return
		case <-child.done:
			child.input.Close()
			o.clear()
		}
		for {
			if ctx.Err() != nil {
				return
			}
			if !o.launchAllowed(ctx) {
				o.state("Exhausted")
				return
			}
			timer := time.NewTimer(750 * time.Millisecond)
			select {
			case <-ctx.Done():
				timer.Stop()
				return
			case <-timer.C:
			}
			next, e := startOwnedProxyMode(ctx, spec, o.coordinated)
			if e != nil {
				continue
			}
			if !o.publish(ctx, next) {
				next.input.Close()
				next.cmd.Process.Kill()
				<-next.done
				return
			}
			child = next
			break
		}
	}
}
func (o *proxyOwner) relay(ctx context.Context, local net.Conn) {
	defer local.Close()
	o.mu.Lock()
	child := o.child
	if child == nil || ctx.Err() != nil {
		o.mu.Unlock()
		return
	}
	o.relays[local] = struct{}{}
	o.mu.Unlock()
	defer func() { o.mu.Lock(); delete(o.relays, local); o.mu.Unlock() }()
	remote, e := (&net.Dialer{Timeout: time.Second}).DialContext(ctx, "tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(child.port)))
	if e != nil {
		return
	}
	defer remote.Close()
	// Fence a successful dial racing an exit/restart. Never move an old stream
	// to a new child, retry a dial, buffer data across epochs, or replay bytes.
	o.mu.Lock()
	valid := o.child == child && ctx.Err() == nil
	o.mu.Unlock()
	if !valid {
		return
	}
	done := make(chan struct{})
	go func() { defer close(done); io.Copy(remote, local); remote.Close() }()
	io.Copy(local, remote)
	local.Close()
	remote.Close()
	<-done
}
func ownerMain(input io.Reader, output io.Writer) int {
	return ownerMainMode(input, output, false)
}
func ownerMainMode(input io.Reader, output io.Writer, coordinated bool) int {
	legacy := json.NewDecoder(io.LimitReader(input, 147456))
	legacy.DisallowUnknownFields()
	var decoder messageDecoder = legacy
	persistent := coordinated && os.Getenv("CITA_NATIVE_PERSISTENT_CONTROL") == "1"
	var frames *boundedFrames
	if persistent {
		frames = newBoundedFrames(input, 147456)
		decoder = frames
	}
	var spec proxyStart
	if decoder.Decode(&spec) != nil || !validHex(spec.Token, 32) || !validHex(spec.Session, 16) || spec.Generation == 0 {
		return 1
	}
	// Validate before creating a process; no configuration/error serialization.
	if _, e := parseConfig(spec.Config); e != nil {
		return 1
	}
	if frames != nil {
		frames.limit = 2048
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	listener, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		return 1
	}
	defer listener.Close()
	child, e := startOwnedProxyMode(ctx, spec, coordinated)
	if e != nil {
		return 1
	}
	o := &proxyOwner{status: ownerStatus{Session: spec.Session, Generation: spec.Generation}, relays: make(map[net.Conn]struct{}), coordinated: coordinated}
	o.publish(ctx, child)
	monitored := make(chan struct{})
	go func() { defer close(monitored); o.monitor(ctx, spec, child) }()
	healthDone := make(chan struct{})
	if coordinated {
		go func() { defer close(healthDone); o.healthLoop(ctx, spec) }()
	} else {
		close(healthDone)
	}
	var workers sync.WaitGroup
	accepted := make(chan struct{})
	slots := make(chan struct{}, 32)
	go func() {
		defer close(accepted)
		for {
			local, e := listener.Accept()
			if e != nil {
				return
			}
			select {
			case slots <- struct{}{}:
				workers.Add(1)
				go func() { defer workers.Done(); defer func() { <-slots }(); o.relay(ctx, local) }()
			default:
				local.Close()
			}
		}
	}()
	shutdown := func() {
		cancel()
		listener.Close()
		<-accepted
		<-monitored
		<-healthDone
		o.clear()
		workers.Wait()
		o.state("Stopped")
	}
	defer shutdown()
	ready := proxyReady{"ListeningUnverified", listener.Addr().(*net.TCPAddr).Port, spec.Session, spec.Generation}
	if json.NewEncoder(output).Encode(ready) != nil {
		return 1
	}
	for n := 0; persistent || n < 64; n++ {
		var command lossCommand
		if e := decoder.Decode(&command); e != nil {
			if e == io.EOF {
				return 0
			}
			return 1
		}
		if command.Session != spec.Session || command.Generation != spec.Generation || command.Milliseconds != 0 || command.IncomingOnly {
			return 1
		}
		switch command.Action {
		case "Snapshot":
			if coordinated {
				o.transportSnapshot(ctx, spec)
			}
		case "BlackholeChild":
			if !coordinated {
				return 1
			}
			o.mu.Lock()
			c := o.child
			o.mu.Unlock()
			if c == nil {
				return 1
			}
			if _, e := c.call(ctx, spec, "Blackhole"); e != nil {
				return 1
			}
		case "CrashChild":
			o.mu.Lock()
			if o.child != nil {
				o.child.cmd.Process.Kill()
			}
			o.mu.Unlock()
		case "Stop":
			shutdown() // Acknowledge completion only after child and relays close.
		default:
			return 1
		}
		if json.NewEncoder(output).Encode(o.snapshot(command.Action)) != nil {
			return 1
		}
		if command.Action == "Stop" {
			return 0
		}
	}
	return 1
}
