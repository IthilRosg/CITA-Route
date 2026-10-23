# Windows socket lifetime investigation, 23 October 2026

Status: defect reproduced and corrected in a separate diagnostic candidate; normal-timeout acceptance completed at 00:05 MSK on 24 October. Deployment into the release requires explicit approval to replace the official bridge build. No production bridge, installed host client, release recipe or published artifact has been changed by this investigation.

## Observed on the working 0.6.3 host

The 23:15 MSK export covers a 7 h 46 min ordinary session with one bridge generation and no recorded reconnect. Two isolated control HTTPS timeouts were followed by verified probes without reconnect. Bridge private memory rose from 66.0 to 121.9 MiB, handles from 527 to 3547, and UDP endpoints from 6 to 514. At 23:45 MSK there were 554 bridge UDP endpoints, of which 436 were over an hour old, against 24 router UDP endpoints. These counters warranted investigation; memory alone was not treated as proof of a leak.

The protected bridge's SHA-256 is `5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF`, the pinned upstream sing-box 1.14.3 binary. Its sing dependency is `v0.9.7-0.20260929150544-0ad23b637bd4`.

## Reproduction

The isolated Windows 11 fixture uses its own echo target and a single TEST-NET /32 TUN route; it never loads a user VPN configuration. It opens short native UDP flows while independently maintaining active traffic. Ordinary direct/nested SOCKS closures, including reset clients, returned endpoints to zero in the separate non-TUN check.

With production five-minute defaults, 96 short native flows initially raised bridge endpoints to 196. After the expiry and a further 70 seconds, 14 remained where four active-path endpoints were expected. Five server TCP control sockets remained in CloseWait. The test failed and its own processes and route were removed.

An accelerated diagnostic scenario uses a five-second bridge UDP timeout, an eight-second TUN lifetime and a 75 ms echo delay on marked native packets. It makes the read deadline expire while the last response is still recent; it does not change installed client settings. The upstream bridge then retained 198 endpoints. A goroutine dump shows synchronous `windows.Recvfrom` inside `syscallPacketReadWaiter` while the corresponding `internal/poll.FD.Close` waits on its outstanding reference.

## Cause and bounded correction

The scalar TCP, vector TCP and UDP Windows read waiters use a `hasData` flag to alternate a readiness probe with a synchronous recv callback. A deadline can interrupt RawConn.Read after that flag was set, before readiness was actually observed. Retrying with the old flag calls recv directly without fresh readiness. It blocks the system thread, ignores the new deadline and prevents descriptor close until another packet arrives. This explains why inactive associations can remain indefinitely.

The correction resets `hasData=false` before each new RawConn.Read in those three waiters. Successful reads already reset it, so the ordinary path keeps its existing behavior. It preserves the existing optimized reader rather than shortening production UDP lifetimes or periodically restarting active connections.

`engine-patches/cita_windows_read_test.go` creates real local sockets, expires a read deadline and retries twice. All three unpatched variants (scalar TCP, vector TCP and UDP) block on the first retry. The corrected variants passed ten repetitions each. A rescue packet releases the original blocking call on failure so the regression test itself does not strand a close.

## Separate candidate

The diagnostic candidate was built from the existing corresponding CITA core source archive, with only the copied sing socket module overridden by the corrected module. It is labelled `1.14.3-cita-socket-lab`, SHA-256 `514BE2258C4E17F7D49DEDC85F83DEF74A43587FC5D09A0AFB72047CB327CAB5`, and stored under CitaLab, outside installed and protected paths.

The accelerated comparison passed with the corrected candidate: four endpoints remained at the active-path checkpoint, against 198 on the original build. After closing its SOCKS client, two expected endpoints remained temporarily for the just-closed native UDP flow, which has no TCP close signal.

The candidate also passed the full 370-second idle phase with the original five-minute default lifetimes. The 96 short native flows were removed after expiry; active SOCKS and native UDP traffic continued. At the same active-path checkpoint there were four endpoints, against 14 left by the original default-timeout run. Final bridge private memory was about 57.3 MiB and handles 215. This is a controlled local workload, not a prediction of all-application host memory usage. The comparison wrapper reported success, all its engines stopped and its test /32 route removed. Receipt: `runtime/vm-lab/w11/udp-socket-comparison.json`.

The prepared real-WireSock follow-up did not run: its guard stopped when the original TUN fixture failed. Real WireSock/protected-agent/update acceptance of a release incorporating the patch remains necessary. There is no long-duration or all-domain stability claim.

Releasing the fix would require a reproducible corrected bridge as well as router build, the complete corresponding source for the socket dependency, updated integrity receipts, and renewed protected-agent/update/WireSock acceptance. The user approved including both corrected runtime roles in 0.6.4. The production recipe now builds `1.14.3-cita.5` with the copied socket module and its regression tests; protected installation and release acceptance are pending. This does not claim the main host is updated or that every cause of resource growth is eliminated.

Local evidence is retained in `artifacts/diagnostics/HOST-0.6.3-20261023-review.md`, `artifacts/diagnostics/HOST-0.6.3-20261023-udp-age.json` and the ignored W11 lab receipts under `runtime/vm-lab/w11/udp-*`.
