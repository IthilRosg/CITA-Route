# UDP probe quality (0.5.0 candidate)

This optional feature measures missing **round-trip replies to a controlled
server**, independently for Direct and VPN. It does not estimate every game's
loss, one-way loss, QUIC migration or streaming quality. HTTPS health is only
a permission check for the VPN path, never the source of a loss percentage.

Without your own reflector, leave this disabled. The advanced diagnostic fields
then show **не измерено**. Compact mode independently uses the existing WireSock
service's [native tunnel estimates](WIRESOCK-STATISTICS.md), which need no
reflector. No public third-party UDP service is contacted by default.

## Controlled server

The optional server is `tools/udp-reflector`, targeting .NET 8 on Linux/Windows.
Publish it from the repository on a trusted build machine, for example:

```sh
dotnet publish tools/udp-reflector/Cita.UdpReflector.csproj -c Release \
  -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/reflector
umask 077
openssl rand -hex 32 > udp-reflector.key
./publish/reflector/Cita.UdpReflector ./udp-reflector.key 0.0.0.0 38565
```

Run it as an unprivileged account and permit the chosen UDP port in the server
firewall. The default bind, when omitted, is **127.0.0.1**; external listening
requires an explicit address. A different server/public IP from the WireGuard
endpoint is preferable, to avoid endpoint-bypass routes and local-server paths.
The Windows ZIP does not install or start this server.

The protocol uses 64-byte HMAC-SHA256-authenticated echo packets with a random
128-bit round nonce and sequence number. The shared key is 32 cryptographically
random bytes, encoded as 64 hexadecimal characters. Invalid packets receive no
response. Replies have the same size as requests; the server caps examination
at 200 packets/s and replies at 40/s globally. This is a small private diagnostic
endpoint, not a general public echo service. Packet contents carry no domains,
application names, traffic contents or WireGuard credentials.

Keep the key file private. Use a service manager for a persistent server;
deployment, firewall changes and credentials are separate from the client.

## Windows configuration

In **Advanced → Settings → Измерение потерь UDP**, enter the server's actual
public IPv4, UDP port (1024–65535), and the key file's contents. Enable and save.
No route restart is required after the candidate's agent has been installed.
The first result takes roughly 6–8 seconds after both the selected route and
the reflector become available. The ordinary policy worker owns collection,
so hiding the GUI does not stop it; manual Disconnect does.

`data/udp-quality.json` holds the optional endpoint and shared key locally.
Do not share it; clean ZIPs exclude it. `runtime/udp-quality.json` contains
bounded measurement metadata, without the key. The GUI checks the session,
  profile, network signature, bridge generation, configuration identity and
freshness before displaying a percentage.

## Method and limits

- Direct uses the router's dedicated Direct SOCKS inbound, whose outbound is
  pinned to the chosen physical interface. VPN uses the existing VPN bridge's
  SOCKS UDP association. There is no native UDP or Direct fallback for VPN.
- A VPN round starts only with fresh matching HTTPS health, fresh Connected
  watcher status, a ready bridge and no recovery/egress hold. A lost VPN proof
  cancels only VPN measurement; the Direct result remains independent.
- Each path sends 10 probes, spaced 500 ms apart, with a 1500 ms response
  deadline; rounds begin no faster than every 10 seconds. This totals about
  two outbound 64-byte probes/s across both paths, plus replies and transport
  overhead. No probe result changes routing, restarts an engine or rejects an
  application's connection.
- The window contains at most 60 probes in six rounds, dropping rounds older
  than 75 seconds. Duplicates never add successes; reordered timely replies
  count once. Late replies observed before round closure are counted separately
  and do not undo a missed deadline. Cancellation and local preparation/send
  errors discard a round instead of adding artificial losses.
- Until the first complete round: **сбор данных**. A wholly unanswered latest
  round, unsupported UDP association, or unavailable path: **нет проверки**,
  never fabricated 0% or proven 100% network loss. An unreachable server and
  complete path loss cannot be distinguished by echo alone.
- The tooltip provides sent/received counts, window start, update time,
  deadline, duplicates, reordering, late replies and the transport/address family.
  Ten samples have coarse precision; the percentage is descriptive, not a
  confidence guarantee. IPv6, PMTU, QUIC and actual TUN/WireSock interception
  remain separate validation work.

## Validation

Agent self-tests cover authenticated packets, nonce/sequence rejection,
controlled drops, duplication, reordering/deadlines, bounded windows, no Direct
fallback, cancellation, independent paths and session/bridge changes. GUI tests
reject stale/mismatched or inconsistent persisted measurements.

```powershell
dotnet build tools/udp-reflector/Cita.UdpReflector.csproj -c Release
& ./runtime/agent-build/RoutePilot.Agent.exe --udp-integration $PWD.Path
```

The integration test starts isolated real sing-box engines on random loopback
ports, exercises nested SOCKS UDP, stops its own bridge, verifies surviving
Direct traffic, and tests the actual reflector with correct/wrong keys. It
never creates a TUN, changes OS routes, touches WireSock or disconnects VPNUS.
It is not evidence of real remote WireSock UDP connectivity.

SOCKS UDP association framing/lifetime follows [RFC 1928](https://www.rfc-editor.org/rfc/rfc1928.txt).
