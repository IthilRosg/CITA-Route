# Connection lifecycle debugging (0.5.6)

The installed working session is not used for fault injection. Driver-level
experiments run on disposable GitHub-hosted Windows machines with generated
local keys. No user profile, secret, native VPN log or configuration is uploaded.

## Reproduced and corrected

- **Setup versus routing race.** The real SmartRouting entry opened `setup.lock`
  only to check it and immediately closed it. Apply/import/protected file
  replacement could enter after that check while startup/repair continued.
  The gate is now held until the action returns, including exception/exit paths.
  An unelevated parent releases its gate before handing control to an elevated
  child, which acquires its own gate. Stop/status/watch and parent-owned offline
  preparation stay available. A fixture pauses the real entry after the gate;
  it failed before the correction and passes afterwards.
- **False disconnect completion.** Unknown WireSock state previously returned
  without error; a successful CLI exit was not followed by a state check.
  Disconnection now requires the exact managed profile, then an independent
  disconnected result. Uncertain state requires attention. A late foreign
  profile switch is preserved and reported, never blindly disconnected twice.
- **Similarly named foreign profile.** The legacy crashed-router cleanup used a
  substring match that included `SmartRouting-AWG2-copy`. It now shares the
  exact profile ownership and disconnect postcondition above. Fixtures preserve
  both an unrelated profile and a similarly named profile.

## Coverage matrix

| User operation | Evidence and boundary |
|---|---|
| Connect | Actual PowerShell transaction orchestration: first setup, quoting/Unicode, cancellation, duplicate operation exclusion, update preparation and readiness. Network/provider calls are replaced in these fixtures. |
| Disconnect | Actual parser and helper: unknown state, managed transition, already disconnected, foreign profile, unconfirmed result and profile change during Stop. |
| Reconnect | Real sing-box sockets with production bridge repair code and injected WireSock observations; session/generation/Stop priority and retry limits. |
| Change profile | Managed-copy reuse/update, backup and import rollback; original profile preserved, foreign state refused, cancelled handoff restores the prior provider. |
| Install/update | Real test installer/shortcuts/registry on clean CI, maintenance lock lifetime, operation contention, corrupt/active sessions and current/shipping 0.5.4 updater rollback/handoff. |
| Uninstall | Dedicated test AppId: refusal during an active route, removal after an internal upgrade, shortcut/registry cleanup, personal data preservation and owner crash. No production installation is removed by tests. |
| Stability | A 20-cycle short loopback soak pauses/restores the bridge while one independent direct TCP socket continuously sends frames. New paused VPN flows must refuse instead of falling back. Router PID, memory/handle growth and direct RTT are recorded. This is not a WAN/game/voice or multi-hour soak. |

## Real WireSock research

The separate `codex/wiresock-driver-lab` research branch contains
`wiresock-lab.yml`; it is not part of this release. It fetches verified dependencies and independently validates a
synthetic user-space WireGuard server before installing the pinned, signed
WireSock SDK. The SDK is evaluation-only and is not redistributed in the client.
The installer and uninstaller are scoped to the disposable hosted runner. A DNS
observation is recorded after installation because an earlier attempt lost DNS
before tunnel testing; no cause is inferred from that observation alone.

The experiment compares transparent interception with a virtual-adapter-bound
application filter, disconnection and explicit rearm/rebind on reconnect. Only a
synthetic benchmark /32 is allowed; no system default route is requested.
Failed baseline/control tests remain failures. Installing the SDK or observing a
handshake is not evidence of usable traffic. SDK-core results do not cover the
GUI service's profile lifecycle, AWG masking, physical packet leakage or real
WAN stability. See TRANSPORT-BOUNDARY.md for remaining production gates.

The shipping changes above do not enable the experimental boundary or switch
WireSock modes. Protected agent replacement follows the existing next-Connect
Windows confirmation.
