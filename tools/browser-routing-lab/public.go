//go:build windows

package main

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	stdjson "encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"time"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	"github.com/sagernet/sing/common/json"
)

type publicTarget struct {
	Domain        string `json:"domain"`
	Address       string `json:"address"`
	BindInterface string `json:"bindInterface"`
}

func publicAddress(value string) bool {
	ip, err := netip.ParseAddr(value)
	if err != nil || !ip.Is4() || !ip.IsGlobalUnicast() {
		return false
	}
	for _, block := range []string{"0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4"} {
		if netip.MustParsePrefix(block).Contains(ip) {
			return false
		}
	}
	return true
}

func validatePublicConfig(data []byte, target publicTarget) error {
	if (target.Domain != "cloudflare-quic.com" && target.Domain != "www.cloudflare.com") || !publicAddress(target.Address) || target.BindInterface == "" || len(target.BindInterface) > 128 || target.BindInterface == "Cita-PublicWeb" {
		return fmt.Errorf("public fixture target refused")
	}
	var cfg struct {
		Inbounds  []map[string]any `json:"inbounds"`
		Outbounds []map[string]any `json:"outbounds"`
	}
	if err := stdjson.Unmarshal(data, &cfg); err != nil {
		return err
	}
	if len(cfg.Inbounds) != 2 || len(cfg.Outbounds) != 2 {
		return fmt.Errorf("unexpected public fixture topology")
	}
	tuns, proxies := 0, 0
	for _, in := range cfg.Inbounds {
		if in["type"] == "tun" {
			tuns++
			routes, _ := in["route_address"].([]any)
			if len(routes) != 1 || routes[0] != target.Address+"/32" || in["auto_route"] != true || in["strict_route"] != false || in["dns_mode"] != "disabled" || in["interface_name"] != "Cita-PublicWeb" {
				return fmt.Errorf("public fixture default route or DNS change refused")
			}
		} else if in["type"] == "mixed" && in["listen"] == "127.0.0.1" && in["tag"] == "policy-ack" {
			proxies++
		} else {
			return fmt.Errorf("non-loopback fixture proxy refused")
		}
	}
	wan, proof := 0, 0
	for _, out := range cfg.Outbounds {
		if out["type"] != "direct" {
			return fmt.Errorf("unexpected public fixture transport")
		}
		switch out["tag"] {
		case "wan":
			wan++
			if out["bind_interface"] != target.BindInterface {
				return fmt.Errorf("public fixture interface binding refused")
			}
		case "proof":
			proof++
		default:
			return fmt.Errorf("unexpected public fixture outbound")
		}
	}
	if tuns != 1 || proxies != 1 || wan != 1 || proof != 1 {
		return fmt.Errorf("duplicate public fixture component")
	}
	return nil
}

func runPublic(root string) error {
	manifest, err := os.ReadFile(filepath.Join(root, "public-target.json"))
	if err != nil {
		return err
	}
	var target publicTarget
	if err = stdjson.Unmarshal(manifest, &target); err != nil {
		return err
	}
	data, err := os.ReadFile(filepath.Join(root, "router.json"))
	if err != nil {
		return err
	}
	if err = validatePublicConfig(data, target); err != nil {
		return err
	}
	nic, err := net.InterfaceByName(target.BindInterface)
	if err != nil || nic.Flags&net.FlagLoopback != 0 || nic.Flags&net.FlagUp == 0 {
		return fmt.Errorf("active physical guest interface required")
	}
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
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return err
	}
	nonce := make([]byte, 24)
	if _, err = rand.Read(nonce); err != nil {
		listener.Close()
		return err
	}
	prefix := "/" + base64.RawURLEncoding.EncodeToString(nonce)
	stopped := make(chan struct{})
	var once sync.Once
	mux := http.NewServeMux()
	mux.HandleFunc(prefix+"/shutdown", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("OK"))
		once.Do(func() { close(stopped) })
	})
	api := &http.Server{Handler: mux, ReadHeaderTimeout: time.Second}
	defer api.Close()
	go api.Serve(listener)
	ready, _ := stdjson.Marshal(map[string]any{"control": "http://" + listener.Addr().String() + prefix, "pid": os.Getpid()})
	path := filepath.Join(root, "ready.json")
	if err = os.WriteFile(path+".tmp", ready, 0600); err != nil {
		return err
	}
	if err = os.Rename(path+".tmp", path); err != nil {
		return err
	}
	select {
	case <-stopped:
	case <-time.After(5 * time.Minute):
	}
	return nil
}
