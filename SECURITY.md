# Security and sensitive data

QuoteMatch is a local desktop application with an optional external-system adapter. Imported documents, saved integration queries and credentials have different trust boundaries.

## Reporting a concern

Use the repository host's private vulnerability-reporting channel when available. If no private channel is configured, request one from the maintainer with a non-sensitive summary before sharing details. Do not post credentials, connection strings, production SQL, private documents or raw business records in a public issue.

The project does not currently publish a supported-release window or a guaranteed response time. Include the affected commit, platform, synthetic reproduction and sanitized impact when reporting a concern.

## Credentials and configuration

SQL passwords are excluded from SQLite profile data and diagnostics. Windows persistence uses current-user DPAPI and fails without a plaintext fallback. Other platforms retain SQL passwords only for the running session; persistent native keychains are not implemented.

Configured SQL is trusted integration code. Named parameters and query-shape validation do not replace database permissions. Use restricted read credentials and a deliberately configured write connection. `ApplicationIntent` is routing metadata, not authorization.

Saving a query does not execute it. Enabling a write adapter does not authorize an external order operation; the application requires a reviewed plan and explicit final action. Pending or uncertain operations must be reconciled rather than blindly retried.

## Documents and local storage

Original supplier files, extracted text, reviewed values and snapshots are business data. Keep the local application directory under the intended user's access controls. Its SQLite database stores source bytes and evidence; moving it can expose those records. Protected credentials remain scoped to their platform/user mechanism.

PDF/regex and spreadsheet readers enforce size/processing limits and cancellation. Spreadsheet formulas are read from saved cached values; macros and external links are not executed. These controls do not replace validation of untrusted inputs or operating-system updates.

## Repository material

Packaged application updates use the public release repository recorded at build time and a separate channel for each OS/architecture. Downloaded packages undergo SDK integrity checks; release notes are displayed as text. Installation requires the user's restart action. Release packages are currently unsigned, and checksums do not establish publisher authenticity. See [releases and updates](docs/releases-and-updates.md) for the distribution boundary and signing status.

Public tests, examples, coverage artifacts and screenshots must use synthetic data. Keep private documents, installation scripts, credentials, databases, extracted contents and private planning out of the repository and CI artifacts. `.local/` remains ignored and must never be force-added.

Diagnostics and user-facing failures must remain sanitized: no stack traces, private SQL, credentials, connection strings or raw documents. See [agent directives](AGENTS.md), [testing](docs/testing.md) and [integration contracts](docs/erp-integration.md).
