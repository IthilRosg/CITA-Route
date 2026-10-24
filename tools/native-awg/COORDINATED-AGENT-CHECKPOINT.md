# Single recovery owner with shared agent backend — W11 checkpoint

Accepted as an **isolated agent backend candidate**. The shared implementation
is `agent/NativeAwgBackend.cs`, compiled both by the regular agent project and
by `tools/native-agent-lab/NativeAgentLab.csproj`. The latter exercises it in the
VM without replacing the installed service or enabling native routing in the UI.
This does not register a production AgentHost command or production transport.

## One authority, one budget

`--coordinated-owner-lab` composes the previous independent recovery gates. The
owner launches `--serve-owned-transport-lab`: that child has no health/recovery
worker of its own. Only its parent's private stdin control can request Rebind.
The parent serializes child requests and verifies their session/generation and
action replies. Control reads are bounded; cancellation/deadline closes the
read pipe and kills only that owned child. Unexpected errors never log config.

Health belongs to the parent. Certificate-validated fixed IPv4 HTTPS and UDP
DNS run through the captured child's authenticated loopback SOCKS endpoint,
never through a direct WAN socket or host DNS. Each pair has a 1.5-second
context. UDP handshake deadlines now respect a shorter caller context.
Either successful probe means `Reachable`, not proof that both protocols or
every application work. Both checks must fail in two consecutive cycles after
an initial success before socket repair. Ordinary cadence is one second;
post-repair delay is two seconds. This short cadence is a laboratory policy.

Both repair categories reserve from a single monotonic owner-lifetime budget:

- at most three total recovery operations;
- at most two socket repairs and two process restarts within that total;
- failed authorized attempts also consume budget; healthy probes never refund it;
- publishing a replacement child resets its health confirmation, **not** budget;
- results captured from an obsolete child are discarded before changing health
  or granting a repair;
- budget grant and child identity/cancellation checks share the owner mutex.

The child also refuses more than two Rebind commands as a defensive local cap;
it cannot initiate recovery independently. Its monitoring/control envelope is
bounded to 64 commands/16 KiB. These finite lifetime limits are lab gates, not
the final long-running production reconnect policy.

Socket repair keeps the AWG device/netstack and user streams. Process restart
closes old relays and retains the stable core endpoint; targeted UDP lifetime
cleanup lets the same application socket attach again. The server-side UDP
tuple can still change. There is no application replay or direct fallback.

## Agent boundary and Stop

`NativeAwgBackend` owns the coordinator Process, redirects private stdin/stdout,
checks start specification, matching readiness, session/generation and action
replies, and bounds each status line and operation. Credentials are absent from
arguments, status and saved logs. Its caller must provide a trusted component
image; production component validation/service authorization are not bypassed.
The laboratory runner gives this implementation the reviewed local native
image and applies a fixed 100-second lifetime.

Stop cancels the same parent context for health and restart. It joins health,
child supervision and relays before reporting `Stopped` and child PID zero.
Unpublishing a dead child clears stale health/transport observations. Final
exhaustion reports both state and health as `Exhausted`, not a previous healthy
sample. Agent disposal closes the owner stdin lease, waits for exit, and uses
bounded fallback teardown of its own process tree if necessary.

The candidate source is compiled into the agent but is not selected by its
normal service or UI. The VM runner links the exact same source; testing a
different mock backend would not establish this integration boundary.

## W11 acceptance, 24 October 2026

Summary: `runtime/native-awg-coordinated-e583f98de8fe4af8adc03a2073985f76.json`.
Both cases match native, patched core and agent-candidate DLL hashes. Each uses
ordinary OS TCP/TLS and UDP through the existing scoped /32 gvisor TUN, verifies
post-stop HTTPS/UDP refusal, stops owned processes, removes temporary credentials
and restores original topology. Guest result files are removed before each run
so an old receipt cannot stand in for a failed launch.

The combined-failure case goes through the actual shared agent implementation:

| Event | Child epoch | Socket repairs | Restarts | Total used | Observed traffic |
|---|---:|---:|---:|---:|---|
| Old outer port blackholed, automatic repair | 1 | 1 | 0 | 1 | Original TCP retained; same OS UDP socket, 3 replies before/after |
| AWG child killed, automatic restart | 2 | 1 | 1 | 2 | Old VPN TCP redials; same OS UDP socket resumes, 3 replies before/after |
| New child's old outer port blackholed | 2 | 2 | 1 | 3 | TCP retained and same OS UDP socket resumes |
| Child killed once more | 2 | 2 | 1 | 3 | Restart denied; Exhausted, no published child |

Both blackholes apply to the old encrypted socket port with **no timed expiry**;
reopening the port is what removes each fault. The two retained HTTPS requests
took 6,571 and 6,572 ms in this run. These are individual diagnostic requests,
not general downtime/latency guarantees. During the process crash the held VPN
TCP client redialed (1→2); it is not relabeled as retained.

A separate direct TCP/TLS client remains held over **the whole sequence**. An
intermediate response confirms first-repair survival; a final response after
the crash, second repair and budget exhaustion still reuses its original TCP
connection (dials 1→1, final sample 33 ms). This is stronger than launching a
new direct client after each fault. Direct UDP was not measured.

The Stop case observes `Suspect` at epoch 1 with budget/socket/restart counts
all zero, then sends Stop through the agent backend before repair is granted.
The acknowledgement reports `Stopped`, health `Stopped`, no child PID and all
counts still zero. No repair or restart takes place after the disconnect;
the held direct TCP client remains usable and no successful VPN fallback is
observed by the scoped probes.

Go unit checks cover shared budget survival across child replacement, stale
child observations, cancellation before repair, non-replenishment and child
repair-command bounds. Existing native tests, module verification and Go vet
pass. Agent identity/action checks and candidate compilation pass; the regular
agent project also compiles (one existing CA2022 warning in UdpQualityTests).
The first build invocation selected system .NET 8; using the existing private
.NET 10 SDK and a writable CLI-home resolved build tooling without installing
or changing machine runtimes. `tools/native-agent-lab/Build.ps1` records this
reproducible path. No runtime distribution is bundled with the test candidate.

## Still before production selection

Profile replacement must atomically retire the old backend/core lease before
new credentials/generation become routable; that handoff is not implemented
here. Production also needs component-image authorization, persistent/rolling
recovery policy, traffic-driven/backoff health cadence, connection-capacity
acceptance, sleep/resume and OS-enforced lifetime of a hung child. The earlier
intermittent UDP warmup observations remain unproven to share the fixed stale
association cause. This adds no universal NIC/IPv6 leak proof, arbitrary domain
acceptance, full core-crash safety or game/QUIC/call continuity. Host VPNUS,
installed CITA and WireSock's production-default role remain unchanged.
