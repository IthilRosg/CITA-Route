# Physical test station, 2026-10-28

The Windows 11 physical station is accessed through a separately authenticated
Radmin VPN connection. Preserve this management path during routing tests;
prepare and verify recovery before activating a tunnel. The station runs build
26200 and identifies itself as a customized Windows image, so its results are
not a substitute for a clean Microsoft installation.

The tested installer is the 0.6.9 native preview candidate, SHA-256
`02BCB08080EC4373F562964192E56647CE6F456DCE597A3CE5EC6145AA95CCB7`.
It is not established that this installer contains all current worktree changes.
The source/test snapshot and compact aggregate measurement summaries were
copied to the station and verified by SHA-256; no VPN configurations were
included.

On a station without Windows Desktop Runtime: silent install without runtime
consent stopped with exit 7; with INSTALLRUNTIME=1 it installed successfully
and Desktop Runtime 10.0.13 was present. A silent reinstall of the same version
declined the replacement with exit 7. Uninstall exited 0, removing the app
executable and desktop shortcut; reinstall exited 0. These are unattended
installer observations, not visual UI or VPN-connection acceptance tests.
The 0.6.8 installer, checked against the locally recorded SHA-256, refused a
silent downgrade over 0.6.9 with exit 7; the registered version stayed 0.6.9.
The native service was prepared via its installed scripts (rather than the UI),
and its authenticated local pipe reported `Unconfigured`, `Desired=false`.
The archive hash passed to setup was computed locally from the installed archive;
this checks the script path, not independent package provenance.
An invalid import request was refused and the service remained unconfigured
with routing undesired. This does not establish end-to-end profile import or
recovery behavior. A separate test peer and a verified management recovery path
are still needed before live tunnel tests.

Follow-up: user provided separate SE and DE test peers. Both passed native
profile import with HTTPS and UDP validation and finished disconnected; the DE
profile is now the protected selected profile on the station. Neither test
enabled the system tunnel. Both profiles use IPv4 default routing, MTU 1420,
keepalive 25 seconds and S4 zero; do not infer throughput or streaming quality
from import success. Direct ICMP DF probes to both endpoints returned 3/3 at
1472, 1392 and 1252 bytes of payload, which is not a tunnel PMTU test.

Public AWG-CITA revision 564b028 uses an IPv4-only client template with default
MTU 1420 and keepalive 25. This revision matches the documented DE-2 deployment
revision, but live settings and other server deployments were not verified.

First physical-station system-tunnel smoke on DE: a SYSTEM scheduled recovery
task was armed after a disconnected-state stop dry-run. The autonomous test
measured 20 DNS/UDP requests to 1.1.1.1 directly and 20 while the native tunnel
reported Connected, then explicitly stopped it. Individual packet paths were
not independently verified. Direct returned 20/20, median
12.6 ms, range 10.6–20.4 ms; VPN returned 20/20, median 95.6 ms, range
55.9–142.6 ms. Radmin SSH was unreachable during the tunnel and returned after
the test; the recovery task also reported disconnected, but this was not a
forced-failure exercise of the timer.

A second independent DE smoke reported direct 20/20, median 12.6 ms, range
6.4–14.2 ms; VPN 11/20, median 100.6 ms, range 58.1–165.1 ms, 750 ms timeout.
Explicit cleanup was confirmed and temporary scheduled tasks removed. The
Windows route lookup for the management peer reported the Radmin interface
before and during the tunnel, which does not prove the overlay transport was
unaffected. The scripted Radmin process-name check was invalid and is not
evidence of process status. These small DNS/UDP samples are not direct Discord
or media tests, do not measure jitter under load, and cannot distinguish server,
provider, tunnel, WFP or application-layer effects. Need independent receiver,
repeated matched paths and a verified management recovery path.

Scoped control follow-up: on the RM only, a temporary profile was derived locally
from the installed DE profile with AllowedIPs=1.1.1.1/32. Three independent SSH
sessions returned while service status was Connected/Desired=true/CoreState=Active.
The control task held the connection for 30 seconds and confirmed cleanup.
Original DE profile was restored locally and temporary tasks removed. This proves
management coexistence for the scoped experiment; full-route Radmin compatibility
is still unresolved. Installed Direct policy includes private LAN and Tailscale
ranges but no Radmin overlay range; do not globally bypass public 26/8 without
validated interface and transport ownership.

Repeated DNS probes also lost replies in the direct control (17/20), making DNS
rate limiting/resolver behavior a confounder. Prior DNS loss figures must not be
treated as measured voice-channel loss. Use a controlled echo receiver next.

Main-PC Discord viewing observation at 22:37 MSK: active WireSock profile CITA-LAN;
10-second Wi-Fi traffic approximately 6.25 Mbps RX/1.79 Mbps TX, no new RX errors
or discarded packets. Service logs had no matched error records. Existing WebRTC
log stopped at 22:18 MSK; receiver RTT records across that historical file ranged
5–2790 ms, median 55 ms, potentially multiple receivers. These are not current
stream statistics or proof of a VPN cause. Aggregate only saved in runtime;
no audio, screen content or packet capture collected.

Controlled receiver follow-up (28 October evening): user explicitly authorized
SE test-key transfer to RM and temporary restricted DE-2 receiver. SE imported;
RM currently holds temporary SE-TEST-Receiver scope (1.1.1.1/32 and DE-2/32),
disconnected, with original SE profile preserved in protected native folder.
Seven fixture tests passed on RM, including real loopback UDP with deliberate
omissions. Full comparative measurement did not occur: initial PowerShell
Measure function conflicted with an alias; renamed and stale output deletion
added. UDP barrier later failed and produced no report. This is an unperformed
measurement, not measured loss. Exit receipts added for the next run.

IP-service egress differed from one TCP SYN source observed at DE-2; fixed
receiver whitelist to that observed source. One authenticated TCP smoke then
confirmed 262144 bytes written and acknowledged. UDP baseline still not proven;
its source must be established independently of TCP. A bounded UDP-header
capture did not yet yield a usable observation. SO_REUSEADDR added for reliable
receiver restart; syntax check passed. Receiver terminated, test-tag firewall
rules verified absent (0), RM task cleanup confirmed and receiver token removed.
No full-route Radmin fix, MTU recommendation or voice-quality acceptance follows.
DE-2 SSH used existing id_ed25519; host key accepted on first use in dedicated
known_hosts, not independently verified through hosting console.

Controlled SE-to-DE follow-up: RM UDP socket accepted three synthetic 3-byte
sends; a common DE capture window recorded two TCP headers but no UDP headers.
This establishes no direct UDP baseline and does not locate its failure.
With SE connected, authenticated 224-byte echo passed 20/20, receiver independently
counted 20 received/sent from expected VPN source, send errors zero, cleanup true.

A completed 160-second SE-only fixture then sent 8000 and received 1257 echoes;
p95 RTT 174.111 ms, p99 561.224 ms, max 635.562 ms, maximum receive gap
1217.167 ms, maximum consecutive missing 54. Send failures false, maximum pacing
delay 12.702 ms, delayed sends >20 ms zero. TCP upload 2 Mbps acknowledged
9495000 bytes; 4 Mbps failed its payload phase (receiver EOF at 12070000),
8 Mbps acknowledged 13520000 bytes, below the required load. Measurement complete
but load/UDP acceptance failed (exit 9); no direct performance comparison exists.
Receiver cumulative UDP received/sent 1284 includes prior short run and barriers;
zero send errors. This suggests a major issue before receiver application counts,
not a localized one-way-loss proof. Socket sends do not prove wire transmission.

Results: runtime/station-controlled-compare.json and
runtime/station-controlled-server-counts.json. Receiver stopped, tag rules 0,
marker absent; RM tasks/token removed, original SE profile restored disconnected.
Next: rate-stepped UDP-only test, then confirmed low TCP load, correlated socket,
interface and receiver/kernel counters before changing MTU or production policy.

User agreed to prioritize matched WireSock/CITA control before more CITA-only
experiments. Installed official WireSock Secure Connect 3.6.1.1 on RM only;
installer SHA-256 matched 734F3090BA095A698F7293FF90EBD6DC64FCD646277011DFA3648AC5DBDF5D37
and Authenticode Valid. Installer log exit 0, CLI status disconnected. Imported
WireSock-SE-Control from RM's existing SE peer, narrowed to the same two test
destinations 1.1.1.1/32 and DE-2/32; temporary plaintext copy removed. No WireSock
VPN test started yet. Radmin transfer/SSH had intermittent resets/timeouts while
CITA routing was disconnected; record these as baseline management instability.
Next gate: independent WireSock disconnect watchdog, UDP-only matched test,
then CITA and WireSock repeat with identical receiver and confirmed path.

Matched A–B–A completed across Moscow midnight 28/29 October: same RM, separate
SE peer, restricted test destination routes, one receiver, same UDP-only executable
(60 s, 50 packets/s, 224 bytes; no TCP load). WireSock first received 2991/3000,
max missing burst 1, p95 77.780 ms, max RTT 95.380 ms, max gap 45.688 ms.
CITA received 477/3000, max burst 43, p95 76.434 ms among received replies,
max RTT 90.464 ms, max gap 885.859 ms. WireSock repeat received 2891/3000,
max burst 6, p95 1017.734 ms, max RTT 1252.780 ms, max gap 163.454 ms.
All measured sends complete, no send errors, duplicates/reorder 0; cleanup true.
Pacing maximum 1.691/22.678/14.079 ms respectively. No inference of low latency
may be made from CITA's received-only RTT while most requests are missing.

Receiver cumulative counts 6408 received/sent, no send errors, expected VPN
source. No per-phase server deltas were captured; do not claim exact one-way loss.
Evidence supports prioritizing CITA's native UDP path, while WireSock's second
latency spike shows changing path conditions and prevents single-factor proof.
Native and WireSock can differ in effective MTU/implementation; installed candidate
does not establish inclusion of all current worktree source changes.
Runtime results wiresock-control-first.json, cita-control-matched.json,
wiresock-control-result.json; receiver rules verified 0, marker absent, RM
original SE restored disconnected, temporary tasks/token removed. Historical
wrapper success=true was completion-only; renamed measurementComplete locally.

Before CITA routing, independent HTTPS requests on the station timed out after
8 seconds for YouTube and Discord; DeepL responded HTTP 451. A Yandex request
reached a captcha page. These individual requests do not establish a general
classification of the services or the cause of each failure.
