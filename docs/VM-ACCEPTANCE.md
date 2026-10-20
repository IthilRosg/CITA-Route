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
  when the original UDP path fails. Successful VPN before sign-in is still **not
  proved** by this observation.
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

## Limits and remaining acceptance

These are short, isolated live-path tests through a temporary relay, not a soak
test, a production endpoint change, a permanent DPI bypass, or proof of healthy
VPN boot before sign-in. Long mixed traffic, Wi-Fi changes, sleep/resume,
successful before-sign-in connectivity and native driver isolation remain open.

The VM's own idle sleep interrupted tests. Resetting its sleep idle timer during
diagnostic invocations avoids changing the permanent power plan. Guest checksum
offload changes did not restore the original path and were reverted and verified.

The lightweight .NET distribution is only the measured design documented in
[LIGHTWEIGHT-DISTRIBUTION.md](LIGHTWEIGHT-DISTRIBUTION.md); no thin installer or
runtime bootstrap is published by these tests.
