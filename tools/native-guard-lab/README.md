# Persistent guard proof (isolated W11 only)

Follow-up: policy/TUN binding and actual service integration are implemented;
see [the service milestone](../../docs/NATIVE-SERVICE-20261025.md).
`Policy` requests support default routes, direct exclusions and mandatory
loopback exclusion. The original restricted `Prefixes` fixture remains available.
Service execution outside the guest is confined to LocalSystem at the protected
agent's fixed native helper path. The ordinary desktop backend remains disabled.

The standalone commands below are isolated prefix proofs. The helper checks the
exact lab VM UUID for these invocations. Production service invocation is
separately restricted as described above. No host WFP changes are made by the
test runners. Build with `Build.ps1`, run `Run-W11.ps1`.

The proof arms persistent ALE connect blocks for IPv4/IPv6 prefixes, kills the
helper, verifies both blocks and an unaffected direct target, then removes only
objects bearing the exact per-run identity. Metadata GUIDs are deterministic
per run/role; all ownership markers are checked before deleting filters.
There is no general WFP reset or wildcard deletion. A hidden independent rescue
process is started before arming, with a fixed 120-second cleanup deadline.
`Run-W11.ps1 -CrashController` kills the fixture via Exit(73), bypassing finally,
and verifies the separate rescue removes filters/address/rule after 30 seconds.

The IPv6 test uses a temporary ULA on the guest uplink and a local TCP listener;
it also checks delivery of fresh nonce-tagged UDP datagrams. `-ResetLink` verifies
the blocks remain after the guest uplink is disabled/re-enabled, while an
unprotected direct target remains reachable. This is a virtual adapter reset,
not a physical Wi-Fi/Ethernet handoff. Temporary inbound rules permit only the
guest's own exact ULA, and are removed along with the fixture address.
it proves IPv6 filtering, not public IPv6 availability or native IPv6 carriage.
The test covers a /24 and /64 rather than two hard-coded /32 firewall entries.
The legacy `Prefixes` proof rejects default routes and prefixes shorter than /8.
The newer compiled `Policy` path supports defaults/exclusions and is tested with
TUN LUID permits, scoped encrypted-server permission and protected service
recovery. Ordinary client setup, adaptive policy publication, live transactional
policy replacement and installer/update rollout remain separate requirements.
WFP persistent objects are not automatically removed when their process exits;
never run an unguarded prototype on a user's normal workstation.

Dependency: github.com/tailscale/wf at
6fbb0a674ee67f6c9b99eb7ce8a4330bb0145ec9 (BSD-3-Clause;
see LICENSE.wf). Version and checksums are pinned in go.mod/go.sum. Upstream
documents that its API is not stable; the pin is intentional. No local library
fork, private ABI access or unreviewed syscall structure layout is used.

References:
- https://learn.microsoft.com/en-us/windows/win32/fwp/object-management
- https://github.com/tailscale/wf
