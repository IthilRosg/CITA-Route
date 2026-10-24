# Native AWG transport laboratory

Experimental and separate from the production CITA transport. No automatic
fallback to host TCP/DNS or changes to system adapters, routes or OS DNS settings.
Probe modes have no listener; proxy lab modes bind IPv4 loopback only. The first
milestone was a single verified HTTPS request via an internal netstack.

Pinned engine: `github.com/amnezia-vpn/amneziawg-go/v3 v3.1.20260828`, commit
`b5928efb6ca19f0153958460c3d141f04abc5c2e`, module checksum
`h1:D8d8gGvwXcTxUIsE4z6F6vjy4/VZddu95vMNtOygh1c=`. Dependencies in go.sum.
Go 1.26.3, CGO disabled. Engine license: MIT; redistribution of a future product
must also include applicable transitive dependency notices and review.

Build from this directory with workspace-local GOMODCACHE/GOCACHE:
`go test ./...` and `go build -trimpath -o ../../runtime/native-awg-lab.exe .`.
The workspace build helper `Build.ps1` checks the toolchain, verifies dependencies,
runs tests/vet, and builds without changing the module manifest.
Configuration is read from stdin, never command arguments. JSON result contains
fixed stages, counters, readiness metadata and a SHA-256 digest of the observed exit address.
Upstream errors/logs are suppressed because they can contain configuration values.

Initial supported scope: one IPv4 peer, literal IPv4 endpoint, IPv4 addresses/DNS,
default IPv4 AllowedIPs, MTU 576–1420, basic WG keys/keepalive and listed AWG
J/S/H/I parameters (validated further by the engine). Hostname bootstrap, IPv6,
scripts, multiple peers and WireSock extensions are explicitly refused.
Production credential/service handling is not provided yet; isolated proxy
modes are described below. The guest runner is
restricted to the approved W11 lab UUID and requires idle CITA/WireSock.

A successful HTTPS request is NOT packet-capture leak proof, UDP acceptance,
connection retention, throughput/resource benchmark or production readiness.

## IPv4 capture and UDP gate, 24 October 2026

The probe now requires a separate bounded DNS/UDP exchange through the internal
stack, matching transaction ID, question, response flags and a public A answer.
This confirms DNS/UDP request-response, not arbitrary UDP applications or loss
characteristics. OS-NIC captures use Pktmon, 128 captured bytes/packet, bounded
8 MiB ETL files; raw files stay under the ACL-protected guest private directory.
No capture filters are removed or installed. An already-running capture causes
start to fail rather than being stopped by the runner.

Controlled W11 result:
- Direct positive control: 8 outbound TCP and 2 outbound UDP packets observed.
  UDP uses an explicit socket to avoid Windows DNS-cache false controls.
- Native HTTPS + UDP/DNS: expected exit, unchanged OS topology, 4 outbound UDP
  packets to the endpoint, zero other outbound TCP/UDP in the observed IPv4 NIC
  traffic. Probe elapsed 351 ms, a single sample, not a benchmark.
- Negative authentication control: original endpoint with a random wrong PSK
  only in process stdin; 2 outbound UDP endpoint packets, HTTPS refused, zero
  other outbound TCP/UDP. Original profile and server settings unchanged.
- Capture stopped after the tests. Result:
  `runtime/vm-lab/w11/native-awg-capture-auth.json` (local ignored receipt).

The earlier unavailable-port test returned the expected application failure,
but its capture did not observe outbound endpoint packets. That capture was NOT
accepted as a full negative packet-observation gate. Wrong-PSK testing is a
separate scenario, not a substitute claim that all server-outage cases passed.

The analyzer accepts bounded little-endian PCAPNG Ethernet EPBs (with one VLAN),
requires known link types and usable headers, and refuses unsupported/malformed
packet encodings. Counts are restricted to the selected local IPv4 source;
incoming/background packets are separate. Positive controls cover both TCP/UDP.
This is evidence for these captures, not proof for every network, IPv6, other
local source addresses, process crash, updates or future transports.

## First acceptance, 24 October 2026

Approved W11 VM only; WireSock and CITA idle, same approved test peer on UDP 3080.
Native HTTPS succeeded with the expected server exit digest, 287 ms measured
inside the process. This is ONE request including internal setup, not comparable
to prior forced-disconnect recovery tests and not an advertised startup latency.
No Windows route/adapter changes were observed. No UI/service/driver installed.

Negative test: same server IP, UDP port 9 in the stdin-only test configuration.
HTTPS failed at HandshakeAndHttps after 10006 ms, no exit digest, unchanged OS
topology. No file profile was changed. This rejects successful application-level
fallback in that test, but does not establish packet-level absence of leakage.

Local receipts (ignored, not distribution):
`runtime/vm-lab/w11/native-awg-probe.json`, `native-awg-unavailable.json`.
Parser regression tests refuse scripts/unknown extensions, duplicate keys,
multiple peers, unsafe endpoints and unsupported scope. Engine refusal test
checks that a malicious configuration value is absent from serialized results.

## TCP retention acceptance, 24 October 2026

`--retention` keeps HTTP/1.1 alive and discards encrypted datagrams for three
seconds without closing the bind, device or netstack. `--retention-inbound`
discards only received datagrams, allowing the request to reach the server.
The second GET must reuse the original TCP connection, with exactly one total
HTTP dial and one bind open, and return the original expected exit digest.
No application traffic is replayed by the test. The fixed idempotent GET could
be retried by Go's HTTP transport, but any new dial makes acceptance fail.

W11 VM results, single samples rather than latency guarantees:

| Loss | Fault | Request elapsed | TCP dials / bind opens | Dropped datagrams |
| --- | --- | --- | --- | --- |
| Both directions | 3000 ms | 3350 ms | 1 / 1 | 5 outgoing |
| Incoming only, accepted repeat | 3000 ms | 4038 ms | 1 / 1 | 10 incoming |

Both accepted runs preserved the expected exit and OS topology. Each capture
included a direct TCP/UDP positive control, then observed endpoint UDP and zero
other outgoing TCP/UDP for the selected local IPv4 source. Capture stopped.
Local ignored receipts: `runtime/vm-lab/w11/native-awg-retention.json` and
`runtime/vm-lab/w11/native-awg-retention-inbound-repeat.json`.

The first incoming-only run retained TCP (4026 ms), but its NIC capture also
contained other TCP/UDP traffic. Its combined acceptance is false; it is not
counted as packet-boundary evidence. No process attribution was collected, so
the additional traffic is not classified as either native leakage or background
traffic. The quiet repeat passed the existing strict criterion without relaxing
filters or changing the VM network.

Reproduce inside the guarded VM: `Run-GuestCapture.ps1 -RetentionOnly`, then
`Run-GuestCapture.ps1 -RetentionOnly -InboundLoss`. Config stays in stdin;
captures remain inside the protected guest folder. Local tests also cover
bounded send/receive suppression and resumption without reopening the bind.

These are synthetic packet-loss tests. They do not establish physical NIC
removal, address/NAT change, server restart, long loss, UDP stream continuity,
process crash recovery or arbitrary application survival.

## Authenticated proxy acceptance, 24 October 2026

`--serve-proxy` accepts a bounded JSON bootstrap through stdin: Config, a new
32-byte hex Token, a 16-byte hex Session identity, and positive Generation.
Stdin must remain open: EOF cancels the server, closes its listener and active
connections, waits for handlers and closes the AWG device. An ephemeral IPv4
loopback port is returned with `ListeningUnverified` and the echoed identity;
this is NOT handshake/Internet readiness. The parent must verify HTTPS first.
The token/config are absent from argv, readiness and native logs. Loopback
SOCKS5 username/password authentication is local access control, not encryption
against an administrator or protection from process-memory inspection.

The default proxy mode supports authenticated TCP CONNECT only, at most 32 handlers, a
5-second handshake and 8-second dial budget. UDP ASSOCIATE, BIND, IPv6 and
nonpublic targets (including CGNAT/Tailscale) are refused. Domain resolution
stays inside the AWG netstack; all returned addresses are validated and one
address is pinned for the dial. No host DNS/dial fallback or stream replay.
This restricted prototype is not a general replacement for production SOCKS.

`Run-GuestProxy.ps1` in the guarded W11 VM passed:

- No secret and incorrect secret refused; correct secret delivered HTTPS with
  the independently recorded VPN exit digest.
- Existing CITA `smart-router.exe` used a SOCKS outbound with the ephemeral
  credentials and delivered the same verified HTTPS exit.
- Closing owner stdin stopped the transport cleanly. A separate `-CrashOwner`
  run killed the transport process. The running core could not complete HTTPS
  after either stop; no successful direct fallback was observed.
- All test processes stopped; the topology receipt confirmed unchanged IPv4
  routes and adapter states. Installed sessions and WireSock were not changed.

`Run-GuestCapture.ps1 -ProxyChain` accepted repeat: direct positive control
observed 4 outgoing TCP and 2 outgoing UDP packets; the proxy-chain observation
had 8 endpoint UDP packets and zero other outgoing TCP/UDP from the selected
local IPv4 source. First attempt was rejected because positive control missed
outgoing UDP, before any proxy test ran. It is not counted as evidence. Capture
filters/acceptance were not relaxed for the repeat. This retains the capture
limitations described above; the crash run has application-level evidence only.

Local ignored receipts: `native-awg-proxy-capture-repeat.json`,
`native-awg-proxy-crash.json`, `native-awg-proxy-topology.json` under
`runtime/vm-lab/w11/`. One post-HTTPS memory sample was 54.5 MiB private / 20.3 MiB
working set for native AWG, and 52.8 / 24.9 MiB for the core. This is not a
steady-state measurement or a comparison with the WireSock implementation.

Unit tests use in-memory connections for authentication refusal, private/IPv6
destination refusal before dialing, explicit UDP rejection, authenticated relay,
active/partial-handshake cancellation and pending-dial cancellation on listener
failure. The VM uses real sockets and the actual CITA core. Credential-bearing
core configuration is confined to the protected guest folder and removed in
cleanup. No production config generator, service or installer was modified.

## Full core/proxy TCP retention, 24 October 2026

The explicit `--serve-proxy-loss-lab` launch mode adds a bounded stdin control
channel after bootstrap. The normal `--serve-proxy` launch cannot inject loss.
Commands must match the immutable session/generation, allow at most one
allowlisted pause (3000 or 10000 ms), and stay within eight commands / 4096 input bytes. Unknown,
malformed or mismatched commands stop the lab owner. Snapshots return counters,
never configuration or authentication tokens. Unit tests reject stale owners,
repeated pauses, unknown actions and durations outside the allowlist.

`--proxy-retention-client` first completes HTTPS through the CITA core and keeps
HTTP/1.1 TCP alive. The parent starts the fault only after that baseline, waits
for the owner's acknowledgement, then releases a second fixed anonymous GET.
Acceptance requires reuse and no additional client/core TCP dial, no additional
AWG bind open, observed loss in the chosen direction and unchanged exit digest.
The runner also requires request duration at least pause minus 500 ms, allowing
bounded parent/pipe overhead between the configured pause and release. Successful
HTTP auto-retry cannot hide a replacement connection: any extra dial fails.

W11 full-chain results, isolated samples rather than latency guarantees:

| Packet loss | Pause | Request | Client dials | Native TCP dials | Bind opens |
| --- | --- | --- | --- | --- | --- |
| Both directions | 3000 ms | 3357 ms | 1 → 1 | 3 → 3 | 1 → 1 |
| Incoming only | 3000 ms | 4046 ms | 1 → 1 | 3 → 3 | 1 → 1 |

The native dial total includes two earlier independent baseline HTTPS checks.
Five outgoing / ten incoming datagrams respectively were suppressed. Captures
observed 9 / 20 endpoint UDP packets and zero other outgoing TCP/UDP from the
selected local IPv4 source, with successful direct TCP/UDP positive controls.
Both runs preserved routes/adapters, stopped processes/capture, and removed the
temporary credential file. Receipts: `native-awg-chain-retention.json` and
`native-awg-chain-retention-inbound-repeat.json` under `runtime/vm-lab/w11/`.

The first incoming capture stopped before the proxy test because its single
positive-control UDP transmission was not observed. That result is rejected.
The control now sends three independent socket DNS queries, 100 ms apart,
and still requires observed direct TCP and UDP. This improves observability;
it does not make NIC capture lossless or extend the IPv4/source-address scope.
Reproduce: `Run-GuestCapture.ps1 -ProxyChain -ChainRetention`, optionally adding
`-InboundLoss`. This is same-address silent packet loss, not NIC removal, NAT
rebinding, server restart or preservation of an arbitrary application stream.

## Longer loss and real guest link interruption, 24 October 2026

The full-chain test now accepts the explicit `-FaultMilliseconds 10000` option.
Only 3000/10000 ms are allowed in both native control and PowerShell runners;
the command remains single-use and session/generation scoped. The acceptance
client has a 25-second header budget, 45-second overall context and the parent
allows at most 30 seconds after releasing the second request. These are lab
measurement budgets, not production timeout changes.

| Fault through CITA core | Request elapsed | Client dials | Native dials | Bind opens |
| --- | --- | --- | --- | --- |
| 10 s both directions | 12954 ms | 1 → 1 | 3 → 3 | 1 → 1 |
| 10 s incoming only | 16585 ms | 1 → 1 | 3 → 3 | 1 → 1 |

Both passed expected-exit, cleanup and unchanged-topology checks. Accepted
captures observed 12 / 21 endpoint UDP packets and no other outgoing TCP/UDP
for the selected local IPv4 source, after direct TCP/UDP controls. Seven outgoing
and one incoming packet were suppressed in the first test; fourteen incoming
in the second. Retransmission delay beyond the outage is real in these samples:
TCP retention does not imply unnoticeable pause or the same time in every app.
Receipts: `native-awg-chain-loss10s.json`, `native-awg-chain-loss10s-inbound.json`.

`Run-GuestProxy.ps1 -Retention -LinkInterruption` is a DIFFERENT scenario: it
selects exactly one up gateway adapter in the UUID-guarded VM, starts an
independent delayed re-enable helper, disables that adapter, releases the second
request, waits three seconds and re-enables. Finally also restores and verifies
the adapter. This does not inject synthetic loss or alter host networking.
Measured disable-command-to-enable-return times include command overhead and
are not exact packet-outage durations.

The full-chain link test FAILED retention: client dials 1 → 2, native dials
3 → 4, bind opens stayed 1. HTTPS recovered via the same VPN exit in 4651 ms,
but a new TCP connection was required. Adapter cycle measured 5423 ms.
Receipt `native-awg-chain-link.json` remains a failed gate, not a passing
recovery test. All processes, temporary credentials, routes and NIC state were
restored. No packet-capture absence-of-leak claim is made for adapter cycling.

A diagnostic repeat `-Retention -LinkInterruption -DirectRetention` bypassed
the CITA core for the retained stream only, using authenticated AWG SOCKS.
It PASSED: request 4857 ms, client dials 1 → 1, native dials 3 → 3, bind opens
1 → 1, adapter cycle 3557 ms. Scope/marker `coreBypassed=true` distinguishes
this from full-chain acceptance, even though the earlier receipt reused the
generic stage text. Current client emits `RetainedTcpDirectProxy` in this mode.
Receipt `native-awg-direct-link.json`; normal core baseline checks and cleanup
still run. The two cycles differ in elapsed time; these are diagnostic samples,
not a controlled performance comparison or proof for all adapters.

Source inspection of pinned sing-box 1.14.3 shows
`route/network.go: NetworkManager.ResetNetwork` calls
`connectionManager.CloseAll()`. Default-interface updates dispatch that reset.
Together with successful direct-proxy retention, this identifies core network
reset as the leading explanation; no per-connection reset trace was collected.
Do not disable all resets: physical direct traffic, DNS, TUN and other protocols
also depend on that lifecycle.

The interface-reset gate described below now has an isolated implementation
and W11 acceptance harness; see [INTERFACE-RETENTION.md](INTERFACE-RETENTION.md).
The earlier failed full-chain receipts remain preserved as baseline evidence.

Interface-reset gate: selectively retain only explicitly owned native-AWG
TCP chains across interface reset. It must verify outbound/session/generation,
keep the endpoint-only encrypted boundary, retain the default reset for other
connections, and close everything on Stop/profile change/crash. Required tests:
mixed direct/native streams, link down/up, stale generations, failed tunnel,
sleep/resume and no direct fallback. The bounded same-adapter W11 gate is now
accepted as described in the linked checkpoint. This does not establish
arbitrary NIC/address migration or make the prototype ready for production.

The next laboratory checkpoint, bounded authenticated UDP association and its
TCP-owned lifetime, is also accepted: see [UDP-CHECKPOINT.md](UDP-CHECKPOINT.md).
Normal TCP-only modes remain unchanged; UDP requires an explicit lab launch.

The bounded same-subnet guest IPv4 / outer-port migration checkpoint is now
accepted: see [MIGRATION-CHECKPOINT.md](MIGRATION-CHECKPOINT.md). TCP is through
the core; UDP continuity there is direct-native, not yet through core/TUN.

The scoped real Windows TUN / full-core TCP+UDP checkpoint is accepted; see
[TUN-CHECKPOINT.md](TUN-CHECKPOINT.md). TCP is retained; UDP is recovered with a
new core association on IPv4 change. Its packet proof is destination-scoped,
not a zero-direct claim for every packet on the VM.

Following gates: provider/public-address changes, production integration and application
workloads; production owner leases, profile/generation handoff
and rollback. Echoing an identity in this lab is not yet production fencing.
Only then optional production transport selection and UI. Keep WireSock as the
production default until those independent gates pass.

The bounded automatic encrypted-socket recovery checkpoint is accepted in its
explicit laboratory launch mode; see [RECOVERY-CHECKPOINT.md](RECOVERY-CHECKPOINT.md).
It retains the AWG device and application streams while reopening the outer UDP
socket. External process supervision and production agent integration remain
separate gates. An intermittent UDP manual-control failure is preserved and
still needs instrumentation; a bounded unchanged-binary repeat passed.

The isolated native child-process owner is described in
[OWNER-CHECKPOINT.md](OWNER-CHECKPOINT.md). It keeps a stable core endpoint,
fences child epochs, bounds crash restarts, and joins shutdown before confirming
Stop. It is a laboratory integration boundary, not an installed service backend;
existing TCP/UDP application streams are not preserved after process death.

The subsequent [UDP lifetime checkpoint](UDP-LIFETIME-CHECKPOINT.md) fixes stale
native SOCKS UDP associations on child death. A controlled opt-in/off W11
comparison restores queries from the same OS UDP socket while retaining direct
TCP. This is local UDP recovery with a new internal association, not preservation
of server-side UDP session identity or VPN TCP across a process crash.

The [coordinated agent checkpoint](COORDINATED-AGENT-CHECKPOINT.md) composes
socket/process recovery under one parent and shared lifetime budget. It is
tested through the shared `agent/NativeAwgBackend.cs` implementation, with
direct TCP held over sequential failures and Stop cancelling recovery. Normal
AgentHost/UI selection remains unchanged; this is an isolated backend candidate.

The [backend profile handoff checkpoint](PROFILE-HANDOFF-CHECKPOINT.md) accepts
strict retirement-before-start, rotating identities/credentials, stale command
and reply fencing, and Stop before startup/publication through the shared agent
coordinator. Live core/TUN outbound rebinding and different-server acceptance
remain separate gates; network preparation outliers are preserved in receipts.
