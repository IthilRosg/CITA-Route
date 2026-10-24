# Native AWG UDP checkpoint — 24 October 2026

Accepted in the isolated W11 VM, experimental branch only. No host network,
installed CITA session, WireSock service or release package was changed.

## Transport

`--serve-proxy-udp-lab` adds SOCKS5 UDP ASSOCIATE to the authenticated native
proxy. Normal `--serve-proxy` and loss-lab launches remain TCP-only. A valid
per-process secret is required BEFORE a UDP relay is allocated. BIND remains
unsupported. Config/secret are supplied through stdin, absent from arguments
and result logs. The TCP control owns the relay and all its tunnel UDP sockets.
EOF, an unexpected control byte, parent cancellation, timeout or shutdown closes
them and waits for receive workers. There is no OS WAN UDP or DNS fallback.

Only IPv4 literal public destinations and unfragmented SOCKS packets are
accepted. Private, CGNAT/Tailscale, loopback, reserved destinations, IPv6, domain
address framing, empty/oversized payloads and invalid RSV/FRAG are refused.
Each destination uses a connected socket in the AWG netstack, accepting replies
only from that peer. Payloads are forwarded once without protocol-level replay.

The local sender is pinned to an IPv4 loopback tuple. A client declaring its
local address/port is pinned immediately. Some SOCKS libraries instead send
the remote target in ASSOCIATE: for those clients, only the first VALID packet
establishes the local source port, with no later rebinding. SOCKS UDP does not
cryptographically authenticate each datagram. This is conventional loopback
source binding, not protection against a privileged local adversary or a race
against the first valid packet from a client that omitted its source port.

Explicit native bridge bounds:

- At most 32 total TCP/UDP handlers, including authenticating clients.
- At most 8 connected destination flows per association (256 if all handlers
  are UDP); extra destinations are dropped until an old flow expires.
- Payload at most 1232 bytes; oversized incoming replies are dropped.
- Shared request/response token budget: burst 128 datagrams, refill 1024/s.
- Association read idle deadline 60 s; each flow read/write deadline 30 s from
  its last outgoing request; local reply write deadline 1 s.
- 128 malformed, wrong-source or over-budget input packets end an association.

These are conservative laboratory limits, not a production throughput claim.
Games, large QUIC downloads and other high-rate workloads need separate tuning.

## CITA core input correction

The first trial with the existing unmodified core transported UDP and respected
TCP EOF, but accepted a changed local sender and a nonzero FRAG packet at its
own inbound relay. It normalized the request before native AWG saw it. The
failed receipt `native-udp-lifecycle.json` is preserved; the native direct
association passed in that same trial.

The isolated core now has a pre-decoder UDP wrapper in its pinned SOCKS library.
It filters the source tuple and validates public IPv4 framing BEFORE the server
wrapper can replace its remembered client or strip the fragment field. It is
enabled only with the explicit per-process environment value
`CITA_NATIVE_UDP_LAB=1`, set by the guarded UDP runner. Other launches retain
their existing behavior. It rejects a nonloopback listener/control peer and
also closes a newly created UDP socket if writing the SOCKS response fails.

`Build-CoreLab.ps1` applies exact-context patches to the isolated corresponding
source, runs the new library tests and static analysis plus the previous TCP
lease tests, and builds `1.14.3-cita-native-lab.2`. Production source/build inputs
are not modified. Core and modified library retain their upstream licensing;
the engine's MIT license does not relabel the routing core.

## Acceptance

Run `Build.ps1`, `Build-CoreLab.ps1`, then `Run-W11Interim.ps1 -Suite Udp`.
Credentials/config stay in protected guest files or stdin. The summary is
`runtime/native-awg-udp-acceptance.json`; individual ignored receipts are under
`runtime/vm-lab/w11/native-udp-interim-*.json`. Native/core hashes must match
the current test binaries. All five final cases passed on the first run:

| Case | Observed result |
| --- | --- |
| Previous mixed TCP/interface-reset gate | Original TCP retained; 1 client dial, 3 native dials, 1 bind; actual core reset retained 1 entry; direct TCP reset |
| UDP lifecycle | Three validated DNS A responses on one association directly and three through the core; incorrect/no secret refused; wrong sender/FRAG got no reply; no reply after TCP close |
| Active owner EOF | Held UDP association worked before stopping bridge; no reply after owner shutdown; no successful new HTTPS fallback |
| Active bridge crash | Same criterion after killing the bridge process |
| NIC observation | Direct control observed 7 outbound TCP / 2 UDP; native chain observed 9 encrypted endpoint UDP / zero other outbound TCP or UDP in the selected source-IPv4 capture |

Each case restored topology, stopped lab processes, and removed the temporary
credential file. Capture stopped. The UDP payload test uses the approved peer's
configured public DNS server and fixed anonymous `example.com` A queries with
random transaction IDs and bounded response validation. HTTPS alongside the
UDP test verifies the independently recorded VPN exit; DNS alone is not an
exit-IP assertion. No keys, tokens or full DNS responses appear in the receipts.

Unit tests independently verify malformed/private frames never reach the
tunnel dialer, a second source cannot write to an existing flow, the ninth
destination is refused, the rate budget is bounded and TCP EOF closes all
owned sockets. The core guard tests cover its independent pre-decoder source
and FRAG boundary. Existing TCP authentication/cancellation and interface-reset
tests still pass. Network-facing VM tests complement the in-memory unit tests.

## Scope remaining

Accepted: basic public IPv4 UDP transport and association lifetime. Not yet
accepted: UDP continuity across an interface/address/NAT change, QUIC and game
sessions, media calls, production TUN capture, IPv6, SOCKS fragment reassembly,
domain-form UDP requests, throughput/CPU soak, service/profile handoff or
installation/update integration. A capture from one source IPv4/NIC is not
universal leak proof. The local core input fix is not silently installed on
users' machines. WireSock remains the released transport.

Next checkpoint: controlled address/network change and then the real TUN path,
with separate TCP and UDP acceptance and recovery limits. Application workloads
and production owner/generation handoff follow before proposing a release.
