# Bounded encrypted-socket recovery — W11 laboratory checkpoint

Accepted for the explicit `--serve-proxy-recovery-lab` mode only. Normal proxy
and migration modes do not run the new health worker. This is in-process AWG
socket recovery, **not** production agent integration or process supervision.

## Policy and ownership

The existing stdin lease and session/generation-checked control channel remain
the owner. EOF cancels the worker and closes relays; a stale control command
cannot inject a fault. Health checks do not expose a management network port.
Stop joins the health worker before destroying the AWG device. No application
request is replayed by the recovery policy.
Manual `Rebind` commands are refused while automatic recovery owns socket
repair, preventing two writers from racing the same device.

Each lab cycle checks certificate-validated HTTPS at a fixed public IPv4 and
UDP DNS through the **native tunnel netstack**, not OS DNS, a host proxy or a
direct fallback. The pair runs concurrently with 1.5-second budgets. A cycle
fails only if **both** checks fail; a single failing service must not trigger
socket churn. `Healthy` here means transport reachability through at least one
probe, not that both protocols or every application work. The acceptance client
independently requires successful OS TUN HTTPS and UDP before and after repair.

There must first be a successful cycle. Startup failures remain `Unverified`;
this worker does not repair an unverified initial configuration. After that,
two consecutive failing cycles permit `listen_port=0` on the same AWG device.
The TUN, netstack, loopback listener and existing application relays remain.
Reopening the socket is `Verifying`, not proof of recovery. Only a later
successful health cycle confirms it. The lab uses one second between ordinary
cycles and two seconds after an attempt.

At most **two attempts per process owner lifetime** are allowed. Healthy cycles
do not replenish the budget; exhausted repair remains observable. This
conservative lab limit is not a production reconnect policy. A future agent
needs an owned rolling budget, network-change fencing and process supervision.

## W11 evidence, 24 October 2026

`Run-W11Recovery.ps1` uses the existing scoped /32 gvisor TUN acceptance harness,
owned-process rescue, binary hash checks, independent OS probes after transport
stop, credential-file cleanup and topology restoration.

The automatic fault blocks encrypted send/receive only while the bind still
uses its original port. There is **no timed fault expiry**. The harness sends
`Blackhole` once; it does not send `Rebind`. Reopening on a new port removes
the fault by changing the actual bind identity. Fault injection is refused
outside the explicit recovery mode. Thus this result cannot be explained by a
three-/ten-second injected loss timer expiring.

Accepted automatic result on the final instrumented build:

- one automatic attempt, followed by a successful verification cycle;
- bind opens 1→2, actual outer port changed, dropped outgoing packets observed;
- OS TCP dials 1→1, original TLS/TCP connection reused, expected exit retained;
- native application UDP associations 1→1; three DNS responses before and after
  on the same OS UDP socket;
- retained HTTPS request took 6,571 ms during failure; harness confirmation
  elapsed 8,787 ms. An earlier build measured 6,555/6,733 ms respectively.
  These are individual samples, not latency guarantees;
- fresh ordinary HTTPS and UDP requests failed after AWG stopped while the
  scoped TUN/core remained; owned processes and credential file removed,
  original topology restored.

Final raw receipt: `runtime/vm-lab/w11/native-recovery-d0bda5218d82419f8ae8c9c0544947b3-automatic.json`.

The manual rebind control on the same binaries initially failed: TCP retained,
but only one of three post-rebind UDP replies passed before the client returned
failure. The exact cause was not instrumented and is **unresolved**; it is not
called harmless. The original failed receipt is retained. One explicit
manual-only repeat on unchanged hashes passed: TCP dials 1, UDP association
retained, HTTPS 64 ms, both no-fallback checks and cleanup passed. The repeated
series references the first series; it does not overwrite the failed result.

An additional run after adding the competing-writer guard failed its first
TUN UDP warmup query, before fault injection; HTTPS had passed. This recurrent
UDP symptom also remains unresolved. The diagnostic client now reports
query/write/deadline/read-timeout/read-error/invalid-response failures without
dumping packets or credentials, and always records TCP dial counts on early
return. The runner snapshots recovery state on failure. No query retry,
timeout increase or weaker pass criterion was added. The symptom did not recur
on the final instrumented build: both automatic and manual cases passed
together, with matching native/core hashes; manual HTTPS took 59 ms.

Final accepted summary: `runtime/native-awg-recovery-d0bda5218d82419f8ae8c9c0544947b3.json`.
Prior summaries and all failed raw receipts remain alongside it. Initial
non-elevated VM starts failed before any guest test; elevated execution was
required for VMware access. They are not network failures or accepted tests.

Unit checks cover unverified startup, isolated failures, confirmed failures,
verification after repair, repair errors, non-replenishing budget, exhaustion,
owner cancellation, independent sessions and recovery-only port fault access.
They also check refusal of a manual repair competing with the automatic owner.
Native dependency verification, Go tests/vet and script parsing pass.

## Remaining gates

This does not restart a crashed AWG process, integrate a selectable backend into
the installed agent, preserve a session across profile/generation replacement,
or establish provider/public-IP migration. It adds no new NIC packet capture;
the earlier destination-scoped capture limits in `TUN-CHECKPOINT.md` still apply.
The TCP check is an idle retained connection with a request during the outage;
UDP is sampled before/after, not a continuous game/QUIC/call workload.

Next: resolve the intermittent UDP control failure with failure-stage/response
instrumentation; add external owner supervision and generation-fenced status
to an isolated agent candidate; test Stop/profile switch racing repair and
whole-core failure before enabling any production backend. Production health
cadence must use traffic signals and backoff rather than this short lab loop.
WireSock remains the production default; host VPNUS and installed CITA are
unchanged.
