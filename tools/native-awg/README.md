# Native AWG transport laboratory

Experimental and separate from the production CITA transport. No automatic
fallback to host TCP/DNS, no listener, system adapter, routes or OS DNS settings.
The first milestone is a single verified HTTPS request via an internal netstack.

Pinned engine: `github.com/amnezia-vpn/amneziawg-go/v3 v3.1.20260828`, commit
`b5928efb6ca19f0153958460c3d141f04abc5c2e`, module checksum
`h1:D8d8gGvwXcTxUIsE4z6F6vjy4/VZddu95vMNtOygh1c=`. Dependencies in go.sum.
Go 1.26.3, CGO disabled. Engine license: MIT; redistribution of a future product
must also include applicable transitive dependency notices and review.

Build from this directory with workspace-local GOMODCACHE/GOCACHE:
`go test ./...` and `go build -trimpath -o ../../runtime/native-awg-lab.exe .`.
The workspace build helper `Build.ps1` checks the toolchain, verifies dependencies,
runs tests/vet, and builds without changing the module manifest.
Configuration is read from stdin, never command arguments. JSON result contains
only fixed stages, elapsed time and a SHA-256 digest of the observed exit address.
Upstream errors/logs are suppressed because they can contain configuration values.

Initial supported scope: one IPv4 peer, literal IPv4 endpoint, IPv4 addresses/DNS,
default IPv4 AllowedIPs, MTU 576–1420, basic WG keys/keepalive and listed AWG
J/S/H/I parameters (validated further by the engine). Hostname bootstrap, IPv6,
scripts, multiple peers and WireSock extensions are explicitly refused. No local
proxy or production credential handling is provided yet. The guest runner is
restricted to the approved W11 lab UUID and requires idle CITA/WireSock.

A successful HTTPS request is NOT packet-capture leak proof, UDP acceptance,
connection retention, throughput/resource benchmark or production readiness.

## IPv4 capture and UDP gate, 24 October 2026

The probe now requires a separate bounded DNS/UDP exchange through the internal
stack, matching transaction ID, question, response flags and a public A answer.
This confirms DNS/UDP request-response, not arbitrary UDP applications or loss
characteristics. OS-NIC captures use Pktmon, 128 captured bytes/packet, bounded
8 MiB ETL files; raw files stay under the ACL-protected guest private directory.
No capture filters are removed or installed. An already-running capture causes
start to fail rather than being stopped by the runner.

Controlled W11 result:
- Direct positive control: 8 outbound TCP and 2 outbound UDP packets observed.
  UDP uses an explicit socket to avoid Windows DNS-cache false controls.
- Native HTTPS + UDP/DNS: expected exit, unchanged OS topology, 4 outbound UDP
  packets to the endpoint, zero other outbound TCP/UDP in the observed IPv4 NIC
  traffic. Probe elapsed 351 ms, a single sample, not a benchmark.
- Negative authentication control: original endpoint with a random wrong PSK
  only in process stdin; 2 outbound UDP endpoint packets, HTTPS refused, zero
  other outbound TCP/UDP. Original profile and server settings unchanged.
- Capture stopped after the tests. Result:
  `runtime/vm-lab/w11/native-awg-capture-auth.json` (local ignored receipt).

The earlier unavailable-port test returned the expected application failure,
but its capture did not observe outbound endpoint packets. That capture was NOT
accepted as a full negative packet-observation gate. Wrong-PSK testing is a
separate scenario, not a substitute claim that all server-outage cases passed.

The analyzer accepts bounded little-endian PCAPNG Ethernet EPBs (with one VLAN),
requires known link types and usable headers, and refuses unsupported/malformed
packet encodings. Counts are restricted to the selected local IPv4 source;
incoming/background packets are separate. Positive controls cover both TCP/UDP.
This is evidence for these captures, not proof for every network, IPv6, other
local source addresses, process crash, updates or future transports.

## First acceptance, 24 October 2026

Approved W11 VM only; WireSock and CITA idle, same approved test peer on UDP 3080.
Native HTTPS succeeded with the expected server exit digest, 287 ms measured
inside the process. This is ONE request including internal setup, not comparable
to prior forced-disconnect recovery tests and not an advertised startup latency.
No Windows route/adapter changes were observed. No UI/service/driver installed.

Negative test: same server IP, UDP port 9 in the stdin-only test configuration.
HTTPS failed at HandshakeAndHttps after 10006 ms, no exit digest, unchanged OS
topology. No file profile was changed. This rejects successful application-level
fallback in that test, but does not establish packet-level absence of leakage.

Local receipts (ignored, not distribution):
`runtime/vm-lab/w11/native-awg-probe.json`, `native-awg-unavailable.json`.
Parser regression tests refuse scripts/unknown extensions, duplicate keys,
multiple peers, unsafe endpoints and unsupported scope. Engine refusal test
checks that a malicious configuration value is absent from serialized results.

Next gates: physical-interface capture with positive controls; explicit UDP
acceptance; interrupted transport while preserving an internal TCP connection;
only then authenticated local proxy integration, production lifecycle and UI.
