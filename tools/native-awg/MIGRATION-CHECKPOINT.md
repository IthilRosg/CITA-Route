# Native transport migration checkpoint — 24 October 2026

Accepted on the isolated W11 VM. This is transport feasibility, not automatic
production roaming or a release. TCP is tested THROUGH the candidate CITA
SOCKS core; UDP is held DIRECTLY through the authenticated native proxy. UDP
continuity through the full core and the actual TUN path remains unaccepted.

## Controlled outer rebind

The explicit `--serve-proxy-migration-lab` mode supports both native TCP/UDP
and one session/generation-scoped `Rebind` command on the parent's stdin.
The only engine command it may execute is fixed `listen_port=0`. Upstream
amneziawg-go closes/reopens the outer UDP bind and clears cached endpoint
source addresses while preserving device, peers, keys and internal netstack.
No configuration or keys are serialized from engine IPC. Missing/failed
callback produces no acknowledgement and stops the lab owner. Repeated or
wrong-generation actions are refused; unit tests cover these boundaries.

The normal TCP proxy, UDP-only lab and loss-only lab do not provide a rebind
callback. There is no local TCP management API or arbitrary IPC passthrough.
Snapshots include actual outer local port, bind opens, TCP dials and successful
internal UDP opens. This proves a local encrypted-port change, not what public
source port a particular external NAT chose on the server side.

## Real guest IPv4 change and rollback

`Guest-IPv4Migration.ps1` is hard-guarded by the approved guest UUID and the
inspected VMnet8 topology. The test temporarily changes guest IPv4 from DHCP
192.168.252.128/24 to 192.168.252.77/24, gateway .2. The temporary address is
outside the inspected DHCP pool .128-.254. Ping preflight and Windows Duplicate
Address Detection must allow it; kernel source-route selection must confirm
the new address before resuming traffic. Host VMware/NAT settings are untouched.

The runner holds existing TCP and UDP sockets, waits for an actual core
interface-reset acknowledgement, then explicitly rebinds outer AWG UDP and
resumes the old streams. The core's lease keeps its native TCP link alive;
the native UDP association must also remain the same.

All address/route mutations target ActiveStore and one saved adapter GUID.
The helper saves the original address, DNS, DHCP and inherited/persistent DHCP
shape in the protected guest folder. It starts a hidden independent 35-second
restore process before mutation. Main `finally` also restores. Restore calls
are serialized and limited to the temporary address/owned route, re-enable
DHCP, request lease renewal and verify original IP/DNS/persistent shape. A
helper stopped by main cleanup is identified by PID AND original start ticks.

An early preflight refused a missing explicit PersistentStore DHCP record,
without changing topology. Inspection showed inherited DHCP; the helper now
preserves that absence rather than creating a persistent setting. That failed
preflight receipt is retained separately from actual migration acceptance.

Microsoft documents that New-NetIPAddress disables DHCP when adding an address
on a DHCP-enabled interface and describes DAD and PolicyStore behavior:
[New-NetIPAddress](https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress),
[Set-NetIPInterface](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipinterface).

## Final acceptance

`Run-W11Migration.ps1` runs three short cases with matching native/core hashes.
Summary: `runtime/native-awg-migration-acceptance.json`. Individual ignored
receipts: `runtime/vm-lab/w11/native-migration-final-*.json`.

| Case | Transition plus exchange verification | HTTPS request after release | Internal sockets |
| --- | --- | --- | --- |
| Local encrypted UDP port rebind | 1034 ms | 57 ms | client TCP 1→1, native TCP 3→3, native UDP 1→1 |
| Guest IPv4 change plus rebind | 9919 ms | 2192 ms | same counts; core reset retained native entry |
| Rebind under NIC observation | 1017 ms | 53 ms | same counts |

Each changed outer bind opens 1→2 and its actual local port changes. The
internal UDP association exchanges three validated DNS responses before and
three after, then still closes on control EOF. The expected HTTPS VPN exit
digest stays unchanged. The total native TCP count includes two earlier
independent health checks; it does not indicate extra retained-stream dials.

Transition timing starts before the mutation and ends after checking TCP and
UDP, including the UDP lifecycle check. It excludes baseline setup and final
Windows restoration. These are isolated samples, not application interruption
guarantees. The IPv4 timing includes Windows configuration, DAD and waiting for
the core event. Sockets are held during mutation; continuous traffic, packet
loss rate, call quality or game continuity are not measured here.

The rebind capture observed direct controls (8 TCP / 2 UDP) and then 16 endpoint
UDP packets with zero other outgoing TCP/UDP in the selected source-IPv4 NIC
observation. Capture stopped. This applies to the same-IP rebind capture only;
the IPv4-mutating case does NOT have a packet-level absence-of-leak assertion.

All three cases passed on the final series, with restored topology and removed
temporary credential files. Separately, the controller process was allowed to
exit after changing IP. The delayed helper independently restored DHCP/IP/DNS
without manual rescue. Receipt:
`runtime/vm-lab/w11/native-migration-autonomous-rollback.json`.

## Remaining boundary

Same VMware NAT/subnet only: no move between providers, public-IP change,
Wi-Fi/cellular switch, captive portal or server restart has been tested. The
rebind is initiated by the lab parent after a controlled event; production
network observation, debouncing, retries and service owner admission are not
implemented by this checkpoint. Existing WireSock/product paths are unchanged.

The subsequent scoped TUN/full-core checkpoint is accepted, including UDP
recovery (not seamless retention): see [TUN-CHECKPOINT.md](TUN-CHECKPOINT.md).
Next: bounded automatic recovery and production profile/generation
handoff. Preserve default reset for unrelated traffic and explicit Stop. Do
not turn these laboratory results into a universal seamless-migration claim.
