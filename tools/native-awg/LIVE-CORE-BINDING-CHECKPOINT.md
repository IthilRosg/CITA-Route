# Live core/TUN native-outbound rebinding — W11 checkpoint

Accepted in isolated core `1.14.3-cita-native-lab.5` and the shared agent
candidate. This is a live core binding gate, following the earlier backend-only
profile gate. The installed client/service, host VPNUS and WireSock default
remain unchanged. There is no production UI selection or release here.

## Data-plane boundary

The explicit laboratory outbound type `cita_native_lab` starts **Closed**. It
never chooses direct WAN as a fallback. A single instance owns the mutable
native endpoint, profile generation and its TCP/UDP resources. Direct outbounds,
the core process and the TUN are separate and stay running during rebinding.

The type requires `CITA_NATIVE_BINDING_LAB=1`, the scoped UDP-lifetime opt-in,
a valid controller identity and inherited pipe stdin. Multiple instances in
one core process are refused. Its configuration contains controller identity
only, not native proxy credentials or VPN keys. Mutable endpoint credentials
arrive on the private inherited command pipe; acknowledgements contain none.

The bounded command protocol implements:

- **Quarantine:** require a newer generation and fresh session; remove the
  active binding, cancel its in-flight dials, close its tracked native TCP/UDP
  resources and revoke its TCP retention lease before acknowledgement.
- **Activate:** require the current quarantined generation/session and a new
  credential; create a SOCKS5 client to literal loopback only and publish it.
  It cannot resurrect a Closed generation or accept an old activation.
- **Close:** revoke the current binding without stopping core/TUN. Old-generation
  Close cannot affect the replacement. EOF/control failure closes the native
  output while the live core still routes protected requests to the closed gate.
- **Snapshot:** identity-fenced state with current generation; rejected commands
  disclose no credentials and do not change the active binding.

Each captured binding has its own cancellation and resource registry. A dial
finishing after revocation is closed before it can be returned. Packet lifetime
wrappers still observe their own SOCKS control EOF, allowing the OS UDP socket
to create a new internal association after replacement. No application stream
or datagram is replayed, and no global ResetNetwork/CloseAll is requested.

The core command stream is a finite laboratory lease: 64 commands/128 KiB.
It is not a long-running production controller protocol. The source patch
applies against exact pinned anchors and its build receipt records source,
patch and binary hashes.

## Agent transaction

`agent/NativeCoreBinding.cs` is linked into the candidate runner and compiled
into the regular agent project. `NativeAwgProfiles` optionally uses its shared
binding interface; the default unbound laboratory path remains available.

After structural preflight, a switch quarantines core native flows, then fully
retires the old transport. The replacement is started privately and must pass
certificate-validated fixed IPv4 HTTPS with expected exit and UDP queries
before core activation. Only then is it published as the active backend.
Structural preflight alone is not engine/server validation.

Failed preparation after quarantine, failed validation or cancelled activation
closes the native gate and disposes the candidate. The old profile is not
silently restored. Unknown binding/transport retirement latches failure and
blocks further replacement. Trusted Stop closes the current core binding as
well as cancelling/retiring the backend; it does not need to stop the TUN.

The core client serializes requests. Once a request is written, its bounded
acknowledgement is consumed independently of initiating-operation cancellation
so later replies cannot be misattributed. Expected command refusal is distinct
from a malformed/oversized reply or broken control stream; protocol failure
closes the inherited input lease, leaving the core's native output closed.
Component image/config authorization is still the caller's responsibility in
this isolated adapter; this is not a new privileged production IPC surface.

## Final W11 evidence, 24 October 2026

Summary: `runtime/native-awg-core-binding-2a325d03347648c9989e18905b37e603.json`.
Raw: `runtime/vm-lab/w11/native-core-binding-2a325d03347648c9989e18905b37e603.json`.
Final native/core/agent-candidate hashes match the local artifacts. The initial
smaller matrix also passed and is retained as the `f3111425…` series.

One live gvisor TUN routes only `1.1.1.1/32` and the configured public DNS /32.
A separate explicit direct inbound binds its outbound to the physical VM
interface, so the protected test address cannot loop through the TUN on that
direct control. TLS certificate validation remains enabled; no system DNS,
host VPNUS or installed WireSock profile is changed.

| Event | Observed result |
|---|---|
| Initially Closed | Fresh OS HTTPS and UDP probes cannot succeed through the protected routes |
| Activate A | Ordinary OS TUN HTTPS/UDP pass |
| Quarantine for B | Native TCP=1 and UDP=2 resources closed; new protected HTTPS/UDP probes fail while the old backend still exists |
| Activate B | Core PID retained; ordinary OS TUN traffic passes; old held TCP requires a new dial, same OS UDP socket resumes |
| Late Activate A | Rejected with current generation B still Active; refusal-as-error also preserves the control stream; fresh B traffic passes |
| Unusable candidate C | Its owned AWG child is deliberately exhausted before validation; candidate rejected, output Closed, no automatic rollback to B or direct fallback |
| Stop during D validation | Core is still Quarantined; Stop cancels and disposes the unpublished candidate; delayed Activate D is refused and output stays Closed |

A direct TCP/TLS stream remains held across the **whole** matrix, with an
intermediate request after B and final request after the C failure/D cancel.
The final request reuses the original TCP connection (dials 1→1). The core
process remains the same and the scoped TUN route is present at the end.
Rebinding code never tears down/recreates the TUN; this is not a measurement
of every adapter event under arbitrary hardware/network changes.

The final stop/cancel path completed without forced core cleanup. All owned
processes stopped; temporary core configuration/PID files were removed; the
original guest route/adapter topology was restored. An independent identity-
checked rescue helper is scoped to this guest/core only and is cancelled on
normal cleanup. The positive tests and failure case use one approved VM peer
sequentially; they are not evidence of two real servers or a real server outage.

Go tests/vet cover generation fencing, Closed-generation resurrection refusal,
targeted TCP/UDP retirement, late-dial registration and unrelated connection
isolation. Agent candidate checks, shared agent compilation and script parsing
pass. The agent project retains its existing CA2022 warning in UdpQualityTests;
there are no build errors. No live core crash/leak acceptance is inferred from
these checks.

## Remaining scope before normal-client selection

This is scoped IPv4 HTTPS/UDP evidence, not universal NIC/IPv6 leak proof. Direct
UDP and stateful game/call/QUIC continuity are not established; server-side UDP
tuple identity can change. Earlier intermittent startup/UDP preparation failures
remain unresolved and are not explained by this successful matrix. Production
still needs persistent controller/lease policy, trusted component authorization,
OS-enforced whole-core/hung-child safety, sleep/interface transitions, workload
capacity and two genuinely different server profiles. Explicit full disconnect
and update cleanup must separately define when protected routes are intentionally
removed; keeping a live core gate closed during handoff is not that UI policy.
