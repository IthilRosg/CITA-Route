# Native transport: five-stage validation

Historical checkpoint retained with its artifact-specific evidence. The
2026-10-25 follow-up is in [NATIVE-RELEASE-FINALIZATION.md](../../docs/NATIVE-RELEASE-FINALIZATION.md):
independent-egress matrices, persistent channels, shared profile/core recovery,
rolling budgets and protected-component checks have now passed. Production
service integration, full-policy/IPv6 protection and installed-client acceptance
remain open. A new development-only firewall rule blocks external inbound
traffic to the stable unit-test executable; host routing and VPNUS are unchanged.

This checkpoint covers an **isolated experimental backend** in CITA TEST W11.
It is not a release, an installed service watchdog, or a claim that every game,
provider, physical interface and IPv6 path has been validated. Host VPNUS,
production CITA and host networking remain unchanged.

## Changes and evidence

1. **Startup and DNS.** `StartupLab` creates six fresh owners, each with three
   concurrent IP HTTPS, hostname HTTPS and UDP lifecycle samples. Failed probes
   and mismatched exits are retained and counted. Earlier intermittent failures
   were real observations; a later green run does not explain all of them.
   DNS diagnostics now distinguish timeout, stale ID, malformed/truncated reply,
   RCODE, wrong question and absent public answers. `exchangeDNS` permits only
   one retransmission of an idempotent DNS query, using the same ID and original
   total deadline; it ignores at most 32 stale responses. It does not replay
   application traffic. Tests inject first-packet loss, stale replies, malformed
   replies and sustained silence. The live network run captured a query/reply
   on the server followed by a successful retry through a new internal UDP
   association: return-path loss/association retirement is possible, but the
   precise responsible hop is not established.

2. **Different profiles and keys; independent nodes still required.** Two owned
   profiles have distinct endpoint hosts and server public keys, but subsequent
   read-only inventory established that both keys belong to interfaces on the
   same known server, with the same public exit. This validates profile/protocol
   handoff, not isolation from failure of an independent server. Hostname endpoints are resolved into private,
   pinned lab copies; original files are untouched. AWG 3.1's base64
   `HeaderProtectionKey` is now validated as exactly 32 bytes and mapped into
   the pinned engine's `header_protection_key` field. Unknown fields still fail
   closed. Backend tests exercise A → B → wrong server key → unreachable
   documentation endpoint → restored A. Live TUN tests also switch A → B under
   one core, with trusted controller-supplied per-profile expected egress hashes,
   stale-command rejection, failed candidate quarantine and Stop races. The
   expected egress is not accepted from imported configuration.
   All other downloaded profiles were checked by public-key fingerprints;
   `WG.conf` is the only key absent from the known node. Its 21-prefix IPv4
   policy excludes the fixed HTTPS control address. It is rejected at the lab's
   configuration gate; no conclusion about that server's reachability is drawn.
   A separate bounded handshake-only diagnostic subsequently verified its
   cryptographic peer in **207 ms**, preserving every imported route and sending
   no application traffic. That establishes a responding peer, not its public
   egress or full-TUN handoff suitability.
   Split IPv4 AllowedIPs are now preserved exactly (at most 64 distinct prefixes),
   with tests preventing widening to 0/0. A suitable independent-node profile
   remains necessary to close the physical-server isolation gate.

3. **Network transitions.** The W11 fixture disables only its identified
   virtual physical NIC briefly, with independent re-enable protection; changes
   its DHCP source to the previously inspected unused same-subnet IPv4 address
   with independent rollback; and suspends/resumes VMware. HTTPS/TCP and the same
   OS UDP socket are checked across transitions. Routes/adapters must return to
   their original topology. A guest OS S1 sleep experiment is separate from
   VMware suspend. VMware converts it into VM suspension, requiring host-assisted
   resume; guest timer self-wake is not established. Availability after sleep
   and retention of a particular TCP connection are reported separately, since
   a remote service can retire an idle connection. Physical Wi-Fi/Ethernet,
   a different provider/subnet, S3/modern standby and IPv6 remain unverified.

4. **Whole-core failure.** `agent/NativeCoreRecovery.cs` owns recovery through
   the same shared binding adapter. An independent VM firewall rule blocks only
   test destinations through the physical NIC; it is armed before core startup
   and survives core death. The fixture kills a core, suspends all its threads,
   tests bounded automatic reconstruction with the same AWG backend, and verifies
   protected HTTPS/UDP closure before repair. Two repairs share an owner-lifetime
   budget; the third fault remains closed. Unavailable guard verification closes
   the native output without removing the live TUN or automatically reactivating.
   This does not prove safety if the actual guard is removed simultaneously with
   core death. The finite lab core command lease expires explicitly rather than
   being misclassified as a process crash. No production registration is added.

5. **Concurrent workload.** A pinned Go QUIC fixture uses short-lived mutual TLS
   and runs on the owned server for at most four minutes. Temporary input rules
   permit only the approved VM peer on the AWG interface. The additional TUN
   fixture uses a different VPN endpoint to prevent routing the outer endpoint
   back into its own TUN. A separately approved experiment restricted the public
   interface to the second profile's verified exit IP, but no packets matched:
   the profile actually entered another AWG interface of the same host. The
   accepted run selects that interface by its server public-key fingerprint and
   exact private peer, with no public input permission. A separate server cleanup helper removes exact owned rules and
   fixture files after 250 seconds even if the host controller disappears.
   Normal cleanup also verifies removal. Concurrent TLS download (8 MiB), paced
   TLS stream (2 MiB), QUIC stream (4 MiB) and 500 QUIC datagrams at 50/s measure
   hashes, loss and latency. These are real TLS/QUIC protocols and synthetic
   video/game/voice traffic, not commercial applications or WebRTC calls.
   `Run-W11Workload.ps1 -ThroughCore` uses ordinary OS TCP/UDP sockets through
   scoped TUN routes; its artifact and cleanup acceptance is separate from the
   initial authenticated SOCKS workload.

The accepted full-TUN workload transferred all three byte streams with correct
SHA-256 values and received 498/500 datagram echoes (0.4% loss), p50 46.246 ms,
p95 62.766 ms, maximum 151.570 ms. Local UDP budget drops and recovery operations
were zero. The core, native transport and measurement processes together peaked
at 113,651,712 working-set bytes and 242,233,344 private bytes; CPU accumulation
was 2.421875 seconds over 15.401 seconds on a two-vCPU guest. These are short
fixture measurements including measurement overhead, not production-client-only
memory figures or a long-duration stability result.

## UDP capacity finding

The original 1024 packets/s shared-direction association bucket dropped 447
packets during a mixed QUIC run, with 493/500 datagram echoes delivered. The
association allowance is now 4096 packets/s with a 256-packet burst, plus a
child-wide 8192 packets/s / 512-packet cap across associations. Invalid frames,
public IPv4 target restrictions, source pinning, flow count and payload limits
remain enforced. A repeat delivered 497/500 echoes (0.6% loss), with zero local
budget drops. That single sample is evidence of improvement, not zero-loss or
99.9% availability proof. Further capacity work needs per-flow scheduling and
controlled bandwidth/latency shaping rather than unbounded rate increases.

## Receipts and execution

Unique sanitized JSON receipts are under `runtime/native-awg-{startup,servers,
core-two-servers,core-network,core-safety,workload}-<run>.json`, with raw guest
receipts under `runtime/vm-lab/w11/`. Each accepted runner checks executable
hashes, child cleanup and topology restoration. Failed receipts are preserved.
Private test certificates/configs remain outside version control and are never
printed. `VALIDATION-RESULTS.json` records selected evidence without treating
old successful binaries as a test of newer changes.

Build native tests with `Build.ps1`; build shared agent fixtures with
`../native-agent-lab/Build.ps1`; build QUIC fixtures with
`../native-workload-lab/Build.ps1`. Guest-specific scripts enforce the W11 UUID.
Do not run them against a user's normal workstation.

Before normal-client selection, persistent trusted guard ownership, production
service/Stop/update coordination, rolling recovery policy, renewed command
leases, IPv6 policy, wider hardware/network coverage and real application tests
remain required. No public release is inferred from this checkpoint.

Final cleanup independently confirmed zero native fixture processes, zero
temporary guard rules and no lab TUN in the guest, and zero workload rules,
processes or directories on the server. Unstarted failed fixture images were
removed only after exact run/ownership/content checks. Private source discovery
and test certificate files were removed; approved private peer fixtures remain
available for future tests. No host network or production installation changed.

## Fixture recovery finding

A host-side resume request issued while VMware was still saving guest sleep
state created a failed `vmware-vmx` launcher holding a configuration pipe. The
failed launch was identified by its exact VM path, timestamp and explicit
exclusive-lock error; only that process was stopped, with saved state retained.
The fixture now waits for the same VM's fresh `VMX exit (0)` record before
resuming. This is a VMware controller race, not evidence of a native routing
engine crash. Earlier long-sleep runs exhausted the HTTPS measurement deadline
and are retained as failures; they do not establish uninterrupted TCP retention.
