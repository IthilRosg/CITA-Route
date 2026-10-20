# Real Windows startup acceptance

Use a separate Windows machine or VM with a working, user-provided WireSock
profile. Do not reboot or change the active development PC's VPN. The public CI
service fixture substitutes transport and changes boot epochs in state; neither
operation constitutes a real boot or real WireSock data path.

Prepare a disconnected CITA installation, apply and validate the profile, then
enable connection before sign-in in Settings. Retain console access to the
stand. Keep WireSock's own competing auto-connect disabled on that stand so
ownership is unambiguous. Do not copy the user's VPN profile into this repo.

## Evidence

Before each real restart run, as the stand's authorized Windows user:

```powershell
./tools/Collect-StartupEvidence.ps1 -Action Arm -ClientRoot 'C:\chosen-folder\client'
```

Perform the restart manually. Leave Windows signed out until the route is ready,
then sign in and collect while health is fresh:

```powershell
./tools/Collect-StartupEvidence.ps1 -ClientRoot 'C:\chosen-folder\client'
```

The collector changes only its local diagnostic files under the client's logs.
It does not start/stop routing, change startup, or reboot Windows. The report
contains counts, timestamps and generation checks. No keys, tokens, profile
names, CLI output or Windows user names are exported.

`rebootBeforeSignInObserved=true` requires a changed real kernel boot, a startup
attempt after the baseline, SYSTEM session 0, zero interactive users observed at
both endpoints, and matching current, healthy session/generation. WTS counts are
point-in-time observations; this is diagnostic evidence, not an attestation of
continuous absence of logons. Unknown WTS information cannot count as zero.

## Matrix

| Case | Required observation |
| --- | --- |
| Restart with network ready | Real kernel boot changes; current route and HTTPS are ready before signing in. |
| Restart without network; network appears after >3 minutes | Waiting heartbeat advances, no attempted transport before readiness; then exactly one successful startup. |
| Cancel waiting, then restore network | StoppedForBoot persists; no transport disconnect was issued merely for cancelling readiness. |
| Manual Stop, restart only the service | No automatic reconnection during the same Windows boot. |
| Change profile while disconnected | Protected config and snapshot select the new profile; source profile stays intact; current-boot manual Stop persists. |
| Update client, approve Windows refresh | Current protected version/snapshot; no connection during refresh; one next-boot connection. |
| Decline Windows refresh | Pending state remains visible; no repeated automatic prompts for that version; explicit Connect/Apply retries. |
| Failed snapshot preparation / service activation | Previous protected package, snapshot and shared state restored together before old service starts. |
| Hybrid shutdown / Fast Startup | Collect separate Windows boot/resume evidence and a new startup attempt. An unchanged kernel boot is deliberately not reported as a proven cold reboot. |
| Foreign WireSock profile or unfinished diagnostics | No silent takeover; explain conflict or wait for recovery; preserve the unrelated connection. |

Do not mark the real WireSock, reboot, hybrid shutdown, Wi-Fi authentication or
WAN data-path cases passed from a synthetic CI result. Save only the redacted
report and observations of the stand. Real driver/adapter/filter acceptance
remains separately tracked by the transport research in draft PR #9.

## Mixed traffic fixture

Dispatch the existing Windows build workflow with `soak_minutes=10` (allowed
1–15). It first waits 185 seconds for a synthetic WireSock dependency, then runs
20 recovery cycles with a persistent direct TCP flow, bulk TCP, controlled-loss
authenticated UDP, restored VPN TCP/UDP and refusal of new VPN traffic during
bridge outages. It publishes `reliability-evidence` with traffic/resource totals.

These use real sing-box processes on loopback and mocked WireSock observations.
They are not real games/calls/video services, WAN latency measurements, proof of
seamless revival of a broken VPN TCP socket, or proof of per-packet driver
isolation. Hardware and real applications remain part of the stand matrix.
