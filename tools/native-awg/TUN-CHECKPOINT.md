# Scoped Windows TUN checkpoint — 24 October 2026

Accepted on the approved W11 VM, experimental branch only. This is the real
Windows TUN adapter with a gVisor routing stack and fixed public IPv4 /32 test
routes, not the production client's complete routing policy or an all-app test.
Installed CITA/WireSock and host networking are unchanged.

## Ordinary application path

`--tun-client` uses OS `net.Dialer` TCP and connected OS UDP sockets. There is
no SOCKS dialer, proxy environment lookup, internal netstack or TLS certificate
override in this code path. HTTPS uses `https://1.1.1.1/cdn-cgi/trace` with normal
certificate verification; UDP uses validated anonymous `example.com` DNS A
queries to the configured public resolver. Local TCP/UDP addresses must belong
to the lab TUN prefix 172.30.127.0/30. Three UDP replies are verified before
and three after each event on the same OS socket. HTTP/1.1 reuse and dial counts
prevent a reconnect from masquerading as retained TCP.

The guest configuration adds `Cita-NativeLab`, DNS integration disabled,
MTU 1400, only 1.1.1.1/32 and the configured public DNS /32. Normal adapter-local
and multicast routes may also appear; default or split-default TUN routes are
explicitly refused. TCP routes to the leased native SOCKS outbound; UDP routes
to the separate authenticated native UDP outbound. The AWG endpoint is outside
the captured /32 addresses. No production rule generator is modified.

An independent hidden 90-second helper can stop only the owned core PID with
matching start ticks if the controller fails. Main cleanup also stops the core
and requires removal of the TUN/temporary routes and unchanged original
topology. IPv4 mutation retains the separate DHCP restore mechanism.

## Complete reset barrier

The old counter receipt was emitted as the connection manager finished, before
the TUN and DNS reset callbacks. The isolated core now also emits a fixed
`CITA_LAB_INTERFACE_RESET_DONE` marker AFTER those callbacks, only when the
explicit `CITA_NATIVE_TUN_LAB=1` process environment is enabled. The runner waits
for both receipts before releasing the post-migration application requests.
This observation patch does not disable TUN resets or preserve UDP NAT state.
Build-CoreLab produces `1.14.3-cita-native-lab.3`; prior lease/UDP tests and
static analysis still run.

## Final five cases

`Run-W11Tun.ps1` writes `runtime/native-awg-tun-acceptance.json`, matches both
binary hashes and checks cleanup. Guest receipts are local ignored files
`runtime/vm-lab/w11/native-tun-final-*.json`.

| Case | Result |
| --- | --- |
| Outer UDP port rebind | OS TCP dials 1→1, native TCP unchanged, native UDP 1→1; six UDP replies; retained HTTPS request 55 ms |
| Guest IPv4 change | OS TCP dials 1→1; actual complete core/TUN reset observed; native UDP 1→2; six UDP replies; HTTPS request 71 ms |
| Lease omitted | Core retains no leased TCP; OS HTTP requires a second TCP dial; expected negative control passes |
| Active AWG crash | TCP and UDP first work; client holds TCP across the barrier; bridge is killed; the next HTTP request cannot complete on that stream or recover successfully |
| Scoped NIC observation | Controls show direct TCP/UDP to the protected targets; no direct TCP/UDP to those targets observed during the TUN rebind run |

For every case, a fresh ordinary curl HTTPS request and an OS UDP DNS probe
also fail after AWG stops while the core and TUN routes remain active. These
checks do not assert safety after the core itself crashes or the TUN is removed.
Negative HTTP GET retries belong to the diagnostic HTTP library; the routing
core does not replay user requests. Positive retention requires exactly one dial.

The IPv4 case is **UDP recovery**, not seamless UDP retention: TUN reset purges
its NAT/association, then the next packet creates a new core/native UDP flow.
The application OS UDP socket remains open, but the server-side source port
may change. Stateful games, calls and QUIC need separate acceptance. A pure
outer-port rebind preserved the native UDP association in the measured case.

## Destination-scoped packet evidence

The initial all-destination test rejected the capture with other outgoing TCP
and UDP. That broad zero-direct condition is inappropriate for a /32 split
route: other destinations are not routed into this TUN. Their origin is not
known because no PID attribution was collected; they are NOT silently called
background traffic or declared harmless.

The accepted criterion is explicit and narrower: count direct physical-NIC
packets to the exact protected public IPv4 targets, on any TCP/UDP port. Before
the TUN starts, direct positive controls to those SAME targets must show both
TCP and UDP. All original other-traffic counters remain in the report. Analyzer
tests cover protected/nonprotected destinations and refusal of malformed input,
unsafe/duplicate protected sets and unsupported captures.

Final accepted capture: control protected TCP=4, UDP=1; TUN run protected TCP=0,
UDP=0, encrypted endpoint UDP=24. Other outgoing TCP=25 and UDP=11 were still
observed and remain unattributed. This is evidence only for those targets and
the selected local source-IPv4/NIC observation. It is not universal leak proof,
IPv6 coverage, or evidence that every packet belongs to a particular process.

The first destination-scoped attempt was rejected because PowerShell serialized
a one-element address list as a scalar. The list shape was corrected and
checked; a single capture-only repeat passed on unchanged binaries. Earlier
functional cases retained their matching hashes. The summary records both
attempts, and previous series/receipts are preserved in the local history.

## Debugging and limits

One early warmup failed before address mutation; it is not counted as a pass.
The parent readiness budget now matches the diagnostic client's longer timing
budget (30 seconds for TUN); the original outlier has no proven root cause and
no universal cold-start claim is made. Another preflight incorrectly checked
physical source selection against 1.1.1.1, which was now owned by TUN. It was
corrected to check the physical gateway; the failed attempt restored topology.

All final cases restored DHCP/IP/DNS and original topology, removed temporary
credential files and stopped owned processes/capture. Unit tests, native/core
static analysis and script parsing passed. Timings are individual request
samples, excluding adapter reconfiguration time; not latency guarantees.

Still outside this checkpoint: production mixed/system TUN stacks, arbitrary
domains/apps, full direct/DPI/VPN policy, provider/public-IP migration, IPv6,
continuous traffic/loss measurement, game/QUIC/call session continuity, sleep,
automatic agent recovery, updates/profile handoff and whole-core crash safety.
The next work is bounded automatic service recovery and preserving UDP session
identity where needed before exposing an optional production transport.

Subsequent lab result: [RECOVERY-CHECKPOINT.md](RECOVERY-CHECKPOINT.md) verifies
bounded automatic outer-socket repair without recreating the TUN/AWG device.
Service/process recovery and production integration are still outstanding.
