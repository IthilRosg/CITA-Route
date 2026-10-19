CITA Route 0.5.0 — compact connection window, native WireSock statistics and component recovery.

- Compact mode opens by default: one connection button, connection stages, traffic counters and a small VPN quality row. Advanced mode retains the full settings, rules, logs and statistics. Switching views preserves the connection and each window size.
- Connect discovers WireSock and asks for a profile when needed. The last confirmed profile is reused. First setup and connection share one Windows elevation transaction, with progress and cooperative cancellation.
- VPN RTT and estimated loss come from the existing WireSock service through read-only local IPC. No additional server is needed. RTT describes the latest handshake, not continuous ping; loss is WireSock's estimate. Missing, stale or foreign-profile data show dashes.
- Recovery repairs individual VPN/bridge/policy components while retaining the direct router where possible. Retry budgets, session-generation guards and Stop priority prevent stale repairs. Control HTTPS failures are reported and rechecked without restarting the TUN.
- Setup now distinguishes protected agent storage from the application folder. Matching settings avoid repeated setup; another portable folder or changed protected settings still require checked setup. Profile operations validate the complete managed profile name.
- Advanced diagnostics include optional authenticated Direct/VPN UDP measurements using a separately configured controlled reflector. They remain disabled by default and never silently fall back from VPN to Direct.
- Release notifications, persistent downloads, checked automatic installation after disconnect and installation rollback remain available.

Upgrade through Settings → Updates while disconnected, or extract the complete ZIP into a writable folder and run RoutePilot.exe. The next Connect updates protected components through Windows confirmation when needed. Apply connection remains available in Advanced settings. Personal configurations and rules are preserved.

Requirements: Windows 10/11 x64 and separately installed WireSock Secure Connect. The ZIP includes .NET. Native statistics were checked against WireSock 3.6.1.1; other versions can show unavailable statistics without changing the tunnel.

Validation includes isolated Windows agent/GUI tests, recovery and update/rollback fixtures, real loopback TCP/UDP engines, and extracted-package verification. Live driver fault recovery, physical Wi-Fi changes, sleep/resume, mixed-DPI monitors and simultaneous game/voice/streaming traffic still require broader hardware testing. Existing TCP sessions cannot migrate seamlessly between different public exit IPs. AWG 3.1 and full IPv6/QUIC routing are not validated. System-wide port-exhaustion monitoring is planned for a later version and is not included in 0.5.0.
