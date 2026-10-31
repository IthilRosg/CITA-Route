//go:build !windows

package socks

import (
	"context"
	"net"
)

func nativeConnectorDial(context.Context, nativeConnectorConfig, string, string, string, uint16) (net.Conn, error) {
	return nil, errNativeConnector
}
