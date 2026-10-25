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
	"strings"
	"syscall"
	"time"

	"github.com/tailscale/wf"
	"golang.org/x/sys/windows"
)

const marker = "CITA native guard lab v1:"

type request struct {
	Action, Run string
	Prefixes    []string
}
type reply struct {
	Success   bool   `json:"success"`
	Stage     string `json:"stage"`
	Rules     int    `json:"rules"`
	ErrorType string `json:"errorType,omitempty"`
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
	b, e := hex.DecodeString(r.Run)
	if e != nil || len(b) != 16 || r.Run != strings.ToLower(r.Run) || r.Action != "Arm" && r.Action != "Inspect" && r.Action != "Cleanup" {
		return nil, errors.New("identity")
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
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	c := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "(Get-CimInstance Win32_ComputerSystemProduct).UUID")
	c.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, e := c.Output()
	if e != nil || !strings.EqualFold(strings.TrimSpace(string(out)), "E24A4D56-C1D9-21AA-DE2E-8572C9F7FE83") {
		return errors.New("wrong isolated guest")
	}
	return nil
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
	switch r.Action {
	case "Arm":
		if len(existing) != 0 {
			return fail("AlreadyArmed", errors.New("already armed"))
		}
		if e = s.AddProvider(&wf.Provider{ID: provider, Name: marker + r.Run, Data: data, Persistent: true}); e != nil {
			return fail("AddProvider", e)
		}
		if e = s.AddSublayer(&wf.Sublayer{ID: sublayer, Provider: provider, Name: marker + r.Run, ProviderData: data, Persistent: true, Weight: 0}); e != nil {
			return fail("AddSublayer", e)
		}
		for i, p := range prefixes {
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
		if e != nil || len(existing) != len(prefixes) {
			return fail("VerifyArmed", errors.New("incomplete guard"))
		}
		// Emit readiness before deliberately waiting for controller termination.
		// Persistent objects must survive a hard process kill, not just Close().
		json.NewEncoder(out).Encode(reply{Success: true, Stage: "Armed", Rules: len(existing)})
		var one [1]byte
		os.Stdin.Read(one[:])
		return reply{Success: true, Stage: "SessionClosed", Rules: len(existing)}
	case "Inspect":
		return reply{Success: true, Stage: "Inspected", Rules: len(existing)}
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
	d := json.NewDecoder(io.LimitReader(os.Stdin, 16385))
	d.DisallowUnknownFields()
	var r request
	if d.Decode(&r) != nil {
		json.NewEncoder(os.Stdout).Encode(reply{Stage: "Decode", ErrorType: "InvalidRequest"})
		os.Exit(1)
	}
	res := execute(r, os.Stdout)
	json.NewEncoder(os.Stdout).Encode(res)
	if !res.Success {
		os.Exit(1)
	}
}
