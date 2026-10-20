# Component liveness and isolated bridge pause (0.5.5)

The policy controller now publishes progress at real execution checkpoints in
`runtime/policy-heartbeat.json`. It includes instance/session identity, PID,
creation time, sequence and a bounded phase deadline. There is no background
timer that keeps a blocked worker looking healthy. Startup/publication have a
45-second budget, ordinary loop progress 15 seconds and terminal cleanup 10.
The watcher requires two stale observations and rechecks progress immediately
before stopping a worker. Missing workers can be started; unverifiable metadata
requires attention instead of arbitrary termination. Older protected agents
retain the previous identity-only check during a package rollback.

Policy repair holds the existing coordinator and network-control lock. It keeps
the router, session, applied rules and active direct flows. Retry budgets remain
bounded and per session. Stop/new-session checks run before replacement. A long
network operation is reported as needing attention after 90 seconds; its bridge
safety checks continue. The client does not blindly kill a network-command tree:
an already submitted WireSock request may outlive its CLI process.

## Independent pause

An uncertain/lost/foreign VPN or a fresh exit mismatch invokes a protected helper
that can stop only the recorded VPN bridge, without waiting for the ordinary
coordinator. It checks the configured client root, session and bridge generation,
exact protected bridge image, same user and exact native process creation time.
Validation and termination use the same Windows process handle, preventing a
reused PID from becoming a different victim. Service/diagnostic ownership blocks
this helper during an independent diagnostic run. The direct router is never a
permitted target. The emergency helper rechecks authenticated native WireSock
status before terminating; normal watcher checks remain conservative. A verified
exit mismatch still requires a hold even when tunnel status says connected.

Confirmed exit mismatches persist in a separate `bridge-safety.json` scoped to
the session. The helper never overwrites `session.json` outside its owner lock.
The watcher, bridge resume path and GUI honor the independent hold, including
after the original health result expires. Corrupt/unreadable safety state cannot
prove a safe exit. A deliberate new connection retires the previous hold. An old
session's late record cannot hold a different session ID. Confirmed bridge stops
are deduplicated to avoid repeatedly launching helpers while the VPN is down.
Failed hold persistence does not prevent bridge termination. The watcher keeps an
in-memory hold for that session and reports attention; persistence across another
watcher crash cannot be promised while storage is unavailable.

## Limits

This is stronger component recovery and observation-triggered bridge shutdown.
It is **not per-packet, driver-enforced VPN-only isolation**. Detection still
depends on watcher scheduling and status sampling; a disconnect or profile change
between observations can precede the pause. No claim of zero direct leakage or
seamless TCP migration across external IP changes is made. The stock WireSock
Kill Switch is not enabled: its documented system-wide blocking conflicts with
preserving direct connections. A selective enforced transport boundary still
needs a separate prototype and tests on real WireSock, IPv4/IPv6 and TCP/UDP.

Protected helpers are installed on the next Connect through the usual Windows
confirmation. The current working session is not replaced by development tests.
Published metadata contains no profiles, private keys, payloads or destinations.

## Validation

- Pure tests: progress identity/deadlines, malformed/unbounded data, startup grace,
  session changes, reused PIDs, clock rollback and stale-worker classification.
- Windows PowerShell: actual Watch loop with injected process/network operations,
  stale/unknown controller cases, coordinator rejection, a still-running repair,
  Stop/generation priority, fresh-progress recheck and deduplicated pauses.
- Real sing-box over loopback: the same direct TCP socket survives bridge pause,
  resume and native stale-worker termination; established VPN sockets close and
  new VPN connections fail while paused. An exclusively held coordinator file
  does not prevent bridge shutdown. Native image/creation-time mismatch cannot
  terminate the direct router, including a one-tick timestamp mismatch.
- GUI tests reject cached HTTPS success during an independent hold and show
  accurate attention reasons. Update/package/installer regression remains required.
- Runtime helpers stay in the existing updater payload (`WireSockRecovery.ps1`)
  rather than requiring a newly named script. Both current update tests and the
  shipping 0.5.4 installer snapshot (commit f880df7, tests/fixtures) run in CI,
  including rollback and GUI handoff. The snapshot contains no user configuration.

References: [WireSock network settings](https://wiresock.net/documentation/wiresock-secure-connect/network-settings.html),
[TerminateProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess),
[GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes).
