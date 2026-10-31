# CITA Route — checkpoint, 2026-10-28

This is a working checkpoint, not release acceptance or a frozen source tree.

- Repository branch: `codex/native-awg-installer-preview`; HEAD `099abf3`.
- The working tree contained 71 modified or untracked status entries at the
  initial observation. Those changes are not represented by HEAD; retain them.
- Native preview candidate installer: `runtime/native-preview-candidate-udp/CITA-Route-0.6.9-win-x64-setup.exe`;
  SHA-256 `02BCB08080EC4373F562964192E56647CE6F456DCE597A3CE5EC6145AA95CCB7`.
  This is an unsigned test artifact, not a published release. Hash does not
  prove provenance or that all current uncommitted source is included.
- Native client supports independent profile import; WireSock is not required.
  Native automatic Direct calibration concerns observed HTTPS/TCP; its DPI
  selector remains a lab-only experiment. Do not project legacy WireSock
  features or UDP quality behavior onto native mode.
- Candidate service tests on W11 exercised protected import, control, recovery,
  and cleanup. The exact installer has not completed clean W11 install to
  import, connect, upgrade and uninstall. The framework candidate SCM build
  and the package's single-file agent are different artifacts; their results
  are not interchangeable.
- Current UDP/TCP observations, failure cases and cleanup evidence are in
  `ROUTING-STAGE-NOTES.md`; live Discord voice/share, sustained YouTube and
  a real-provider Zapret comparison remain open.
- Keep VPN configurations, credentials, raw session data and private runtime
  out of commits and distribution. Check actual contents before any packaging.

## Current gates

1. Freeze a source snapshot only when the complete candidate is identified;
   preserve unrelated changes and record exact component hashes and receipts.
2. Validate measurement semantics and independent receivers before using
   synthetic results to change production policy.
3. Investigate transport and complete application/lifecycle acceptance before
   publishing a test release. Historical plans are evidence, not current status.

## Measurement correction

The fixture now separates completion, receiver-confirmed TCP load, and UDP
reply-quality; overall success requires all three. Each of four phases requires
2000 sent packets and at least 95% round-trip replies; this is a laboratory
acceptance threshold, not an estimate or guarantee of Discord quality. The
offline analyzer no longer counts client-sent bytes as receiver-confirmed load.
Unit cases reject a lossy phase, incomplete sends and incomplete length; replay
of a prior high-loss report rejects UDP quality and missing ACK is not accepted.
A 160-second local loopback positive control passed with 8000/8000 replies and
all receiver-confirmed load phases. Live independent receiver tests are still
required; no production routing has changed.

Independent receiver follow-up on the same W11 direct route: the client sent
8000 and received 7920 UDP echoes; receiver logged 7996 received/sent including
readiness packets, no send errors. All TCP loads met receiver-confirmed minimums.
The laboratory 95% reply threshold passed, but maximum missing-reply burst was
26 and max observed receive gap 536 ms: this result is not an acceptance of
voice-call quality. Compare with DE-2's 1261 replies on a sequential run, not a
controlled simultaneous comparison; endpoint/path differences are not causation.
The bounded server process, firewall rules and temporary guest metadata were
verified removed. Raw aggregate: `runtime/vm-lab/second-receiver-counts.json`.

Follow-up DE-2 read-only snapshot showed zero cumulative UDP receive/send buffer
errors and zero qdisc drops in the reported interfaces; no rate/hashlimit rule
was listed directly in the INPUT chain. This is a point-in-time cumulative
snapshot after the test, not packet capture, not a complete firewall/provider
audit, and cannot locate the observed losses. The TCP fixture now distinguishes
EOF, truncated reply, timeout and other network errors at ACK read. No previous
run is retroactively reclassified; a fresh run is needed.

Fresh W11 candidate-to-DE-2 run: 2 Mbps phase was acknowledged at 9.5 MB;
4 and 8 Mbps phases reported `AckReadTimeout`, with 12.65 MB and 13.38 MB
client-written respectively. The receiver logged an ACK for 12.65 MB and a
timeout after 11.88 MB for another connection; concurrent log ordering is not a
connection identifier. The 42-second client deadline includes the 38-second
send, so a deadline-sensitive late ACK remains a plausible test limitation;
not evidence of a production root cause. Client got 1254/8000 UDP echoes,
receiver logged 1256 including readiness and sent all. Candidate service and
temporary rules/metadata were restored/removed. `runtime/vm-lab/de2-ack-debug-counts.json`.

Follow-up: TCP phases now start on independent fixed schedules, stop writes at
the phase deadline and allow a separate 12-second ACK wait. Server log records
bounded route and phase labels; prior buffered/retransmitted traffic can still
overlap a later phase, so this is not isolation proof. Local 160-second
loopback: 8000/8000 replies, all receiver-confirmed loads, zero observed phase
start delays. The first candidate preparation timed out, the repeat passed.
On the candidate-to-DE-2 run, 2 Mbps acknowledged 9.48 MB after 2.834 s;
4 Mbps acknowledged 15.24 MB after 8.929 s (80.2% of intended 19 MB, meeting
the lab's 80% threshold narrowly); 8 Mbps write ended before reaching its target,
with 14.12 MB written and server reporting timeout after 12.74 MB. UDP echo
1248/8000 at client, server counted 1264 incl. readiness. These are sequential
experiments with a synthetic fixture, not live Discord performance; extra ACK
time explains at least one former false timeout, not the remaining capacity
failure. Service and test-owned firewall rules/metadata were restored/removed.
`runtime/vm-lab/de2-phase-debug-counts.json`.

Isolated 8 Mbps TCP upload, no UDP or preceding phases: loopback receiver
acknowledged 38 MB, no writes over 20 ms. W11 candidate VPN to DE-2 sent and
received 12.3 MB, max write blocked 2945 ms, 16 writes exceeded 20 ms, ACK wait
10.354 s; this failed the 80% workload criterion. Sequential W11 direct control
to same receiver: 38 MB acknowledged, max write 1 ms, no slow writes, ACK wait
47 ms. Interface and egress matched their respective paths. Receiver log confirms
both byte counts (`runtime/vm-lab/de2-isolated-tcp-counts.json`); test processes,
rules and temporary metadata cleaned up, guest service restored. This isolates
the issue from concurrent UDP and previous phases, not from the VPN server,
network route, NAT, or native implementation; it is not a Discord test.

Independent-client preparation: official AmneziaWG 3.1.0 x64 MSI downloaded
from the official release, SHA-256 matched the published digest and Windows
reported a valid Authenticode signature. Installed in W11 guest only; manager
service remains installed. The candidate profile was found with full IPv4 and
MTU 1420; the CITA laboratory core uses its own TUN settings, so future direct
comparisons must record both values. Independent tunnel preparation did not
complete: the first attempt stalled due to a GUI-only error path; after redirect
and compatibility fixes, the second reported install failure and cleanup was
not immediately confirmed. A separate final guest check found no AWG tunnel
services, comparison adapters or temporary profile copies. No independent
traffic measurement happened. This is not evidence the profile or the official
client is generally broken; investigation of the failure and robust cleanup
remains open. The test VM has the independent client installed.

Independent-client comparison follow-up: the official client ran the same
private W11 peer.conf as the candidate fixture. Its 8 Mbps isolated upload
to the same temporary DE-2 receiver was 37.92 MB at profile MTU 1420. A separate
run used a test-only profile copy with MTU 1400 (matching the CITA lab TUN MTU):
36.48 MB acknowledged, max write 1158 ms, 9 writes over 20 ms. Immediately
afterward, candidate service over its Cita-NativeLab interface wrote 11.86 MB,
max write 3168 ms, 16 slow writes, and did not receive ACK within 15 s. Server
logged VPN-source ACK sends for 36.48 and 11.86 MB, respectively. The candidate
transport profile itself retained its own MTU 1420; TUN stack and client
architecture also differ. Runs are sequential and not proof of precise root
cause or real Discord quality. Cleanup independently verified no test services,
adapters, profiles, firewall rules or metadata; DE-2 process/rules removed.
The standalone official client remains installed in the test guest.
Server evidence: `runtime/vm-lab/de2-awg-mtu1400-compare-counts.json`.

The user requested stopping further tests until a separate physical machine is
available. The in-progress W11 candidate preflight finished and restored the
original service; temporary egress metadata was removed. No subsequent test
endpoint was started. Host route lookup to DE-2 selected VPNUS, but this alone
does not establish the actual outer-packet path to the candidate VPN endpoint.
Lab proxy and receiver instrumentation changes are unvalidated for a full
network run; retain their tests and do not treat them as performance evidence.
