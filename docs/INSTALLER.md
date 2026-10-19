# Windows installation

The primary download is `CITA-Route-VERSION-win-x64-setup.exe`. It installs for the
current user, creates Desktop/Start shortcuts, registers in Windows Apps and offers
to launch when finished. WireSock remains a separate dependency. Setup never
connects a VPN or installs a network service. First Connect performs checked setup.

## Layout and compatibility

The fixed default is `%LOCALAPPDATA%\Programs\CITA Route`:

```
CITA Route/
  client/          application, scripts, engines, local settings and licenses
  maintenance/     uninstall safety check
  unins000.exe     Windows uninstaller
  unins000.dat
```

Existing scripts keep their relative paths inside `client`, avoiding relocation of
sessions and changes to protected-agent ownership. Users open a shortcut. AppId
`CITA.Route.Client` is stable; reinstall targets the same per-user directory.

The installer consumes files in a matching self-contained portable manifest,
checks every size/hash/path, and regenerates a manifest omitting the redundant
`runtime\app-build\RoutePilot.exe`. The GUI remains `RoutePilot.exe` for lifecycle
compatibility. Licenses and source archives remain included. The ZIP retains the
duplicate for older updater compatibility. From 0.5.3 the internal updater no
longer installs that unused copy.

The ZIP remains the internal update format with trusted HTTPS metadata, SHA-256,
staging verification and rollback. Public files update inside `client`, keeping
shortcuts valid. Setup handles initial installation/manual repair; it does not
replace the transactional updater. An older Setup cannot downgrade an internally
updated client.

## Reinstall and removal

Setup never copies developer `data`, `private`, logs, tokens or live sessions.
Default domain files are created only if missing and retained on uninstall.
There is no recursive wildcard deletion of personal directories. Unknown files
and user settings survive. WireSock/profiles and the shared protected rescue
service remain. Choose **Disconnect and exit** before uninstalling.

A hidden guard holds `connection-operation.lock`, `setup.lock`, `upgrade.lock`
and `network-control.lock` in updater order, plus `installation-maintenance.lock`,
until Setup/uninstall finishes or its owner exits. It refuses active/malformed
sessions, this installation's running GUI/engines, concurrent operations,
symlink/junction ancestors, unknown product directories and Setup downgrades.
New GUIs check the maintenance gate before startup. The guard never stops a
process, service or route. Another copy/VPN can keep working during installation.

Uninstall permits a newer product version, so the original uninstaller still
works after an internal update. Portable folders are not deleted or migrated;
stop their routes before connecting the new copy. Global profile-name history
survives, but root-specific settings/ownership are not blindly copied. There is
no unattended reconnection.

## Build and verification

`Build-Installer.ps1` pins the official Inno Setup 6.7.3 download to SHA-256
`9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`
and requires a valid Authenticode signature from Pyrsys B.V. It uses
[official portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm) inside
`runtime/installer-tool` without tool shortcuts/uninstall registration. Explicit
`-Compiler` means the caller supplies its trusted compiler. Project release
executables are not yet code signed.

```
./Verify-InstallationGuard.ps1
./Build-Installer.ps1 -PackageDirectory <verified-stage>
./Build-Installer.ps1 -PackageDirectory <verified-stage> -TestBuild
./Verify-Installer.ps1 -Setup dist/CITA-Route-VERSION-win-x64-test-setup.exe
```

The fixture installer has its own AppId/shortcut names. Integration checks cover
installation in a Unicode/spaced path, shortcut targets, Windows registration,
absent duplicate executable, active-route refusal, repeat-install preservation,
removal after an internal version upgrade and retained private data. Guard checks
cover contention, bad state, downgrades, owner crash and maintenance-blocked GUI
startup. Fixtures never connect a VPN or alter service configuration. CI runs
them before publishing production artifacts; test installers never enter Releases.
