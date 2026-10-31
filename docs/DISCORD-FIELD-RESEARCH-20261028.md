# Discord: field reports and implications for CITA Route

Research checkpoint, 2026-10-28. Community reports are anecdotal, not controlled
experiments or estimates of prevalence. Network, ISP, Discord version and date
can change the outcome.

## Evidence

- Discord describes voice and video as real-time UDP and reports moving most
  of that traffic to Cloudflare's edge. The selected voice endpoint and route
  can therefore affect latency and loss independently of the website:
  https://discord.com/blog/how-we-moved-discord-voice-to-the-edge
- Discord's troubleshooting notes identify UDP-capable VPN, firewall and voice
  region as possible factors, and its streaming guide also discusses hardware
  acceleration. A network diagnosis should not exclude a client-side defect:
  https://support.discord.com/hc/en-us/articles/115001310031-Voice-Connection-Errors
  https://support.discord.com/hc/en-us/articles/33030151293079-Discord-Voice-Video-Streaming-Guide
- A Russian user on MegaFon in Moscow region reported that voice setup failed
  directly but an existing session survived after disconnecting a VPN. This
  is one user's observation; it does not establish the cause or prove that a
  route can safely be switched while a flow is active:
  https://github.com/Flowseal/zapret-discord-youtube/issues/7810
- Another user reported one-way voice failing after about a minute; replies
  proposed different Discord/STUN UDP fake strategies, with no confirmed
  resolution in the quoted discussion:
  https://github.com/Flowseal/zapret-discord-youtube/discussions/16001
- Users report different working strategies for different providers and that
  strategies can stop working over time. This is evidence for per-network
  selection, not for automatically trusting any specific community preset:
  https://github.com/Flowseal/zapret-discord-youtube/discussions/17377
- The upstream zapret2 repository has a Linux example targeting Discord UDP
  IP discovery. It is not a Windows-ready implementation and must not be
  applied to unrelated UDP traffic:
  https://github.com/bol-van/zapret2/blob/master/init.d/custom.d.examples.linux/50-discord-media
- GoodbyeDPI's upstream documentation describes HTTP/TLS manipulation; it is
  not evidence that Discord's UDP media stream itself has been fixed:
  https://github.com/ValdikSS/GoodbyeDPI
- One report described Discord failures both with a DPI strategy and VPN,
  with some participants reporting a browser workaround. This leaves both
  client and network causes plausible:
  https://github.com/Flowseal/zapret-discord-youtube/discussions/12480

## Local evidence and next acceptance gates

The 2026-10-28 same-W11 synthetic comparison showed direct TCP upload
acknowledged at 2/4/8 Mbps, but VPN upload was only acknowledged at 2 Mbps;
4/8 Mbps phases did not meet the test's acceptance conditions. Both paths
received 7990/8000 UDP echoes, but tests were sequential, destinations and
routes can differ, and the installed agent hash differed from the candidate.
It used UDP echo with TCP upload: it is not a Discord call, bidirectional UDP
video, or proof of DPI interference. See `ROUTING-STAGE-NOTES.md`.

1. Reproduce with the exact candidate agent, verified actual egress and
   interface, and a controlled endpoint. Separate upload and download tests;
   include UDP background media, rates, packet sizes, loss bursts and RTT
   percentiles. Capture actual delivered load and reject inadequate samples.
2. Repeat Direct/VPN under comparable conditions; compare idle and loaded
   paths and vary MTU only inside the VM. Check server health and CPU as well
   as client queues; endpoint selection is not an explanation by itself.
3. Only then compare a scoped experimental UDP DPI method on a network where
   direct Discord UDP is independently known to fail. Pin any third-party
   artifact/version and inspect its license; do not copy community commands
   or enable a system-wide driver by default. Test exclusion and cleanup.
4. Treat DNS/HTTPS and UDP media as separate evidence. Keep established flows
   pinned to their path; select a new path only for new flows using bounded,
   fresh observations and hysteresis. No claim of seamless migration or 0%
   loss without a real application test.
5. Validate actual Discord voice and screen share in both directions with a
   consenting test account/channel and compare browser/desktop if needed.
   Validate YouTube with sustained playback, not merely HTTP 200.

## Deeper investigation: additional leads

- Discord's own incident analysis identifies saturated inter-network peering,
  UDP transmit queue contention and application scheduling starvation as real
  causes of spikes. The congestion example was peak-time and provider-specific;
  the same article says each call uses one shared voice host, so the nearest
  VPN server is not automatically the best call route for everyone. Instrument
  client scheduling, server CPU/UDP counters and path quality at comparable times;
  do not infer a DPI block from a single UDP loss or a high latency sample.
  https://discord.com/blog/how-we-moved-discord-voice-to-the-edge
- A Windows 11 26H2 user reports `WSASendMsg`/`WSARecvMsg` WSAEFAULT in the
  bundled AmneziaVPN AWG implementation while standalone AWG works on the same
  device; follow-up users report similar symptoms. This is an unconfirmed report,
  not proof that CITA uses the affected build or has this failure. Record exact
  OS build and engine version, capture bounded socket error categories, and
  compare the same VM/profile against an independent implementation before
  attributing failed handshake to firewall, DPI or MTU.
  https://github.com/amnezia-vpn/amnezia-client/issues/3277
- The upstream AWG Go issue describes a possible packet-classification loss
  with AWG 3.1 RandomTrailers and particular H1-H3 ranges, plus separate fixes
  related to padding and cookies. Applicability depends on the exact version,
  backend and profile features; AWG 2.0 observations cannot be attributed to
  this without matching evidence. Do not print profile values or change a
  production server based on the report.
  https://github.com/amnezia-vpn/amneziawg-go/issues/186
- Our DE-2 alternation produced widely overlapping and variable throughput for
  MTU 1280 and 1420; a fixed lower MTU is not established as the solution.
  Check packet-size dependence and PMTU in a controlled guest, with paired
  repeat trials and rollback, before contemplating a per-path choice.
  See `DE-2-MEASUREMENTS-20261026.md`.
- A community OpenWrt project reports packet-level emission of Discord/STUN
  decoys but explicitly says live voice was not tested. Its single-path HTTP
  experiments distinguish TLS headers from completed response bodies. Treat
  the results as hypotheses, and never equate emitted packets, HTTP 200, or
  TLS success with an end-to-end call or playable video.
  https://github.com/rikkichy/zapret2-openwrt
- The same proposed Discord fake has both positive and negative user reports
  in one discussion. Earlier release notes also advised removing a particular
  fake under some symptoms. Competing reports argue for bounded A/B checks
  instead of an unconditional UDP fake or a universal preset.
  https://github.com/Flowseal/zapret-discord-youtube/discussions/12534
  https://github.com/Flowseal/zapret-discord-youtube/issues/7810

## Experiment sequence and interpretation

1. Establish artifact identity (package hash, engine/OS build, profile protocol)
   and separate original user report on WireSock 0.6.5 from native results.
2. Repeat in the VM against the same owned endpoint, alternating routes and
   recording actual bidirectional offered/received load; account for warmup,
   invalid probes, pacing, packet-size distribution, CPU and NIC drops.
   Direct traffic may inherit the host's VPN; document egress instead of
   assuming the VM's Direct means an unfiltered Russian connection.
3. Compare low load to realistic concurrent upload and download; inspect
   one-way sequence gaps and burst durations as well as RTT percentiles.
   One-way delay requires synchronized clocks; otherwise report RTT only.
4. Test MTU and AWG engine/protocol one change at a time. Use safe temporary
   test peers/configs and independently verify cleanup and rollback.
5. Compare narrowly scoped DPI controls only on a validated affected network;
   validate no interference with games, VPN, DNS and private-network routes.
6. Real Discord and sustained YouTube playback remain separate acceptance
   gates. Automatic route selection must not turn synthetic echo quality into
   an assertion that a particular real media flow is healthy.

## Windows Zapret integration constraints

- Upstream describes winws2 as a Windows packet interceptor using WinDivert;
  its port and raw filters should be kept narrowly scoped, and coexistence
  with the native TUN/guard and other packet filters requires a VM test.
  https://github.com/bol-van/zapret2
- Flowseal's distribution states MIT terms for its repository and separately
  identifies WinDivert as LGPLv3 or GPLv2. This is a preliminary dependency
  inventory, not a complete license review of a future bundle. Pin exact
  component versions and preserve applicable notices and obligations before
  distribution. https://github.com/Flowseal/zapret-discord-youtube/blob/main/LICENSE.txt
- Do not replace or delete another application's driver/service, bypass driver
  signing, silently disable security software, or enable broad interception
  before controlled tests prove cleanup and conflict handling. Observe the
  existing protected routes and do not capture the VPN's own outer traffic.
