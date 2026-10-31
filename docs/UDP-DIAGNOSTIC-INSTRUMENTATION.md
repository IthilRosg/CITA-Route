# UDP diagnostic candidate, 29 October 2026

Installed RM owner/core/guard SHA-256 values exactly matched verifiedBinaries
in runtime/native-corresponding-source.zip.receipt.json. Local production Go
source files in tools/native-awg matched the corresponding-source archive except
fault.go, proxy_udp.go and proxy_loss.go, which contain the new instrumentation.
The installed agent is 0.6.9; current uncommitted agent changes are not validated
by this binary match.

Separate diagnostic owner: runtime/native-owner-udp-diagnostic.exe. No installed
component has been replaced. Its current digest must be recorded at deployment.

Added authenticated snapshot counters:

- Outer send attempts/accepted are batch elements; errors count failed calls.
  Partial transmission on a failed batch is unknown. Acceptance is not proof
  of physical transmission. Outer receive counts positive-size datagrams before
  injected loss, including handshake/keepalive and other traffic.
- Proxy input/reject, successful tunnel writes, write errors, tunnel reads,
  reply writes/errors; separate budget, aggregate budget, flow limit, dial,
  deadline, read, reply-size and reply-budget counters.
- Counters contain no keys, destinations, tokens or payload. They are process
  cumulative and read sequentially, not an atomic conservation equation. Record
  child PID/epoch, restart and before/after settled deltas. Normal socket closing
  can increase error counters; cancellation branches are not network losses.

Targeted transport-loss, UDP frame/relay/budget tests passed. The relay ownership
test also validates input=14, rejected=3, writes/replies=10, flow-limit drops=1
after its reply goroutines settle. This covers those exercised branches, not
all diagnostic error branches. No throughput claim follows from unit tests.

Next gate: create a rollback-capable diagnostic package with corresponding source;
install on RM only, expose/read complete authenticated owner snapshots, correlate
proxy/outer counters with controlled receiver and OS counters. Core TUN counters
are still absent. Do not claim instrumentation complete or identify a production
root cause before this run. Compare effective MTU and installed binaries, and
repeat unchanged candidate if the diagnostic build changes behavior.

## First diagnostic RM run

Diagnostic package installed through normal native setup, owner SHA-256
6CFA0DF5BD45E6C854387E2BBBCD608182C56B934FB0B5FE291901EAB8D330F1;
core/guard matched original digests. Two successive sidecar samples from PID
9716 updated timestamps and counters. Saved via child Snapshot handling; file
absence/staleness is diagnostic failure, not zero counts.

UDP-only 60-second run sent 3000, received 993, no send error; max pacing
delay 7.463 ms, max gap 882.465 ms, max missing burst 43. Fresh receiver
counted 993 received/sent, no send errors. Client counter snapshot: relay input
3063, rejected 3, tunnel writes 3060, write errors 0, tunnel reads/replies
1053, reply errors/budget drops 0. Reasons zero except read errors 58.
Outer send attempts/accepted 3718, error calls 0, outer received 1690.
Counters include health/other traffic and are not settled before/after deltas;
do not subtract them as exact test-flow conservation. Read errors are not yet
classified and cannot be assumed harmless health closures. Receiver counts
alone are not unique-sequence counters. No TUN or physical-interface capture
was made, so no client-wire delivery proof exists.

Aggregates: runtime/rm-udp-diagnostic-{counters,result}.json and
runtime/rm-udp-diagnostic-server.txt. Receiver stopped, tag rules 0, marker absent.
RM original SE restored; original package reinstalled, original owner hash
FE2D16809B4EAFE52A218F6F4222B75BAB42782211DF7BEDED65C1124ABC5F90
confirmed, phase Disconnected/Desired=false; temporary tasks/token removed.

Next: settled same-child before/after deltas, classify read errors, bounded
outer-flow interface metadata on RM and server kernel/ingress counters with
capture drops. This narrows investigation beyond successful proxy writes but
does not localize a defect or demonstrate a fix.

## StdNet hypothesis and packet localization, 2026-10-28 22:06–22:11 UTC

Experimental stdnet owner SHA979567D7D0AF1778668206110A961ABDF50C385A280956D6613D30ACC44EE030 installed and verified. Compile flag selected NewStdNetBind; MTU, same peer and narrow routes retained. First run 992/3000 replies, next 935/3000. Does not support RIO alone as explanation; no production fix claimed.

Second run metadata capture on SE awg-cita2: all3000 outbound test packets (224byte payload) and935 inbound replies, capture3935/filter3935/kernelDrops0. The outer capture selected a previous endpoint source port and saw only2packets; INVALID for outer-flow conservation because new bind reopened its port. Do not use that outer result as a loss count. Counts do not prove payload integrity/checksum, but establish test-sized inner packets reached server TUN.

Fresh DE receiver counted1928 valid packets across both tests (992+935+one probe), same number sent and0 send errors. This localizes the unexplained deficit after server TUN injection toward receiver/application, rather than proving client WAN/TUN send loss in this run. Next decisive check: simultaneous SE physical egress and DE ingress metadata with UDP checksum/kernel counters, plus receiver invalid length/auth counters. Do not infer Internet loss vs server forwarding vs receiver rejection before this.

Cleanup: DE temporary tag rules0, marker absent. Original RM profile SE-TEST.conf and original owner FE2D16809B4EAFE52A218F6F4222B75BAB42782211DF7BEDED65C1124ABC5F90 restored through normal installer; see rm-stdnet-cleanup-result.json. No changes to main PC WireSock. Two brief RM SSH outages recovered without network restart; keep management path a separate issue.
