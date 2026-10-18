# CITA Route 0.5.0: candidate validation

2026-10-19, Windows PowerShell 5.1, Windows x64. This is a candidate on
`codex/component-recovery`, not a new stable release. No working VPN was
disconnected and no candidate service was installed over the active session.

## Implemented and checked locally

- Agent build/self-tests: detailed VPN-health outcomes, exact managed-profile
  identity including negative/transitional statuses, pre/post profile validation,
  bounded probes and existing adaptive-policy/concurrency/generation scenarios.
- Component recovery fixtures, including the actual Watch loop: stale generation,
  coordinator contention, retry exhaustion, Stop priority, cancelled health,
  EgressMismatch hold, unrelated HTTPS failure and an unbypassable held bridge.
- Start/Stop/profile replacement use exact identity. Tests reject similarly
  named and conflicting profiles and cancel the owned in-progress handshake.
- Actual sing-box loopback: one established direct TCP connection survived a
  bridge pause/resume; old VPN socket closed, new VPN sockets were refused while
  paused, and new VPN traffic succeeded after resuming. WireSock and the global
  lease were mocked. This is not a live interception or UDP/QUIC result.
- Windows PowerShell parent/child operations: setup then Start under one
  transaction, script exit 0, exact failure propagation, Cyrillic/quotes/empty
  values, cooperative cancellation with completed cleanup and deadline-triggered
  cancellation. An unfinished operation blocks another Start and the updater.
- WPF controls: physical compact/advanced sizes, retained advanced size/page,
  unchanged routing mode/profile/connection across a view switch, cancellation
  button states, duplicate-start prevention and rejection of queued old progress.
- Offline regression: portable defaults, release install/rollback and GUI
  handoff, active/foreign-session refusal, Auto/Lists preparation, atomic storage,
  profile lifecycle/rollback, feed publication and protected-agent package rollback.
- Existing optimization integration passed. Preview bitmaps cover five compact
  states and all advanced pages. Preview mode avoids live backend polling and
  preference writes.

## Resources

Local `--benchmark-ui`, 2026-10-19 01:38–01:39 MSK: 200 synthetic connection rows,
one update per second, 30 seconds per view after warm-up, 16 logical processors.

| View | CPU, entire machine | Private memory at end | Peak working set |
|---|---:|---:|---:|
| Compact | 0.241% | 102.1 MiB | 150.1 MiB |
| Advanced | 0.394% | 141.3 MiB | 177.5 MiB |

This measures WPF presentation only, excluding live backend polling, probes,
PowerShell, WireSock and routing engines. Views ran sequentially; shared caches,
warm-up and GC affect the comparison. Private memory is committed memory, not
physical RAM. A short run does not prove the absence of leaks. Raw local data:
`logs/ui-resource-fixture.json`, excluded from Git and distributions.

The installed protected agent returned `healthy=true` and RoutePilotRescue was
Running at 01:50:57 MSK. That confirms the unchanged existing session only; it
does not validate candidate installation or the new live recovery code.

## Remaining release gates and limitations

- Loss fields intentionally say **not measured**. Stage 6's controlled UDP
  reflector/collector is not implemented. There are no fabricated 0% results or
  packet-loss estimates derived from HTTPS.
- Validate real WireSock installation, native UAC approval/refusal, driver
  interception, failure recovery and simultaneous app traffic on a separate PC.
  The local chat connection is not a fault-injection testbed.
- Validate 125/150/200% DPI, mixed-DPI monitors, sleep/resume, Wi-Fi changes,
  games/voice/streaming and UDP/QUIC. Bitmap previews are not those tests.
- A WireSock profile changed under the same name, especially its server DNS/IP,
  can require **Apply connection** in advanced settings to renew protected setup.
- VPN-only enforcement between status observations is still a separate open
  stage. Existing VPN sockets can be lost when the bridge pauses; arbitrary TCP
  sessions are not migrated between different public exit IPs.
- The compact view skips hidden charts/grids and reuses fresh watcher status and
  session-health data. Total live CPU/RAM and long-duration leak checks remain
  separate from the synthetic UI measurement.

Keep the PR in draft and the stable update channel at 0.4.0 until the remaining
release gates are met. A clean ZIP is suitable for isolated candidate testing.
