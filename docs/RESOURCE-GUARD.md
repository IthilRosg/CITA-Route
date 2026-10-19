# Windows resource monitor (0.5.4)

The existing protected RoutePilotRescue service observes system resources even
when the GUI exits. It writes a bounded `resource-state.json` in its protected
ProgramData directory. The GUI reads that report, shows a compact warning only
when an issue is confirmed, and offers a report in Settings or the compact menu.
Tray notifications require a running GUI (including a window hidden to the tray).
After installing this release, the protected agent updates on the next Connect
through the existing Windows confirmation. A missing/stale report is unavailable,
not a successful resource check and not proof of lost internet.

## Measurements and limits

- IPv4 and IPv6 UDP endpoint tables come from GetExtendedUdpTable with owner PID.
  Local addresses are discarded. Distinct in-range port numbers are counted;
  repeated endpoints/shared bindings do not inflate the percentage.
- Actual UDP dynamic ranges and exclusions are read separately per family through
  bounded read-only netsh calls. The normal configuration cache lasts 15 minutes;
  failed configuration reads retry after a minute. Unknown ranges never fall back
  to an assumed default. Overlapping exclusions are merged. Missing exclusion data
  uses the full known range conservatively and reports partial coverage.
- A check binds one temporary socket per family to port zero and disposes it. It
  sends no packets. Access-denied and unsupported/unknown failures are separate
  from resource allocation errors. Table occupancy estimates potential pressure;
  it is not an exact number of sockets another application can still allocate.
- GetPerformanceInfo supplies system commit/limit, available/total physical memory
  and total handles. Top UDP owners, private-memory and handle consumers are
  sampled locally. These are candidates for investigation, not proof of fault.
- Component growth is checked only for exact CITA executable paths and PID/start
  time identity. It requires at least 12 samples spanning ten minutes, 80% rising
  steps, and growth of at least 512 MiB private bytes or 2,048 handles. This is a
  suspicion of sustained growth, not a proven leak. Other applications are not
  declared faulty solely because their memory use grows.
- At most 2,048 processes are inspected per full sweep; any limit is recorded.
  At most 24 process rows and 32 incident transitions are published. No process
  command lines, paths, socket addresses, payloads, profiles, keys or tokens enter
  this report. Inherited sockets can outlive their original parent, and the PID
  in the native table may no longer identify a readable process. Missing owner
  metadata never proves that pressure has gone away.

Normal sampling is every ten seconds, increasing to two seconds near port pressure.
Full process metrics refresh every thirty seconds. Port warning/critical thresholds
are 80%/95%. System commit warnings additionally require less than 2 GiB remaining;
critical commit requires at least 97% used and less than 512 MiB remaining. Physical
memory pressure requires at most 5% and less than 512 MiB available (critical below
128 MiB). A single observation cannot notify: two distinct observations confirm an
issue and three consecutive reliable clear samples retire it. Unavailable data
cannot confirm recovery. Process growth confirmation uses distinct process samples.
UTC clock rollback resets confirmation rather than replaying an old sample.

Notifications deduplicate the issue fingerprint, have a five-minute cooldown, and
permit escalation from warning to critical. Recovery is announced only after a
previous notification and three complete, distinct clear reports. Incident history
survives service restart; pending/active confidence and growth trends do not.

## Actions and lifecycle

The monitor has no third-party process termination, service control, profile deletion, network
restart or connection action. It cannot reserve the system's ports against another
application. Its role is early detection and a useful local report. The user can
open Task Manager and save/copy the report. It is not general automatic repair of
third-party software. Integration with component recovery is a later stage.

Sampling runs outside the network coordinator lock, does not change health/VPN
status and does not connect an intentionally stopped session. Cancellation closes
its own sockets, native buffers and helper processes. Report failures do not stop
the rescue service; stale UI data expire after 45 seconds. Generated data live
outside release packaging and must never be uploaded as repository diagnostics.

## Verification

Agent self-tests cover native layouts/endian fields and invalid/truncated buffers,
localized/custom ranges, overlapping exclusions/shared port numbers, synthetic
MSI-sized exhaustion without allocating those ports, confirmation/recovery, stale
and unavailable states, allocation and memory errors, PID reuse, growth confidence
and bounded history. `--resource-integration <project-root>` additionally verifies
real native counters, sixteen local bind-only socket owners and disposal, service-
style report publication and cancellation. Timing is saved in ignored local test
data. This short check does not establish long-term resource use or absence of leaks.

Implementation references:
[UDP owner tables](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable),
[Windows performance information](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information),
[dynamic port configuration](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/tcp-ip-port-exhaustion-troubleshooting).
