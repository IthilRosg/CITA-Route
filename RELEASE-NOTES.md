CITA Route 0.5.6 — connection lifecycle fixes and regression debugging.

- Prevent profile setup/import/protected-agent replacement from overlapping startup or reconnect after the initial setup check. The gate stays held through the action and is released on failure; manual Stop remains available.
- Report an unverified WireSock disconnect instead of silently claiming success. Require a disconnected state after the command; preserve a foreign profile selected during the operation.
- Protect similarly named foreign profiles during crashed-router cleanup by using exact managed-profile identity rather than substring matching.
- Add a real entry-point race regression and a short 20-cycle loopback recovery soak with continuous direct traffic, refused paused VPN flows, memory/handle growth and latency observations. Extend release checks for profile rollback, disconnect and maintenance lifecycle.

The real WireSock SDK/Windows-filter experiments remain development tools on disposable machines. This release does not enable a new driver boundary, change WireSock modes, prove zero leakage, or promise seamless migration of existing connections across external IP changes. Loopback tests do not replace a physical WAN/AWG/game/voice soak.

The protected agent updates on the next Connect through normal Windows confirmation. Windows 10/11 x64 and separately installed WireSock Secure Connect are required. Setup and ZIP include .NET. Release executables are not yet code signed.
