//go:build windows && (amd64 || arm64)

package tun

import (
	"github.com/sagernet/sing-tun/internal/winipcfg"
	"github.com/sagernet/sing-tun/internal/winsys"
	"golang.org/x/sys/windows"
	"net/netip"
	"os"
	"testing"
	"time"
	"unsafe"
)

func TestCitaTailAddress(t *testing.T) {
	for _, test := range []struct {
		description string
		up          bool
		ip          string
		want        bool
	}{
		{"Tailscale Tunnel", true, "100.80.1.2", true},
		{"Tailscale Tunnel #2", true, "fd7a:115c:a1e0::123", true},
		{"Tailscale", true, "100.64.0.1", true},
		{"Ethernet", true, "100.80.1.2", false},
		{"Tailscale Tunnel", false, "100.80.1.2", false},
		{"Tailscale Tunnel", true, "192.168.1.2", false},
		{"Tailscale Tunnel", true, "fd7a:115c:a1e1::123", false},
		{"Tailscale Tunnel", true, "100.128.0.1", false},
	} {
		if got := citaTailAddress(test.description, test.up, netip.MustParseAddr(test.ip)); got != test.want {
			t.Errorf("adapter boundary: %+v got %v", test, got)
		}
	}
}

// Explicit VM-only opt-in. The build runs only TestCitaTailAddress on the host.
// Dynamic WFP objects disappear even if the fixture process is killed.
func TestCitaWfpLifecycle(t *testing.T) {
	if os.Getenv("CITA_WFP_LAB") != "1" {
		t.Skip("isolated Windows guest only")
	}
	var engine uintptr
	if err := winsys.FwpmEngineOpen0(nil, winsys.RPC_C_AUTHN_DEFAULT, nil, &winsys.FWPM_SESSION0{Flags: winsys.FWPM_SESSION_FLAG_DYNAMIC}, unsafe.Pointer(&engine)); err != nil {
		t.Fatal(err)
	}
	defer winsys.FwpmEngineClose0(engine)
	sub, err := windows.GenerateGUID()
	if err != nil {
		t.Fatal(err)
	}
	layer := winsys.FWPM_SUBLAYER0{SubLayerKey: sub, Weight: 65535}
	layer.DisplayData = winsys.CreateDisplayData("CITA test only", "temporary DNS filter validation")
	if err = winsys.FwpmSubLayerAdd0(engine, &layer, 0); err != nil {
		t.Fatal(err)
	}
	adapters, err := winipcfg.GetAdaptersAddresses(windows.AF_UNSPEC, winipcfg.GAAFlagDefault)
	if err != nil || len(adapters) == 0 {
		t.Fatal("no guest interface", err)
	}
	get := windows.NewLazySystemDLL("fwpuclnt.dll").NewProc("FwpmFilterGetById0")
	for _, v6 := range []bool{false, true} {
		id, err := citaAddDNSFilter(engine, sub, citaDNSKey{uint64(adapters[0].LUID), v6})
		if err != nil {
			t.Fatal(err)
		}
		var filter *winsys.FWPM_FILTER0
		code, _, _ := get.Call(engine, uintptr(id), uintptr(unsafe.Pointer(&filter)))
		if code != 0 || filter == nil {
			t.Fatal("filter inspection failed", code)
		}
		if filter.NumFilterConditions != 3 || filter.Action.Type != winsys.FWP_ACTION_PERMIT || filter.Flags != 0 {
			t.Fatal("overbroad DNS exception")
		}
		conditions := unsafe.Slice(filter.FilterCondition, filter.NumFilterConditions)
		if conditions[0].FieldKey != citaLocalInterface || conditions[0].ConditionValue.Type != winsys.FWP_UINT64 || *(*uint64)(unsafe.Pointer(conditions[0].ConditionValue.Value)) != uint64(adapters[0].LUID) {
			t.Fatal("DNS permit lost LUID restriction")
		}
		if conditions[1].ConditionValue.Value != 53 || conditions[2].FieldKey != citaRemoteAddress {
			t.Fatal("DNS permit lost port/address restriction")
		}
		winsys.FwpmFreeMemory0(unsafe.Pointer(&filter))
		if err = citaRemoveDNSFilter(engine, id); err != nil {
			t.Fatal(err)
		}
		if err = citaRemoveDNSFilter(engine, id); err == nil {
			t.Fatal("deleted filter unexpectedly remained")
		}
	}
}

func TestCitaLabIdle(t *testing.T) {
	if os.Getenv("CITA_WFP_LAB") != "1" {
		t.Skip("isolated Windows guest only")
	}
	time.Sleep(30 * time.Second)
}
