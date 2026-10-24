# Native AWG interface-reset checkpoint

Experimental branch only. Production CITA, WireSock, installers, services and
the host network are unchanged. The checkpoint concerns TCP through the lab
SOCKS inbound and the native AWG netstack, not the installed TUN routing path.

## Change and ownership

The pinned CITA core tracked its loopback SOCKS socket to native AWG together
with ordinary physical sockets. `NetworkManager.ResetNetwork` closed all of
them on an interface update. A new **opt-in** SOCKS option,
`cita_native_retention: {session, generation}`, creates an immutable lease.

The option is accepted only with SOCKS5, literal `127.0.0.1`, nonzero port,
username `cita`, a 32-byte hex password, a 16-byte hex session identifier,
positive generation, TCP-only network, no UDP-over-TCP, detour or explicit
interface/address binding. It does not apply to ordinary SOCKS outbounds.

Only the exact destination AND actual TCP peer/local loopback addresses can
receive a lease wrapper. The wrapper stays in the connection manager's tracked
list. Only a default-interface-update reset may leave a live leased entry open.
Explicit CloseAll, shutdown, suspend and OOM reset keep the original full-reset
behavior. The outbound's lifecycle scope revokes its lease, closes existing
owned sockets and prevents pending/stale dials from surviving revocation.
Revoking one generation does not revoke a separate new generation.

The source patch is local to this experimental build. It does not turn off
network monitoring, DNS resets, direct socket cleanup or other protocol hooks.
The AWG bridge still holds encrypted endpoint UDP externally; no direct
fallback is added to the native path. Session identifiers are supplied by the
trusted lab parent and echoed by the bridge before configuration is generated.
This is not a production service admission or cross-process fencing protocol.

## Acceptance method

`Build-CoreLab.ps1` checks the corresponding base-source archive against SHA256
`ECD1BC4D44C42BFD8D94148F47BA821C999DD1EDC13604324BEBE01785A9564D`.
It extracts an isolated copy, applies exact-count guarded patches, runs focused
unit tests and static analysis, and builds `runtime/native-core-lab.exe` with
the pinned Go 1.26.3 toolchain. Its receipt records binary and patch hashes and
the retained modified source directory. No production binaries are replaced.

`Run-W11Interim.ps1` copies the two lab binaries and UUID-guarded runners into
the approved W11 VM, runs five short cases, and writes
`runtime/native-awg-interim-acceptance.json`. Each individual guest receipt
records the native/core SHA256; the host acceptance requires both to match the
current input binaries. The raw configurations and credentials are never
included. The peer is used by this VM alone, with WireSock disconnected.

The cases are:

1. **Mixed link interruption with lease:** native and direct TCP streams are
   open concurrently. The VM gateway adapter is disabled, with an independent
   delayed restoration helper and finally cleanup. The native client sends a
   request during interruption. After re-enable, the parent waits for a scalar
   core reset receipt and requires a third GET on the SAME native TCP. Native
   dial/bind counters must remain unchanged; direct TCP must show reset.
2. **Lease-off control:** same binary and topology, option omitted. An actual
   reset must report closed sockets and no retained entries; native TCP must
   require a new dial. The expected refusal is a passing negative control.
3. **Active stream and owner EOF:** a keep-alive TCP stream is held while the
   bridge stdin owner closes. The held stream must cease to deliver HTTPS;
   a new request through the still-running core must also fail.
4. **Active stream and bridge crash:** same criterion after killing only the
   experimental AWG process. Successful direct fallback is forbidden.
5. **NIC packet observation:** explicit TCP/UDP direct controls, then the
   candidate core/native chain under three seconds of encrypted packet loss.
   The retained stream must pass with endpoint UDP and no other outgoing
   TCP/UDP in the selected source-IPv4 NIC observation.

Every case requires cleanup of all lab processes, temporary credential file,
and unchanged IPv4 routes/adapter states. Capture cases additionally require
the capture to stop. See the local ignored receipts under
`runtime/vm-lab/w11/native-core-interim-*.json`.

## Why the reset acknowledgement matters

An early lease-off test appeared to retain native TCP. The client could finish
before a delayed interface-reset callback executed. That result is rejected
as a causal gate. The corrected harness requires the core's actual reset
receipt, then a third request on the original stream. The receipt contains
only `closed` and `retained` counts, no endpoints, profile data or tokens.

With this stronger barrier, the first accepted comparison observed:

- Lease on: reset retained 1; native dials 3 to 3, client dials 1 to 1; third
  HTTPS succeeded on reused TCP and the independently expected VPN exit.
- Lease off: reset closed 1, retained 0; native dials 3 to 4 and client dials
  1 to 2. HTTPS could recover, but the original stream was lost as expected.

Direct streams can fail in the OS before the core callback, so a reset receipt
may report zero newly closed direct sockets. The VM proves that direct streams
are not retained; unit tests independently assert selective manager cleanup
with both entries still present at reset time. Request duration during an
adapter cycle includes retransmission and command timing; it is not a latency
guarantee or an exact packet-outage duration.

## Verification in code

Tests cover lease admission, wrong endpoints, nonloopback peers, default
behavior without an option, explicit CloseAll, repeated interface updates,
immediate revocation, concurrent pending registration/revocation, exactly-once
closure and isolation between two generations. Existing native proxy tests
still cover authentication, private/IPv6 refusal, owner cancellation, unsupported
UDP and partial handshake cleanup. No crypto implementation was changed.

The `core-patches` directory is a set of source templates copied into the
pinned core. Its nested go.mod excludes those different target packages from
the native bridge module's `go test ./...`; tests are compiled in their real
target packages by `Build-CoreLab.ps1`.

## Accepted checkpoint result, 24 October 2026

The final binary-hash-matched series passed all five cases. Mixed-link native
TCP completed its interrupted request in 3383 ms, still had one client dial,
three total native dials including earlier baseline checks, and one UDP bind.
The core receipt retained one entry. The lease-off control closed one entry,
retained none and required a second client TCP dial. Direct streams required
new TCP connections in both cases. Active EOF and crash cases closed the
held stream and refused a subsequent successful fallback request.

The first final capture was rejected despite HTTPS success: it observed two
other outgoing UDP packets. Process attribution was unavailable, so their
origin is unresolved. Its failed receipt is preserved. The single repeat,
under unchanged binaries and filters, passed: direct control 7 TCP / 1 UDP,
native chain 16 endpoint UDP / zero other TCP or UDP in the scoped observation.
The acceptance summary records both attempts. This is bounded evidence for
the accepted capture, not a claim that the earlier packets were harmless.

Use `Run-W11Interim.ps1 -RetryCaptureOnly` only for one repeat after four
passing noncapture cases on the exact same binary hashes. The harness refuses
a second retry. A fully new series can be started explicitly if new code,
failures or unresolved concerns justify it; do not loop until a capture passes.

All final cases report restored topology, stopped processes/capture where
applicable, and removal of temporary credential files. Unit tests and static
analysis passed for native AWG and the three modified core packages. The
intermediate implementation/test checkpoint is complete within this scope.

## Limits and next checkpoint

This checkpoint does NOT establish full TUN/app continuity, IPv6 handling,
new IP address or NAT rebinding, Wi-Fi/cellular migration, sleep/resume
retention, server restart, UDP/QUIC/game continuity, production service update
or profile handoff. Current explicit suspend/Stop behavior intentionally
continues to close sockets. Packet observations are restricted to a selected
source IPv4/NIC capture; they are not universal leak proof.

Keep WireSock as the released default. The next checkpoint is authenticated
UDP association with bounded resources and TCP-owned lifetime, then address
change/TUN integration and the real service's owner/profile/generation
handoff. Each needs independent acceptance before a UI transport switch or
release is proposed.
