# Windows VM validation, 2026-10-20

Stand: Windows 10 Enterprise LTSB 2016, build 14393, VMware, WireSock Secure
Connect 3.6.1.1. The stand has its own test peer; the host's live VPN and installed
agent were not changed. Private configurations, addresses and credentials are
excluded from this report and release assets.

## Accepted observations

- Actual installation, repeat installation, uninstall and installation after
  uninstall preserve user settings/rules and the source profile. The retained-data
  owner marker and protected-owner fallback fix the observed reinstall refusal.
- A real guest restart starts the protected service in session 0 before any
  interactive sign-in. The Russian CLI status `Нет подключения` now passes
  dependency readiness; the service attempts connection and safely rolls back
  when the original UDP path fails. A subsequent real restart through the
  additional server port proves current healthy VPN before sign-in: changed
  kernel boot, SYSTEM/session 0, zero interactive users at start and connection,
  matching session/generation and fresh protected HTTPS. These are WTS
  point-in-time observations, not continuous logon attestation.
- The enabled test peer exists in the live AWG interface. Its public identity,
  server key, allowed address and protocol settings match the fresh panel export.
  On the original UDP path the server receives initiation packets and sends
  responses; the guest's filtered NDIS trace observes outgoing requests but no
  incoming response. This does not identify the ISP, bridge/filter or other
  component responsible for the loss.
- A disposable, peer-checked relay on an already allowed, unused UDP port
  establishes standalone WireSock in 1.373 seconds and passes two HTTPS requests.
  No extra response padding is necessary on that temporary path. This does not
  prove DPI as the cause on the original path or create a permanent solution.
- The exact protected VPN bridge passes certificate-validated HTTPS by IP and
  hostname, with its external address matching the server. Testing the unused
  client-folder bridge is excluded: WireSock's application rules differ by path.
- One complete CITA Route connection/HTTPS/Stop cycle takes 11.737 seconds to
  connect; a separate repeat cycle takes 12.322 seconds. Both confirm the current
  session/generation with the protected agent and stop successfully. Each uses a
  fresh temporary relay. A failed second cycle within one relay is excluded from
  product conclusions: the disposable relay intentionally pins the first source
  endpoint and rejects the new UDP source port after reconnecting.
- With explicit authorization, the server now has a separate owned nftables NAT
  table and enabled systemd unit for the already allowed alternative UDP port.
  Existing AWG interfaces, keys, peers, original port and firewall tables were
  preserved. The temporary rollback timer was armed before the live rule and
  cancelled only after complete standalone WireSock/HTTPS cycles passed before
  and after persistent installation. Server reboot persistence is configured,
  not tested by rebooting the production server. This is operator configuration,
  not client-side port scanning or proof that DPI caused the original failure.
- Two consecutive complete CITA Route cycles through that native alternative
  take 12.443 and 12.145 seconds, independently verify the selected exit and stop
  correctly. One native-port test transiently failed a public HTTPS request;
  the subsequent three complete standalone cycles all passed. Do not count that
  failed request as a successful sample or hide it in the median.

## Fixes discovered during testing

The operation child can exit while background Watch/policy processes retain a
redirected pipe. An unbounded `Process.WaitForExit()` can then hold the operation
open although routing is already healthy. Wait for the primary process with the
finite-time overload instead. A Windows PowerShell regression fixture requires
the operation to finish while its owned background worker remains alive and
cleans that worker by PID plus creation time. Existing cancellation, error,
handoff and maintenance-lock tests continue to pass.

Initial protected HTTPS can fail transiently during interface setup. Retry once
after 500 ms only for control HTTPS, unavailable status or missing reported exit;
recheck session/generation and Stop priority. Do not retry an exit mismatch into
success, relax identity checks or allow direct fallback. Retain a bounded reason
code, attempts and generation in `logs/connection-health-last.json` after cleanup,
and show distinct missing-exit/profile/status failures to the user.

A repeated connection exposed an initial policy canary timeout: the worker
previously exited after the first four-second engine acknowledgement deadline.
Retry actual publication within one 25-second startup budget, preserving Stop,
generation and proof checks. The loopback integration starts the real engine
after the first canary deadline and requires a later actual acknowledgement;
30 subsequent route transitions preserve the existing VPN TCP flow.

Boot commands use primary-process completion without capturing inherited
background pipes. A Windows PowerShell fixture retains a live owned child while
requiring primary completion, exact exit status and cancellation. Background
processes are cleaned by identity, not by a global process name.

One real boot after delayed DHCP still exhausted initial policy confirmation;
a repeat real boot connected successfully. Record bounded canary stage/reason
and session identity. Allow at most one whole startup retry only for a fresh,
matching transient policy failure after the session is fully stopped. Wrong
exit, unknown/permission errors, stale/foreign records and active sessions do not
authorize that retry. A disposable SYSTEM-service fixture covers exactly two
attempts followed by health confirmation; independent failed health still rolls
back one attempt. This mechanism is separate from the first successful real
boot evidence and is not claimed as the proven cause of that earlier timeout.

## Limits and remaining acceptance

The original reverse UDP path remains unresolved; the additional native server
port provides a verified alternative. Healthy startup before sign-in is observed
on the actual stand. Long mixed traffic, Wi-Fi changes, sleep/resume, repeated
real boot stability and native driver isolation remain separate acceptance
items. The alternative port is not a permanent DPI mechanism.

## Live control and resources

A 601-second startup-session run completed 38 protected health and independent
VPN HTTPS/exit checks without failure, session/generation changes or stale
watch/policy observations. Native WireSock telemetry ended at 56 ms handshake
RTT and 0% estimated loss; that estimate is not an independently measured WAN
packet-loss rate. Intentional Stop completed successfully.

On the two-logical-CPU guest, the sampled routing processes plus WireSock and
occasional diagnostic child processes averaged 1.92% machine CPU and peaked at
304.18 MiB private memory, without the GUI. The long-lived policy worker changed
54.41 -> 54.53 MiB, router 79.60 -> 77.97 MiB and bridge 57.44 -> 56.98 MiB.
The rescue service changed 37.02 -> 47.77 MiB and WireSock's service 28.23 ->
38.59 MiB. These short, controlled HTTPS measurements are not gaming/video load,
a complete application footprint or proof against long-term leaks.

## Pause/resume boundary

A single certificate-validated TLS/TCP connection survived a minute of normal
requests without reconnecting. During a separate controlled 12-second VM pause
(about 16 seconds including VMware resume), the existing connection was aborted
immediately on resume. The CITA session ID survived, while the bridge generation
advanced and protected HTTPS recovered later. Existing VPN TCP continuity across
this pause is therefore **not passed**.

The recovery history exposed rejected full-reconnect attempts while WireSock was
still Transitioning; they unnecessarily consumed its retry budget. Observe an
unconfirmed/transitioning/foreign state until ownership is confirmed, including
when the router is missing or network mismatch is repeated. Independent bridge
pausing continues to prevent unconfirmed direct egress. Confirmed managed
disconnection, router failure and changed networks retain their repair paths.
The regression matrix covers these combined cases. This avoids invalid retry
attempts; it does not promise to revive a socket already closed by the stack.

The VM's own idle sleep interrupted tests. Resetting its sleep idle timer during
diagnostic invocations avoids changing the permanent power plan. Guest checksum
offload changes did not restore the original path and were reverted and verified.

The lightweight .NET distribution is only the measured design documented in
[LIGHTWEIGHT-DISTRIBUTION.md](LIGHTWEIGHT-DISTRIBUTION.md); no thin installer or
runtime bootstrap is published by these tests.
