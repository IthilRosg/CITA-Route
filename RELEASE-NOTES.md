CITA Route 0.3.2 — connection setup fix.

- Fixed Apply connection and profile import failing with a missing argument for ImportPath, SourceProfile or ExpectedEgressIp under Windows PowerShell 5.1.
- Empty optional settings now survive the elevated parent/child process boundary; leaving the expected VPN exit IP empty remains supported.
- Automatic WireSock discovery omits an empty CLI argument.
- Regression checks run the actual setup command against an isolated fixture, including Cyrillic, spaces, apostrophes, literal shell characters, explicit exit IP, import and failure reporting. Tests do not modify WireSock, services or network settings.

Extract the complete ZIP and run RoutePilot.exe. Existing users can install through Settings → Updates after disconnecting, then Apply connection to refresh the protected agent. Personal profiles and rules are preserved.
