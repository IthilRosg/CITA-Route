# Standalone routing and failure coverage

## Current behaviour

New observed HTTPS/TCP destinations begin on the protected VPN path. The native
controller independently verifies a direct path for the exact domain, application,
address and port. A confirmed rule applies to new flows only, with bounded
duration and observation budgets. Existing flows keep their original path.
Unverified destinations remain on VPN, including observations without an
identified application. A successful HTTPS probe does not prove
that media, UDP, authentication or all resources of a service work.

The standalone component does **not** currently perform automatic DPI selection;
the service rejects DPI policy requests until a real fragmented path is implemented.
An experimental core now has a TCP-only, explicitly lab-gated TLS record
fragmentation primitive and a separately scoped anonymous probe. Tests cover
fragmented ClientHello, certificate-validated TLS, refusal of an untrusted
certificate, and refusal when the lab gate or operation ownership is absent.
The lab wrapper buffers a split first TLS record within a fixed size budget and
refuses malformed or unfragmentable ClientHello messages. It requires a usable
visible server name; unsupported cases need to remain on VPN.
In a controlled test a mock filter refused an unfragmented ClientHello and
accepted the fragmented one while TLS certificate validation remained enabled.
A separate anonymous TLS-fragmented HTTPS request succeeded from the Windows 11
guest over a real network, without CITA's TUN or guard. These results do not
prove bypass of a real provider block. A separate guest-only integration test
then exercised the lab core through TUN and the guard: it preserved an existing
VPN flow on DPI publication, established a new DPI flow, retained it on rule
withdrawal and retired the permit. An independently scoped DPI probe published
the matching application rule; an unobserved application was refused and the
next request remained on VPN. Test-owned filters were independently cleaned up.
The same fixture also independently confirmed a Direct probe and verified that
a subsequent new connection used its scoped Direct rule before testing DPI.
This was repeated after adding expiry and rule-budget handling to the lab DPI
publisher. One run failed during initial VPN validation with a proxy dial timeout;
two subsequent runs passed, so the timeout remains an intermittent issue rather
than evidence that either individual path always works.
In the VM a test-only selector preferred a confirmed Direct probe, and selected
a confirmed DPI probe after a simulated Direct failure. Two negative results
kept VPN; cancellation and control errors did not advance to another path.
The Direct failure in that case was simulated, so this does not demonstrate
automatic detection of a provider block. The production scheduler does not yet
use this selector, and the lab does not prove behaviour against an actual block. The
experimental core is built separately with `Build-CoreLab.ps1 -PolicyOnly -DpiLab`;
it is not part of the distributed native component bundle.
Do not infer native support from the legacy WireSock controller or its tests.
The generated core enables process lookup; existing protected core configs are
migrated transactionally. If the process is unavailable, automatic direct
calibration is skipped rather than generating a domain-wide rule.
Current counters represent the session's aggregate direct checks, successful
checks, failed checks, errors and active confirmed rules; they are not per-site
history or a measurement of VPN/DPI performance.

## Implementation gates for three-path routing

1. Establish a bounded, independently verifiable TLS-fragmentation path bound to
   the selected uplink, including protected-scope creation and safe retirement.
   No replay of user requests, credentials, POST bodies or established streams.
2. Probe VPN, direct and DPI independently using route-specific DNS and validated
   TLS. Distinguish DNS, certificate, server refusal, timeout and partial-body
   failures; only compare a path when its evidence is sufficient for that use.
3. Record a bounded, local, opt-in decision trace by domain/application/protocol
   and generation with TTL and reason, redacting addresses and private data from
   exports by default. A stale or ambiguous result cannot authorize a route.
4. Publish decisions atomically after guard acknowledgement, retain old scopes
   until admitted flows finish, and preserve VPN fallback without leaking to
   direct when the VPN or the control channel is unavailable.
5. In isolated VMs test DNS poisoning and DNS timeouts, TLS failures and HTTP
   refusals, partial response stalls, packet loss/reordering, adapter changes,
   sleep/resume, clock changes, process crashes, guard loss, cancel/stop races,
   simultaneous traffic and bounded resource use. Include real profile import,
   connectivity and rollback; a synthetic result is not a Discord call.

## Known gaps

The standalone UI has an opt-in, memory-only trace of recent HTTPS observation
and Direct calibration events (24 entries, at most 10 minutes). It does not
expose IP addresses, full executable paths or payloads, and is not included in
diagnostic exports. It is not a complete history of connections or DPI outcomes.
Long-running native recovery, an actual Discord call/screen share and a
complete install-to-import-to-connect test on the latest package remain separate
acceptance gates. A Windows 10 test installer lifecycle and a Windows 11 native
module replacement with an extra simulated overlay adapter passed. A Windows 11
reinstall preserved the lab profile, and a scoped HTTPS request was confirmed
on VPN first and Direct on a subsequent request. An intermittent UDP acceptance
failure and an intermittent control TLS-handshake timeout were seen; their exact
causes are not yet proven. One anonymous UDP acceptance retry is allowed only for
a timeout in the same unverified candidate epoch; it never replays user traffic.
These do not replace a
clean Windows 11 installer acceptance or fault-injected UDP retry validation.
An earlier standalone packaging attempt detected a mismatch between the native
owner binary and the recorded source receipt. The current core and owner were
reconciled with a new source archive, and all three native components were
independently rebuilt from that archive using its offline module proxy. The
source correspondence gate remains mandatory for future packaging. Negative
packaging tests rejected an unverified archive and mismatched hashes for each
of the three components. A freshly packaged agent passed its self-test in the
Windows 11 lab; this is not a complete clean-install acceptance test.
