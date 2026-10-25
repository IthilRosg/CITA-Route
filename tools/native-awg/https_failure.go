package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"net"
	"syscall"
)

// Error messages may contain names, URLs and credentials. Publish only fixed
// categories derived from typed causes, never err.Error() or nested raw text.
func httpsFailureCode(e error) string {
	if e == nil {
		return ""
	}
	var reply controlSocksReply
	if errors.As(e, &reply) {
		switch reply {
		case 3, 4:
			return "NetworkUnreachable"
		case 5:
			return "ConnectionRefused"
		case 2:
			return "ProxyPolicyRefused"
		default:
			return "ProxyRefused"
		}
	}
	if errors.Is(e, errControlIdentity) {
		return "ProxyIdentityRefused"
	}
	var cert *tls.CertificateVerificationError
	var unknown x509.UnknownAuthorityError
	var hostname x509.HostnameError
	if errors.As(e, &cert) || errors.As(e, &unknown) || errors.As(e, &hostname) {
		return "CertificateRejected"
	}
	var record tls.RecordHeaderError
	if errors.As(e, &record) {
		return "TlsRecordRejected"
	}
	if errors.Is(e, context.Canceled) {
		return "Canceled"
	}
	if errors.Is(e, context.DeadlineExceeded) {
		return "Timeout"
	}
	// Windows netpoll returns native Winsock numbers, which are distinct
	// from Go's portable syscall.ECONN* values on this target.
	var winsock syscall.Errno
	if errors.As(e, &winsock) {
		switch uint64(winsock) {
		case 10053, 10054:
			return "ConnectionReset"
		case 10061:
			return "ConnectionRefused"
		case 10050, 10051, 10064, 10065:
			return "NetworkUnreachable"
		case 10060:
			return "Timeout"
		}
	}
	var timed net.Error
	if errors.As(e, &timed) && timed.Timeout() {
		return "Timeout"
	}
	if errors.Is(e, syscall.ECONNRESET) || errors.Is(e, syscall.ECONNABORTED) {
		return "ConnectionReset"
	}
	if errors.Is(e, syscall.ECONNREFUSED) {
		return "ConnectionRefused"
	}
	if errors.Is(e, syscall.ENETUNREACH) || errors.Is(e, syscall.EHOSTUNREACH) {
		return "NetworkUnreachable"
	}
	if errors.Is(e, io.ErrUnexpectedEOF) {
		return "Truncated"
	}
	if errors.Is(e, io.EOF) || errors.Is(e, net.ErrClosed) {
		return "Closed"
	}
	return "Unclassified"
}

func httpsFailureLayers(e error) (layers []string, number uint64) {
	for i := 0; e != nil && i < 6; i++ {
		layers = append(layers, fmt.Sprintf("%T", e))
		if n, ok := e.(syscall.Errno); ok {
			number = uint64(n)
		}
		e = errors.Unwrap(e)
	}
	return
}
