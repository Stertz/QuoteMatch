<h1 align="center">QuoteMatch</h1>

<p align="center">An offline-first desktop application for supplier quotations and purchasing decisions.</p>

<p align="center">
  <!-- PROJECT-BADGES:START -->
  <a href="https://github.com/Stertz/QuoteMatch/actions/workflows/verify.yml"><img src="https://github.com/Stertz/QuoteMatch/actions/workflows/verify.yml/badge.svg" alt="Build and tests" /></a>
  <a href="https://github.com/Stertz/QuoteMatch/releases"><img src="https://img.shields.io/github/v/release/Stertz/QuoteMatch?label=release" alt="Latest release" /></a>
  <a href="https://github.com/Stertz/QuoteMatch/actions/workflows/verify.yml"><img src="https://img.shields.io/badge/Coverage-90.92%25-brightgreen" alt="Coverage 90.92%" /></a>
  <a href="https://github.com/Stertz/QuoteMatch/actions/workflows/verify.yml"><img src="https://img.shields.io/badge/Core-87.62%25-brightgreen" alt="Core 87.62%" /></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10.0" /></a>
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/SDK-10.0.402-512BD4" alt="SDK 10.0.402" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/Avalonia-12.1.4-FF6600" alt="Avalonia 12.1.4" /></a>
  <a href="https://learn.microsoft.com/ef/core/"><img src="https://img.shields.io/badge/EF%20Core-10.0.14-512BD4" alt="EF Core 10.0.14" /></a>
  <a href="https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/"><img src="https://img.shields.io/badge/MVVM%20Toolkit-8.4.2-0078D4" alt="MVVM Toolkit 8.4.2" /></a>
  <a href="https://learn.microsoft.com/sql/connect/ado-net/"><img src="https://img.shields.io/badge/SqlClient-7.1.1-CC2927" alt="SqlClient 7.1.1" /></a>
  <!-- PROJECT-BADGES:END -->
</p>

QuoteMatch imports PDFs and spreadsheets, preserves their original evidence, normalizes confirmed product quantities and prices, and keeps a versioned record of purchase choices. An optional integration can read an external catalog and synchronize purchase orders after an explicit preview and confirmation.

The application is independent of any ERP vendor. The current external-system adapter uses configurable SQL Server queries; it does not require a particular vendor's tables or ship production integration scripts. Other database engines and API-based integrations require an adapter implementation. See [integration and customization](docs/erp-integration.md).

## What it does

- Imports text-based PDFs, Excel, LibreOffice/OpenDocument and CSV/TSV files through versioned templates.
- Suggests a PDF template and extracts a review when its identifier and item layout match; supports file drop and manual override.
- Preserves original files, hashes, source locations, captured values and reviewed corrections.
- Separates product identification, packaging conversion, requested quantities, price references and purchase choices.
- Compares normalized unit prices and complete quoted lots, with accessible calculation details.
- Saves local revisions, reusable confirmed mappings, purchase choices and order-operation receipts for offline consultation.
- Previews external order creation or changes, records the operation before dispatch and reconciles uncertain outcomes without blindly resending.

Local quotation work needs no ERP connection, cloud account, installed spreadsheet application or proprietary UI license. The interface defaults to Brazilian Portuguese and includes English resources, light/dark/system themes and keyboard-accessible workflows.

## Downloads and updates

Find installers and self-contained builds in [Releases](https://github.com/Stertz/QuoteMatch/releases): Windows x64, Linux x64, macOS Intel and macOS Apple Silicon. If no release is listed yet, use the source instructions below.

Installed releases check for updates in the background. The footer shows the current version and announces a downloaded update. Open it to read what's new, then choose **Install and restart** when ready. Source builds remain usable without an update feed. See [releases and updates](docs/releases-and-updates.md) for formats, channels and verification limits.

## Run from source

Install the .NET SDK selected by [global.json](global.json), then run:

```powershell
dotnet restore QuoteMatch.slnx
dotnet run --project src/QuoteMatch.Desktop
```

Package versions are centralized in [Directory.Packages.props](Directory.Packages.props). Initial restoration requires package-feed access; local quotation work is offline.

The application creates its SQLite database and preferences under the current user's local application-data directory in `QuoteMatch`. For an isolated data directory, set `QUOTEMATCH_DATA_DIRECTORY` to an absolute path before starting. No local profile, private document or development note is required. See [getting started](docs/getting-started.md).

## Workflow

1. Create a quotation and import a supplier document. Review captured items before adding the proposal.
2. Confirm product identity and numeric packaging factors. Enter desired quantities or use a document/order reference when appropriate.
3. Compare prices and complete lots. A lower unit price and a lower whole-lot purchase cost are different decisions.
4. Prepare purchases by item or supplier package. Choices are versioned; valid choices survive analysis updates. A first preparation with one supplier selects unambiguous eligible lots initially.
5. Optionally preview and confirm external order creation or updates through a configured integration. Finalizing a local quotation does not create an external order.

See [business rules](docs/business-rules.md) for formulas, eligibility, identity and revision behavior.

## Engineering overview

| Area | Implementation |
| --- | --- |
| Desktop | C#, Avalonia, Fluent Theme, MVVM and localized resources |
| Application | Import, product-review and purchasing use cases with explicit boundary interfaces |
| Domain | Deterministic identity, conversion, comparison and purchase rules using `decimal` |
| Persistence | EF Core and SQLite; immutable source evidence and versioned snapshots |
| Extraction | PdfPig and bounded regex; spreadsheet readers without an office-suite runtime |
| Optional integration | SQL Server adapter, parameterized contracts, protected credentials and operation journals |
| Verification | Synthetic fixtures, actual SQLite, headless desktop interactions and aggregate line-coverage checks |

[Architecture](docs/architecture.md) explains dependency direction, data ownership, recovery and the current integration boundaries. [Customization](docs/customization.md) gives contributors and coding agents concrete starting points for adapting the application.

## Verify

With PowerShell 7 available:

```powershell
./scripts/Test-Coverage.ps1
dotnet format QuoteMatch.slnx --no-restore --verify-no-changes
```

The coverage script restores, builds with warnings as errors, runs both test suites and enforces at least 80% aggregate line coverage for the core and all measured application assemblies. Tests do not require supplier documents, credentials or a running ERP. The CI workflow configures the same checks on Windows, Linux and macOS; this is separate from native desktop and packaging verification. See [testing](docs/testing.md).

## Scope and current limits

- PDFs require a text layer; OCR is not implemented. Unconfirmed import reviews remain in memory.
- Purchase comparison uses complete lots. Divisible purchases and supplier-wide purchase constraints are not implemented.
- Financial captures and policy contracts exist, but tax calculations, allocation, rounding and order defaults belong to the installed integration.
- The current order model uses explicit brand metadata and history-backed buying-supplier resolution. Adapters must supply those semantics or the application contracts must be deliberately adapted.
- Persistent SQL-password protection uses Windows DPAPI. Other platforms retain SQL passwords only for the running session.
- Native runtime behavior, packaging and live integration behavior need validation for each target environment. No vendor compatibility certification or universal ERP support is claimed.

See [project status](docs/project-status.md) for the boundary between implemented capabilities and work that requires further validation.

## Documentation and contribution

- [Documentation index](docs/README.md)
- [Contribution guide](CONTRIBUTING.md)
- [Agent directives](AGENTS.md)
- [Integration contracts](docs/erp-integration.md)
- [Security and sensitive data](SECURITY.md)

Use synthetic data in public examples, tests and screenshots. Private documents, credentials, installation queries and local plans must remain outside published repository content.

## License

A project license file has not been selected yet. Dependency licenses are separate from the project's license status.
