# Backend profile handoff and stale-operation fencing

Accepted for the isolated shared agent backend candidate. This checkpoint
does **not** hot-switch a running core/TUN to a replacement outbound. It tests
transport ownership, publication and cancellation through real native AWG
processes and authenticated SOCKS traffic in the W11 guest. Installed CITA,
WireSock, host VPNUS and Windows routes remain unchanged.

## Coordinator behavior

`agent/NativeAwgProfiles.cs` coordinates the existing `NativeAwgBackend`:

1. Require strictly increasing generation and fresh session/transport credential.
   Keep the generation high-water mark even if preparation is rejected. Only
   credential fingerprints are retained for reuse checks; no secret is logged.
2. Structurally validate the new native configuration with the offline
   `--validate-profile-lab` helper before retiring an active transport. The
   helper does not create an AWG device, listener or socket and returns no config.
   It checks the native parser, not engine negotiation or server reachability.
3. Stop the old backend with its exact identity, require the completed shutdown
   acknowledgement, dispose it and wait for process exit before new startup.
   Unknown retirement/cleanup latches `RetirementFailed` and blocks replacement
   rather than silently publishing another transport.
4. Start an unpublished candidate, then recheck the latest intent and cancellation
   before publishing it. Cancellation during startup disposes that candidate.
   Candidate cleanup failure also preserves the reference and blocks publication.
5. Accept commands only for the active identity while its intent is current.
   Recheck intent after an awaited command reply; old replies are discarded even
   when they began before switching. Old Stop cannot cancel the newer intent.
6. Stop for the newest desired identity cancels pending preparation/startup,
   waits for cleanup and returns after there is no published backend. A newer
   explicit connection intent supersedes an older queued Stop. Disposal retires
   the coordinator and cancels its outstanding operation.

The async operation gate serializes transport mutation; a separate intent lock
lets Stop/new intent invalidate work waiting on I/O. Backend process identity
sampling tolerates disposal racing a status read. No old socket is transferred
to a new profile. This is break-before-make; uninterrupted VPN connections during
profile switching are not promised. The returned readiness still means
`ListeningUnverified`; transport publication is not an internet-health claim.

The same coordinator source is compiled by the normal agent project and linked
into the laboratory runner. It is not wired into production AgentHost/UI.
Internal lab barriers make tests deterministically stop after old retirement
before startup, and after startup before publication. They are disabled by
default; the tests do not depend on winning a timing race with a short sleep.

## Final W11 evidence, 24 October 2026

Accepted summary:
`runtime/native-awg-profiles-c4f0f417dec84e19b4d3a940d47b1c7b.json`.
Raw receipt:
`runtime/vm-lab/w11/native-profiles-c4f0f417dec84e19b4d3a940d47b1c7b.json`.
Native and candidate-agent DLL hashes match the final local artifacts. Three
cases pass, with no residual live processes and unchanged guest topology.

- **Handoff/fencing:** working profile A holds TCP and a UDP association. An
  invalid replacement fails preflight without replacing A's process. For valid
  B, both old owner/child processes are gone before B starts. Old TCP cannot
  resume and old UDP control lifetime is closed. Late status reply, old command,
  old Stop, reused session and reused transport credential are rejected. Old
  credentials cannot authenticate at B's endpoint. B passes certificate-validated
  HTTPS with expected exit and UDP queries; its recovery budget starts at zero.
- **Stop before start:** A is retired; replacement is held at the pre-start
  barrier. Stale Stop for A is rejected and does not cancel B. Stop for B cancels
  the pending intent, with no candidate publication or remaining live process.
- **Stop before publication:** B's process has started but is not published.
  Stop cancels the intent and disposes the unpublished candidate. Final state is
  Stopped, published PID zero and no live native processes.

Profiles A/B use **one approved VM peer sequentially**, with different control
sessions, generations and credentials. This establishes lifecycle fencing, not
two-server/key-diversity interoperability. Tests use the backend SOCKS endpoint,
not a live OS TUN/profile-route switch. No successful SOCKS probe can fall back
to direct WAN through the helper; this is not a universal OS leak proof.

HTTPS lifecycle probes use fixed `https://1.1.1.1/cdn-cgi/trace`, matching earlier
TUN gates, with normal certificate validation. A separate hostname probe to
`www.cloudflare.com` is recorded independently; it returned HTTP 200 in the
final run. UDP queries still use the configured public resolver. Final initial
and replacement UDP baselines both passed on the first attempt; **no fixture
retry was used** in the accepted run.

## Debugging history and verification limits

Earlier raw receipts remain preserved rather than overwritten:

- An early handoff passed but a later cancellation check returned an IOException
  without a precise saved assertion; that series is not accepted.
- The process check originally treated every enumerated process entry as alive.
  It now checks HasExited and disposes enumeration handles. The final pre-start
  race actually observed one enumerated entry with **zero live processes**;
  the pre-publication case observed zero entries. This demonstrates the false
  positive in the old measurement, without retroactively proving the exact
  cause of every earlier exception.
- Other attempts failed before switching on hostname HTTPS or UDP preparation,
  or on the combined replacement-traffic assertion. Those are failed runs, not
  evidence of a correct network path. Assertions now separate identity fencing,
  authentication, HTTPS, UDP and unexpected recovery; UDP helper failures report
  fixed cause categories instead of packet/config contents.
- Fixture setup permits one explicitly recorded short repeat of a failed HTTPS
  or UDP preparation on the unchanged process/child epoch. It does not retry
  commands, replay application data or weaken retirement/fencing checks. The
  first failed samples remain in observations. Earlier network failures remain
  unexplained; a later successful hostname request is not their root-cause fix.
- A separate races-only run passed both cancellation barriers before the final
  complete run. The final full run passed all three cases on one artifact pair.

Go dependency verification, tests/vet (including offline preflight secret-output
checks), agent candidate checks, shared agent compilation and script parsing
pass. HTTPS helpers now allow an explicit fixed-IPv4 diagnostic target and
report HTTP status/connection failure stage; default hostname behavior remains.

## Next boundary

Bind this lifecycle to generation-fenced **core outbound/lease activation**:
quarantine the new endpoint until validation, retire the old VPN core flows,
preserve direct flows and keep protected routes closed throughout failure.
This checkpoint does not rotate/rebind the running core's credentials or lease.
Also still required: two genuinely different server profiles, startup-failure
diagnosis, production component authorization, persistent recovery policy,
sleep/resume, hung-child enforcement and application workload acceptance before
enabling the optional transport in the ordinary client. No installer or release
is produced here.
