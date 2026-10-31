//go:build windows

// Isolated persistent-prefix guard proof. Never selected by the normal client.
package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/netip"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/tailscale/wf"
	"golang.org/x/sys/windows"
)

const marker = "CITA native guard lab v1:"

type request struct {
	Action, Run      string
	Prefixes         []string
	Policy           *guardPolicy
	ExpectedPolicy   string
	InterfaceIndex   uint32
	Endpoint         *endpointPermission
	ExpectedEndpoint string
	Direct           *directPermission
	Connector        *connectorPermission
	EvidencePort     uint16
	Continuous       bool
	SocketLease      string
}
type reply struct {
	Run                 string          `json:"run"`
	Success             bool            `json:"success"`
	Stage               string          `json:"stage"`
	Rules               int             `json:"rules"`
	ErrorType           string          `json:"errorType,omitempty"`
	PolicyDigest        string          `json:"policyDigest,omitempty"`
	Prefixes            []string        `json:"prefixes,omitempty"`
	TunnelLuid          uint64          `json:"tunnelLuid,omitempty"`
	Permits             int             `json:"permits"`
	Reason              string          `json:"reason,omitempty"`
	Shape               []string        `json:"shape,omitempty"`
	EndpointDigest      string          `json:"endpointDigest,omitempty"`
	DirectPermits       int             `json:"directPermits"`
	DirectDigest        string          `json:"directDigest,omitempty"`
	ConnectorPermits    int             `json:"connectorPermits"`
	ConnectorDigest     string          `json:"connectorDigest,omitempty"`
	ConnectorProtocol   int             `json:"connectorProtocol,omitempty"`
	InjectorProtocol    int             `json:"injectorProtocol,omitempty"`
	ConnectorDrops      []*wf.DropEvent `json:"connectorDrops,omitempty"`
	SocketLease         string          `json:"socketLease,omitempty"`
	SocketLeaseProtocol int             `json:"socketLeaseProtocol,omitempty"`
	SystemPermits       int             `json:"systemPermits"`
	SocketAgentPermits  int             `json:"socketAgentPermits"`
}

func identity(run, role string) windows.GUID {
	sum := sha256.Sum256([]byte(marker + run + ":" + role))
	h := hex.EncodeToString(sum[:16])
	g, e := windows.GUIDFromString("{" + h[:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:] + "}")
	if e != nil {
		panic(e)
	}
	return g
}
func validate(r request) ([]netip.Prefix, error) {
	if r.Continuous || r.SocketLease != "" {
		return nil, errors.New("unexpected continuous lease scope")
	}
	if r.Action == "InspectDrops" {
		if r.EvidencePort < 1024 {
			return nil, errors.New("exact evidence socket required")
		}
	} else if r.EvidencePort != 0 {
		return nil, errors.New("unexpected evidence scope")
	}
	if r.Action == "GrantConnector" || r.Action == "RevokeConnector" {
		if _, _, e := connectorTarget(r.Connector); e != nil {
			return nil, e
		}
		if len(r.ExpectedPolicy) != 64 || len(r.ExpectedEndpoint) != 64 {
			return nil, errors.New("connector guard identity")
		}
	} else if r.Connector != nil {
		return nil, errors.New("unexpected connector scope")
	}
	if r.Action == "GrantDirect" || r.Action == "RevokeDirect" {
		if _, _, e := directTarget(r.Direct); e != nil {
			return nil, e
		}
		if len(r.ExpectedPolicy) != 64 || len(r.ExpectedEndpoint) != 64 {
			return nil, errors.New("direct guard identity")
		}
	} else if r.Direct != nil {
		return nil, errors.New("unexpected direct scope")
	}
	if r.Endpoint != nil && r.Action != "Arm" && r.Action != "Plan" {
		return nil, errors.New("unexpected endpoint")
	}
	b, e := hex.DecodeString(r.Run)
	if e != nil || len(b) != 16 || r.Run != strings.ToLower(r.Run) || r.Action != "Arm" && r.Action != "Inspect" && r.Action != "InspectDrops" && r.Action != "Cleanup" && r.Action != "Plan" && r.Action != "BindTunnel" && r.Action != "CloseTunnel" && r.Action != "GrantDirect" && r.Action != "RevokeDirect" && r.Action != "GrantConnector" && r.Action != "RevokeConnector" {
		return nil, errors.New("identity")
	}
	if r.Action == "BindTunnel" {
		if r.InterfaceIndex == 0 || len(r.ExpectedPolicy) != 64 {
			return nil, errors.New("binding identity")
		}
	} else if r.InterfaceIndex != 0 {
		return nil, errors.New("unexpected interface")
	}
	if r.ExpectedPolicy != "" {
		b, e := hex.DecodeString(r.ExpectedPolicy)
		if e != nil || len(b) != 32 {
			return nil, errors.New("policy identity")
		}
	}
	if r.Action == "Plan" || r.Action == "Arm" && r.Policy != nil {
		if r.Policy == nil || len(r.Prefixes) != 0 {
			return nil, errors.New("policy form")
		}
		return compilePolicy(*r.Policy)
	}
	if r.Policy != nil {
		return nil, errors.New("unexpected policy")
	}
	if r.Action != "Arm" {
		if len(r.Prefixes) != 0 {
			return nil, errors.New("unexpected policy")
		}
		return nil, nil
	}
	if len(r.Prefixes) < 1 || len(r.Prefixes) > 32 {
		return nil, errors.New("policy size")
	}
	var prefixes []netip.Prefix
	seen := map[netip.Prefix]bool{}
	for _, s := range r.Prefixes {
		p, e := netip.ParsePrefix(s)
		if e != nil || p != p.Masked() || p.Addr().Is4In6() || p.Addr().IsLoopback() || p.Addr().IsUnspecified() || p.Addr().IsMulticast() || p.Bits() < 8 {
			return nil, errors.New("unsafe lab prefix")
		}
		if seen[p] {
			return nil, errors.New("duplicate")
		}
		seen[p] = true
		prefixes = append(prefixes, p)
	}
	return prefixes, nil
}
func guestOnly() error {
	// Production invocation is confined to LocalSystem in the protected agent
	// location. The service independently verifies/leases the component catalog.
	if productionSocketGuard() {
		return nil
	}
	if id, e := nativeGuestUUID(); e == nil {
		if allowedLabIdentity(id) {
			return nil
		}
		return errors.New("wrong isolated guest")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	c := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "(Get-CimInstance Win32_ComputerSystemProduct).UUID")
	c.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, e := c.Output()
	if e != nil || !allowedLabIdentity(strings.TrimSpace(string(out))) {
		return errors.New("wrong isolated guest")
	}
	return nil
}
func allowedLabIdentity(id string) bool {
	if strings.EqualFold(id, "E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83") {
		return true
	}
	if !strings.EqualFold(id, "F8798200-12FB-11F1-8F47-A104BEE59100") || os.Getenv("CITA_PHYSICAL_GUARD_LAB") != "1" {
		return false
	}
	image, e := os.Executable()
	expected := filepath.Join(os.Getenv("ProgramFiles"), "RoutePilot", "Agent", "zapret-live-lab", "native", "native-guard.exe")
	return e == nil && strings.EqualFold(filepath.Clean(image), filepath.Clean(expected))
}
func owned(s *wf.Session, r request) ([]*wf.Rule, error) {
	providers, e := s.Providers()
	if e != nil {
		return nil, e
	}
	id := wf.ProviderID(identity(r.Run, "provider"))
	found := false
	for _, p := range providers {
		if p.ID == id {
			if p.Name != marker+r.Run || !bytes.Equal(p.Data, []byte(marker+r.Run)) || !p.Persistent {
				return nil, errors.New("provider ownership")
			}
			found = true
		}
	}
	rules, e := s.Rules()
	if e != nil {
		return nil, e
	}
	var ours []*wf.Rule
	for _, f := range rules {
		if f.Provider == id || f.Sublayer == wf.SublayerID(identity(r.Run, "sublayer")) {
			if found && (isSystemCompanion(r.Run, f) || isDynamicSocketAgent(r.Run, f)) {
				continue
			}
			if !found || f.Provider != id || f.Sublayer != wf.SublayerID(identity(r.Run, "sublayer")) || f.Name != marker+r.Run || !bytes.Equal(f.ProviderData, []byte(marker+r.Run)) || !f.Persistent {
				return nil, errors.New("filter ownership")
			}
			ours = append(ours, f)
		}
	}
	return ours, nil
}
func execute(r request, out io.Writer) (res reply) {
	res.Stage = "Validate"
	prefixes, e := validate(r)
	if e != nil {
		res.ErrorType = "InvalidRequest"
		return
	}
	if r.Action == "Plan" {
		res = reply{Success: true, Stage: "Planned", PolicyDigest: policyDigest(prefixes), ConnectorProtocol: 1, InjectorProtocol: 1, SocketLeaseProtocol: dynamicSocketProtocol()}
		if r.Endpoint != nil {
			app, addr, e := endpointIdentity(r.Endpoint)
			if e != nil {
				return reply{Stage: "PlanEndpoint", ErrorType: "InvalidEndpoint"}
			}
			res.EndpointDigest = endpointHash(app, addr, r.Endpoint.Port)
		}
		for _, p := range prefixes {
			res.Prefixes = append(res.Prefixes, p.String())
		}
		return
	}
	res.Stage = "GuestIdentity"
	if e = guestOnly(); e != nil {
		res.ErrorType = "WrongGuest"
		return
	}
	res.Stage = "OpenWfp"
	s, e := wf.New(&wf.Options{Name: marker + r.Run, Dynamic: false, TransactionStartTimeout: 2 * time.Second})
	if e != nil {
		res.ErrorType = fmt.Sprintf("%T", e)
		return
	}
	defer s.Close()
	fail := func(stage string, e error) reply { return reply{Stage: stage, ErrorType: fmt.Sprintf("%T", e)} }
	provider := wf.ProviderID(identity(r.Run, "provider"))
	sublayer := wf.SublayerID(identity(r.Run, "sublayer"))
	data := []byte(marker + r.Run)
	existing, e := owned(s, r)
	if e != nil {
		return fail("VerifyOwnership", e)
	}
	var protected []netip.Prefix
	var luid uint64
	var permits int
	// Cleanup relies on exact ownership, not a newer policy parser accepting
	// every old filter shape. Otherwise an upgrade could disable its own rescue.
	if r.Action != "Cleanup" {
		protected, luid, permits, e = describe(existing)
		if e != nil {
			bad := fail("VerifyShape", e)
			bad.Reason = e.Error()
			for _, f := range existing {
				if len(bad.Shape) >= 4 {
					break
				}
				types := ""
				for _, c := range f.Conditions {
					types += fmt.Sprintf("%T,", c.Value)
				}
				bad.Shape = append(bad.Shape, fmt.Sprintf("action=%v weight=%d hard=%t disabled=%t values=%s", f.Action, f.Weight, f.HardAction, f.Disabled, types))
			}
			return bad
		}
	}
	if r.ExpectedPolicy != "" && policyDigest(protected) != r.ExpectedPolicy {
		return fail("PolicyMismatch", errors.New("policy mismatch"))
	}
	endpointDigest := ""
	if r.Action != "Cleanup" {
		endpointDigest, e = describeEndpoint(existing)
		if e != nil {
			return fail("VerifyEndpoint", e)
		}
		if r.ExpectedEndpoint != "" && endpointDigest != r.ExpectedEndpoint {
			return fail("EndpointMismatch", errors.New("endpoint mismatch"))
		}
	}
	switch r.Action {
	case "InspectDrops":
		events, e := s.DropEvents()
		if e != nil {
			return fail("ReadConnectorDrops", e)
		}
		var selected []*wf.DropEvent
		for _, event := range events {
			if len(selected) >= 16 {
				break
			}
			// Include other providers too: an injected packet may be rejected by
			// a different WFP sublayer. Keep this read-only evidence confined to
			// our ephemeral port and the currently protected destination.
			contained := false
			for _, prefix := range protected {
				if prefix.Contains(event.RemoteAddr.Addr()) {
					contained = true
					break
				}
			}
			age := time.Since(event.Timestamp)
			if contained && event.LocalAddr.Port() == r.EvidencePort && event.IPProtocol == 6 && event.RemoteAddr.Port() == 443 && age >= 0 && age < 30*time.Second {
				selected = append(selected, event)
			}
		}
		return reply{Success: true, Stage: "ConnectorDrops", PolicyDigest: policyDigest(protected), EndpointDigest: endpointDigest, ConnectorDrops: selected}
	case "Arm":
		for _, p := range protected {
			found := false
			for _, want := range prefixes {
				if p == want {
					found = true
				}
			}
			if !found {
				return fail("PolicyChangeRequiresStop", errors.New("different policy"))
			}
		}
		providers, e := s.Providers()
		if e != nil {
			return fail("ListProviders", e)
		}
		hasProvider := false
		for _, p := range providers {
			if p.ID == provider {
				hasProvider = true
			}
		}
		if !hasProvider {
			if e = s.AddProvider(&wf.Provider{ID: provider, Name: marker + r.Run, Data: data, Persistent: true}); e != nil {
				return fail("AddProvider", e)
			}
		}
		sublayers, e := s.Sublayers()
		if e != nil {
			return fail("ListSublayers", e)
		}
		hasLayer := false
		for _, sl := range sublayers {
			if sl.ID == sublayer {
				if sl.Provider != provider || sl.Name != marker+r.Run || !bytes.Equal(sl.ProviderData, data) {
					return fail("SublayerOwnership", errors.New("ownership"))
				}
				hasLayer = true
			}
		}
		if !hasLayer {
			if e = s.AddSublayer(&wf.Sublayer{ID: sublayer, Provider: provider, Name: marker + r.Run, ProviderData: data, Persistent: true, Weight: 0}); e != nil {
				return fail("AddSublayer", e)
			}
		}
		if e = armEndpoint(s, r, existing); e != nil {
			return fail("ArmEndpoint", e)
		}
		for i, p := range prefixes {
			found := false
			for _, old := range protected {
				if old == p {
					found = true
				}
			}
			if found {
				continue
			}
			layer := wf.LayerALEAuthConnectV4
			if p.Addr().Is6() {
				layer = wf.LayerALEAuthConnectV6
			}
			f := &wf.Rule{ID: wf.RuleID(identity(r.Run, fmt.Sprintf("rule-%d", i))), Name: marker + r.Run, Provider: provider, ProviderData: data, Sublayer: sublayer, Layer: layer, Weight: 1, Action: wf.ActionBlock, Persistent: true, Conditions: []*wf.Match{{Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: p}}}
			if e = s.AddRule(f); e != nil {
				return fail("AddFilter", e)
			}
		}
		existing, e = owned(s, r)
		expectedCount := len(prefixes) + permits
		directCount, _, directError := describeDirect(existing)
		if directError != nil {
			return fail("ArmDirectShape", directError)
		}
		expectedCount += directCount
		connectorCount, _, connectorError := describeConnectors(existing)
		if connectorError != nil {
			return fail("ArmConnectorShape", connectorError)
		}
		expectedCount += connectorCount
		if r.Endpoint != nil {
			expectedCount++
		}
		if e != nil || len(existing) != expectedCount {
			return fail("VerifyArmed", errors.New("incomplete guard"))
		}
		// Emit readiness before deliberately waiting for controller termination.
		// Persistent objects must survive a hard process kill, not just Close().
		endpointDigest, e = describeEndpoint(existing)
		if e != nil {
			return fail("VerifyEndpoint", e)
		}
		json.NewEncoder(out).Encode(reply{Run: r.Run, Success: true, Stage: "Armed", Rules: len(existing), PolicyDigest: policyDigest(prefixes), EndpointDigest: endpointDigest})
		var one [1]byte
		os.Stdin.Read(one[:])
		return reply{Success: true, Stage: "SessionClosed", Rules: len(existing)}
	case "Inspect":
		count, digest, e := describeDirect(existing)
		if e != nil {
			return fail("InspectDirect", e)
		}
		connectorCount, connectorDigest, e := describeConnectors(existing)
		if e != nil {
			return fail("InspectConnector", e)
		}
		return reply{Success: true, Stage: "Inspected", Rules: len(existing), PolicyDigest: policyDigest(protected), TunnelLuid: luid, Permits: permits, EndpointDigest: endpointDigest, DirectPermits: count, DirectDigest: digest, ConnectorPermits: connectorCount, ConnectorDigest: connectorDigest}
	case "GrantConnector", "RevokeConnector":
		if len(protected) == 0 {
			return fail("ConnectorRequiresProtection", errors.New("empty guard"))
		}
		if e := updateConnector(s, r, existing, protected, r.Action == "GrantConnector"); e != nil {
			return fail("UpdateConnector", e)
		}
		now, e := owned(s, r)
		if e != nil {
			return fail("VerifyConnectorOwnership", e)
		}
		count, digest, e := describeConnectors(now)
		if e != nil {
			return fail("VerifyConnector", e)
		}
		return reply{Success: true, Stage: r.Action, Rules: len(now), PolicyDigest: policyDigest(protected), TunnelLuid: luid, Permits: permits, EndpointDigest: endpointDigest, ConnectorPermits: count, ConnectorDigest: digest}
	case "GrantDirect", "RevokeDirect":
		if len(protected) == 0 {
			return fail("DirectRequiresProtection", errors.New("empty guard"))
		}
		if e := updateDirect(s, r, existing, r.Action == "GrantDirect"); e != nil {
			return fail("UpdateDirect", e)
		}
		now, e := owned(s, r)
		if e != nil {
			return fail("VerifyDirectOwnership", e)
		}
		count, digest, e := describeDirect(now)
		if e != nil {
			return fail("VerifyDirect", e)
		}
		return reply{Success: true, Stage: r.Action, Rules: len(now), PolicyDigest: policyDigest(protected), TunnelLuid: luid, Permits: permits, EndpointDigest: endpointDigest, DirectPermits: count, DirectDigest: digest}
	case "BindTunnel":
		value, e := bindTunnel(s, r, existing)
		if e != nil {
			return fail("BindTunnel", e)
		}
		now, e := owned(s, r)
		if e != nil {
			return fail("VerifyBinding", e)
		}
		_, bound, count, e := describe(now)
		if e != nil || bound != value {
			return fail("VerifyBinding", errors.New("binding"))
		}
		return reply{Success: true, Stage: "TunnelBound", Rules: len(now), PolicyDigest: policyDigest(protected), TunnelLuid: bound, Permits: count, EndpointDigest: endpointDigest}
	case "CloseTunnel":
		if e := closeTunnel(s, existing); e != nil {
			return fail("CloseTunnel", e)
		}
		remaining := len(protected)
		if endpointDigest != "" {
			remaining++
		}
		return reply{Success: true, Stage: "TunnelClosed", Rules: remaining, PolicyDigest: policyDigest(protected), EndpointDigest: endpointDigest}
	case "Cleanup":
		for _, f := range existing {
			if e = s.DeleteRule(f.ID); e != nil {
				return fail("DeleteFilter", e)
			}
		}
		// A partial Arm can leave metadata without filters. Delete only exact,
		// marked objects; never use wildcard/system-wide WFP reset commands.
		sublayers, e := s.Sublayers()
		if e != nil {
			return fail("ListSublayers", e)
		}
		for _, sl := range sublayers {
			if sl.ID == sublayer {
				if sl.Provider != provider || sl.Name != marker+r.Run || !bytes.Equal(sl.ProviderData, data) {
					return fail("SublayerOwnership", errors.New("ownership"))
				}
				if e = s.DeleteSublayer(sublayer); e != nil {
					return fail("DeleteSublayer", e)
				}
			}
		}
		providers, e := s.Providers()
		if e != nil {
			return fail("ListProviders", e)
		}
		for _, p := range providers {
			if p.ID == provider {
				if p.Name != marker+r.Run || !bytes.Equal(p.Data, data) {
					return fail("ProviderOwnership", errors.New("ownership"))
				}
				if e = s.DeleteProvider(provider); e != nil {
					return fail("DeleteProvider", e)
				}
			}
		}
		providers, e = s.Providers()
		if e != nil {
			return fail("VerifyProviderCleanup", e)
		}
		for _, p := range providers {
			if p.ID == provider {
				return fail("ProviderStillPresent", errors.New("cleanup"))
			}
		}
		sublayers, e = s.Sublayers()
		if e != nil {
			return fail("VerifySublayerCleanup", e)
		}
		for _, sl := range sublayers {
			if sl.ID == sublayer {
				return fail("SublayerStillPresent", errors.New("cleanup"))
			}
		}
		left, e := owned(s, r)
		if e != nil || len(left) != 0 {
			return fail("VerifyFilterCleanup", errors.New("cleanup"))
		}
		return reply{Success: true, Stage: "Cleaned", Rules: 0}
	}
	return fail("Action", errors.New("action"))
}
func main() {
	d := json.NewDecoder(io.LimitReader(os.Stdin, 65537))
	d.DisallowUnknownFields()
	var r request
	if d.Decode(&r) != nil {
		json.NewEncoder(os.Stdout).Encode(reply{Stage: "Decode", ErrorType: "InvalidRequest"})
		os.Exit(1)
	}
	if r.Action == "HoldSystemConnector" || r.Action == "CheckSystemConnector" {
		res := holdSystemConnector(r, d, os.Stdout)
		res.Run = r.Run
		res.SocketLease = r.SocketLease
		json.NewEncoder(os.Stdout).Encode(res)
		if !res.Success {
			os.Exit(1)
		}
		return
	}
	res := execute(r, os.Stdout)
	res.Run = r.Run
	json.NewEncoder(os.Stdout).Encode(res)
	if !res.Success {
		os.Exit(1)
	}
}
