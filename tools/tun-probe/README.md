# Native TUN acceptance client

This VMware-only tool makes authenticated IPv4 HTTPS requests to the fixed public
Cloudflare trace endpoint using Windows DNS and native TCP. `UseProxy=false` is
explicit; it contains no SOCKS client or certificate validation override.

Build with the repository's pinned .NET SDK. Run the resulting application host
inside the authorized Windows guest, not through `dotnet.exe`: router telemetry
must identify `Cita.TunProbe.exe` as the application.

```
Cita.TunProbe.exe --request C:\CitaLab\TunProbe\one
Cita.TunProbe.exe --direct C:\CitaLab\TunProbe\direct
Cita.TunProbe.exe --hold C:\CitaLab\TunProbe\held
Cita.TunProbe.exe --active-hold C:\CitaLab\TunProbe\active
```

Results are written atomically to `<prefix>.ready.json` or `<prefix>.failed.json`.
`--active-hold` also samples the same connection roughly once per second and
replaces `<prefix>.active.json` with the latest sample and count. It shares the
150-second total deadline and refuses reconnects just like `--hold`. A matched
no-fault control must pass before attributing a later TCP failure to VPN loss.
`--direct` uses the fixed HTTPS trace endpoint at 1.1.1.1, so a soak harness can
check a separate IP destination while the hostname is explicitly routed to VPN.
Its certificate is validated normally against that IP; the harness must verify
the actual exit rather than treating the mode name as routing evidence.
The report includes a hash of the observed exit, the number of TCP connections,
local source port, TLS/HTTP result and whether the native socket used FakeIP.
It does not output a raw exit address or exception text containing a URL/header.
The report prefix must stay under `C:\CitaLab\TunProbe\`.

A hold lives for at most 150 seconds. Write an atomic `<prefix>.command.json`
containing `{"id":1,"action":"sample"}` to repeat the GET on the same HTTP/1.1
connection; a required reconnect is an error. Results go to `<prefix>.1.json`.
IDs increase to 20; `action="exit"` ends the hold. This lets a guest harness change
rules while independently checking that the original connection and exit survived.

`--resolve` obtains the public fixture's A records over authenticated DoH for
guest-local fault injection experiments. These addresses stay in the guest.
It does not prove that a network fault affected only direct: the harness must
establish that independently before counting a routing result.

The client does not configure the tunnel or change routes, firewall, trust stores,
services, user profiles or Windows proxy settings. Those transitions belong to
the separate guest harness, with explicit ownership and cleanup. An HTTP egress
check is not a packet capture or proof of a physical VPN-only boundary.

`--patient-hold` is the same active connection test with a 30-second HTTP request
deadline, to distinguish application cancellation at 10 seconds from TCP loss.
It does not change CITA timeouts or promise uninterrupted delivery during loss.
