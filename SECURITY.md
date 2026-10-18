# Security

Do not post private VPN configurations, keys, controller tokens, raw diagnostics
or personal endpoints in public issues. Use GitHub private vulnerability reporting
when available; otherwise open a minimal issue asking for a private contact without
including the vulnerability details or secrets.

The native agent runs under LocalSystem from a protected Program Files directory.
It accepts bounded allowlisted IPC operations and does not execute arbitrary
workspace scripts as SYSTEM. Normal routing scripts request UAC when needed.

Update metadata and hashes come from the fixed `IthilRosg/CITA-Route` repository
over HTTPS. Hashes detect incomplete or changed artifacts, but are not independent
release signatures. Windows code signing and signed update metadata are future work.
The non-elevated updater refuses active sessions, preserves user data, backs up
public files and leaves the protected agent update to the normal setup transaction.

Tests cover untrusted release URLs, version ordering, checksum mismatch, modified
payloads and unsafe ZIP paths. This does not replace testing against a specific
Windows configuration, VPN provider or network.
