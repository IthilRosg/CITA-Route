//go:build windows

package main

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/tailscale/wf"
	"golang.org/x/sys/windows"
)

// Unlike the persistent session deny, each exact System/agent tuple belongs
// to a live dynamic WFP session. Production requires the protected service's
// installed guard running as LocalSystem; a user process cannot opt in by env.
func productionSocketIdentity(image, programFiles, sid string) bool {
	if sid != "S-1-5-18" || !filepath.IsAbs(image) || !filepath.IsAbs(programFiles) {
		return false
	}
	expected := filepath.Join(programFiles, "RoutePilot", "Agent", "native", "native-guard.exe")
	return strings.EqualFold(filepath.Clean(image), filepath.Clean(expected))
}
func productionSocketGuard() bool {
	image, e := os.Executable()
	if e != nil {
		return false
	}
	root, e := windows.KnownFolderPath(windows.FOLDERID_ProgramFiles, 0)
	if e != nil {
		return false
	}
	user, e := windows.GetCurrentProcessToken().GetTokenUser()
	return e == nil && productionSocketIdentity(image, root, user.User.Sid.String())
}
func dynamicSocketProtocol() int {
	if productionSocketGuard() {
		return 2
	}
	if os.Getenv("CITA_PHYSICAL_GUARD_LAB") != "1" {
		return 0
	}
	image, e := os.Executable()
	if e != nil {
		return 0
	}
	expected := filepath.Join(os.Getenv("ProgramFiles"), "RoutePilot", "Agent", "zapret-live-lab", "native", "native-guard.exe")
	id, e := nativeGuestUUID()
	if e == nil && strings.EqualFold(id, "F8798200-12FB-11F1-8F47-A104BEE59100") && strings.EqualFold(filepath.Clean(image), filepath.Clean(expected)) {
		return 2
	}
	return 0
}
func isSystemCompanion(run string, f *wf.Rule) bool {
	if f.Persistent || f.Name != marker+run+":system" || f.Provider != wf.ProviderID(identity(run, "provider")) || f.Sublayer != wf.SublayerID(identity(run, "sublayer")) || !bytes.Equal(f.ProviderData, []byte(marker+run)) {
		return false
	}
	system := false
	for _, c := range f.Conditions {
		if c.Field == wf.FieldALEAppID && c.Value == "System" {
			system = true
		}
	}
	hash, e := connectorRuleIdentity(f)
	return system && e == nil && f.ID == wf.RuleID(identity(run, "system-"+hash))
}
func isDynamicSocketAgent(run string, f *wf.Rule) bool {
	if f.Persistent || f.Name != marker+run+":socket-agent" || f.Provider != wf.ProviderID(identity(run, "provider")) || f.Sublayer != wf.SublayerID(identity(run, "sublayer")) || !bytes.Equal(f.ProviderData, []byte(marker+run)) {
		return false
	}
	agent := false
	for _, c := range f.Conditions {
		if c.Field == wf.FieldALEAppID {
			app, _ := c.Value.(string)
			agent = strings.EqualFold(filepath.Base(app), "RoutePilot.Agent.exe")
		}
	}
	hash, e := connectorRuleIdentity(f)
	return agent && e == nil && f.ID == wf.RuleID(identity(run, "socket-agent-"+hash))
}
func holdSystemConnector(r request, decoder *json.Decoder, out io.Writer) reply {
	fail := func(stage string, e error) reply { return reply{Stage: stage, Reason: e.Error()} }
	production := productionSocketGuard()
	if dynamicSocketProtocol() != 2 || r.Connector == nil || r.Connector.Role != "" {
		return fail("SystemLeaseAuthority", errors.New("protected socket controller required"))
	}
	if production && r.Action == "HoldSystemConnector" && !r.Continuous {
		return fail("SystemLeaseLifetime", errors.New("production socket requires renewal"))
	}
	// Reuse the strict request/host validation, including exact installed image.
	validation := r
	validation.Action = "GrantConnector"
	validation.Continuous = false
	validation.SocketLease = ""
	if r.Continuous {
		identity, e := hex.DecodeString(r.SocketLease)
		if e != nil || len(identity) != 32 || r.SocketLease != strings.ToLower(r.SocketLease) {
			return fail("SystemLeaseIdentity", errors.New("socket and epoch identity required"))
		}
	} else if r.SocketLease != "" {
		return fail("SystemLeaseIdentity", errors.New("unexpected socket identity"))
	}
	if _, e := validate(validation); e != nil {
		return fail("ValidateSystemLease", e)
	}
	if e := guestOnly(); e != nil {
		return fail("SystemLeaseHost", e)
	}
	image, e := os.Executable()
	if e != nil {
		return fail("SystemLeaseImage", e)
	}
	labImage := filepath.Join(os.Getenv("ProgramFiles"), "RoutePilot", "Agent", "zapret-live-lab", "native", "native-guard.exe")
	if !production {
		station, err := nativeGuestUUID()
		if err != nil || !strings.EqualFold(station, "F8798200-12FB-11F1-8F47-A104BEE59100") || !strings.EqualFold(filepath.Clean(image), filepath.Clean(labImage)) {
			return fail("SystemLeasePhysicalOnly", errors.New("protected physical fixture required"))
		}
	}
	expected := filepath.Join(filepath.Dir(filepath.Dir(image)), "RoutePilot.Agent.exe")
	if !strings.EqualFold(filepath.Clean(r.Connector.Image), expected) {
		return fail("SystemLeaseOwner", errors.New("installed agent identity"))
	}
	local, remote, e := connectorTarget(r.Connector)
	if e != nil {
		return fail("SystemLeaseTarget", e)
	}
	if r.Action == "CheckSystemConnector" {
		check, e := wf.New(&wf.Options{Name: marker + r.Run + ":check"})
		if e != nil {
			return fail("SystemLeaseCheck", e)
		}
		defer check.Close()
		if _, e := owned(check, r); e != nil {
			return fail("SystemLeaseCheckOwnership", e)
		}
		rules, e := check.Rules()
		if e != nil {
			return fail("SystemLeaseCheck", e)
		}
		systemCount, agentCount := 0, 0
		for _, f := range rules {
			if !isSystemCompanion(r.Run, f) && !isDynamicSocketAgent(r.Run, f) {
				continue
			}
			sameLocal, sameRemote, samePort := false, false, false
			for _, c := range f.Conditions {
				switch c.Field {
				case wf.FieldIPLocalAddress:
					p, ok := conditionPrefix(c.Value)
					sameLocal = ok && p.Addr() == local
				case wf.FieldIPRemoteAddress:
					p, ok := conditionPrefix(c.Value)
					sameRemote = ok && p.Addr() == remote
				case wf.FieldIPLocalPort:
					samePort = c.Value == r.Connector.LocalPort
				}
			}
			if sameLocal && sameRemote && samePort {
				if isSystemCompanion(r.Run, f) {
					systemCount++
				} else {
					agentCount++
				}
			}
		}
		if systemCount+agentCount > 0 {
			return reply{Success: true, Stage: "SystemLeasePresent", Rules: systemCount + agentCount, SystemPermits: systemCount, SocketAgentPermits: agentCount}
		}
		return reply{Success: true, Stage: "SystemLeaseAbsent"}
	}
	luid, e := physicalLuid(r.Connector.InterfaceIndex)
	if e != nil {
		return fail("SystemLeaseInterface", e)
	}
	if e = connectorSourceReady(r.Connector.InterfaceIndex, local); e != nil {
		return fail("SystemLeaseSource", e)
	}
	s, e := wf.New(&wf.Options{Name: marker + r.Run + ":system", Dynamic: true, TransactionStartTimeout: 2 * time.Second})
	if e != nil {
		return fail("SystemLeaseOpen", e)
	}
	defer s.Close()
	existing, e := owned(s, r)
	if e != nil {
		return fail("SystemLeasePolicy", e)
	}
	protected, _, _, e := describe(existing)
	if e != nil || policyDigest(protected) != r.ExpectedPolicy {
		return fail("SystemLeasePolicy", errors.New("policy mismatch"))
	}
	endpoint, e := describeEndpoint(existing)
	if e != nil || endpoint != r.ExpectedEndpoint {
		return fail("SystemLeaseEndpoint", errors.New("endpoint mismatch"))
	}
	// The same tuple must already be granted to the agent. A standalone System
	// request cannot create an exception on an otherwise unowned connection.
	app, e := wf.AppID(expected)
	if e != nil {
		return fail("SystemLeaseApp", e)
	}
	agentHash := connectorHash(directHash(app, remote, 443, 6, luid), local, r.Connector.LocalPort)
	agentFound := false
	for _, f := range existing {
		if f.ID == wf.RuleID(identity(r.Run, "connector-"+agentHash)) {
			h, e := connectorRuleIdentity(f)
			agentFound = e == nil && h == agentHash
		}
	}
	if !agentFound && !r.Continuous {
		return fail("SystemLeaseAgent", errors.New("held agent tuple permission required"))
	}
	hash := connectorHash(directHash("System", remote, 443, 6, luid), local, r.Connector.LocalPort)
	id := wf.RuleID(identity(r.Run, "system-"+hash))
	rule := &wf.Rule{ID: id, Name: marker + r.Run + ":system", Provider: wf.ProviderID(identity(r.Run, "provider")), ProviderData: []byte(marker + r.Run), Sublayer: wf.SublayerID(identity(r.Run, "sublayer")), Layer: wf.LayerALEAuthConnectV4, Weight: 15, Action: wf.ActionPermit, Conditions: []*wf.Match{
		{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: "System"},
		{Field: wf.FieldIPRemoteAddress, Op: wf.MatchTypeEqual, Value: remote},
		{Field: wf.FieldIPRemotePort, Op: wf.MatchTypeEqual, Value: uint16(443)},
		{Field: wf.FieldIPProtocol, Op: wf.MatchTypeEqual, Value: wf.IPProtoTCP},
		{Field: wf.FieldIPLocalAddress, Op: wf.MatchTypeEqual, Value: local},
		{Field: wf.FieldIPLocalPort, Op: wf.MatchTypeEqual, Value: r.Connector.LocalPort},
		{Field: wf.FieldIPLocalInterface, Op: wf.MatchTypeEqual, Value: luid}}}
	if !isSystemCompanion(r.Run, rule) {
		return fail("SystemLeaseShape", errors.New("invalid exact tuple"))
	}
	agentID := wf.RuleID(identity(r.Run, "connector-"+agentHash))
	if r.Continuous {
		agentRule := *rule
		agentRule.Conditions = append([]*wf.Match{}, rule.Conditions...)
		agentRule.Conditions[0] = &wf.Match{Field: wf.FieldALEAppID, Op: wf.MatchTypeEqual, Value: app}
		agentRule.Name = marker + r.Run + ":socket-agent"
		agentID = wf.RuleID(identity(r.Run, "socket-agent-"+agentHash))
		agentRule.ID = agentID
		if !isDynamicSocketAgent(r.Run, &agentRule) {
			return fail("SocketAgentShape", errors.New("invalid dynamic agent tuple"))
		}
		if e = s.AddRule(&agentRule); e != nil {
			return fail("SocketAgentGrant", e)
		}
	}
	if e = s.AddRule(rule); e != nil {
		return fail("SystemLeaseGrant", e)
	}
	rules, e := s.Rules()
	if e != nil {
		return fail("SystemLeaseInspect", e)
	}
	found := false
	dynamicAgentFound := !r.Continuous
	for _, f := range rules {
		if f.ID == id {
			found = isSystemCompanion(r.Run, f)
		}
		if r.Continuous && f.ID == agentID {
			dynamicAgentFound = isDynamicSocketAgent(r.Run, f)
		}
	}
	if !found || !dynamicAgentFound {
		return fail("SystemLeaseInspect", errors.New("kernel rule not confirmed"))
	}
	if e = json.NewEncoder(out).Encode(reply{Run: r.Run, Success: true, Stage: "SystemLeaseHeld", SocketLease: r.SocketLease}); e != nil {
		return fail("SystemLeaseAck", e)
	}
	type control struct{ Release, Renew bool }
	commands := make(chan control, 1)
	go func() {
		defer close(commands)
		for {
			var command control
			if decoder.Decode(&command) != nil {
				return
			}
			commands <- command
			if command.Release || !r.Continuous {
				return
			}
		}
	}()
	budget := 20 * time.Second
	if r.Continuous {
		budget = 6 * time.Second
	}
	watchdog := time.NewTimer(budget)
	defer watchdog.Stop()
	faultApplied := false
waiting:
	for {
		select {
		case command, ok := <-commands:
			if !ok || command.Release || !r.Continuous || !command.Renew {
				break waiting
			}
			// Renewal confirms the exact kernel rule, the original physical source
			// and its agent permit. A live process alone is never health evidence.
			if e = connectorSourceReady(r.Connector.InterfaceIndex, local); e != nil {
				return fail("SystemLeaseLost", e)
			}
			currentLuid, err := physicalLuid(r.Connector.InterfaceIndex)
			if err != nil || currentLuid != luid {
				return fail("SystemLeaseLost", errors.New("interface changed"))
			}
			// Explicit physical-fixture fault injection. The public/production
			// protocol never accepts a caller-supplied filter ID or delete action.
			if !production && !faultApplied && r.Continuous {
				switch os.Getenv("CITA_DYNAMIC_LAB_DROP") {
				case "system":
					if e = s.DeleteRule(id); e != nil {
						return fail("FaultInjection", e)
					}
					faultApplied = true
				case "agent":
					if e = s.DeleteRule(agentID); e != nil {
						return fail("FaultInjection", e)
					}
					faultApplied = true
				}
			}
			currentRules, err := s.Rules()
			if err != nil {
				return fail("SystemLeaseLost", err)
			}
			alive, agentAlive := false, false
			for _, f := range currentRules {
				if f.ID == id {
					alive = isSystemCompanion(r.Run, f)
				}
				if f.ID == agentID {
					h, err := connectorRuleIdentity(f)
					agentAlive = err == nil && h == agentHash && (!r.Continuous || isDynamicSocketAgent(r.Run, f))
				}
			}
			if !alive || !agentAlive {
				return fail("SystemLeaseLost", errors.New("exact rule disappeared"))
			}
			if err = json.NewEncoder(out).Encode(reply{Run: r.Run, Success: true, Stage: "SystemLeaseCurrent", SocketLease: r.SocketLease}); err != nil {
				return fail("SystemLeaseLost", err)
			}
			if !watchdog.Stop() {
				select {
				case <-watchdog.C:
				default:
				}
			}
			watchdog.Reset(budget)
		case <-watchdog.C:
			break waiting
		}
	}
	if e = s.Close(); e != nil {
		return fail("SystemLeaseClose", e)
	}
	check, e := wf.New(&wf.Options{Name: marker + r.Run + ":verify"})
	if e != nil {
		return fail("SystemLeaseVerifyClose", e)
	}
	defer check.Close()
	rules, e = check.Rules()
	if e != nil {
		return fail("SystemLeaseVerifyClose", e)
	}
	for _, f := range rules {
		if f.ID == id || r.Continuous && f.ID == agentID {
			return fail("SystemLeaseStillPresent", errors.New("dynamic rule survived close"))
		}
	}
	return reply{Success: true, Stage: "SystemLeaseReleased"}
}
