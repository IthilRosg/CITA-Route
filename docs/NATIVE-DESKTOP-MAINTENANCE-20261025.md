# Native desktop and maintenance milestone — 2026-10-25

The ordinary desktop now understands the optional protected native snapshot.
Settings offer WireSock Auto and a clearly labelled preliminary native mode.
The latter is enabled only when the service reports a prepared snapshot. First
connection confirms that profile; its identity is remembered locally. This is
selection of an already prepared profile, **not first-use `.conf` import**.

Start, cancel and Stop use the authenticated rescue pipe and session operation
identity. A replaced session cannot be stopped by an old UI request. Connected
requires both Connected service phase and Active core state. Preparation,
recovery, stopping, failure and unavailable service have separate labels.
Unavailable measurements are shown as dashes rather than old WireSock values.
The UI retains the last session ownership when IPC fails; it does not silently
switch to the legacy backend. Unavailable-service polling backs off ten seconds.

The native policy description distinguishes FullVpn from selected prefixes.
The mode explicitly warns that live adaptive Auto lists still require WireSock.
Current transport is IPv4; protected IPv6 remains blocked.

`NativeMaintenance.ps1` confirms native Stop before either client update or
protected-agent replacement. Unknown state, refused Stop, changed operation or
timeout abort replacement. Existing coordinator ownership then blocks racing
Start requests. ValidateOnly does not disconnect. The helper is included in the
portable and updater allowlists. This is maintenance wiring, **not acceptance of
a shipped installer, migration from older updater allowlists or failed-copy
rollback of a complete public native package**. A differently credentialled
administrator may be denied pipe access; that case fails before replacement.

Verification:

- WPF acceptance constructs the real window and checks seven status displays,
  retained profile, cleared stale metrics and the service JSON contract.
- An isolated source snapshot contains only staged milestone changes, excluding
  unrelated workspace edits; its desktop build passes without warnings.
- Maintenance tests cover absent/disabled settings, confirmed Stop, unavailable
  service, refusal, concurrent session replacement and malformed settings.
- The W11 SCM runner additionally checks desktop-client IPC metadata, traffic,
  core/transport/service recovery, stale Stop rejection, maintenance exclusion,
  manual disconnect persistence and restoration of the original service.

Remaining release gates: protected first-use import/setup, adaptive domain policy
publication, packaged install/update/uninstall and rollback acceptance, actual
Windows boot, physical NIC handoff, IPv6 carriage and corresponding-source
packaging. No host native snapshot is installed and no public release is made.

Accepted clean-source receipt:
`runtime/native-scm-2d696673789342febe8df7a316ce54f0.json` (42 seconds of guest
SCM scenarios). Agent SHA-256:
`729775D78ECDF9589B187462F7655FE8C681244BBBD9077EE14E260C6B62384F`.
Desktop SHA-256:
`82F32067EFAE49D3A0591A88654E6D7CAFF4A5BD12426E35F20EA4FC823A4C62`.
Final audit `runtime/native-desktop-final-audit.json` confirms zero native child
processes, guard marker occurrences, lab TUNs and candidate directories; the
original service is Running and the guest uplink is Up.
