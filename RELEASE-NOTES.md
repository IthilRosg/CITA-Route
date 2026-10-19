CITA Route 0.5.4 — Windows resource pressure monitoring.

- The existing protected rescue service observes IPv4/IPv6 UDP port ranges, exclusions, bind availability, system memory/commit and process counters. It continues while the GUI exits, provided the configured client is still installed.
- Two distinct observations confirm a warning; three reliable clear samples retire it. Unknown/stale data do not prove recovery. Sustained growth warnings apply only to identified CITA components and do not claim a proven memory leak.
- Confirmed issues appear as a compact warning, with a local report in Settings and the compact menu. The report lists large UDP/memory/handle consumers and can open Task Manager. Tray alerts are deduplicated and require the GUI to be running (including hidden to the tray).
- Collection runs outside the network coordinator, uses bounded buffers/history and read-only Windows queries, and binds only one temporary socket per address family without sending packets. It never stops applications, changes VPN profiles or restarts networking. It cannot reserve system resources against another application's behavior.
- The protected agent updates through normal Windows confirmation on the next Connect after installing this release. A missing monitor report beforehand is expected. The existing installer, shortcuts and verified updates remain available.
- Added synthetic MSI-sized exhaustion, native UDP layouts and ownership, exclusion/shared-port accounting, confirmation/recovery, stale/partial state, memory/growth and PID-reuse tests. A native integration check verifies sixteen local sockets, disposal, background publication and cancellation. No real port exhaustion is induced.

This is the first resource-safety stage: detection, confirmation and useful diagnostics. Automatic component-recovery integration, full VPN-only isolation, flow preservation, physical network changes and extended UDP/QUIC/IPv6/MTU validation remain separate work. A short integration run does not establish long-duration CPU/RAM use or absence of leaks.

Windows 10/11 x64, separately installed WireSock Secure Connect. Setup and ZIP include .NET. Releases are not yet code signed.