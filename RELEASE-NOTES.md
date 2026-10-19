CITA Route 0.5.2 — automatic connection handoff and update disconnection.

- Connect releases a recognized active WireSock profile and a supported VPNUS service before starting CITA. Profiles and keys are retained. Unknown/ambiguous connections and another running CITA engine remain protected from blind termination.
- Intent is recorded before a conflicting connection stops. Failure/cancellation attempts to restore the prior profile and start the stopped VPNUS service; a newer active profile is never overwritten. An interrupted journal is retried before the next Connect. This is not an independent crash guard, and service restart does not prove the other client's internet access.
- Installation revalidates the release and package while the current VPN still works, then automatically stops this copy's own route. Network-dependent verification finishes before disconnection. A reservation blocks concurrent Connect until the installer acquires ownership; failed Stop prevents installation. Foreign routes and diagnostics still block updates.
- Release requests use the existing VPN bridge while routing is active. The update popup/settings explain that installation interrupts CITA connections. After installing, press Connect again; automatic reconnection is not introduced.
- Includes the 0.5.1 Windows CRLF/localized WireSock status fix. New fixtures cover handoff/rollback, cancellation, interrupted journals, service identity, a changed foreign profile, setup ordering and update/Connect contention.

Upgrade through Settings → Updates or extract the complete ZIP. Upgrading from 0.5.1 or earlier still requires disconnecting before installation because the older updater owns that operation. Once 0.5.2 is installed, its updater can stop its own active route. The next Connect updates protected components through Windows confirmation. Personal settings are preserved.

Requirements: Windows 10/11 x64 and separately installed WireSock Secure Connect. The ZIP includes .NET. All 0.5.0 features remain available.

Broader live driver/hardware and IPv6/QUIC limitations from 0.5.0 remain. System-wide resource-exhaustion protection is planned separately and is not included in this fix.
