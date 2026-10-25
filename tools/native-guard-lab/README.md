# Persistent guard proof (isolated W11 only)

Not a production guard, routing policy compiler or native client integration.
The helper independently checks the exact lab VM UUID before WFP operations.
No host WFP changes are made. Build with `Build.ps1`, run `Run-W11.ps1`.

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
Default routes and prefixes shorter than /8 are deliberately rejected here.
Full-policy compilation/exclusions, TUN LUID permits, narrow encrypted-endpoint
exceptions, transactional policy replacement, service restart/startup recovery
and protected ownership storage remain required before ordinary client use.
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
