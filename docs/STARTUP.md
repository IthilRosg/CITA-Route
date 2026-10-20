# Installation and Windows startup (0.5.7)

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

The service waits up to three minutes for a physical gateway and a recognized
WireSock status, then performs the ordinary connection transaction, adaptive
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
