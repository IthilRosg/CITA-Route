# CITA Route

Windows client for adaptive routing through **WireSock Secure Connect**.
Part of the CITA family: [AWG CITA](https://github.com/IthilRosg/AWG-CITA) manages VPN interfaces and client configurations; CITA Route chooses how local applications reach the network.

The [Sector Console interface](docs/STYLE.md) shares AWG CITA's SIGNALIS-inspired palette, sharp frames and terminal typography.

**0.5.5** detects a live but stalled policy controller and can pause only the owned VPN bridge while another recovery operation holds the coordinator. Independent per-session safety holds also invalidate old GUI health results. Resource warnings, the Windows installer and verified updates remain included. See [component liveness and limits](docs/COMPONENT-LIVENESS.md): this release does not claim driver-enforced zero-leak VPN-only isolation.

[Download stable releases](https://github.com/IthilRosg/CITA-Route/releases) · [Report a bug](https://github.com/IthilRosg/CITA-Route/issues)

## What it does

- Finds installed WireSock and its profiles; imports your own `.conf` without overwriting an existing profile.
- Routes listed restricted destinations through VPN and other traffic directly.
- Auto mode compares direct HTTPS, TLS record fragmentation and VPN for eligible failing IPv4 TCP/443 connections, with direct access preferred.
- Provides connection statistics, application/domain rules, logs and automatic recovery of a running session.
- Shows new-release notifications at startup and every six hours. Downloads survive restart and can install automatically once disconnected, with an installer ownership handshake and rollback.
- Restores the last applied/confirmed WireSock profile across app restarts and release folders; restoring a selection never connects automatically.
- Opens a compact connection window by default. The menu switches to the complete advanced view without changing routing; each view remembers its own size.
- Reports actual connection stages and supports cooperative cancellation. First setup and connection share one Windows elevation transaction.

## Install

Windows 10/11 x64 and separately installed WireSock Secure Connect are required. Both downloads include .NET.

1. Download **CITA-Route-VERSION-win-x64-setup.exe** from Releases, run it and click **Install**. Open **CITA Route** from the Desktop or Start shortcut (or launch at the end of Setup). For a portable installation, extract the complete ZIP and run **RoutePilot.exe**.
2. Click **Connect**. The client finds WireSock and offers a profile picker only when needed; you can also import `.conf` there.
3. Accept Windows elevation when requested. CITA Route prepares the protected agent and its managed profile, connects and verifies the VPN exit. A valid saved profile is reused on the next click.
4. Disable WireSock's **Virtual Adapter Mode** and **Override profile split-tunneling settings with global ones** if enabled. Connect automatically stops a recognized active WireSock profile and the supported VPNUS service before starting CITA. Original profiles are preserved. Unknown services, ambiguous status and another running CITA routing engine are reported instead of being terminated by name.
5. Use **⋮ → Advanced view** for connection settings, rules, diagnostics and detailed statistics. Closing the window hides it to the tray by default; **Disconnect and exit** stops the route first.

When upgrading from **0.4.0**, the old **Settings → Connection → Apply connection** remains available in the advanced view.

The installer uses `%LOCALAPPDATA%\Programs\CITA Route\client` for the current user; network setup retains its normal elevation prompt. Repeat installation preserves user rules/preferences. Setup and uninstall require **Disconnect and exit**, hold the same operation locks as the updater, reject active/unreadable sessions and refuse downgrades. Uninstall through Windows Apps removes installed public files and shortcuts, retains personal data, and leaves WireSock/profiles and the shared protected rescue service alone. Old portable copies are not automatically deleted or migrated; profile-name history is shared, but a new root needs checked protected setup on the next Connect. See [installer design and verification](docs/INSTALLER.md).

Your original profile is preserved. Private keys stay in your WireSock installation and the local private configuration folder. Never share a configured working directory; share the clean installer or release ZIP.

## Updates

The stable channel is `IthilRosg/CITA-Route` GitHub Releases. A popup offers a new version while the window is active; background notifications do not steal focus. The same release is not repeatedly announced. Open Settings to check or download again. Downloaded packages survive restart and are reverified against fresh trusted release metadata, including the archive's manifest identity, before installation. Another repository's assets and unsafe archive paths are rejected. Installation backs up replaced public files and rolls them back on failure. Personal rules, profiles, keys, statistics and preferences are retained.

Automatic installation of a downloaded package is enabled by default. It verifies the release and package while the connection still works, then stops this copy's owned route through normal Windows elevation and installs the update. Open CITA connections can be interrupted. Another copy's engine, diagnostics or an unfinished connection operation still blocks installation. Disable auto-installation in Settings if manual timing is preferred; choosing Later for an already downloaded release postpones automatic installation for four hours. A preparation reservation blocks a concurrent Connect until the installer owns the update. The GUI exits only after that ownership handshake. The next **Connect** updates the protected Windows agent when necessary; **Apply connection** remains available in Advanced. Installation does not automatically reconnect a stopped session. SHA-256 is checked against release metadata delivered over HTTPS; releases are not currently code signed.

Connection switching records its intent before stopping a supported competing connection. Failure/cancellation attempts to restore the previous WireSock profile and restart a stopped VPNUS service, without overwriting a newly selected connection. Interrupted intent is retried before a later Connect. This is not an independent background recovery guard: restoration can require attention if the process crashes, the old profile disappears or the other client's reconnect behavior differs. The rollback starts the VPNUS service; it does not claim a verified VPNUS internet connection. No profiles, keys or unrelated network adapters are deleted.

Profile history contains only profile/CLI metadata in `%LOCALAPPDATA%\CITA\Route\profile-history.json`, not configuration keys. A missing remembered profile is reported rather than silently replaced. A new release folder still requires checked protected setup; restoring the selection does not transfer ownership of another running copy's service.

Mini statistics cover the current CITA session. Compact mode reads VPN RTT and estimated loss from the existing WireSock service; no additional server or tunnel is required. RTT reflects the last handshake, while loss is the engine's own estimate, whose window is not exposed. Unavailable/stale/foreign-profile data show dashes. The optional independent Direct/VPN UDP reflector measurement remains in advanced diagnostics. HTTPS failures and traffic totals are never converted into loss percentages. See [native tunnel statistics](docs/WIRESOCK-STATISTICS.md) and [optional UDP setup](docs/UDP-QUALITY.md).

## Scope and compatibility

The [resource monitor](docs/RESOURCE-GUARD.md) runs in the existing protected rescue service after its normal update on the next Connect. It observes IPv4/IPv6 UDP ranges/reservations, actual bind availability, memory and process counters; the compact window warns only on confirmed issues. Settings and the compact menu offer a detailed report and Task Manager. Two distinct observations confirm pressure, and three reliable clear samples retire it. Tray alerts require the GUI to be running, including hidden to the tray. A stale/missing report is unavailable. This stage detects resource problems; it does not stop unrelated processes, reserve ports against other applications or restart the network. Long-duration workload/leak testing and integration with component recovery remain separate work.

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
./Build-Installer.ps1 -PackageDirectory dist/CITA-Route-VERSION-win-x64-STAGE
```

`Bootstrap.ps1` downloads a pinned sing-box release and verifies its archive and executable SHA-256. Public routing data is fetched separately. If necessary, pass `-DownloadProxy socks5h://127.0.0.1:PORT` for the dependency downloads.

The resulting `dist/CITA-Route-VERSION-win-x64.zip` includes the runtime, public data, source and third-party license material. `Build-Installer.ps1` consumes a matching verified package and creates `dist/CITA-Route-VERSION-win-x64-setup.exe`. It bootstraps the pinned official Inno Setup compiler in portable mode inside `runtime`, checking SHA-256 and Authenticode before executing its download. `-Compiler` accepts an explicit trusted compiler; `-DownloadProxy` configures downloads. `dist/SHA256SUMS.txt` includes both public downloads; the stable updater continues using the ZIP. Local data, private configs, logs and binaries are ignored by Git.

`Verify-All.ps1` and VPNUS fault-injection checks are advanced, disruptive diagnostics for a prepared test machine. CI uses offline fixtures and separate loopback engines instead.

The component recovery work on this branch keeps the direct router alive during bridge/VPN repair. Unknown WireSock status pauses the bridge conservatively; it never authorizes switching a foreign profile. Control HTTPS failures are rechecked and reported without restarting the TUN. Retries are bounded and Stop supersedes late callbacks. `Verify-ComponentRecovery.ps1 -Engine` verifies one established direct TCP socket across a real loopback bridge restart, and refuses VPN-only connections while paused. This does not validate live WireSock interception, UDP/QUIC, physical network changes or gap-free VPN flow migration. See [the staged reliability plan](docs/ROUTING-RELIABILITY-PLAN.md).

`./Measure-Resources.ps1 -Seconds 120 -IntervalSeconds 10` passively records the main GUI, agent, router, bridge and WireSock process CPU/memory/handle counters in `logs/`. It does not generate traffic or change networking. Shared working sets cannot be summed as unique RAM, and this sampler does not fully attribute short-lived PowerShell/CLI helpers. Use a longer controlled run for leak investigations.

`RoutePilot.exe --benchmark-ui` compares compact and advanced WPF presentation on 200 synthetic rows without backend polling or network activity. Results in `logs/ui-resource-fixture.json` are a presentation benchmark, not total VPN resource use. Preview renderers also avoid live polling and preference writes.

## Maintenance

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). GitHub Actions checks pull requests and main. The manually dispatched release workflow validates the tag/version, runs tests, builds and verifies the ZIP, then publishes the ZIP and checksums. To bump a version, update `product.json` and `Directory.Build.props` together.

## License

Application source: [Apache-2.0](LICENSE). Separate engine/runtime components retain their licenses; see [THIRD-PARTY.md](THIRD-PARTY.md) and the license/source material in release archives.
