CITA Route 0.4.0 — updates and remembered connections.

- New release popup in the Sector Console style, with background notifications that do not steal focus from games or calls.
- Downloaded updates survive application restart. Archives and extracted manifests are reverified against the trusted release before installation.
- Automatic installation waits until routing and diagnostics are stopped, including routing owned by another copy. The GUI exits only after the installer acknowledges exclusive ownership. Failed installation rolls back; protected service updates still use normal UAC through Apply connection.
- Last applied/confirmed profile is remembered per user outside the release folder. Restoring a selection never connects or silently changes the protected agent. Missing profiles are reported rather than replaced by another profile.
- Hidden-window telemetry polls less often; unchanged preferences/history are not repeatedly written; download progress updates are coalesced.
- Regression checks cover restart persistence, foreign/active sessions, installer handoff, changed manifests, profiles across folders, Unicode and rollback.

Upgrade from 0.3.2 through Settings → Updates while disconnected, or extract the complete ZIP. After upgrading, Apply connection updates the protected agent with Windows confirmation. Personal configurations and rules are preserved. The routing recovery improvements are being developed separately from this UI/update release.