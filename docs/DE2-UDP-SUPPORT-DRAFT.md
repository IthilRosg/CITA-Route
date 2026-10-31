# Draft only — not sent

Counter-collection correction: the original cleanup helper read periodic receiver snapshots before shutdown. The DE-WAN receiver final log has now been re-read after termination: 2999 requests/replies, not the preliminary 2905. The client and completed packet-capture counts are unchanged. Historical cleanup snapshots alone must not be treated as final receiver counters.

Latest qualification, 29 October 2026: a native VPN-to-DE-local control returned 3000/3000 packets (50pps, 60s, p95 51.022ms), while a plain SE-to-DE test during the same listener lifecycle returned 313/1000 (50pps, 20s). Exact interval overlap was not recorded. Thus the earlier approximately 8pps observation is not an invariant limit; the latest ordinary flow delivered approximately 16pps. A specific provider rule has not been established.

Additional 29 October, 06:37–06:38 UTC control: native VPN client through DE to our controlled SE responder. DE ens3 captured 3000 outgoing requests and only 471 incoming responses; SE responder accepted/sent 2999 with zero source/auth/length rejects or send errors. The VPN client received all 471 responses seen on DE ens3. Incoming DE timestamps form 58 groups of exactly eight packets plus one group of seven when separated by gaps above 250ms; maximum receive gap 1006.407ms. A separate same-tuple A/B comparison of ordinary UDP and RFC-formatted STUN Binding (96 bytes, 50pps) delivered exactly 160/1000 in each of four phases. These results support an external path/filtering investigation, without identifying a particular provider rule.

Subject: Reproducible UDP loss before VPS ingress, 143.20.37.241

Please help identify a possible network-level UDP policer or filtering rule affecting traffic from 138.124.250.203 to my VPS 143.20.37.241.

During testing on 28 October 2026 around 22:18 UTC, an authenticated UDP echo flow used 224-byte payloads at 50 packets/s. The source VPS external interface captured all 3000 outgoing requests over 60 seconds. The destination VPS external interface captured only 480 incoming requests and all 480 responses. Destination UDP checksum errors, receive-buffer errors, and interface drops did not increase. Local firewall rules explicitly allowed the source IP and test port.

The same symptom reproduces with a plain Linux UDP socket and no VPN/client involved: 1000 sent, 160 replies over 20 seconds. At 4 packets/s, 79 of 80 arrive; at 16 packets/s, 152 of 320 arrive. Both destination UDP ports 49214 and 49215 show the issue. In a reverse test, the source VPS received all 1000 packets from this VPS and sent 1000 replies, but only 160 replies returned to 143.20.37.241.

Could you inspect filtering/mitigation counters for this source/destination pair and clarify any UDP protocol, packet-rate, or byte-rate limits? We have not identified a matching local packet policer. Please do not disable protection globally; a narrowly scoped temporary test exception, if appropriate, would allow a controlled before/after verification.

Additional tests around 22:45 UTC: 96-byte and 1024-byte payloads both delivered 159/1000 replies at 50 pps. Two simultaneous flows with different source ports delivered 160 and 159 replies respectively, so the apparent limit follows each flow rather than total source IP bandwidth. Source qdisc drops, interface txDropped and txErrors deltas were zero during this matrix.

The test listeners and their temporary guest firewall rules have been removed. No production service restart is requested. Additional bounded tests can be coordinated after identifying the relevant upstream rule.

A further independent test from our physical Windows station directly to DE-2 (no CITA/VPN) sent 3000 packets at 50pps and received472 replies. Its actual UDP source address was verified from packet headers before the valid run; DE receiver accepted/sent472, with zero auth/length/source rejects and zero send errors. Thus the same approximate per-flow8pps deficit is reproduced from a source independent of the SE VPS. Note: a failed earlier run with an HTTP/UDP NAT source mismatch was excluded and is not evidence of network loss.

A valid direct Windows station → SE control (allow-list adjusted to its separately verified per-destination NAT source) returned all3000/3000 packets at50pps over60seconds, whereas station→DE returned472/3000 and SE→DE480/3000. SE receiver confirmed3000received/sent with no errors. This control had substantial latency and is not claimed as a clean real-time path, but did not reproduce the~8pps missing-packet limit seen on DE.

Latest checks on 29 October 2026: an authenticated full CITA TUN/core/AWG control terminating locally on SE returned3000/3000 packets at50pps, p95RTT50.28ms. Its path was verified on the AWG interface; it deliberately did not traverse the SE→DE segment. A DE listener with onlyUDP and noTCP listener still returned159/1000 for the plain SE→DE20-second test. We therefore cannot attribute the deficit to CITA or merely to simultaneousTCP/UDP listeners. These are observations, not proof of a particular vendor rule; please inspect the network-side counters/profile for DE-2.
