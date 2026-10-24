# Targeted UDP association lifetime — W11 checkpoint

Accepted in the isolated core `1.14.3-cita-native-lab.4`. Neither the installed
client nor the Windows rescue service is updated by this checkpoint.

## Cause and change

The pinned sing SOCKS client creates an `AssociatePacketConn` with a UDP data
socket and an underlying TCP control connection. Its old implementation closes
both on explicit Close, but never watches control EOF while blocked reading
UDP. AWG child death therefore closes TCP without promptly closing that UDP
reader. The core can retain a stale packet association for the application's
existing UDP tuple; a fresh OS socket succeeds while the old one times out.

The optional lifetime wrapper watches that association's **own** control
connection. EOF/read failure (or unexpected control bytes) closes its UDP data
socket and wakes the blocked core packet reader. The normal core cleanup then
removes that flow. A subsequent packet from the same application UDP socket
creates a new association to the current child. This is resource-local
invalidation, not a global epoch broadcast, TUN reset or network restart.
No packet is retried, queued or replayed by the wrapper. Explicit Close is
idempotent and closes the control watcher as well as the UDP resource.

Two explicit opt-ins are required: `cita_native_udp_lifetime: true` on the
native UDP SOCKS outbound and `CITA_NATIVE_UDP_LIFETIME_LAB=1` on the owned core
process. Only literal 127.0.0.1 SOCKS5, username `cita`, a 32-byte hex credential
and a nonzero port qualify. Core validation also requires UDP-only network,
no UoT, detour, bind overrides or TCP-retention lease. Other SOCKS clients,
other outbounds and direct streams keep their original behavior. This is
explicit laboratory configuration, not production profile ownership fencing.

No new owner-management API is needed: each TCP association is already tied to
one child incarnation. EOF from an old control connection closes only its own
data resource, not connections opened for the replacement child. The stable
supervisor endpoint, session/generation and bounded restart budget are unchanged.

## Controlled W11 comparison, 24 October 2026

Summary: `runtime/native-awg-udp-life-187b10d8e3b74fba8e0f557f5d632dc7.json`.
All three cases use identical native/core binary hashes and restore topology,
stop owned processes and remove temporary credential-bearing configuration.

| Case | Original OS UDP socket | Parallel direct TCP | Result |
|---|---|---|---|
| Opt-in absent | 3 DNS replies before child crash, 0 after; ReadTimeout | Not measured | Expected negative control reproduces stale flow |
| Opt-in enabled | 3 replies before, 3 after child crash; no DNS failure | Same TCP/TLS stream, dials 1→1 | Targeted recovery accepted |
| Stop in observed restart backoff | 3 before, 0 after intentional Stop | Same stream, dials 1→1 | No resurrection or successful VPN fallback |

The ordinary OS UDP client opens **one** connected UDP socket and keeps it open
across the crash barrier. No diagnostic query retry, extra socket creation,
deadline increase or relaxed DNS validation was added. Its existing four-second
per-query deadline is unchanged. The positive run observes a different AWG
child behind the same core endpoint, then all three replies on the original
OS socket. The child readiness interval was 1,038 ms; this is not a complete
traffic-ready latency bound. A separate fresh OS TUN client also passed TLS
with expected exit and all six DNS samples; its retained HTTPS sample was 53 ms.

The original held VPN TCP connection still breaks when the AWG process dies;
the diagnostic HTTP library redialed (dials 1→2, request 169 ms after the restart
barrier). Its generic combined `success` field therefore correctly stays false.
Acceptance checks the original socket's independent UDP-before/after fields
and the expected broken TCP behavior, rather than relabeling TCP as retained.
The parallel direct TCP client reused its connection (30 ms request sample).

After transport Stop/exhaustion, fresh ordinary OS HTTPS and UDP probes fail
while scoped TUN routes/core remain. The Stop case observes backoff at epoch 1
and confirms completed shutdown at the same epoch with no child PID. Existing
restart-budget checks still pass in the crash cases. This is scoped IPv4 probe
evidence; no new NIC capture, universal leak claim or whole-core crash proof.

## Verification and limits

The isolated sing/core source patch is applied only against exact pinned source
anchors. Build records include the corresponding source and patch hashes.
Library tests cover control EOF waking a blocked reader, explicit Close joining
the watcher, repeated Close, unrelated-connection isolation and opt-in scope.
Core tests cover default behavior and refusal of other network/bind/lease modes;
existing retention/guard tests, Go vet and script parsing pass.

This restores **local socket usability**, not preservation of server-side UDP
session identity. The AWG process/netstack and relay were recreated, so the
remote source port can change. Stateful games, calls, QUIC, continuous loss and
multiple simultaneous UDP applications still require their own acceptance.
Direct TCP was measured, not direct UDP continuity. There is no replay of an
in-flight lost datagram. The earlier intermittent UDP warmup failures are not
proven to share this cause; this comparison reproduces and fixes specifically
the stale association after a confirmed child crash.

Next: compose process restart and in-process socket repair under one owned
budget/status contract in an isolated agent backend, then test profile handoff,
simultaneous Stop/repair and application workloads before production selection.

The subsequent [coordinated backend checkpoint](COORDINATED-AGENT-CHECKPOINT.md)
accepts the shared owner/budget and agent-candidate boundary. Profile handoff
and production selection remain separate gates.
