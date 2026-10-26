# Installation and startup

## Next development version: one auto-connect setting

Settings separates **Open CITA in tray at Windows sign-in** from **Connect when
CITA Route starts**. The latter uses the selected profile once per application
process, including `--startup`. Opening a window from the tray or launching a
second copy does not reconnect. Manual Stop remains effective for that process;
after exiting and starting CITA again, enabled auto-connect attempts a new
connection. A missing profile or incompatible WireSock displays an explanation
without a hidden modal profile prompt. The ordinary Connect button can select
the profile. Active ordinary/startup sessions and maintenance block a duplicate
automatic attempt. Progress and cancellation use the normal connection flow.

The old before-sign-in checkbox is removed. Its enabled preference migrates to
program-start auto-connect. An inactive old startup setting is disabled through
the protected service. An active old route is preserved, and Settings explains
that it must be stopped before applying the new settings. Failed retirement is
reported; a running route is never stopped merely to show the application.

This behavior is in development, not yet in the published 0.6.4 release.

## Legacy protected before-sign-in implementation (0.5.7–0.6.4)

The following describes the retained compatibility implementation, not an
additional checkbox offered by the next version.

Setup shows the destination folder on a clean installation, remembers it for
updates, and uses the CITA dark console palette with red artwork. An existing
installation cannot silently relocate to a second directory. Choose a folder
writable by the current user. Setup remains per-user; protected network service
installation uses a separate Windows confirmation when a profile is prepared.

There are two independent startup settings:

- **Open in tray at sign-in:** a quoted HKCU Run entry launches the current
  executable with `--startup`. It never attempts a VPN connection by itself.
  Setup offers this as an unchecked task; settings can change it later.
- **Connect before sign-in:** after selecting/applying a working profile and
  disconnecting, enable it in Settings / Windows startup. One Windows elevation
  updates the protected agent and prepares a snapshot. No Windows password or
  scheduled interactive logon is used. Enabling affects the next Windows boot.

The existing automatic SYSTEM service executes only an administrator-owned
snapshot under Program Files/RoutePilot/Agent/startup-client. The fixed script
and engine list is hashed and checked before execution; source junctions are
rejected. Mutable user-install scripts and prior runtime journals are not
executed or adopted by SYSTEM. A fresh profile export is requested from the
existing WireSock service by the trusted routing entry, rather than copying
user VPN keys into a release or installation manifest.

The service waits cancellably for a physical gateway and a recognized
WireSock status, refreshing its waiting state every 30 seconds. A network or
WireSock service that appears later than three minutes is still accepted;
individual CLI calls retain their four-second deadline. It then performs the ordinary connection transaction, adaptive
policy startup and HTTPS verification. Readiness is checked against the same
session/generation health result before the startup state becomes Connected.
The UI reads the protected session and offers Stop through the SID-restricted
service pipe. Changing profiles, installation and independent diagnostics are
excluded while startup owns routing. Auto-update verifies downloads first,
then stops an owned startup route before file replacement.

Manual Stop suppresses another automatic attempt in the same Windows boot,
including service restarts. Cancelling readiness before a route was attempted
does not issue a VPN disconnect. Unknown/failed stop remains visible and blocks
parallel starts. Removing the client disables its inactive startup setting;
an active startup route must be stopped first. A missing source installation,
changed client version, changed protected connection settings or changed code
hash prevents using an old snapshot. Profile Apply now refreshes an enabled
snapshot inside the same elevated protected-package transaction. Both the old
package (including its snapshot/settings) and shared startup state are restored
before restarting the previous service if preparation or activation fails. An
exclusive setup handle is duplicated from its live owner and its final Windows
path checked; no unconditional skip-lock flag is accepted. Manual Stop remains
suppressed for the current boot after a refresh.

After an app update, a normal GUI launch refreshes enabled startup with one
Windows confirmation and the saved profile. This also detects Setup upgrades,
which do not have a pending-update journal. Cancellation/failure leaves startup
pending and suppresses further automatic prompts for that client version;
Connect or applying startup explicitly retries. Launching to tray at sign-in
never automatically raises UAC. Changes to personal rules still require applying
the startup setting again. Captive portals and Wi-Fi that
requires an interactive sign-in can require manual action after Windows logon.

`Verify-Startup.ps1` is a development-only disposable Windows integration test.
It installs the real service, runs its actual snapshot/start/stop orchestration
as SYSTEM in session 0 and substitutes a filesystem-only transport. It cannot
create a VPN, TUN or network route. It verifies delayed activation, one startup,
manual Stop, same-boot restart suppression, cancellation and update invalidation.
It does not prove real WireSock transport before sign-in, vendor auto-connect
compatibility, Wi-Fi authentication or behavior across a physical PC reboot.
The real SDK transport research in draft PR #9 remains separate.

On a separate real Windows stand, use `tools/Collect-StartupEvidence.ps1 -Action
Arm -ClientRoot <installed-client>` before a manual reboot, keep the machine
signed out, then run the collector after signing in. It compares the real kernel
boot, protected connection generation, fresh health, service session and WTS user
counts captured at startup/connection. It never prints profile/configuration
contents or performs a reboot. An unchanged kernel boot (including hybrid
shutdown) is not reported as a proven cold reboot. WTS counts are point-in-time
observations, not proof of continuous absence of all logons. See the manual
acceptance matrix in `docs/STARTUP-LAB.md`.
