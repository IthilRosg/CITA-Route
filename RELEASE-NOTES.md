CITA Route 0.6.5 Native Preview — separate experimental native transport build.

- Bundle the verified AWG 3.1/WG userspace transport, routing core and scoped native guard with EXE Setup; preparation uses the bundled archive after SHA-256 verification.
- Keep WireSock available and retain explicit native setup, profile import and backend selection. No automatic migration of an active connection.
- Download the Microsoft .NET prerequisite separately only when missing. Native corresponding source and offline rebuild recipe are provided as a separate preview asset.
- Include the current native DNS, scoped routing, coordinated recovery and bounded socket-repair work. This is a pre-release, not a stable-channel update; see docs/NATIVE-PREVIEW.md for limitations and test evidence.

CITA Route 0.6.4 — socket resource cleanup, resource delivery, Mini Apps and WireSock discovery.

- Fix Windows TCP/UDP readiness state after a read deadline in both router and bridge. Closed or expired associations can release their sockets without periodically restarting active traffic. Includes corrected source and regression tests.

- Distinguish stalled HTTP bodies from reachable headers; retain bounded startup hints without extending route TTL.
- Route Telegram Mini App domains through the Telegram rules and migrate older IP-only Telegram lists. Include WebView2 in transport discovery.
- Find WireSock through installation folders, registry, service and application paths. Selecting its GUI can locate its CLI. Display the detected version; a latest release is not required. Older editions with a different CLI are reported separately and are not claimed as supported connection backends.
- Add profile folder shortcuts in advanced settings and a useful starting directory for import.
- Experimental FirstUseGate remains off by default; it affects only eligible new TCP/443 flows and does not migrate existing connections or cover IPv6/UDP/IP-only traffic.
- Publish only the smaller framework ZIP and EXE Setup. Download the signed Microsoft .NET prerequisite separately when absent. MSI is excluded.

Windows 11 acceptance: two cold browser runs with complete tested images; ten-minute idle/HTTPS test passed. These checks do not establish all-domain availability or long-duration stability.

CITA Route 0.6.3 — moved-installation connection repair and smaller updates.

- Explicit Connect can transfer an inactive pre-login startup binding from a previous client folder owned by the same Windows account. Both installations are reserved, active/unknown routes and other accounts are refused, and the previous protected package and startup state are restored if activation fails. No previous client scripts or VPN configurations are imported or executed.
- Updates always select the framework ZIP. If the required machine-wide .NET Desktop Runtime is missing, download it separately from the pinned Microsoft URL, verify SHA-512 and the Microsoft Authenticode signature, and install with Windows confirmation before stopping the current route. Cancellation or failure leaves the route available.
- Clear an obsolete failed-update message after manual Setup has already installed that version. Failures targeting a newer release remain visible.
- Allow Setup to update an explicitly selected, previously committed installation when its Windows registration points to an older folder; retain the destination ownership and transaction checks.
- Preserve root-manifest validation with the optional browser extension. Surface protected setup failure details instead of only a generic failure notice.

For clients whose old updater still reports one manifest.json, use setup.exe once. The installer also downloads .NET only when necessary. MSI remains excluded.

CITA Route 0.6.2 — update manifest recognition hotfix.

- Recognize the package's root manifest in flat and single-folder ZIP archives. The optional browser extension's nested manifest no longer blocks download or restoration of a prepared update.
- Retain SHA-256, version, architecture, payload checksums, unsafe-path and ambiguous-root checks. A modified nested browser manifest is still rejected as a corrupted file.
- Remove the operation's extracted directory after a rejected download so repeated attempts do not accumulate abandoned packages.
- Exercise actual full and framework release ZIPs through the GUI updater's archive validation before publication, in addition to fixture installation tests.

**Upgrading from an older client:** if downloading 0.6.1 reports that the archive must contain one manifest.json, install this release with setup.exe. The already installed updater contains the bug and cannot repair itself by downloading another ZIP with the same extension layout. Profiles and personal rules are retained by the installer. The fixed client supports subsequent automatic ZIP updates.

See [hotfix evidence and test boundaries](https://github.com/IthilRosg/CITA-Route/blob/main/docs/RELEASE-0.6.2.md). MSI is excluded. The following routing and resource improvements from 0.6.1 are retained:

DNS fallback, web resource routing and recoverable updates:

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
