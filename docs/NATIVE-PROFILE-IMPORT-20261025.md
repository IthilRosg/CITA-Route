# Native profile import — 2026-10-25

Implemented import into an **already prepared protected native installation**.
Settings provide a `.conf` picker; the desktop sends bounded configuration data
through the authenticated service pipe, never executable paths or command-line
secrets. A disconnected session and its current operation identity are required.
This does not bootstrap missing native binaries on a new PC.

The service resolves a hostname endpoint to a pinned IPv4 address, validates the
profile with the actual transport parser, and checks HTTPS exit and UDP DNS using
a userspace candidate. It does not install OS routes during preflight. Candidate
processes belong to a kill-on-close job. Script directives and unsupported fields
are rejected rather than run or silently discarded. Display names are sanitized.

After validation, the service compiles IPv4 routes from the peer's AllowedIPs,
retaining the previously protected Direct exclusions. It does not widen split
IPv4 peer routes to make the test pass. Protected IPv6 remains blocked. A new
profile, policy, core configuration and digest-pinned catalog are written as
immutable revisions. Files are created atomically with SYSTEM/Administrators
access only; an atomic service-settings replacement is the commit point.
The old catalog and profile remain available if preparation is rejected.

Import has its own operation identity and excludes Start and maintenance.
An old Stop cannot cancel a newer import. Cancel targets that exact operation;
the request identity is known before the reply, allowing cleanup after a lost
acknowledgement. The desktop displays reading, validation, HTTPS, UDP, policy and
storage stages. Import does not auto-connect; the selected profile survives SCM
restart and is confirmed on the next connection as needed.

Current bounds: one IPv4 peer, UTF-8 configuration up to 12 KiB, supported IPv4
Address/DNS/AllowedIPs, MTU 576–1420, and the existing transport's control route
requirement (1.1.1.1 must be reachable through that peer). IPv6 configuration,
arbitrary scripts, and unknown extensions fail closed. Retained revisions are
capped at 32; automatic private-revision collection is a later task. Endpoint
hostname pinning is performed at import, not continuously refreshed by DNS TTL.

Verification covers nine malformed/unsupported input cases and canonical CIDR
handling offline; eight real WPF status displays; and isolated W11 SCM tests for
invalid input, script rejection, cancellation, stale Stop, concurrent Start,
private-file permissions, successful selection, restart persistence, HTTPS/TUN
traffic and import through the actual desktop client. The failed first VM attempt
was retained: creating a file and then setting ACL through a generic ReadWrite
handle was refused. Creating the file with its final security descriptor fixes
that failure without relaxing permissions.

Still open: missing-component first-use setup, live adaptive domain policy,
full installer/update/uninstall rollback, interrupted-publication crash injection,
physical NIC handoff, native IPv6 carriage and public packaging. This milestone
does not certify a public native-backend release or automatic rollback after a
later application/network outage.

Accepted clean-source W11 receipt: `runtime/native-scm-076a1df6b4cd4905a729bd6f06398925.json`.
Agent SHA-256: `AA281092275FACB9145EDAA97B9965BAE86BB6C1104969B03330273056E9D473`.
Desktop SHA-256: `A5BA55C21B5C0DC0CDEF04186F0513685B8FB8941F0F6B14F6FAF8067555C3BC`.
The guest scenarios completed in 61 seconds. The agent build retains the existing
CA2022 warning in UdpQualityTests; the desktop and import-unit builds have no warnings.

`runtime/native-import-final-audit.json` confirms zero test guard markers, native
processes, TUNs or candidate directories; the original service and uplink are up.
