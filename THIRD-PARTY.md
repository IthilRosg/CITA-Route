# Components in the Windows distribution

- CITA Route sources (Apache-2.0) are supplied in `source`; PowerShell lifecycle scripts are at the package root.
- sing-box 1.14.3, Copyright nekohasekai / SagerNet, GPL-3.0-or-later with the naming condition in its LICENSE. Two identical, unmodified engine binaries are named `smart-router.exe` and `vpn-bridge.exe` so WireSock can isolate their traffic. License, GPL text, release source archive and build version output are in `licenses`. Upstream: https://github.com/SagerNet/sing-box/tree/v1.14.3 . Dependencies and build instructions are recorded in the supplied source's go.mod/go.sum and Makefile.
- .NET 8 runtime and Windows Desktop runtime, Microsoft and contributors, redistributed with self-contained applications. License and third-party notices are in `licenses`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf .
- Public routing lists: https://github.com/itdoginfo/allow-domains and https://core.telegram.org/resources/cidr.txt . These lists are not user VPN configurations.
- WireSock Secure Connect is a separately installed product; its binaries and driver are not redistributed in this ZIP.

The included router performs TLS record fragmentation. No ByeDPI or separate DPI driver is bundled.
