# UDP lifecycle acceptance

Runs the shipped `runtime/vpn-bridge.exe` and `runtime/smart-router.exe` against an echo service on the same machine. It never reads a VPN profile. Default mode uses only loopback sockets, without WireSock, TUN, firewall, service or system route changes.

```powershell
dotnet publish tools/udp-lifecycle-lab/Cita.UdpLifecycleLab.csproj -c Release -r win-x64 --self-contained false -o runtime/udp-lifecycle-lab
runtime/udp-lifecycle-lab/Cita.UdpLifecycleLab.exe . runtime/udp-lifecycle-result.json 10
```

The workload opens 192 data-carrying and 96 empty associations, directly and through nested SOCKS, then 128 clients reset during handshake and 128 after sending a packet. An independent active association must continue exchanging random echo payloads and verify each response. After all control clients close, bridge UDP endpoints must return to zero. Late ICMP from intentionally closed UDP clients is counted and must not terminate the fixture echo server.

Optional `bound` mode binds the bridge to an active physical adapter and uses an echo listener on that adapter's own local IPv4 address. It does not contact an external host.

Optional `tun` mode is only for an isolated Windows VM, requires `CITA_UDP_TUN_LAB=1` and refuses an existing router process. It creates the `CitaUdpLab` adapter with exactly `203.0.113.77/32` as a test route, maps that destination to its own local echo, and generates 96 short native UDP flows in addition to the SOCKS workload. Use 370 idle seconds to exceed the real five-minute default NAT lifetime; active traffic continues every 500 ms. Before the active clients close, the bridge must have at most four endpoints (two live paths); after closing the SOCKS path, at most two are allowed for the just-closed native UDP path, which has no TCP close signal. This mode does not connect WireSock or alter the default route. The VM wrapper verifies test-route removal and process cleanup.

JSON observations record actual endpoint counts, memory, handles and times. A failed or interrupted fixture is not a routing pass. This test does not establish all-domain availability or long-term memory stability.
