# Contributing to CITA Route

Use Windows and .NET 10 SDK. Start with `Bootstrap.ps1`, then `Build-All.ps1`.
The application and protected Windows agent share their version through
`Directory.Build.props`; distribution metadata is in `product.json`.

Run the relevant offline fixtures and GUI self-tests. `Verify-Optimization.ps1`
uses isolated loopback engines. Do not run `Verify-All.ps1`, VPNUS recovery tests,
or system route fault injection on a user's live connection without an explicit
request and an independent recovery path.

Keep pull requests focused and explain resulting behavior, validation and limits.
Never commit `private`, `runtime`, `data`, `logs`, configured domain lists, VPN
profiles, keys, tokens or production diagnostics. Synthetic test values must stay
synthetic. Runtime process/service names are legacy compatibility identifiers;
renaming them requires an installation migration.

For releases, update both version files, write RELEASE-NOTES.md, and run the
manually dispatched **Publish stable release** workflow with the matching `vX.Y.Z`.
Only stable releases with the expected Windows ZIP and SHA256SUMS.txt are offered
by the client. Do not replace a published version's assets; release a new version.
