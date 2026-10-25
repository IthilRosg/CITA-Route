# Policy-bound native service milestone

Implemented and tested through the actual Windows SCM `RoutePilotRescue` service
in the isolated Windows 11 guest. This closes the policy/TUN binding and service
controller milestones. The ordinary desktop client still selects WireSock;
shipping UI/setup/update integration is a separate release gate.

## Implemented

- Canonical IPv4/IPv6 prefix compilation, direct exclusions, deterministic
  policy identity, bounded expansion and mandatory loopback exclusion. The core
  captures exactly the compiled IPv4 policy; mismatched configurations fail.
- Persistent blocks and permissions limited to the validated TUN LUID. A
  physical adapter with a different name/type cannot be substituted. Rebinding
  replaces permissions while retaining the blocks. IPv6 protected addresses are
  **blocked**, not carried by this IPv4 native transport.
- Encrypted-server permission matches the protected owner executable's WFP app
  identity, UDP, one server IPv4 address and one port. A separate identity binds
  this tuple; broad protocols/addresses and mismatched endpoint identity fail.
  The core excludes only that endpoint host route to prevent encapsulation loops.
- Protected component/profile/policy/core-config catalog. ACL checks, pinned
  SHA-256 values and read leases protect the complete snapshot. Keys stay in the
  protected profile and private stdin streams; IPC accepts neither file paths
  nor key/config content.
- Native commands on the existing authenticated service pipe: `native-status`,
  `native-start`, `native-stop`. Start/Stop acknowledgements return immediately;
  the service owns asynchronous work and exposes progress/state. Stop carries
  the current operation identity, so stale requests cannot stop a newer session.
- Atomic protected intent journal, advancing generation and rotated operation
  identity on service restart. Desired connections resume; manual disconnects
  remain disconnected. A Job Object owns the core, owner and helper children.
- Core repair retains the selected owner. Owner death/terminal exhaustion is
  detected instead of showing a false active state; the service can replace the
  complete session with a fresh generation under a monotonic repair budget.
  Failure of the guard does not trigger unverified automatic reconstruction.
- Ordinary operation leases and diagnostics cannot race the native controller.
  Invalid native configuration prevents a silent fallback to the old startup
  route. An existing legacy router prevents a native start.

## Accepted failure matrix

The isolated SCM runner installs a temporary protected snapshot, then checks
real HTTPS exit and UDP through the OS TUN after initial connection, core kill,
service kill/restart, and automatic recovery following owner kill. It rejects a
stale Stop, disconnects explicitly, restarts the service and verifies the saved
disconnect. The final matrix uses a full public IPv4 policy and protected public
IPv6 blocking, with LAN/Tailscale exclusions. Original service configuration is
restored and the temporary candidate/profile directory removed. An independent
rollback is armed before the service path is changed.

The separate WFP/TUN runner verifies wrong-adapter/wrong-policy refusal, no
physical fallback during core replacement, rebind, pause/reconnect and kernel-job
retirement. This is a **single-peer** proof, not a new independent-server matrix.
Earlier independent-node receipts retain their own scope/artifact hashes.

Receipts under `runtime/native-scm-*.json` and `runtime/native-wfp-session-*.json`
pin the tested artifacts. `tools/native-awg/VALIDATION-RESULTS.json` selects the
accepted runs; failed attempts are retained. A successful receipt certifies only
its own binaries, not every subsequent rebuild.

## Findings corrected during integration

- Windows can enumerate effective WFP weights as zero through the pinned API.
  Writes retain fixed priorities; ownership/action/conditions are validated,
  nonzero mismatches rejected and actual data-plane behavior tested. Host /32
  conditions may be returned as an address rather than a prefix.
- Owned cleanup no longer depends on a newer parser accepting an older filter's
  shape. Exact provider/sublayer/run ownership still has to match.
- Core control readiness can precede TUN IP readiness. Binding waits with a
  deadline rather than treating the transient adapter state as permanent.
- Windows PowerShell 5 writes a UTF-8 BOM with `Set-Content -Encoding UTF8`.
  Generated core JSON now has no BOM, and the controller rejects it before arm.
- The service snapshot no longer reports Connected while the owner is gone.

## Deployment contract and remaining gates

The optional service snapshot is `native/` beside the protected agent. Its
`service.json` enables the backend and pins `native-catalog.json`. Catalog roles
are owner/core/guard/policy/profile/core-config plus expected exit hash. The
production WFP helper is restricted to LocalSystem at
`%ProgramFiles%\RoutePilot\Agent\native\native-guard.exe`; isolated tests use an
independent exact guest UUID check. No snapshot is installed on the host by these
tests. Native mode is disabled when the protected snapshot is absent.

The snapshot currently requires a **pinned IPv4 peer endpoint**. Hostname
resolution/pinning belongs to protected setup. The reserved endpoint host route
does not grant other applications a broad physical bypass: if management access
to that public IP is intended to be direct, policy must explicitly exclude it.
Live arbitrary policy/profile replacement is rejected rather than removing the
old protection; maintenance must disconnect and retire the old snapshot first.

Still needed before public native-backend selection: normal UI/profile import,
adaptive domain-policy publication to this backend, installer/updater maintenance
and rollback, corresponding-source packaging, physical NIC handoff and public
IPv6 carriage. SCM restart is not an actual Windows reboot or early-boot filter
acceptance. TCP flows destroyed by a process crash require application reconnect;
the tests establish safe recovery, not uninterrupted replay of application data.
