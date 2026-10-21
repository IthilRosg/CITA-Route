# Tailscale coexistence

## Confirmed configuration defect

On an IPv4-only physical connection, CITA previously advertised a broad IPv6
TUN route and later rejected captured IPv6. A user-supplied incident report
describes a Tailscale SSH connection by its IPv4 address failing while the
Tailscale peer still appeared online. Removing one CITA IPv6 route restored
that connection in one observed before/after test. That is evidence of a
routing interaction; it does not identify which control/DERP packet failed.

The IPv4 peer cannot directly select an IPv6 route. Its specific Tailscale
route also takes precedence over a broad CITA route. The missing IPv4
`100.64.0.0/10` exclusion was not the defect: that exclusion already existed.

## Implemented boundary

Normal Auto/Lists configurations and both diagnostic generation paths now
explicitly select their TUN route ranges. With no physical IPv6 gateway,
IPv6 routing is limited to the TUN's own internal subnet. With a physical
IPv6 gateway, normal IPv6 routing remains available. The next preparation
derives this again from the physical interface, including an installed
production template created under different network conditions.

Keep both internal interface address families and `strict_route=true`.
Removing the IPv6 address alone is unsafe: the pinned engine's Windows
implementation then installs a global IPv6 WFP block. Merely omitting the
IPv6 route list is insufficient too: the engine treats an empty family list
as the automatic default. An explicit, nonempty internal prefix avoids both.

The known Tailscale daemon/CLI/tray processes use the physical direct path
before sniffing or adaptive classification. Both overlay prefixes remain
excluded from the TUN: `100.64.0.0/10` and `fd7a:115c:a1e0::/48`. Other
applications retain their existing manual and adaptive routing rules.

## Validation

- Windows PowerShell 5 generates Auto and Lists configurations both with and
  without a physical IPv6 gateway; the actual bundled engine checks all of them.
- Agent tests cover old-template migration in both directions, internal IPv6
  preservation, bounded route prefixes, overlay exclusions, early process rules,
  idempotence and the existing dedicated policy-proof boundary.
- In the isolated Windows guest on 21 October, an old-style TUN generated one
  broad IPv6 route without a physical IPv6 gateway. The corrected configuration
  generated none, retained 69 IPv4 route entries, kept strict mode, and allowed
  a separate process to connect over IPv6 loopback. Both test engines stopped;
  no test routes remained. No host routing or live host VPN was changed.

These are configuration and route-table checks, not a passed real Tailscale
peer/SSH acceptance test. The guest test did not join the user's tailnet.

## Remaining acceptance

1. With CITA active, test Tailscale peer reachability and a limited SSH readback
   by IPv4, then repeat after reconnecting CITA. Online status alone is insufficient.
2. Separately check IPv6 overlay traffic, direct versus relayed Tailscale
   transport, and physical IPv6 present/absent. Observe the real daemon paths.
3. Check MagicDNS FQDNs and short names. Global Windows DNS hijacking can block
   another interface's DNS, and `*.ts.net` currently lacks its own DNS/FakeIP
   policy. The topology fix does not claim to solve these separate DNS cases.
   Any follow-up must avoid an OS-resolver loop, handle late Tailscale startup,
   preserve ordinary DNS protection, and cover pre-login startup/templates.
4. Test Tailscale exit-node and subnet-router modes separately. They introduce
   additional routes and must not be declared supported from this test alone.
5. Keep intentional user Stop separate from transport failure. A partial
   manual route deletion must not be interpreted as proof that WireSock stopped.

The other-PC local SOCKS refusals remain a separate observation. Deleting one
IPv6 route does not by itself stop the local bridge; the supplied archives
lack the watcher/safety state needed to establish that bridge's stop reason.

## Engine references

- [Pinned sing-tun automatic route construction](https://github.com/SagerNet/sing-tun/blob/bca4bf029237/tun_rules.go)
- [Pinned Windows WFP behavior](https://github.com/SagerNet/sing-tun/blob/bca4bf029237/tun_windows.go)
- [TUN DNS and routing options](https://sing-box.sagernet.org/configuration/inbound/tun/)
- [Tailscale coexistence and overlay prefixes](https://tailscale.com/docs/reference/faq/other-vpns)
