# Native WireSock statistics (0.5.0 candidate)

Compact mode uses the existing WireSock service's tunnel estimates. No additional
server, profile modification, SDK installation or second tunnel is required.
The optional numbered UDP reflector tests remain separate in advanced settings.

## Meaning

- RTT is `estimated_rtt`: elapsed time for the latest initiated WireGuard
  handshake, not a continuous ICMP ping or game-server latency. This service
  response does not expose that handshake's age; a freshly read RTT can still
  refer to an earlier handshake.
- Loss is `estimated_loss × 100`, matching WireSockUI's conversion. It is the
  engine's estimate for the VPN tunnel, not the direct path or a count of failed
  HTTPS requests. The API does not report its estimator window/sample count.
- Both are marked approximately. Zero RTT, missing/stale data, another profile,
  failed reads and an unverified routing generation display dashes. Loss is not
  shown before the service reports received tunnel bytes. A genuine zero loss
  estimate after received traffic is allowed.
- These values do not prove that an application's website, account or region is
  available. Existing independent HTTPS health retains its own connection state.

## Read-only integration

The adapter sends exactly one hard-coded local RPC:
`/WireSockGrpcAPI.WireSockGrpc/RpcGetConnectionStatus` on `WireSockControl`.
It does not send Hello, Connect, Disconnect, profile/config export, network-lock
or update commands, and never loads `wgbooster.dll` or a vendor managed DLL.

The response uses the WireSock 3.x embedded GrpcDotNetNamedPipes transport and
protobuf contract for **3.6.1.1**. Supported adapter version gate: 3.4–3.6.
Other versions fail gracefully rather than guessing a new schema; broader
version compatibility remains a separate validation task.

The reader verifies the pipe server PID against the protected installation's
service image. Where process-query rights are denied, read-only Windows Service
Control Manager status/config queries verify PID, running state and image path.
Untrusted servers, linked installation paths and unsupported installs are refused.
Profile matching is exact and case-insensitive for the CITA-managed profile.

Requests have a 1500 ms overall deadline. Replies have bounded field/frame counts
and allocation budgets; unary data is accepted only after successful RPC trailers.
The reader handles fragmented frames, cancellation, malformed/duplicate fields,
unknown extra fields and invalid values. No response bodies, credentials, IPs or
profiles are written to diagnostic logs.

GUI polls at most once every three seconds during its existing refresh cycle;
hidden/minimized refreshes remain slower. Failed reads clear prior values, and
cache freshness expires after 12 seconds. A new session/profile/network/bridge,
connection operation or Exit cancels/discards old results. The reader never owns
network recovery or changes routes when statistics are unavailable.

## Verification

`RoutePilot.Agent.exe --statistics-self-test` runs isolated pipe/protobuf tests.
The normal `--self-test` also includes them. App self-tests cover native display
formatting, stale/future/failed reads, no-traffic guards and operation transitions.

`RoutePilot.Agent.exe --wiresock-statistics` safely samples the current protected
WireSock setup, emitting only availability/code, RTT, loss, duration and read time.
An unavailable sample is diagnostic data, not a command to reconnect.

Synthetic fixtures verify RTT, fractional loss and duration decoding, including
valid zero estimates and missing-data handling. These do not establish all
driver/network failure scenarios or all supported WireSock versions.

Protocol and semantics references:

- [WireSock SDK statistics structure](https://github.com/wiresock/WireSockUI/blob/main/sdk-contract/include/wgbooster.h#L60-L67)
- [WireSockUI statistics rendering](https://github.com/wiresock/WireSockUI/blob/main/WireSockUI/Forms/frmMain.cs)
- [Named-pipe transport schema](https://github.com/cyanfish/grpc-dotnet-namedpipes/blob/master/GrpcDotNetNamedPipes/Internal/Protocol/transport.proto)

The client implements a small independent wire adapter for interoperability;
no vendor binaries or additional NuGet dependencies are redistributed.
