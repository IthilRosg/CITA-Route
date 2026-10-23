# CITA Route 0.6.4

- Correct Windows TCP/UDP read readiness after a deadline in both router and VPN bridge. This prevents inactive associations from retaining blocked reads and socket resources. Production UDP lifetimes and active flows remain unchanged. Complete corresponding modified sources are included.

- Bounded body-aware route probes distinguish successful headers from a stalled transfer. Bounded startup hints schedule fresh probes without extending expired decisions.
- Telegram Mini App domain rules now complement Telegram IP rules, including migration of existing rule files. WebView2 participates in transport discovery without receiving extension browser privileges.
- WireSock discovery checks standard x64/x86 folders, installation registrations, services and running application paths. Selecting the GUI exe can locate its sibling CLI. Discovery does not require the latest version. Legacy wiresock-client.exe is reported separately; its connection backend is not implemented.
- Profile folder shortcuts and a useful initial import directory are available in advanced connection settings. Existing explicit profile selections remain intact.
- TCP first-use gating has passed two controlled cold browser runs and a ten-minute idle/HTTPS VM check. It remains off by default in this candidate; enabling it requires the explicit FirstUseGate policy setting. It does not migrate established TCP streams or cover IPv6/IP-only/UDP flows.
- Distribution is framework-only; the .NET prerequisite remains a separate verified Microsoft download when absent. No MSI release.

Acceptance: Windows 11, WireSock 3.6.1.1. Two cold Windows64 visits completed DOM in 4175/3331 ms with 43/43 images; Ryuugames external images 4/4. Ten-minute VM HTTPS run: ten samples passed, no recovery, cleanup completed. This does not establish all-domain availability or long-duration stability.

Package and GUI self-tests passed in Windows 11. EXE update from an installed 0.6.3 preserved existing rules; a metadata-retention reinstall preserved the selected profile. Applying the connection installed protected agent 0.6.4.0, verified HTTPS and stopped normally. The separate test installer passed installation/removal, shortcuts, startup registration, active-session refusal and interrupted-copy rollback. Those installer checks preceded the socket correction. The corrected .5 core requires renewed protected-agent, WireSock and package acceptance before publication.
