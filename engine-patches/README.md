# CITA routing core

This is a separately branded derivative, without upstream affiliation. The
unmodified VPN bridge remains the verified official binary. Only the router
receives the Windows DNS compatibility modification.

Build inputs are the SHA-256-pinned 1.14.3 source archive, the checksum-pinned
sing-tun module at `bca4bf029237`, Go **1.26.3**, and these two Go files.
`Build-RouterEngine.ps1` verifies its patch contexts and runs the adapter boundary
tests. It stages the compiled router independently of a potentially running
workspace router. `Bootstrap.ps1` explicitly publishes it into the runtime;
the portable builder validates the separate build receipt before packaging.

The added permit stays inside the original dynamic WFP session and sublayer.
All three conditions must match: an active Tailscale interface LUID, destination
Quad100 (`100.100.100.100` or `fd7a:115c:a1e0::53`), and destination port 53. The
permit has no clear-action-right flag. Ordinary DNS blocking, interface IPv6
addresses and strict mode remain present. Adapter changes are checked every two
seconds. The loop stops before the filtering session closes; dynamic filters
also disappear if the process exits. No firewall rules or Tailscale preferences
are persisted.

The VM-only native fixture verifies real Windows filter registration, condition
types, scope and deletion for both families. The regular host build runs only
the pure adapter/address boundary tests. GitHub's isolated Windows runner also
runs the native fixture. Neither fixture establishes a real tailnet connection.

The distribution includes the original license and both original and modified
source archives, including a build recipe. The narrow module replacement is
relative, so the extracted core and TUN source directories remain buildable together.
