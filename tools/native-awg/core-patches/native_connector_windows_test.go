//go:build windows

package socks

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

// Explicit physical-lab test, skipped in normal unit runs. The production
// Windows dialer/framing implementation is called without a mock transport.
func TestCitaNativePolicyConnectorPhysical(t *testing.T) {
	if os.Getenv("CITA_ZAPRET_PHYSICAL_LAB") != "1" {
		t.Skip("physical station only")
	}
	host, _ := os.Hostname()
	if !strings.EqualFold(host, "USER") {
		t.Fatal("wrong physical station")
	}
	var input struct {
		Endpoint                              nativeConnectorConfig
		Domain, Application, Address          string
		HoldMilliseconds, ParallelConnections int
		FaultExpected, RejectStale            bool
	}
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 4097))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || !input.Endpoint.valid() || input.Domain != "www.wikipedia.org" || input.HoldMilliseconds != 0 && input.HoldMilliseconds != 25000 || input.ParallelConnections != 0 && input.ParallelConnections != 4 {
		t.Fatal("physical fixture refused")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 18*time.Second+time.Duration(input.HoldMilliseconds)*time.Millisecond)
	defer cancel()
	wrong := input.Endpoint
	wrong.ServerPid++
	if connection, err := nativeConnectorDial(ctx, wrong, input.Domain, input.Application, input.Address, 443); err == nil {
		connection.Close()
		t.Fatal("foreign pipe server PID accepted")
	}
	if input.ParallelConnections == 4 {
		type result struct {
			status string
			err    error
		}
		ready := make(chan error, 4)
		results := make(chan result, 4)
		release := make(chan struct{})
		for i := 0; i < 4; i++ {
			go func() {
				connection, err := nativeConnectorDial(ctx, input.Endpoint, input.Domain, input.Application, input.Address, 443)
				ready <- err
				if err != nil {
					results <- result{err: err}
					return
				}
				defer connection.Close()
				select {
				case <-release:
				case <-ctx.Done():
					results <- result{err: ctx.Err()}
					return
				}
				connection.SetDeadline(time.Now().Add(10 * time.Second))
				encrypted := tls.Client(connection, &tls.Config{ServerName: input.Domain, MinVersion: tls.VersionTLS12})
				if err = encrypted.HandshakeContext(ctx); err != nil {
					results <- result{err: err}
					return
				}
				request, _ := http.NewRequestWithContext(ctx, "HEAD", "https://"+input.Domain+"/", nil)
				request.Close = true
				if err = request.Write(encrypted); err != nil {
					results <- result{err: err}
					return
				}
				response, err := http.ReadResponse(bufio.NewReader(encrypted), request)
				if err != nil {
					results <- result{err: err}
					return
				}
				response.Body.Close()
				results <- result{status: response.Status}
			}()
		}
		for i := 0; i < 4; i++ {
			select {
			case err := <-ready:
				if err != nil {
					cancel()
					t.Fatal("parallel admission failed", err)
				}
			case <-ctx.Done():
				t.Fatal("parallel readiness timed out")
			}
		}
		close(release)
		statuses := []string{}
		for i := 0; i < 4; i++ {
			select {
			case result := <-results:
				if result.err != nil {
					t.Fatal("parallel TLS failed", result.err)
				}
				statuses = append(statuses, result.status)
			case <-ctx.Done():
				t.Fatal("parallel payload timed out")
			}
		}
		report, _ := json.Marshal(map[string]any{"tlsValidated": true, "wrongServerPidRefused": true, "status": strings.Join(statuses, ", "), "parallelConnections": 4, "allReadyBeforePayload": true, "realWindowsPipe": true})
		fmt.Println("CITA_FLOW_READY")
		fmt.Println("CITA_CONNECTOR_INTEROP " + string(report))
		return
	}
	connection, err := nativeConnectorDial(ctx, input.Endpoint, input.Domain, input.Application, input.Address, 443)
	if err != nil {
		t.Fatal("real Windows connector dial failed")
	}
	defer connection.Close()
	if connection.SetDeadline(time.Now().Add(10*time.Second+time.Duration(input.HoldMilliseconds)*time.Millisecond)) != nil {
		t.Fatal("connector deadline refused")
	}
	encrypted := tls.Client(connection, &tls.Config{ServerName: input.Domain, MinVersion: tls.VersionTLS12})
	if encrypted.HandshakeContext(ctx) != nil {
		t.Fatal("real connector TLS handshake failed")
	}
	request, _ := http.NewRequestWithContext(ctx, "HEAD", "https://"+input.Domain+"/", nil)
	request.Close = input.HoldMilliseconds == 0 && !input.FaultExpected
	if request.Write(encrypted) != nil {
		t.Fatal("anonymous request write failed")
	}
	reader := bufio.NewReader(encrypted)
	response, err := http.ReadResponse(reader, request)
	if err != nil {
		t.Fatal("anonymous response read failed")
	}
	response.Body.Close()
	fmt.Println("CITA_FLOW_READY")
	reused := false
	failClosed := false
	failClosedMs := int64(0)
	if input.FaultExpected {
		if response.Close {
			t.Fatal("control server refused persistent HTTP; fault observation unavailable")
		}
		started := time.Now()
		connection.SetReadDeadline(time.Now().Add(10 * time.Second))
		var sample [1]byte
		n, err := encrypted.Read(sample[:])
		failClosedMs = time.Since(started).Milliseconds()
		if n != 0 || err == nil || failClosedMs >= 9500 {
			t.Fatal("path loss did not close the same TLS flow within the observation budget")
		}
		failClosed = true
	}
	if input.HoldMilliseconds > 0 {
		if response.Close {
			t.Fatal("control server refused persistent HTTP; long-flow measurement unavailable")
		}
		select {
		case <-time.After(time.Duration(input.HoldMilliseconds) * time.Millisecond):
		case <-ctx.Done():
			t.Fatal("hold cancelled")
		}
		request.Close = true
		if request.Write(encrypted) != nil {
			t.Fatal("same TLS flow write failed after hold")
		}
		response, err = http.ReadResponse(reader, request)
		if err != nil {
			t.Fatal("same TLS flow response failed after hold")
		}
		response.Body.Close()
		reused = true
	}
	if !input.FaultExpected && connection.(*nativeConnectorConn).CloseWrite() != nil {
		t.Fatal("real pipe directional EOF failed")
	}
	staleRefused := false
	if input.RejectStale {
		retry, cancelRetry := context.WithTimeout(ctx, 3*time.Second)
		defer cancelRetry()
		old, err := nativeConnectorDial(retry, input.Endpoint, input.Domain, input.Application, input.Address, 443)
		if err == nil {
			old.Close()
			t.Fatal("stale epoch/capability admitted after path shutdown")
		}
		staleRefused = true
	}
	report, _ := json.Marshal(map[string]any{"tlsValidated": true, "wrongServerPidRefused": true, "status": response.Status,
		"realWindowsPipe": true, "heldMilliseconds": input.HoldMilliseconds, "sameTlsFlowReused": reused, "failClosedObserved": failClosed, "staleEpochRefused": staleRefused, "failClosedMs": failClosedMs, "implementation": "production nativeConnectorDial and framing"})
	fmt.Println("CITA_CONNECTOR_INTEROP " + string(report))
}
