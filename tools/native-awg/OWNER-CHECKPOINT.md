# Isolated process-owner checkpoint

`--supervise-proxy-lab` is a separate Windows laboratory owner process for the
native AWG child. It is not registered as a service, exposed by the installed
agent, selected in the UI or enabled by default. WireSock remains unchanged.

## Lifetime and integration boundary

The owner launches only its own executable, in explicit UDP-proxy child mode.
Config/token stay in memory and on redirected stdin, never in arguments or
reports. The child readiness envelope must match session and generation and a
valid loopback port. Upstream stderr is discarded rather than logged with
possible configuration values. Child launches have a six-second deadline and
hidden windows.

The core connects to a stable loopback TCP listener owned by the supervisor;
credentials are still checked by the child SOCKS server. The supervisor
forwards bytes only to that child, never to a public address. UDP ASSOCIATE
still returns the child's ephemeral UDP relay. Existing authenticated UDP
control connections therefore belong to that child, not to a permanent relay.

On child exit, the owner unpublishes the endpoint and closes every existing
relay. New requests are refused during restart, not queued. A relay cannot
switch to a new child, retry its stream or replay buffered application data.
Publication checks owner cancellation, and each dial is fenced against the
exact child instance it captured. No TUN, core or Windows route is restarted.

A child incarnation (`epoch`) is separate from logical session/generation.
Restarting the same configured transport increments epoch, not profile
generation. A profile change must end the entire old owner lease and create a
new owner with new identity/credentials; this checkpoint does not implement
that production handoff. Status/readiness means `RunningUnverified`, never
proof that HTTPS works. The acceptance client verifies traffic independently.

There are at most two restarts per owner lifetime, with 750 ms backoff. Once
exhausted, the stable listener refuses new flows and reports `Exhausted`; the
budget does not replenish. Commands are bounded and require the current
session/generation on the private stdin channel. A stale identity or malformed
command fails closed. `Stop` cancels restart, closes the listener/relays and
child, joins workers, and only then acknowledges `Stopped` with no child PID.
EOF performs the same cleanup. Child EOF also terminates it after owner death.

The child here runs the earlier UDP proxy, **not** the socket-health worker.
The two recovery gates are deliberately measured separately. Combining them
requires a shared logical-owner budget and coordinated health/status handling
in the future agent candidate; nested budgets must not silently reset on crash.

## W11 acceptance

`Run-W11Owner.ps1` runs three short cases on pinned native/core hashes:

1. Through the real /32 Windows TUN, establish OS TLS/TCP and UDP, kill the
   owned AWG child, wait for a different child/epoch behind the same core
   endpoint, and verify fresh OS HTTPS and UDP with expected exit. Kill two
   more children and verify only two restarts, no published child and stable
   exhaustion. Existing application streams are expected to break on crash.
2. Observe `Restarting` in the backoff window, send Stop before epoch 2, and
   verify no resurrection, acknowledged complete shutdown, and failed OS
   HTTPS/UDP while core/TUN remain. Cleanup checks original topology and no
   remaining owned processes or credential file.
3. Without creating a TUN, kill the owner and verify the child exits through
   stdin EOF. Separately send wrong generation and wrong session commands;
   each owner rejects them and closes its child. Cleanup checks process
   identity (PID plus creation time) before any forced orphan cleanup; forced
   cleanup would fail acceptance, not mask a surviving child.

The first candidate passed the crash/budget and stop-race cases; a continuation
on identical hashes passed owner-death and stale-identity checks. Review then
found Stop acknowledged intent before child shutdown had joined. This was
corrected before final acceptance; the final test explicitly observes backoff
and requires the Stop acknowledgement to contain epoch 1 and child PID zero.
Earlier candidate receipts are retained. Final summary:
`runtime/native-awg-owner-168a1efcb9a044e8a470504d9f5a6c6e.json`.
All three cases passed on matching final binary hashes. Child readiness after
the first crash took 1,026 ms (not a traffic-ready guarantee); a fresh retained
HTTPS request took 56 ms after its independent warmup, with six UDP responses.
The stop receipt shows `Restarting` at epoch 1/restart 1 before the command,
then `Stopped` at epoch 1/restart 1/child PID zero. Both TUN cases passed the
post-stop OS HTTPS/UDP refusal checks, stopped owned processes, removed the
temporary credential file and restored original topology. The lease case
passed owner-death, wrong-session and wrong-generation checks with no forced
orphan cleanup. Timings are individual laboratory samples.

The original held TCP client redialed after child crash; it is not counted as
retention. The original OS UDP socket timed out after crash in the first
candidate, while a fresh OS UDP socket passed all six queries. This is a known
limit, not a seamless-recovery claim: stale core UDP associations need targeted
epoch invalidation before stateful UDP workloads can be accepted. The ordinary
diagnostic HTTP library can retry an idempotent GET; the owner never replays it.

Native Go tests/vet include restart-budget bounds, independent epoch/profile
generation and cancellation-before-publication. Guest scripts are parsed and
all VM acceptance results include cleanup. No new packet capture is made;
the earlier protected-destination/source-IPv4 limits continue to apply. A fresh
request failing while AWG is stopped proves only those scoped probes, not
whole-machine leak safety after a core/TUN crash.

## Next integration work

Consume this lifecycle from an isolated agent backend, compose socket repair
and process restart under one budget, and add targeted invalidation of the
old VPN UDP associations without resetting the TUN or unrelated direct flows.
Then accept real profile replacement, simultaneous Stop/repair/status races,
whole-core crash, hung child (including OS-enforced child lifetime), and
application UDP/QUIC/call workloads. Owner-crash EOF with a responsive child
does not establish forced teardown of a hung child. The intermittent UDP
warmup/control failures documented in `RECOVERY-CHECKPOINT.md` remain separate
unresolved observations; a process-restart success does not fix them.
