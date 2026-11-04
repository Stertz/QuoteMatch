# Contributing

QuoteMatch uses C#/.NET, Avalonia and SQLite. Read [AGENTS.md](AGENTS.md) for repository-wide directives and [architecture](docs/architecture.md) for dependency boundaries. The [customization guide](docs/customization.md) helps scope a change with a coding agent or another contributor.

## Set up a clean checkout

Install the SDK pinned in [global.json](global.json) and PowerShell 7 for coverage, then run:

```powershell
dotnet restore QuoteMatch.slnx
dotnet run --project src/QuoteMatch.Desktop
```

No ERP, private configuration or supplier PDF is needed. Use an isolated absolute `QUOTEMATCH_DATA_DIRECTORY` when working with demonstration data. [Getting started](docs/getting-started.md) describes storage and import setup.

## Keep changes reviewable

- Describe the concrete behavior and affected workflow before introducing a new boundary or dependency.
- Inspect existing implementations and reuse them when their semantics fit.
- Keep views focused on presentation, Application on coordination, Domain on deterministic rules and Infrastructure on external effects.
- Preserve original documents, exact codes, decimal calculations and historical snapshots.
- Keep serialized enum values, result aliases and operation recovery compatible, or provide an explicit migration.
- Use localization resources for UI text. Keep code, comments, tests and public technical documentation in English.

Use [integration contracts](docs/erp-integration.md) for external-system changes. A new provider should have a concrete adapter and test scope; it does not require a general plugin framework.

## Verification

```powershell
./scripts/Test-Coverage.ps1
dotnet format QuoteMatch.slnx --no-restore --verify-no-changes
```

Before preparing a commit, run `dotnet format QuoteMatch.slnx` and inspect its changes. For a confirmed bug, add a regression that fails because of the behavior, fix it and rerun that regression and related checks. Use real SQLite for persistence behavior and synthetic files for public document tests.

The coverage script enforces 80% aggregate line coverage for the core and the measured application assemblies. Do not exclude business code, weaken assertions or duplicate tests to satisfy the threshold. Documentation-only changes need reference and diff review rather than artificial behavior tests. See [testing](docs/testing.md).

## Data and external effects

Keep `.local/`, private quotation files, derived business fixtures, local databases, credentials and installation SQL out of staged/public content. Never force-add ignored material. Public fixtures and screenshots must be synthetic from the start.

Use database-enforced read-only permissions for reads. External order writes need an explicitly authorized disposable scope, reviewed effects, durable receipts and recovery tests. Do not change an ERP schema or write to a production order just to demonstrate a feature.

## Change description

Explain the problem and final behavior, relevant design tradeoffs, actual verification and remaining limits. Include a small before/after example when it clarifies the change. Update the affected public guides; they must not depend on private plans or another repository.

Report native platform and live integration verification separately from compilation/headless tests. Do not include private records or raw exception/connection details in a report or pull request.

## License status

A project license file has not been selected. Keep the repository's stated license status accurate and do not add a license declaration or third-party branding claim without an explicit project decision.
