# Components in the Windows distribution

- CITA Route sources (Apache-2.0) are supplied in `source`; PowerShell lifecycle scripts are at the package root.
- `vpn-bridge.exe` and `smart-router.exe` are **CITA routing core 1.14.3-cita.5**, separately branded derivatives of SHA-256-pinned sing-box 1.14.3 (Copyright nekohasekai / SagerNet, GPL-3.0-or-later with its naming condition), checksum-pinned sing-tun `bca4bf029237` and sing `0ad23b637bd4`. Both roles include a Windows socket readiness reset that allows TCP/UDP deadline retries and descriptor cleanup. The router also includes scoped Tailscale Quad100 DNS permits, bounded route-specific DNS fallback, a short-lived QUIC domain cache and opt-in TCP first-use gating. No upstream affiliation. Original licenses, GPL text, original release sources and the complete modified core/TUN/socket source archive are in `licenses`; the modified archive includes go.mod/go.sum, regression tests and `CITA-MODIFICATIONS.txt`. The build recipe and patches are in the repository. Upstream: https://github.com/SagerNet/sing-box/tree/v1.14.3 .
- .NET 10 runtime and Windows Desktop runtime, Microsoft and contributors, are downloaded separately from Microsoft only when required; the framework distribution does not bundle them. License and third-party notices are in `licenses`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf .
- Public routing lists: https://github.com/itdoginfo/allow-domains and https://core.telegram.org/resources/cidr.txt . These lists are not user VPN configurations.
- WireSock Secure Connect is a separately installed product; its binaries and driver are not redistributed in this ZIP.

The included router performs TLS record fragmentation. No ByeDPI or separate DPI driver is bundled.
