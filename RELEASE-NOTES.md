CITA Route 0.5.1 — fix Windows WireSock status detection.

- Fix a 0.5.0 regression: multiline WireSock CLI replies with Windows CRLF line endings could classify CITA's own SmartRouting-AWG2 profile as foreign. This aborted connection and could leave the managed tunnel connected after cleanup. The router and protected HTTPS-checking agent now retain exact profile identity for both CRLF and LF.
- Recognize the actual English/Russian establishing, disconnecting and disconnected messages. Transitional and negative statuses cannot prove a healthy VPN; similarly named or conflicting profiles remain rejected.
- Regression coverage includes real redirected Windows PowerShell stdout in UTF-8/OEM866, multiline statuses, complete profile names and managed-connection cleanup.

Upgrade through Settings → Updates while disconnected, or extract the complete ZIP into a writable folder and run RoutePilot.exe. The next Connect updates the protected agent through Windows confirmation. Apply connection remains available in Advanced settings. Personal configurations and rules are preserved. An actual foreign WireSock profile or another active VPN must still be disconnected by its owner.

Requirements: Windows 10/11 x64 and separately installed WireSock Secure Connect. The ZIP includes .NET. All 0.5.0 features remain available.

Broader live driver/hardware and IPv6/QUIC limitations from 0.5.0 remain. System-wide resource-exhaustion protection is planned separately and is not included in this fix.
