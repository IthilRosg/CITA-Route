CITA Route 0.6.0 — stable routes and smaller installation.

- Confirm a return from VPN/DPI to a preferred path with a second calibration at least 15 seconds later. A confirmed failure of the current path allows prompt replacement. Manual/VPN-only rules and TLS validation retain priority; automatic rule publication does not close existing connections.
- Wake the controller when calibration completes. Reduce idle network-profile enumeration and state writes; Windows network events invalidate the polling cache. Always re-read network evidence before applying results and at publication barriers.
- Discard pending adaptive evidence after a controller pause or clock rollback. Keep the same-session VPN/DPI direction as stale until revalidated; changing Windows time cannot silently remove it and send the site direct.
- Setup checks machine-wide .NET Desktop Runtime 8 x64 instead of bundling it. When missing, offer the official Microsoft download with progress, a pinned SHA-512 and a valid Microsoft publisher signature. Existing runtimes are reused and shared runtimes survive client uninstall.
- Provide a smaller framework-dependent ZIP for new updaters with the machine runtime. Preserve the self-contained ZIP for old clients and runtime-free machines. A staged update retains its original archive flavor, integrity checks and rollback.

The following pause/network fixes from candidate 0.5.9 are included:

- Retain a previously verified IPv4 exit for one protected connection configuration. A changed external-address label in WireSock no longer invalidates the same independently verified exit. Managed profile checks remain required; a genuine mismatch retains the safety hold. Reapplying a connection requires fresh confirmation, and an explicit expected exit retains priority.
- Discard pre-sleep HTTPS results and accumulated observations after a long observation gap or clock rollback. Resume with fresh checks rather than counting old samples as new evidence.
- Wait for an eligible physical IPv4 network without consuming network retry attempts. Show «Ожидание сети» in the UI and retain bounded recovery once the network returns.
- Test the current verified routing core in component and UDP fixtures. Fix the startup test reader's sharing mode so it can coexist with atomic service-state publication.

Validation includes agent/GUI self-tests, current and legacy update checks, a 20-cycle simultaneous TCP/UDP loopback recovery test, and real WireSock in the Windows VM. The VM retained its router and exit assertion across a 35-second VMware pause and an 18-second adapter outage; Direct/VPN HTTPS and STUN worked afterward. One of six STUN replies was lost in the first short post-outage sample. Loopback continuity, synthetic 20% UDP loss, fresh real connections and long-term stability are separate claims: see docs/RESUME-RECOVERY.md.

Real Wi-Fi/Ethernet handoff, physical sleep/hibernation, Tailscale SSH/MagicDNS on the second PC, and the long mixed-use run remain separate acceptance tests. Existing TCP flows cannot be guaranteed to survive a change of physical network or exit address. Android remains a separate plan. See docs/RELEASE-0.6.0.md for scope, packaging and acceptance details.

Windows 10/11 x64 and separately installed WireSock Secure Connect are required. Choose setup.exe for normal installation. An old updater may still download one full archive; the new setup prepares the runtime for subsequent smaller updates.
