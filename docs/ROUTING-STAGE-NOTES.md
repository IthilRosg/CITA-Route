# Standalone routing: stage notes

## Stage 1 — probe evidence (in progress)

- The scoped probe now emits fixed, non-sensitive categories for dial, TLS,
  HTTP write/header/body failures, server refusals and success. It does not
  resolve DNS: it uses the IP already observed in a connection, so DNS remains
  unverified by this probe.
- The controller rejects contradictory or unknown categories and keeps older
  cores compatible as `legacy-unclassified`. A positive result still has the
  same routing meaning as before; failure categories are not yet used to
  select DPI.
- Completed so far: experimental Go policy tests and a Windows 11 guarded TUN
  run. Direct and DPI success categories were acknowledged by the controller;
  retained VPN/DPI connections, refusal of unobserved apps and independent
  cleanup also passed. The Direct failure in the selector remains simulated.
  Result retention across network generations, DNS-specific evidence and the
  remaining failure injection tests are pending.
- The freshly built agent's self-test passed in Windows 11, including legacy,
  certificate-error, contradictory, unknown and malformed result cases.
- The automatic Direct probe now rechecks process identity, core state and the
  selected network signature after a successful probe before using its result.
  The signature includes addresses, gateways, DNS servers, interface status and
  IPv4 interface index. A simulated stale result was refused in a guarded VM;
  VPN remained usable and the temporary permission was retired. A second run
  cancelled immediately after a successful probe and confirmed the temporary
  permission was retired without publishing a rule.
- This is a sampled check, not an atomic lock on Windows network changes. A
  change after the sample and before publication, or changes that do not alter
  this signature, still need explicit event/generation handling and fault tests.
  A guarded VM test simulated a change just before and just after policy
  acknowledgement, as well as a failing check after acknowledgement: automatic
  rules were withdrawn and subsequent test connections used VPN. This does not
  rule out a transient flow between publication and rollback.
- Cancellation immediately after policy acknowledgement now also withdraws
  automatic rules with an independent cleanup deadline. A Windows 11 guarded
  run confirmed rollback to VPN, temporary permission retirement and fixture
  cleanup. This does not claim atomic avoidance of a flow before rollback.
- Address and availability events now increment a generation and wake the
  controller, so a rapid change back to the same sampled signature still
  invalidates in-flight evidence. DNS/gateway changes are also compared by
  signature. The guarded automatic DPI scenario passed with this watcher; real
  adapter changes and every notification-loss case remain to be tested.
- Two separate VPN test requests failed despite an active core and a healthy
  aggregate transport state; both runs cleaned up their guard. A later complete
  run passed. The lab records sanitized HTTP/DNS evidence on future failures;
  the root cause of those earlier failures remains unproven.
- A VM DNS-fallback run with the first profile resolver unreachable completed
  32 requests across 16 domains: 31 received headers; one independent DoH round
  timed out. A separate all-DNS-outage fixture unexpectedly resolved its test
  name while the VPN control remained healthy, even after flushing the guest
  cache. The guest has a separate private system DNS, so this test did not
  isolate the profile resolver. The revised fixture measures OS DNS separately,
  disables the earlier Direct decision and verifies a new fake-IP attempt at
  the VPN owner. With the profile resolver unreachable, that attempt did not
  connect, the guest system resolver still answered and the known-address VPN
  control stayed operational. The VM run passed with independent cleanup.
- A three-page browser audit completed with the same core and independent
  cleanup, but it is not a full page pass: windows64 loaded with two aborted
  analytics fetches, srnky redirected to a page with two font CORS failures,
  and the ryuugames audit timed out before producing a DOM result. These are
  observations, not proof that all three sites or their embedded content work.
- The current non-DPI core and all native components were rebuilt from a new
  corresponding source archive with an offline module proxy. The framework
  package builds, its agent self-test passed in the Windows 11 guest, and
  negative packaging checks rejected forged component receipts. The laboratory
  DPI scheduler remains gated and is not a production feature of that package.
- After that package, TCP-write segmentation and the HTTPS response-budget fix
  were added. The standard core was rebuilt and all three native components
  reproduced from a refreshed offline source archive. A new framework package
  was built, and a guarded Windows 11 run confirmed automatic Direct and cleanup.
  Release acceptance for that updated package remains incomplete.

## Later stages

Stage 2 is in progress: a laboratory-only scheduler can attempt DPI after a
fresh classified Direct network failure. Certificate failures, HTTP refusals,
unclassified legacy results and stale/cancelled probes are not DPI evidence.
The ordinary service still rejects DPI. A guarded Windows 11 run exercised the
full automatic scheduler: the initial request used VPN, a simulated Direct
timeout led to a real, certificate-validated DPI probe, and a subsequent request
used the selected route. Its first attempt did not reach routing because VPN
connection validation timed out. The Direct failure was simulated, so the test
cannot establish effectiveness against a real provider block. The latest
aggregate path-probe category is available without a domain or IP in the
in-memory session snapshot; it is not a DNS result.

A Windows 11 live lab run compared anonymous HTTPS root probes for DeepL,
Ryuugames, srnky and Discord against Direct and DPI independently. Three
targets returned usable responses on both paths; Ryuugames returned HTTP 403
on the VPN seed and was refused by both probes. The TLS and certificate
checks remained enabled, and cleanup passed. Because Direct already worked
for the other targets, this is not proof of DPI bypassing a live block; VPN
host routing, exit IP and per-site anti-bot policies may affect the outcome.
Another Windows 11 HTTPS root-probe run compared four additional domains:
YouTube returned HTTP 200 through VPN but Direct and fragmented DPI both failed
while reading headers; Wikipedia returned 200 through VPN while both other
paths timed out in TLS; Instagram returned 200 through VPN while both probes
reported an empty body; Telegram was reachable by all three paths. A root
request is not a complete app test, and an empty body is not proof of blocking.
The lab scheduler was expanded to consider HTTP I/O errors for an independently
verified DPI probe, while continuing to exclude certificate errors, HTTP refusals
and empty bodies. None of these outcomes demonstrates a successful DPI bypass.

The lab core now has an alternative SNI-aware TCP-write segmentation strategy;
it leaves the TLS byte stream intact and is selected only in the experimental
fixture. A Go test verified that it emits multiple writes and preserves all TLS
bytes. An isolated Windows 11 comparison of YouTube, Instagram, Telegram and
Wikipedia completed with cleanup: YouTube and Instagram failed identically for
Direct and this strategy, while Telegram and Wikipedia succeeded on both.
Different destinations, timing, DNS and routing may explain differences across
runs, and separate writes do not by themselves prove separate network packets.
This does not establish DPI bypass or equivalence to GoodbyeDPI's packet-level
WinDivert techniques; a packet capture and controlled GoodbyeDPI comparison are
still required.

GoodbyeDPI comparison: upstream 0.2.3rc3-2 was tested only inside W11 after
checking its archive hash, driver signature and absence of pre-existing
WinDivert drivers. Three anonymous HTTPS HEAD requests were repeated using the
same selected IPv4 address per hostname, first without GoodbyeDPI and then
with its -6 and -9 modes. YouTube, Wikipedia and Telegram returned HTTP 200 in
all modes, including Direct. Thus this test confirms fixture operation and
cleanup, but does not establish any bypass advantage. The guest's observed
egress differed from the separately tested VPN server; the host's VPN and
physical route were not controlled, so no inference about the ISP route or
about blocked-site access can be made. No driver or GoodbyeDPI was installed on
the host. A stricter comparison requires a known-blocked destination and the
same address, resolver, network path, request and response criteria for each
strategy. Upstream: https://github.com/ValdikSS/GoodbyeDPI/releases/tag/0.2.3rc3

Follow-up on the earlier YouTube discrepancy: the same pinned address returned
HTTP 200 to curl HEAD and GET, but the native-equivalent GET stopped at exactly
12,288 bytes while reading headers. Different HTTP headers, TLS 1.2 and ALPN did
not change that. Curl reported roughly 13 KiB of response headers; raising the
bounded response budget to 65,536 bytes made the equivalent GET pass in Direct,
GoodbyeDPI -6 and -9. The native core now uses this bounded limit, with tests
for valid headers above 12 KiB and rejection above 64 KiB. A guarded VM run of
the experimental core then confirmed YouTube Direct and DPI root probes both
reachable; Instagram remained an empty-body result. This fixes a false negative
in the probe, not a proven DPI bypass or a guarantee of full site availability.

Real-block validation, recovery, installer lifecycle and release acceptance
remain open. Do not promote laboratory DPI to the production scheduler on the
strength of a single HTTPS success.

## Recovery checkpoint

An older Windows 11 recovery fixture failed before starting because it required
a WireSock CLI even for the standalone client. The fixture now checks that
WireSock is disconnected only when its CLI exists; absence is accepted. The
failed attempt removed its test configuration and guard and left no test
processes; no recovery result was produced by that attempt.
The corrected fixture passed with WireSock absent: an independent guard blocked
protected HTTPS/UDP during recovery; core crash recovered in 6.2 s and hang in
10.5 s while retaining the backend. Repair-budget exhaustion and an unavailable
guard verifier failed closed. Cleanup and topology checks passed. These are
single VM measurements, not a guarantee of seamless existing connections.
The isolated UDP socket recovery test also passed its automatic and manual
control cases: the automatic case reported one successful repair in 8.8 s,
with one existing TCP connection and the UDP fixture retained. Its HTTP request
was delayed by 7.6 s; retaining a socket therefore did not mean a gap-free call.

## Performance checkpoint

The first short performance run failed in an HTTPS/UDP control; a second
recorded failure before any UDP exchange, without DNS error evidence. The
performance fixture's persistent CPU calculation initially assumed a fixed
number of processes. Inspection showed one core and two transport processes in
the current VM, so the corrected calculation requires both roles and stable
identities, while permitting more than one process per role. A later run stopped earlier: its
independent Direct probe timed out waiting for HTTPS headers and the scheduler
kept VPN. Temporary guard cleanup succeeded. The interrupted runs cannot be
used as performance measurements; a subsequent 27.5-second run passed, with
about 3.0% machine-normalized native job CPU at idle and 235.8 MiB peak job
commit, excluding desktop UI and service host. Its target decision took 1.89 s;
these are single-VM observations, not an average or a cold-start benchmark.
The persistent-process CPU field remained unavailable because an intermediate
fixture incorrectly assumed exactly one transport process; that defect was
corrected after the run. A fresh 27-second Windows 11 run passed and reported
0.19% idle CPU for persistent core/transport processes, 2.32% for the whole
native job including helpers, 236.4 MiB peak job commit and 1.83 seconds from
observation to automatic Direct rule publication. These single-VM figures exclude
the desktop and service host; they do not establish long-term resource use.
This is not proof of a provider block.

The first full SCM service run reached component bootstrap, profile import,
connection and transport health but its DNS counter assertion failed. The
guest service was restored and the temporary component directory removed. The
fixture had asked the independent OS resolver to resolve the name while
expecting a VPN fake-IP counter to increase. The service DNS client now uses
an explicit fake-IP test destination, and the runner checks the laboratory
controller hash too; a repeat is needed before treating the suite as passed.
The corrected full SCM run passed: protected bootstrap and profile import,
VPN fake-IP DNS, core and owner recovery, interrupted link and changed IPv4
address, 12-second resource sampling and manual-disconnect persistence. The
original VM service was restored, the candidate removed, and binary hashes
matched. It does not replace a clean installer acceptance or a long soak.
The bootstrap/DNS/service recovery suite was repeated after the policy-cancel
fix with the newly built service and also passed; the original VM service was
restored and candidate files removed.
An additional isolated core-network run passed link interruption, IPv4 change,
guest S1 sleep/wake and VMware suspend/resume, with topology and guard cleanup
confirmed. The existing TCP fixture survived the link and IPv4 transitions and
VM suspend, but did not survive guest S1 sleep; recovery after sleep worked.
This test used a virtual NIC and does not establish physical Wi-Fi/Ethernet or
S3 sleep behavior. It used the standard backend lab controller rather than the
experimental DPI scheduler.

## Installer checkpoint

The separately identified 0.6.9 test installer was refused on W11 because the
existing protected native service belongs to a different installation. This is
expected ownership protection; no files were replaced. An attempted test-only
environment path isolation did not change that behavior and was removed. A
clean Windows 11 installer matrix still requires a guest without another native
configuration, or a controlled reversible fixture that restores it and its data.
The isolated test installer subsequently passed the full Verify-Installer matrix
on CITA TEST 2 (Windows 10 build 14393, Desktop Runtime 10.0.13): initial
installation, version comparisons, interruption rollback, shortcut/registration
checks, uninstall and personal-data preservation. It was an isolated test AppId,
not a Windows 11 clean install or an actual historical-version upgrade.
The full Windows 10 fixture was repeated successfully after rebuilding the
package with the policy-cancel fix; its test installer hash is recorded in
`runtime/native-installer-test2-latest.json`.
The standalone installation transaction tests passed (commit, rollback,
interruption and preservation of user data). The maintenance-guard test had an
outdated equal-version fixture which masked its other scenarios; it now checks
equal-version refusal independently and uses an older installed version for
the remaining checks. The corrected guard suite passed without network changes.
# YouTube and Discord comparison (2026-10-27)

- Follow-up measurement fixture: per 10-second sample, receive gaps now use
  arrival-time order (not sequence order), with p99 RTT and maximum consecutive
  missing echoes recorded. Missing echoes mean missing by end of the overall
  observation, not proof of one-way packet loss. Gaps and bursts crossing a
  sample boundary are not included in either sample's maximum. A reordered
  arrivals / missing echoes unit test passed. Historical reports are unchanged;
  no new VM comparison or candidate service validation is implied.
- Later repeat on W11 build 26200: the framework-dependent candidate service
  passed bootstrap, profile import and preservation, DNS, recovery, disconnect
  and restoration. The lab artifacts' hashes matched; the previous VM service
  was restored and the temporary candidate removed. This was not a clean
  installer test and does not identify the exact packaged single-file binary.
  Report: `runtime/native-scm-09a730069fde4003be3175188f6b721e.json`.
- Fixture additionally reports full-run loss-burst/gap metrics across sample
  boundaries and local send scheduling delay; these are diagnostics, not proof
  of one-way loss or real Discord performance.
- Updated fixture completed the 160-second local loopback run: 8000/8000 UDP
  echoes, all three uploads met the receiver-confirmed load criterion, maximum
  send scheduling delay 5.954 ms. This checks the fixture only. An initial
  sandboxed loopback attempt failed its UDP readiness barrier; the successful
  repeat used an unsandboxed loopback process, without changing routes.
- A follow-up network/performance/DNS run omitted `-Bootstrap` and failed its
  DNS check with four fake-IP refusals, while transport health remained good.
  The seed fixture deliberately sets `dns_mode=disabled`; this configuration is
  incompatible with the native DNS assertion. Both W11 runners now reject
  `-Dns` without `-Bootstrap` before any service changes. That run is invalid
  evidence for a production DNS defect; restoration and cleanup succeeded.
- The corrected bootstrap/network/DNS/performance attempt passed DNS and link
  interruption but failed the HTTPS OS proof after the temporary IPv4 change.
  Transport health simultaneously reported Suspect, HTTPS=false, UDP=false,
  with one socket repair. The original service and guest networking were
  restored and the candidate removed. This is a real unsuccessful acceptance
  observation; it does not by itself identify whether the interruption is due
  to the client, VMware NAT, or timing. Repeat before changing recovery logic.
- Repeated the bootstrap/network/DNS run: HTTPS and DNS after link interruption
  and IPv4 migration passed, component hashes matched, and original service and
  topology were restored. The immediately preceding failure remains a single
  transient observation, not a confirmed fix or proof of uninterrupted calls.
- Candidate voice fixture was added to the isolated SCM runner with an explicit
  TUN-interface and egress check, a per-run firewall rule and delayed cleanup.
  The first attempt used the installed service's egress for an independent
  candidate profile and was rejected; a PowerShell switch/result name collision
  was fixed. After capturing the candidate's own verified HTTPS egress, a second
  attempt correctly refused the receiver because it was the candidate VPN's
  endpoint, routed via Ethernet0. No workload was measured. Candidate service
  restoration and firewall cleanup succeeded, and the temporary server and its
  rules and metadata were removed. An independent trusted receiver is needed.
- Follow-up with DE-2 as a distinct receiver: direct and framework candidate
  service runs were sequential on W11. Candidate selected `Cita-NativeLab`,
  matched its egress, received 1261/8000 UDP echoes and had 2 Mbps TCP upload
  acknowledged; 4/8 Mbps failed acknowledgment/load criteria. Direct selected
  `Ethernet0`, received 1262/8000 echoes and met upload load criteria at all
  rates. Very similar periodic missing replies exist even without CITA and at
  baseline; shared endpoint/path or measurement setup must be investigated
  before interpreting UDP as a VPN or Discord defect. TCP results remain a
  separate signal. The candidate run failed overall acceptance; original service,
  temporary VM rules and server process/rules were confirmed cleaned up. These
  are synthetic UDP-echo plus TCP-upload tests, not real Discord media.
- DE-2 SSH host key was initially obtained directly over the network at the
  user's request and pinned temporarily (TOFU); independent host identity was
  not established. No production default known_hosts entry was changed.
- Instrumented DE-2 repeat: direct client sent 8000 UDP probes and received
  1261; the server logged 1263 received and 1263 sent, including readiness
  probes. Candidate client sent 8000 and received 1267; server logged 1271
  received and 1271 sent, including readiness probes; send errors were zero.
  Counters do not include packets dropped before the receiving application or
  distinguish client/network/kernel causes. TCP direct phases received 9.5,
  19 and 38 MB; candidate server logged 9.5 and 13.44 MB with ACK sent, and
  14.26 MB on timeout, while the client did not receive any phase ACK. Thus
  candidate TCP phases do not pass acceptance despite server-side receipts.
  Tests ran sequentially, using UDP echo and TCP upload, not live Discord.
  The test service, exact firewall rules, metadata and candidate service were
  cleaned up, original guest service restored. Raw bounded aggregate counters:
  `runtime/vm-lab/de2-voice-server-counts.json`.

- Windows 11 guest, anonymous isolated Chrome. Direct, official GoodbyeDPI `-6` and `-9` each returned HTTP 200 for the YouTube watch page and Discord app page. Direct also worked in this VM, whose host egress is not controlled, so these results do not demonstrate censorship bypass. No logged-in Discord voice or streaming was tested.
- The first YouTube player sample remained paused at zero seconds in all three modes. Requests reached video CDN hosts, but that is not proof of playback. An improved harness retries muted autoplay after the video element appears.
- Native CITA automatic-session setup and cleanup passed once, but both browser observations exceeded their deadlines. A subsequent attempt failed at connection validation with UDP unverified, despite a matching HTTPS exit, and independently cleaned up its guard. This remains an unresolved test/transport issue; it is not evidence that YouTube or Discord works through CITA.
- Browser test artifacts contain hostnames and request status only, no URLs with query strings or account data. Profiles are temporary. The comparisons must not be conflated with voice-quality tests, long-duration playback, or a controlled blocked network.
- A second run with repeated muted-play attempts produced: Direct paused at 0.2 s; `-6` YouTube timed out after 18 s; `-9` showed 1.87 s of playback and was not paused at sampling. Discord reached `complete` in all three cases. One short run cannot establish reliability or attribute the difference to DPI; driver and temporary firewall rule cleanup were confirmed. The VM browser timeout makes the `-6` result inconclusive.
- Follow-up: a test prerequisite incorrectly required a separate domain to be calibrated Direct, so a legitimate VPN fallback prevented the media test; the media harness no longer requires that unrelated condition. Two CITA runs reached both pages (HTTP 200); YouTube was paused around 0.1–0.25 s. Discord had many `ERR_CERT_VERIFIER_CHANGED` script events on one run and zero on the repeat, so a persistent site failure is not established. Independent cleanup succeeded. UDP validation now records only bounded lifecycle stage, query count and safety-check booleans, to distinguish a failed association from missing replies without recording addresses or credentials.
- The lab self-test had an obsolete empty application identity in schedule fixtures, incompatible with the newer process isolation policy. Fixtures now use a named application and separately verify empty identities are refused. The lab self-test passes.
- Existing 160 s synthetic VPN voice/upload run: 7991/8000 UDP replies, no samples over 250 ms, maximum measured reply gap 75.4 ms. It is not a Discord call, and it does not demonstrate zero loss or success across networks.
- Playback-state follow-up in CITA: player reported buffering (state 3), time changed from 0.014 to 0.223 s, then paused; Discord completed with no recorded failures. Under the same instrument in separate clean browser profiles, Direct and `-6` had an unready buffering player at zero; `-9` timed out. Discord completed in each. These one-off samples are inconclusive for bypass selection; the playback harness must not report a successful page as a working stream. All VM guard/driver cleanup was confirmed.
- The next CITA repeat independently confirmed the new classification: both pages audited, `youtubePlaybackAdvanced=false`, player paused in buffering at 0.174 s. Lab controller self-test and build passed; this is not evidence of the cause or a completed playback fix.

## Test candidate checkpoint (2026-10-27)

- Candidate installer 0.6.9 native preview is framework-dependent and unsigned; no GitHub release or host installation was performed. Experimental DPI automatic selection is not included in the production scheduler.
- Fresh isolated installer matrix passed on Windows 10 after the import retry change. It covers installation, version decisions, rollback, uninstall and data preservation, but is not a clean Windows 11 installer or an upgrade from every historic client version.
- On Windows 11 the candidate service passed profile import, ACL and invalid-profile checks, component upgrade with retained profile, rollback, DNS, core/owner recovery, virtual link and address changes, disconnect persistence and cleanup; source service restored and hashes matched. During sampling the transport health once reported HTTPS degraded with UDP reachable, so it is not proof of uninterrupted service.
- A failed earlier run had HRESULT 0x80070070 (guest disk full); browser test caches were removed within the checked lab-only directories. One separate import failed at UDP validation; exact network cause remains unknown. Import now repeats only an anonymous fixed UDP timeout once in the same unverified owner epoch, with tests for changed epoch, cancellation and malformed replies. This does not retry user traffic or accept unverified UDP.
- Clean installer-to-import-to-connect on Windows 11, actual Discord voice/screen share, controlled real-block DPI comparison and stable YouTube playback remain outstanding acceptance gates.
- The exact single-file agent from the candidate package passed its self-test inside the Windows 11 VM; its copied hash matched the package. This complements, but does not substitute for, end-to-end installer acceptance.
- 2026-10-28: Windows 11 repeated the experimental DPI selection with a simulated direct failure and passed with independent cleanup; this is not a real provider-block test and the experimental selector is not distributed. A total profile-DNS outage stayed protected and known-address HTTPS/UDP still worked; a second run with the first DNS unreachable and a working fallback completed its 16-domain coverage fixture. Domain HTTP responses do not prove video, voice or entire site availability.
- Voice sample audit: the old fixture writes one phase-wide stream byte count into each 10-second row, so summing those rows quadruples the transfer. A bounded analyzer now counts it once and checks at least 80% of the requested 38-second upload. In `voice-final-vpn`, the 4 and 8 Mbps phases achieved only 63% and 54% of their intended load; `voice-final-direct` achieved 42% and 0%. Those phases cannot validate the claimed workload and direct/vpn reports were made on different dates/VMs. The 7991/8000 UDP count remains a recorded result under the load actually delivered, not evidence for 4/8 Mbps streaming or an actual Discord call.
- 2026-10-28: Reworked voice fixture in `tools/voice-quality-lab` waits for each phase result and fails when workload is missing, send failed, server acknowledgement is missing, or server-received upload is below 80% of 38 seconds at its requested rate. Loopback validation received 8000/8000 UDP replies and server acknowledged 9.5, 19 and 38 MB for the 2, 4 and 8 Mbps upload phases. That validation was loopback only and predates the additional echo-token identity check; no VPN/GoodbyeDPI comparison, download test, Discord call or jitter conclusion is implied. Rebuilding and the echo-identity unit test passed after that check was added.
- 2026-10-28: A same-VM comparison used a temporary public test endpoint restricted to the verified distinct direct and VPN egress addresses. Direct and VPN interface selections were observed, UDP sample counts were captured, but **all TCP upload acknowledgements were missing**, so both full test runs failed acceptance and traffic load is unverified. Direct UDP replies by phase: 1999/2000, 1995/2000, 1997/2000, 1998/2000. VPN: 294/2000, 584/2000, 1996/2000, 1995/2000. The early VPN loss is a signal for investigation, not proven Discord loss or a valid comparison under known load. An initial VPN connection attempt failed, the next connected, and both VM firewall cleanup and VPN stop were confirmed. The temporary server was terminated and its own firewall rules confirmed removed. Safe per-phase TCP stage diagnostics were added afterward and compile/unit checks pass; a new server run is required to find the missing acknowledgements.
- Follow-up: server recorded receiving 262144 bytes and sending an ACK while the client saw EOF after TCP half-close. Replacing half-close with explicit framed end markers passed both local and network smoke checks. The repeated direct run passed, receiving 7990/8000 UDP replies, with server-acknowledged uploads of 9.5, 19 and 38 MB. VPN startup first failed `NativeUdpTimeout`; a retry connected and returned 7990/8000 UDP replies. The 2 Mbps upload was acknowledged (9.5 MB), while higher phases were not validated: at the 4 Mbps step the server logged 14.71 MB and an ACK, which the client did not receive; at 8 Mbps the server recorded a timeout and no ACK. Each 40-second interval had its own sample; these are two sequential tests through potentially different network paths, not a Discord call and not a controlled provider-block experiment. VM service was version 0.6.9, but its executable hash did not match the current candidate package. Firewall cleanup and VPN stop were confirmed, the temporary server/rules and marker were removed. Receiver diagnostics now retain the received byte count on exceptions; that diagnostic change was made after this run.
