CITA Route 0.6.1 — DNS fallback, web resource routing and recoverable updates.

- Add separate, bounded DNS fallback for each route and retry public IPv4 addresses before connecting. Valid negative DNS answers and TLS validation keep their meaning; requests are not replayed after connection.
- Improve first-use CDN discovery and distinguish a healthy page from interrupted resource transfers. HTTP 451 can trigger fallback; a generic 403, a certificate error or a browser extension block is not treated as proof of network filtering.
- Parse both LF and Windows CRLF routing logs. Short connections retain their domain/application evidence and can trigger calibration without waiting for an API snapshot.
- Update the routing core to 1.14.3-cita.3. A bounded, short-lived QUIC domain cache keeps retries within the current application/domain rule. Ordinary UDP and existing TCP connections keep their routing behavior.
- Include an optional experimental Chrome/Edge browser adapter. After a real resource failure, CITA independently verifies another path and can retry an eligible failed image once. It is disabled by default, requires explicit registration and browser installation, and does not replay forms, POST requests or file downloads. URLs, query strings and page contents are not sent to the agent. Firefox is not supported by this adapter.
- Keep resource-pressure notifications off by default while retaining measurements and diagnostics. Add bounded recovery evidence and per-component resource history.
- Make EXE installation recoverable after interrupted public-file replacement. Verify backup hashes, preserve personal data, recover before launching and retain failed recovery evidence. Fix atomic JSON writes under long Windows PowerShell paths.
- Deliver the browser adapter files as a complete verified set; reject partial packages and roll back locked-file failures. Fix communication between an ordinary browser and an elevated agent, without changing UAC or granting process-control access.

Choose setup.exe for normal installation. It offers to download and install the verified Microsoft .NET Desktop Runtime 10.0.13 x64 only when needed. The full ZIP includes .NET; the smaller framework ZIP uses the machine runtime. MSI is outside this release.

The browser adapter remains opt-in. An older updater may need a full setup/package installation to receive its newly added files. Reload an unpacked extension after replacing its files. Store distribution, SYSTEM browser delegation and silent-hang detection remain future work.

Acceptance evidence and limits: [release report](https://github.com/IthilRosg/CITA-Route/blob/main/docs/RELEASE-0.6.1.md), [browser adapter](https://github.com/IthilRosg/CITA-Route/blob/main/docs/BROWSER-FEEDBACK.md). Lab transport tests do not promise access to every website or survival of every connection across a physical network change. Separately installed WireSock and your own working profile are required. Existing routing, profile discovery, compact UI, statistics, updates and TLS-fragmentation support remain available.
