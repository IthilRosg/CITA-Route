CITA Route 0.5.7 — installation and Windows startup.

- Choose the destination folder during a clean installation. Updates keep the existing directory to avoid splitting the connection and settings across two copies.
- Use the CITA dark console theme, red branding and terminal headings in Setup and Uninstall.
- Optionally open the compact client in the tray after Windows sign-in. Setup offers an unchecked startup task; settings can enable or disable it later.
- Add opt-in routing before sign-in through the existing automatic SYSTEM service. A Windows confirmation installs the current protected agent and saves a fixed, hashed snapshot of routing code and rules. Network readiness, profile identity, current session/generation and HTTPS readiness are checked.
- Show startup progress in the normal UI and stop its route through the protected service. Manual Stop prevents another startup attempt in the same Windows boot. Updates stop the owned route; uninstall disables an inactive startup setting. Changed client versions or protected connection settings require refreshing the snapshot.

Validation includes a real SYSTEM/session-0 service with a synthetic filesystem-only transport, plus installer, update, rollback and disconnect regressions. The service fixture does not prove real WireSock transport before sign-in, a physical reboot, captive portal/Wi-Fi authentication or seamless migration of established connections. Real-driver boundary research remains separate in draft PR #9.

Windows 10/11 x64 and separately installed WireSock Secure Connect are required. Setup and ZIP include .NET. Release executables are not yet code signed.