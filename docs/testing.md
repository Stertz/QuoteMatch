# Testing and line coverage

Verification is reproducible from a clean checkout with synthetic inputs. No ERP connection, credentials, private supplier file or local planning workspace is required.

## Run the checks

Run from the repository root with the SDK selected by [global.json](../global.json) and PowerShell 7:

```powershell
./scripts/Test-Coverage.ps1
dotnet format QuoteMatch.slnx --no-restore --verify-no-changes
```

The script restores and builds the solution with warnings as errors, then runs both core and Avalonia headless suites. It writes isolated build artifacts under `.local/verification/` and reports under `.local/coverage/`; both are ignored generated output, not required input.

Run `./scripts/Test-ReleaseAutomation.ps1` for the README generator's version/coverage contracts. Packaging uses the real Velopack CLI and validates its feed, package payload, notes and checksums. See [releases and updates](releases-and-updates.md) for commands, pipeline permissions and native installation/update verification limits.

Use `-NoRestore` only after restoring the same artifacts directory. `-ArtifactsDirectory` and `-OutputDirectory` select other output paths; they resolve from the repository root. For example:

```powershell
./scripts/Test-Coverage.ps1 -ArtifactsDirectory artifacts/verification -OutputDirectory artifacts/coverage
```

For changes without coverage collection, the solution also supports:

```powershell
dotnet build QuoteMatch.slnx -p:ContinuousIntegrationBuild=true
dotnet test --solution QuoteMatch.slnx
```

Before a commit, run `dotnet format QuoteMatch.slnx` and inspect its changes. Documentation-only changes need source/reference and diff review; do not create artificial tests for prose.

## What is measured

The Microsoft.Testing.Platform coverage extension emits Cobertura reports from both suites. [Collector configuration](../tests/coverage.config.xml) measures Domain, Application, Infrastructure and Desktop; test assemblies, dependencies and code marked with `GeneratedCodeAttribute` are excluded. Async/LINQ business code is retained. The diagnostics command-line entry point is outside this measured scope.

[The coverage script](../scripts/Test-Coverage.ps1) merges source-line hits by module/file/line, counting a line exercised by either suite once. `summary.json` contains per-module counts plus combined core and total percentages. Both the core aggregate (Domain/Application/Infrastructure) and total aggregate (all four) must reach 80%.

This is line coverage, not branch coverage or proof of correctness. Do not lower the threshold, exclude business code, duplicate cases or weaken assertions to obtain a passing result.

## Test layers and evidence

| Area | What public tests establish | Representative sources |
| --- | --- | --- |
| Domain | Exact codes, grouping versions, decimal conversion, comparison formulas, eligibility, ties and retained choices | [Imported analysis](../tests/QuoteMatch.Tests/ImportedAnalysisTests.cs), [purchase preparation](../tests/QuoteMatch.Tests/PurchasePreparationTests.cs) |
| PDF import | Actual PdfPig output, bounded regex, culture, page evidence, invalid records and first-match recognition/cache reuse | [PDF import](../tests/QuoteMatch.Tests/PdfImportTests.cs), [automatic recognition](../tests/QuoteMatch.Tests/AutomaticPdfTemplateTests.cs) |
| Spreadsheets | Actual format readers, cached formulas, formatted codes, encoding, mapped headers and original-file recovery | [Import](../tests/QuoteMatch.Tests/SpreadsheetImportTests.cs), [formats](../tests/QuoteMatch.Tests/SpreadsheetFormatTests.cs) |
| Persistence | Real SQLite migrations/backups, transactions, exact decimal snapshots, revision conflicts and unchanged history | [Quotation migration](../tests/QuoteMatch.Tests/QuotationInputMigrationTests.cs), [preparation](../tests/QuoteMatch.Tests/PurchasePreparationTests.cs) |
| Catalog boundary | Typed results, exact alternative/internal codes, null history, limits and invalid-contract rejection | [Read contracts](../tests/QuoteMatch.Tests/ErpReadContractTests.cs), [identification](../tests/QuoteMatch.Tests/ErpIdentificationTests.cs) |
| External writes | Frozen plans, durable local journals, reservation receipts, stale inputs and reconciliation without replay using synthetic adapters | [Execution](../tests/QuoteMatch.Tests/SynchronizationExecutionTests.cs), [financial contracts](../tests/QuoteMatch.Tests/SynchronizationFinancialTests.cs) |
| Desktop | Actual ViewModels/repositories with headless keyboard, dialogs, selection, file-drop events, localization and themes | [Automatic import](../tests/QuoteMatch.Desktop.Tests/AutomaticPdfImportWorkspaceTests.cs), [purchase continuity](../tests/QuoteMatch.Desktop.Tests/PurchaseContinuityTests.cs) |

The suites also cover quotation lifecycle/ownership, original evidence, atomic multi-item review, demand provenance, mapping reuse, package combinations, financial-policy invalidation, literal wildcard handling and preferences. Assertions use explicit observable outcomes rather than copying production calculations into the test.

## Regression discipline

For a confirmed behavioral defect:

1. Reproduce it at the smallest faithful level.
2. Verify the test fails because of that behavior, not compilation, a broken fixture or an unavailable dependency.
3. Fix the implementation and verify the same test passes.
4. Run relevant regressions and the required coverage/build/format checks.

Use actual SQLite where ordering, decimals, migrations or transactions matter. Control clocks/concurrency where relevant. Synthetic ERP adapters test the application boundary; they must not remove the semantics responsible for a failure.

## CI and platform boundaries

[The CI workflow](../.github/workflows/verify.yml) defines Windows, Linux and macOS jobs, rejects tracked private development material, runs coverage and formatting, and uploads only generated synthetic coverage reports. A workflow definition is not a claim that a particular hosted run passed.

Compilation/headless results do not establish native rendering, operating-system file drag-and-drop, packaging or successful live ERP execution. Record those checks separately for each intended environment. A SQL connection opening successfully does not validate query results, permissions, order allocation, financial rules or recovery.

Native DPAPI round-trip assertions run when supported. A restricted Windows profile instead verifies a sanitized protection failure with no plaintext fallback. Other platforms' session-only password behavior does not establish persistent keychain support.

## Public fixtures and reports

Generate PDFs and workbooks from synthetic data inside tests. Label demonstration screenshots clearly. Never import a private quotation or rename derived business data into a public fixture.

Keep original business documents, private queries, connection profiles, credentials, local databases and private plans out of staged content and CI artifacts. Generated reports must not include those records. See [security](../SECURITY.md) and [agent directives](../AGENTS.md).
