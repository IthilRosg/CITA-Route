# Selective transport boundary: isolated experiment

Status: development prototype, outside the installed client and agent. This does
not change the 0.5.5 release or close its between-observation WireSock gap.

## Architecture decision

The current bridge opens ordinary sockets on the physical NIC. WireSock's
transparent packet interception is a separate operation. An application block at
Windows ALE can therefore stop the very sockets WireSock needs. The published
WireSock network lock also has system-wide blocking semantics. Neither option is
enabled on a working CITA Route session by this experiment.

The candidate is a dedicated VPN adapter plus socket binding and an independent
application-specific Windows boundary for the bridge. Direct-router sockets must
remain outside that boundary. Adapter creation, default routes, DNS, transport
capture and interface identity must all be controlled before this can replace
the transparent implementation. WireSock's documented virtual mode changes
system-wide routes; simply switching its GUI setting is unsafe for this design.

The first experiment tests a narrower prerequisite: whether persistent BFE block
filters can restrict **one private executable** by interface LUID, survive abrupt
controller death, revoke established flows and preserve direct flows from a
different executable. It uses real Windows filtering and loopback TCP/UDP with
IPv4 and IPv6. It does not install or call WireSock.

## Running the fixture

`Verify-TransportBoundary.ps1` builds `tools/transport-lab` into
`runtime/transport-lab-build` and runs checks without installing filters.
`-Native` additionally requires an administrator on an **isolated Windows x64
test machine**. The separate `transport-lab.yml` workflow supplies that machine;
do not run the native experiment on the user's active VPN computer.

The native fixture:

1. Creates a generated `runtime/transport-lab-<GUID>` directory. Its executable
   and managed assembly must match exact private copies of the lab. Other
   application paths, reparse-point directories and oversized ledgers are refused.
2. Opens independent, persistent direct/guarded TCP and UDP sockets for both IP
   families. All senders and echo receivers use loopback only.
3. Atomically installs persistent, application-scoped BLOCK filters at
   `ALE_AUTH_CONNECT_V4/V6`: a nonmatching interface and an unknown interface are
   blocked. No PERMIT filter is installed. Existing firewall restrictions remain.
4. Kills the installer process without cleanup, then verifies allowed traffic.
   Replaces the allowed interface with a different existing LUID and kills that
   controller too. Existing guarded flows must stop; new TCP must fail with
   Windows access denied. UDP can report access denied or be silently dropped.
   A UDP timeout alone is insufficient: the receiver must get no guarded payload,
   an unguarded positive control must reach that same receiver, and restored
   guarded traffic must work. The same direct sockets must continue working.
5. Kills a replacement controller before its transaction commits; the previous
   restriction must remain enforced. Tests an unconditional hold scoped to that
   same private application, then
   restores the loopback restriction. New guarded flows must work again without
   restarting the direct sockets. No seamless revival of revoked TCP is claimed.
6. Removes only filters whose deterministic fixture keys, sublayer, display label
   and application ID match the ledger. Verifies their absence. Results include
   explicit `wiresockDriverTested=false`, `physicalInterfaceFailoverTested=false`
   and `perPacketIsolationProved=false`.

The ledger is flushed before any filter is installed. If the runner is
interrupted, retain its fixture directory and use the **same build** with
`Cita.TransportLab.exe --cleanup <absolute-fixture-root>` as administrator on
that test machine. Do not delete fixture files before cleanup. A hard-killed
controller intentionally leaves its narrowly scoped persistent block in place.
The workflow machine is disposable. No production startup or update invokes the
lab, and it is not added to the client payload.

## Acceptance gates before production integration

- Prove the actual WireSock virtual/transparent interception order against the
  candidate filters. A loopback result does not prove it.
- Prove adapter mode can avoid conflicting global routes/DNS and TUN recursion;
  retain endpoint reachability and direct-router bindings.
- Validate adapter GUID/LUID ownership, removal, LUID/index reuse, DHCP, Wi-Fi
  changes, IPv4/IPv6 source selection and weak-host settings. An alias or index
  alone cannot establish trust.
- Capture traffic on a separate receiver during tunnel stop, profile replacement,
  driver/controller/service crash, already-open TCP/UDP flows and new flows. ALE
  is stateful and is not itself a custom per-packet driver. Route changes without
  policy reauthorization still need proof or a stronger transport boundary.
- Arm protection before starting the bridge; keep it armed during failure. Stop
  and verify the bridge before removing protection. Updates, rollback, uninstall,
  machine restart and BFE boot-time gaps need a protected lifecycle and recovery
  ownership, not a deletion in a generic finally block.
- Test concurrent direct TCP, video, voice and game UDP; quantify interruption,
  CPU/memory and latency on actual hardware. An IP change can require application
  reconnection regardless of route isolation.

Until these gates pass, the shipping client keeps the existing mode and its
documented limits. No automatic production migration or release is introduced by
this research change.

Primary references:
[WireSock modes](https://wiresock.net/documentation/wiresock-secure-connect/virtual-adapter-mode.html),
[WireSock network lock](https://wiresock.net/documentation/wiresock-secure-connect/network-settings.html),
[WireSock SDK](https://wiresock.net/documentation/wiresock-secure-connect/sdk-overview.html),
[ALE reauthorization](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-re-authorization),
[WFP object lifetimes and transactions](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management),
[Microsoft SDK declarations](https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders).
