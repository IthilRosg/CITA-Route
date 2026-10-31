//go:build windows

package socks

import (
	"context"
	"github.com/tailscale/go-winio"
	"golang.org/x/sys/windows"
	"net"
)

func nativeConnectorDial(ctx context.Context, config nativeConnectorConfig, domain, application, address string, port uint16) (net.Conn, error) {
	if !config.valid() {
		return nil, errNativeConnector
	}
	setup, cancel := context.WithTimeout(ctx, 5*1000000000)
	defer cancel()
	conn, err := winio.DialPipeContext(setup, `\\.\pipe\`+config.Pipe)
	if err != nil {
		return nil, errNativeConnector
	}
	// Never send the capability to a pipe squatter. The controller PID arrived
	// through inherited private stdin, not from the pipe we are authenticating.
	fd, ok := conn.(interface{ Fd() uintptr })
	var pid uint32
	if !ok || windows.GetNamedPipeServerProcessId(windows.Handle(fd.Fd()), &pid) != nil || pid != config.ServerPid {
		conn.Close()
		return nil, errNativeConnector
	}
	return nativeConnectorHandshake(setup, conn, config, domain, application, address, port)
}
