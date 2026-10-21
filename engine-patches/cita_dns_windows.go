//go:build windows && (amd64 || arm64)

// CITA routing core modification, 2026. Distributed with the corresponding
// upstream source and license. No affiliation with the upstream project.
package tun

import (
	"fmt"
	"net/netip"
	"os"
	"runtime"
	"strings"
	"time"
	"unsafe"

	"github.com/sagernet/sing-tun/internal/winipcfg"
	"github.com/sagernet/sing-tun/internal/winsys"
	"golang.org/x/sys/windows"
)

var citaLocalInterface = windows.GUID{Data1: 0x4cd62a49, Data2: 0x59c3, Data3: 0x4969, Data4: [8]byte{0xb7, 0xf3, 0xbd, 0xa5, 0xd3, 0x28, 0x90, 0xa4}}
var citaRemoteAddress = windows.GUID{Data1: 0xb235ae9a, Data2: 0x1d64, Data3: 0x49b8, Data4: [8]byte{0xa4, 0x4c, 0x5f, 0xf3, 0xd9, 0x09, 0x50, 0x45}}
var citaDeleteFilter = windows.NewLazySystemDLL("fwpuclnt.dll").NewProc("FwpmFilterDeleteById0")

type citaDNSKey struct {
	LUID uint64
	IPv6 bool
}

func citaTailAddress(description string, up bool, address netip.Addr) bool {
	description = strings.ToLower(strings.TrimSpace(description))
	return up && (description == "tailscale" || description == "tailscale tunnel" || strings.HasPrefix(description, "tailscale tunnel #")) &&
		(netip.MustParsePrefix("100.64.0.0/10").Contains(address.Unmap()) || netip.MustParsePrefix("fd7a:115c:a1e0::/48").Contains(address))
}

func citaDNSInterfaces() (map[citaDNSKey]bool, error) {
	adapters, err := winipcfg.GetAdaptersAddresses(windows.AF_UNSPEC, winipcfg.GAAFlagDefault)
	if err != nil {
		return nil, err
	}
	wanted := map[citaDNSKey]bool{}
	for _, adapter := range adapters {
		for address := adapter.FirstUnicastAddress; address != nil; address = address.Next {
			ip, ok := netip.AddrFromSlice(address.Address.IP())
			if !ok || !citaTailAddress(adapter.Description(), adapter.OperStatus == winipcfg.IfOperStatusUp, ip) {
				continue
			}
			wanted[citaDNSKey{uint64(adapter.LUID), ip.Is6() && !ip.Is4In6()}] = true
		}
	}
	return wanted, nil
}

// The permit lives in the SAME dynamic WFP session/sublayer as the engine's
// port-53 block. It is limited to Quad100, port 53 and an actual Tailscale LUID.
// A LUID, unlike an interface index, cannot silently become a different NIC.
func citaAddDNSFilter(engine uintptr, subLayer windows.GUID, key citaDNSKey) (uint64, error) {
	luid := key.LUID
	conditions := make([]winsys.FWPM_FILTER_CONDITION0, 3)
	conditions[0].FieldKey = citaLocalInterface
	conditions[0].ConditionValue.Type = winsys.FWP_UINT64
	conditions[0].ConditionValue.Value = uintptr(unsafe.Pointer(&luid))
	conditions[1].FieldKey = winsys.FWPM_CONDITION_IP_REMOTE_PORT
	conditions[1].ConditionValue.Type = winsys.FWP_UINT16
	conditions[1].ConditionValue.Value = 53
	conditions[2].FieldKey = citaRemoteAddress
	layer := winsys.FWPM_LAYER_ALE_AUTH_CONNECT_V4
	target6 := netip.MustParseAddr("fd7a:115c:a1e0::53").As16()
	var pinned runtime.Pinner
	pinned.Pin(&luid)
	pinned.Pin(&target6)
	defer pinned.Unpin()
	if key.IPv6 {
		layer = winsys.FWPM_LAYER_ALE_AUTH_CONNECT_V6
		conditions[2].ConditionValue.Type = winsys.FWP_BYTE_ARRAY16_TYPE
		conditions[2].ConditionValue.Value = uintptr(unsafe.Pointer(&target6[0]))
	} else {
		conditions[2].ConditionValue.Type = winsys.FWP_UINT32
		conditions[2].ConditionValue.Value = 0x64646464
	}
	for i := range conditions {
		conditions[i].MatchType = winsys.FWP_MATCH_EQUAL
	}
	filter := winsys.FWPM_FILTER0{}
	filter.DisplayData = winsys.CreateDisplayData("CITA routing core", "Tailscale Quad100 DNS only")
	filter.SubLayerKey = subLayer
	filter.LayerKey = layer
	filter.FilterCondition = &conditions[0]
	filter.NumFilterConditions = uint32(len(conditions))
	filter.Action.Type = winsys.FWP_ACTION_PERMIT
	filter.Weight.Type = winsys.FWP_UINT8
	filter.Weight.Value = 11
	// Do not clear action rights: unrelated firewall/security policies still apply.
	var id uint64
	if err := winsys.FwpmFilterAdd0(engine, &filter, 0, &id); err != nil {
		return 0, os.NewSyscallError("CITA Quad100 filter", err)
	}
	return id, nil
}

func citaRemoveDNSFilter(engine uintptr, id uint64) error {
	code, _, _ := citaDeleteFilter.Call(engine, uintptr(id))
	if code != 0 {
		return fmt.Errorf("CITA DNS filter removal failed: %d", code)
	}
	return nil
}

func (t *NativeTun) startCitaDNS(subLayer windows.GUID) error {
	active := map[citaDNSKey]uint64{}
	refresh := func() error {
		wanted, err := citaDNSInterfaces()
		if err != nil {
			return err
		}
		for key, id := range active {
			if wanted[key] {
				continue
			}
			if err = citaRemoveDNSFilter(t.fwpmSession, id); err != nil {
				return err
			}
			delete(active, key)
		}
		for key := range wanted {
			if _, present := active[key]; present {
				continue
			}
			id, addErr := citaAddDNSFilter(t.fwpmSession, subLayer, key)
			if addErr != nil {
				return addErr
			}
			active[key] = id
		}
		return nil
	}
	if err := refresh(); err != nil {
		return err
	}
	t.citaDNSStop = make(chan struct{})
	t.citaDNSDone = make(chan struct{})
	go func() {
		defer close(t.citaDNSDone)
		ticker := time.NewTicker(2 * time.Second)
		defer ticker.Stop()
		lastWarning := time.Time{}
		for {
			select {
			case <-t.citaDNSStop:
				return
			case <-ticker.C:
				if err := refresh(); err != nil && time.Since(lastWarning) > time.Minute {
					if t.options.Logger != nil {
						t.options.Logger.Warn("CITA Tailscale DNS refresh unavailable; ordinary DNS protection retained")
					}
					lastWarning = time.Now()
				}
			}
		}
	}()
	return nil
}
