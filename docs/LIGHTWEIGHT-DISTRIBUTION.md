# Lightweight distribution plan

Status: design and size experiment, not an implemented release format.
The production installer and updater still consume self-contained packages.

## Measured starting point

Local Windows x64 measurements on 2026-10-20, product 0.5.7:

| Artifact | Size, MiB |
| --- | ---: |
| Existing self-contained ZIP | 209.47 |
| Existing installer | 129.72 |
| ZIP with only app/agent executables replaced by framework-dependent builds | 57.62 |

The experimental ZIP has deliberately invalid manifest hashes and must never be
installed or uploaded. It measures compression only; it does not implement
runtime discovery, installation, migration, or service compatibility. Its app
and agent target .NET 8. The size of a working lightweight installer is not yet
measured. The engine binaries and third-party licenses/source remain included.

## Runtime and installation

1. Move app and protected agent together to .NET 10 LTS, including CI and build
   SDKs. First test on supported Windows versions and the existing LTSB lab.
   A newer major runtime does not automatically satisfy the present .NET 8 app.
   WireSock's installed .NET 10 Desktop Runtime can then be reused where its
   architecture, patch level, and both shared frameworks meet requirements.
2. Require machine-wide Windows x64 `Microsoft.WindowsDesktop.App` and
   `Microsoft.NETCore.App`, in a supported version meeting the package's minimum
   patch. Inspect trusted machine registration/shared-framework files and reject
   reparse points. Do not count an SDK, an x86 runtime, a user-local runtime, or a
   `dotnet.exe` found through PATH as sufficient for a SYSTEM service.
3. Setup shows the prerequisite before changing client files. If absent, offer
   download and installation inline, with progress, cancellation, disk-space
   checks and a clear explanation of the Windows elevation prompt. Use a pinned
   Microsoft HTTPS installer, SHA-512 and valid Microsoft Authenticode identity;
   verify again immediately before execution. The package determines a known
   prerequisite; never execute an arbitrary URL or command from release notes.
4. Download into a private staging location, verify any cached installer again,
   and avoid another download when an adequate runtime is already installed.
   Use bounded timeouts and clean partial files. Recheck both shared frameworks
   after installation. Cancellation, denied elevation, offline failure, corrupt
   cache, unexpected exit or required restart leave the existing client usable.
   Never reboot automatically. Do not uninstall a shared runtime with CITA Route.

## Update and service compatibility

5. Keep the existing self-contained ZIP asset under its current name during
   migration: older clients explicitly reject `selfContained=false`. Add a
   distinct framework-dependent ZIP asset and explicit manifest runtime metadata.
   New clients prefer it only after implementing all prerequisite checks; old
   clients continue receiving a compatible package. Do not label thin payloads
   self-contained to bypass old validation.
6. Download, verify and, when needed, install the runtime while the current VPN
   can still provide access to Microsoft/GitHub. Complete this before stopping
   routing or handing off to the transactional updater. An unavailable or
   refused prerequisite must not disconnect the working version. Revalidate at
   installation handoff to detect changes after preparation.
7. Preserve package path/hash allowlists, downgrade protection, ownership locks,
   saved profile/rules, rollback, and source/license delivery in both formats.
   Account for staged older packages and upgrades from 0.5.4/0.5.7. Unknown runtime
   requirements fail closed before public files or protected service are changed.
8. Agent installation/refresh must verify machine-wide runtime readiness before
   replacing or starting the protected executable. Keep service refresh and
   manual-stop semantics. Verify SYSTEM startup before sign-in after a real guest
   restart; a successful user-launched GUI is insufficient evidence.

## Release gates

- Clean machine without .NET; adequate runtime already installed; only x86,
  only Core, older patch, and newer unsupported major installed.
- Standard user, UAC accepted/denied, offline mode, download cancellation,
  hash/signature failure, corrupt cache, disk full, reboot-required result.
- Active VPN is kept until prerequisites are ready; refusal and failure preserve
  the working route. Runtime checks and download do not change WireSock profiles.
- Old self-contained client -> transitional/full release -> thin release;
  retained prepared update; repair/reinstall; uninstall with retained data.
- SYSTEM agent startup before sign-in; service refresh/rollback; direct HTTPS
  after intentional Stop; source profile and selected profile preserved.
- CI verifies both manifests/archives and installer variants, prevents private
  files and test-only artifacts from entering Releases, and reports actual
  compressed sizes. Long-running resource tests remain a separate gate.

## Primary references

- [Microsoft Windows installation](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [.NET 8/9 end of support](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [.NET 10 supported Windows versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

.NET 8 support ends on 2026-11-10. Do not introduce a new distribution dependency
on that runtime immediately before its end of support.
