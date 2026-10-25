# Native backend finalization

Continue after f754945. This is a work ledger, not release acceptance.

| Stage | Acceptance |
|---|---|
| HTTPS failures | Classify failures without sensitive error text; reproduce bounded faults; verify readiness distinguishes transport reachability from HTTPS success; retain unexplained historical failures as such. |
| Independent node | Existing WG profile answers handshake; a separate explicit lab full-route copy may test actual HTTPS/UDP without modifying the imported original. Confirm distinct exit before full profile/failure matrix. |
| Networks | Existing W11 link/IP/S1/VM-suspend evidence retained. Verify all available adapters and isolated network variants; physical Wi-Fi/Ethernet needs actual available hardware. |
| Client integration | Trusted components, durable guard ownership, long-lived control lease, service lifecycle and Stop/update/startup integration, IPv6 handling. A lab-only controller is not production integration. |
| Installed candidate | Build and install only in isolated W11 guest; verify lifecycle and cleanup; package only after acceptance. Never replace the host's working connection to test this stage. |

No server credentials, profile text, private keys or browsing history belong in
the release or this ledger. Preserve unrelated working-tree changes. Test profile
policy overrides must be explicit and isolated; default import preserves routes.

## 2026-10-25 results

Completed in the isolated W11 guest:

- A second existing WG profile completed HTTPS and UDP with a distinct public
  exit. The known AWG server does not contain its server public key. Its original
  restricted AllowedIPs were preserved; a separate full-route lab copy was used.
- A -> independent B -> invalid key -> unreachable endpoint -> A passed the
  backend matrix. The core/TUN handoff matrix passed with B's verified exit.
  This is not a remotely powered-off-server test or proof of separate hardware.
- A persistent owner/core channel passed 257 commands, 104448 reply bytes and
  a 65-second health interval without replacing the owner, child or core. HTTPS,
  retained TCP and UDP through the OS TUN remained valid.
- The combined profile/core controller recovered the selected B profile after
  a core crash, canceled repair on Stop without resurrection, restarted A and
  rejected stale B Stop. Stop plus observation took approximately 1.94 seconds;
  this includes the fixture's deliberate 700 ms observation interval.
- Protected component acceptance rejected a wrong catalog digest, writable ACL
  and modified binary. Open leases prevented writes/replacement. All temporary
  Program Files fixture directories were removed. No production service was
  registered by this fixture.

Fixed findings:

- Private profile DNS was incorrectly sent to a public-only UDP acceptance
  client. The control probe now uses its fixed allowed public target; imported
  DNS configuration is preserved.
- HTTPS failures now expose fixed categories, phase and numeric OS error without
  raw sensitive exception text. A controlled reset exposed Winsock 10053; it is
  classified with connection resets. This does not explain every old incident.
  Only the anonymous acceptance GET can retry once on the same candidate epoch;
  certificate errors, wrong exits and application requests are never replayed.
- Persistent channels have bounded individual frames rather than lifetime
  command/byte limits. Legacy finite laboratory leases remain the default.
- Persistent repair budgets now age by monotonic time, instead of expiring
  forever after two lifetime repairs. The core permits two attempts/5 min and
  six/hour. The native owner permits two/category and three total/5 min,
  twelve/hour. Healthy probes do not refill allowances. Local child repair has
  its own two/category, three-total/5 min and twelve/hour cap. Burst exhaustion
  remains fail-closed; a new explicit controller is needed after terminal core
  exhaustion. Windows wall-clock changes do not refill the core allowance.
- A Stop/profile intent arriving during failed-core disposal no longer ends the
  reusable controller as if its repair budget had been exhausted.
- Go unit tests now use a stable executable path. One development-only Windows
  firewall block covers all external inbound addresses for that executable and
  excludes loopback. Two sequential builds/tests passed. No global notification
  or firewall setting was disabled and no inbound LAN/public access was opened.

## Release gates still open

**This branch is not ready for a public native-backend release.**

1. Durable protection covering actual VPN policy, interface changes and IPv6.
   The independently verified fixture firewall covers control addresses only.
2. Production service ownership and protected profile/policy storage, normal
   UI/IPC/status, disconnect, upgrade rollback and startup wiring. Shared classes
   and component trust validation are implemented; ordinary client selection is
   not wired and must not be described as implemented.
3. IPv6 policy and acceptance, actual application coverage and acceptance of the
   installed ordinary client with the above lifecycle.
4. Physical Wi-Fi/Ethernet switching. The host has working Wi-Fi and disconnected
   Ethernet; neither host connectivity nor VPNUS was changed to simulate this.
   Existing VM link/IP/suspend receipts retain their original artifact hashes.
5. Corresponding-source/license packaging and a release built from accepted
   production artifacts. Installing protected test components is not installer
   or release acceptance. No new public version has been published.

Runtime receipts retain failed attempts too. A trust-fixture retry fixed its
ACL restoration code (restore serialized SDDL so .NET persists the original
descriptor); the final acceptance passed. Earlier failed receipts are not
silently removed or counted as passes.
