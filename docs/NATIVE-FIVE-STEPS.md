# Native rollout execution

The five requested steps are one release effort. A check is closed only with a
receipt for its actual implementation; a lab receipt does not certify packaging.

1. **Implemented, bounded preview — adaptive HTTPS routing.** WFP Direct grants
   are scoped to the trusted core image, public IPv4, exact port/protocol and
   physical NIC. Unknown traffic retains VPN while an anonymous TLS/header/body
   probe evaluates Direct. Domain/application/IP decisions are acknowledged before
   new flows use them; existing VPN flows survive publication. Failed TCP dials
   invalidate the affected decision before VPN fallback, without replaying payload.
   Scope isolation, revocation, publication and automatic selection have W11 receipts.
   Calibration has a renewable monotonic limit of 32 probes/minute and continues
   after a network epoch change; negative revalidation withdraws the old decision.
   Observations now rotate through a fixed 128-entry LRU window instead of
   stopping at the first 128 destinations. Retry history follows that window;
   the oldest new-flow decision is retired when the rule budget is full.
   Remaining production gates: obsolete-scope flow-aware collection and DPI/UDP decisions.
   Budget exhaustion falls back to VPN; no general physical-core bypass exists.
2. **Accepted in W11 — first use.** A hash-checked, framework-only native bundle,
   protected unconfigured template, private import and remembered selection work
   in W11. A real missing-service installation, connection and active-route removal
   pass. The download chain verifies trusted URLs, length, checksum and corrupt-file
   cleanup with an isolated HTTP fixture. Actual GitHub native assets are not
   published yet. VMware refused CLI snapshot creation despite matching stored
   credentials; a user-created snapshot allowed the test. Reversion restored the
   exact original agent/config hashes. The user's Snapshot 1 is retained.
3. **Accepted in W11 — install/update/remove.** W11 verifies component replacement,
   retained profile, injected rollback and maintenance exclusion. Setup/updater
   carry the new helpers; the native agent upgrade preserves existing shared ACLs.
   Removal requires confirmed disconnect and matching client/SID before touching
   the service. Fresh native service removal passes. The complete framework package
   and dedicated Inno installer pass their integration fixtures: shortcuts,
   registration, repeated install, interrupted-copy rollback, uninstall and retained
   personal data. MSI is outside this effort.
4. **Accepted VM scenarios — lifecycle.** Actual W11 reboot retains the selected profile,
   avoids an unrequested connection and permits explicit connect afterward.
   Service/core/owner recovery, link interruption, changed IPv4 and DHCP/DNS
   restoration pass in the protected service. An actual DHCP regression was found:
   default-route protection captured DHCP broadcast. Generated plans now exclude
   only 255.255.255.255/32, and component updates migrate old policy/TUN pairs.
   Explicit old-policy migration passes in the isolated-source service build.
   Guest S1 and VMware suspend/resume also pass the TUN TCP/UDP scenario. A TCP
   connection was renewed after S1 while HTTPS/UDP recovered; automatic wake was
   not certified (host-assisted resume). Physical NIC handoff and public
   IPv6 carriage remain separate gates; protected IPv6 is blocked, not transported.
5. **Accepted internal candidate — publication remains gated.** Native ZIP includes notices/licenses and
   refers to a matching source ZIP. The source contains the exact modified modules,
   dependency-source proxy and build recipe. Core/owner/guard were rebuilt offline
   with matching hashes. W11 protocol regression and nine UI states pass. Neither
   source reproducibility nor a component ZIP alone certifies complete product packaging.
   No version bump, GitHub publication or host installation was performed.

## Evidence available in runtime (not committed or publicly distributed)

- `native-policy-auto-022d10fd55594634943c6964d23b95d9.json`: automatic domain
  selection and independent cleanup, matching binaries.
- `native-scm-b2d17e93f73a4e7eadc4959711029a6c.json`: bootstrap/import/rollback,
  service/core/owner recovery, link and IPv4/DHCP restoration. The receipt pins
  the exact final agent artifacts, including DHCP migration and bounded framing.
- `native-boot-recovered.json`: actual reboot, retained profile, explicit reconnect
  and restored original service.
- `native-protocol-regression.json`: native policy, observation, retained binding
  and UDP lifetime protocol tests in W11. A harness exit-code reporting mistake
  was corrected; the repeat reads both actual process exit code and PASS output.
- `native-corresponding-source.zip.receipt.json`: all three native component
  rebuilds match; `publicReleaseReady` remains false.
- `native-fresh-f971fc4b84a440e8bfde064609f4c5a0.json` and
  `native-fresh-restored.json`: real fresh service creation, import/connect,
  active update retaining the profile, removal of current and retired components,
  and restoration of the original protected installation.
- `native-awg-core-network-0f4489e718244bc4842ca12a9622f87a.json`: S1 sleep,
  host-assisted wake and VM suspend/resume; TCP renewal is not socket retention.
- `native-package-ad18cc0e00c14ba4af41797812f28569.json`: final full package and
  dedicated installer acceptance after the implementation changes above.

Preserve the current host connection. Run destructive network scenarios only in
the authorized VM, with owned cleanup and independent recovery. No long soak is
required in this run. The earlier sleep request was completed; it is not a
recurring instruction for this new five-step effort.

## Observation-window follow-up — 26 October

Fixed the lifetime observation ceiling: a bounded LRU window now admits new
destinations after the first 128. Active entries refresh their position; invalid
targets do not displace valid evidence. Evicted observations cannot authorize a
probe until observed again. The retry schedule removes entries outside the current
window and selects never-attempted/oldest-due entries first. Automatic rules retain
their 128-entry limit by withdrawing the oldest decision for new flows only.
Empty snapshots also clear the controller's previous observation list.

Go policy tests and vet, C# scheduling checks, and the W11 observation test pass.
`runtime/native-observation-window.json` records 10,000 inputs with 128 retained
entries. `native-policy-auto-6e9835e3a2344c1bb5cbc51c4821d950.json` confirms actual
automatic selection with the new core/controller. The follow-up policy-session
receipt confirms retained VPN TCP, new Direct selection, explicit blocking and
return to default VPN, with independent cleanup and matching binary hashes.

This closes observation rotation, not Direct-permission collection. Existing
permissions remain for admitted flows, up to the existing 256-scope cap. Safe
collection needs per-scope live/in-flight flow accounting and an acknowledged
retirement barrier before WFP revocation; age alone is insufficient. No source
bundle/package acceptance or public release is inferred for these new binaries.
