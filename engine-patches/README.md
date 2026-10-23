# CITA routing core

Both runtime roles are CITA routing core **1.14.3-cita.5**, separately branded derivatives without upstream affiliation. The same verified binary is published as `smart-router.exe` and `vpn-bridge.exe`; each receives its role-specific configuration. First-use gating remains off by default.

Build inputs are SHA-256-pinned sing-box 1.14.3 sources, checksum-pinned sing-tun `bca4bf029237` and sing `0ad23b637bd4`, Go **1.26.3**, and the patches/tests in this directory. `Build-RouterEngine.ps1` verifies patch contexts, runs native TCP/UDP deadline retry and adapter boundary regressions, and stages the binary independently of a running installation. `Bootstrap.ps1` publishes both runtime roles. The portable builder checks both binaries and the complete corresponding source archive against the current build receipt.

`cita_windows_read_fix.ps1` resets readiness state before each Windows scalar TCP, vector TCP and UDP RawConn.Read. A timeout must not leave the next read in a synchronous receive without a readiness wait. `cita_windows_read_test.go` expires and retries deadlines on real local sockets, ten repetitions per reader. The unpatched versions fail these cases. See [resource investigation](../docs/UDP-LIFETIME-20261023.md) for reproduction and limitations.

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
relative, so the extracted core, TUN and socket source directories remain buildable together.
