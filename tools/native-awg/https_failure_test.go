package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"net/url"
	"syscall"
	"testing"
)

func TestHttpsFailureTypedCausesDoNotDiscloseRequest(t *testing.T) {
	for _, test := range []struct {
		cause error
		want  string
	}{{context.DeadlineExceeded, "Timeout"}, {context.Canceled, "Canceled"}, {syscall.ECONNRESET, "ConnectionReset"}, {syscall.ECONNREFUSED, "ConnectionRefused"}, {syscall.ENETUNREACH, "NetworkUnreachable"}, {io.EOF, "Closed"}, {io.ErrUnexpectedEOF, "Truncated"}, {&tls.CertificateVerificationError{Err: x509.UnknownAuthorityError{}}, "CertificateRejected"}, {errors.New("private-token-and-domain"), "Unclassified"}} {
		wrapped := &url.Error{Op: "Get", URL: "https://private.invalid/?token=secret", Err: fmt.Errorf("private context: %w", test.cause)}
		if got := httpsFailureCode(wrapped); got != test.want {
			t.Fatalf("category: got %q, want %q", got, test.want)
		}
	}
}

func TestWinsockAbortIsNotAnUnclassifiedTlsFailure(t *testing.T) {
	for _, code := range []syscall.Errno{10053, 10054} {
		if httpsFailureCode(fmt.Errorf("private diagnostic: %w", code)) != "ConnectionReset" {
			t.Fatal("native Winsock abort not recognized")
		}
	}
}
