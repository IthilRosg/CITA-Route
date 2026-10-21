CITA Route 0.5.9 — recovery after pauses and network interruptions.

- Retain a previously verified IPv4 exit for one protected connection configuration. A changed external-address label in WireSock no longer invalidates the same independently verified exit. Managed profile checks remain required; a genuine mismatch retains the safety hold. Reapplying a connection requires fresh confirmation, and an explicit expected exit retains priority.
- Discard pre-sleep HTTPS results and accumulated observations after a long observation gap or clock rollback. Resume with fresh checks rather than counting old samples as new evidence.
- Wait for an eligible physical IPv4 network without consuming network retry attempts. Show «Ожидание сети» in the UI and retain bounded recovery once the network returns.
- Test the current verified routing core in component and UDP fixtures. Fix the startup test reader's sharing mode so it can coexist with atomic service-state publication.

Validation includes agent/GUI self-tests, current and legacy update checks, a 20-cycle simultaneous TCP/UDP loopback recovery test, and real WireSock in the Windows VM. The VM retained its router and exit assertion across a 35-second VMware pause and an 18-second adapter outage; Direct/VPN HTTPS and STUN worked afterward. One of six STUN replies was lost in the first short post-outage sample. Loopback continuity, synthetic 20% UDP loss, fresh real connections and long-term stability are separate claims: see docs/RESUME-RECOVERY.md.

Real Wi-Fi/Ethernet handoff, physical sleep/hibernation, Tailscale SSH/MagicDNS on the second PC, and the long mixed-use run remain separate acceptance tests. Existing TCP flows cannot be guaranteed to survive a change of physical network or exit address. Windows 10/11 x64 and separately installed WireSock Secure Connect are required; this installer still includes .NET.
