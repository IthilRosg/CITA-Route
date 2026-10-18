# CITA Route

Windows client for adaptive routing through **WireSock Secure Connect**.
Part of the CITA family: [AWG CITA](https://github.com/IthilRosg/AWG-CITA) manages VPN interfaces and client configurations; CITA Route chooses how local applications reach the network.

The [Sector Console interface](docs/STYLE.md) shares AWG CITA's SIGNALIS-inspired palette, sharp frames and terminal typography.

[Download stable releases](https://github.com/IthilRosg/CITA-Route/releases) · [Report a bug](https://github.com/IthilRosg/CITA-Route/issues)

## What it does

- Finds installed WireSock and its profiles; imports your own `.conf` without overwriting an existing profile.
- Routes listed restricted destinations through VPN and other traffic directly.
- Auto mode compares direct HTTPS, TLS record fragmentation and VPN for eligible failing IPv4 TCP/443 connections, with direct access preferred.
- Provides connection statistics, application/domain rules, logs and automatic recovery of a running session.
- Shows new-release notifications at startup and every six hours. Downloads survive restart and can install automatically once disconnected, with an installer ownership handshake and rollback.
- Restores the last applied/confirmed WireSock profile across app restarts and release folders; restoring a selection never connects automatically.

## Install

Windows 10/11 x64 and separately installed WireSock Secure Connect are required. The release ZIP includes .NET.

1. Extract the whole ZIP into a writable folder and run **RoutePilot.exe**. The product is named CITA Route; legacy executable and service names remain for compatibility.
2. Open **Settings → Connection**, choose your WireSock profile, or import `.conf`.
3. Click **Apply connection** and accept Windows elevation. This installs the protected agent and prepares a separate managed profile; it does not connect the VPN yet.
4. Disable WireSock's **Virtual Adapter Mode** and **Override profile split-tunneling settings with global ones** if enabled. CITA Route checks these settings.
5. Disconnect another active VPN/profile yourself, then click **Connect**.

Your original profile is preserved. Private keys stay in your WireSock installation and the local private configuration folder. Never share a configured working directory; share the clean release ZIP.

## Updates

The stable channel is `IthilRosg/CITA-Route` GitHub Releases. A popup offers a new version while the window is active; background notifications do not steal focus. The same release is not repeatedly announced. Open Settings to check or download again. Downloaded packages survive restart and are reverified against fresh trusted release metadata, including the archive's manifest identity, before installation. Another repository's assets and unsafe archive paths are rejected. Installation backs up replaced public files and rolls them back on failure. Personal rules, profiles, keys, statistics and preferences are retained.

Automatic installation of a downloaded package is enabled by default and waits for the route and diagnostics to stop. Another copy's engine also blocks installation. Disable this in Settings if manual installation is preferred; choosing Later for an already downloaded release postpones automatic installation for four hours. The GUI exits only after the installer confirms exclusive ownership. After the application restarts, **Apply connection** updates its protected Windows agent using the normal UAC transaction. The updater never silently disconnects a route, switches VPNUS, or reconnects a previously disconnected session. SHA-256 is checked against release metadata delivered over HTTPS; releases are not currently code signed.

Profile history contains only profile/CLI metadata in `%LOCALAPPDATA%\CITA\Route\profile-history.json`, not configuration keys. A missing remembered profile is reported rather than silently replaced. A restored selection in a new release folder still needs Apply connection; it does not transfer ownership of another running copy's service.

## Scope and compatibility

- Single-peer WireGuard / AmneziaWG profiles with an IPv4 endpoint, or a hostname resolving to IPv4. Compatibility depends on the installed WireSock version. AWG 3.1 compatibility is not yet validated; the panel's support does not imply client support.
- Adaptive DPI probing covers IPv4 TCP/443; this does not guarantee UDP, QUIC, streaming, account or regional access. Some destinations stay pinned to VPN; manual rules take priority.
- The built-in DPI candidate is sing-box TLS record fragmentation. No separate ByeDPI driver is installed.
- VPN health compares an HTTPS-observed exit with WireSock's reported external IPv4 or a configured expected exit. The server endpoint can differ from its NAT exit.
- First connection is manual. A deliberately disconnected or missing session is not automatically connected. Physical Wi-Fi switching, sleep and reboot behavior still require broader hardware testing.
- Legacy extended diagnostics require a configured VPNUS baseline. Normal routing and recovery do not require VPNUS.

## Build and test

Install the .NET 8 SDK on Windows, then use Windows PowerShell:

```powershell
./Bootstrap.ps1
./Build-All.ps1
./Verify-Portable.ps1
./Verify-ComponentRecovery.ps1 -Engine
./Verify-Optimization.ps1
./Build-Portable.ps1
```

`Bootstrap.ps1` downloads a pinned sing-box release and verifies its archive and executable SHA-256. Public routing data is fetched separately. If necessary, pass `-DownloadProxy socks5h://127.0.0.1:PORT` for the dependency downloads.

The resulting `dist/CITA-Route-VERSION-win-x64.zip` includes the runtime, public data, source and third-party license material. `dist/SHA256SUMS.txt` is the stable updater's checksum asset. Local data, private configs, logs and binaries are ignored by Git.

`Verify-All.ps1` and VPNUS fault-injection checks are advanced, disruptive diagnostics for a prepared test machine. CI uses offline fixtures and separate loopback engines instead.

The component recovery work on this branch keeps the direct router alive during bridge/VPN repair. Unknown WireSock status pauses the bridge conservatively; it never authorizes switching a foreign profile. Control HTTPS failures are rechecked and reported without restarting the TUN. Retries are bounded and Stop supersedes late callbacks. `Verify-ComponentRecovery.ps1 -Engine` verifies one established direct TCP socket across a real loopback bridge restart, and refuses VPN-only connections while paused. This does not validate live WireSock interception, UDP/QUIC, physical network changes or gap-free VPN flow migration. See [the staged reliability plan](docs/ROUTING-RELIABILITY-PLAN.md).

`./Measure-Resources.ps1 -Seconds 120 -IntervalSeconds 10` passively records the main GUI, agent, router, bridge and WireSock process CPU/memory/handle counters in `logs/`. It does not generate traffic or change networking. Shared working sets cannot be summed as unique RAM, and this sampler does not fully attribute short-lived PowerShell/CLI helpers. Use a longer controlled run for leak investigations.

## Maintenance

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). GitHub Actions checks pull requests and main. The manually dispatched release workflow validates the tag/version, runs tests, builds and verifies the ZIP, then publishes the ZIP and checksums. To bump a version, update `product.json` and `Directory.Build.props` together.

## License

Application source: [Apache-2.0](LICENSE). Separate engine/runtime components retain their licenses; see [THIRD-PARTY.md](THIRD-PARTY.md) and the license/source material in release archives.
