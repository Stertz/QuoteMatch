# Getting started

## Prerequisites

- Install the .NET SDK selected by [global.json](../global.json).
- Allow access to the package feed for the initial restore.
- Install PowerShell 7 to use the repository's coverage script.

An ERP account and an installed office suite are not prerequisites. Avalonia native dependencies and packaging need verification for the operating system where the desktop will run; the automated CI matrix alone does not establish native runtime support.

## Start the application

Run from the repository root:

```powershell
dotnet restore QuoteMatch.slnx
dotnet run --project src/QuoteMatch.Desktop
```

The application creates its local data store on startup. Settings control language and appearance. The default UI culture is `pt-BR`; `en-US` resources are also included. Imported supplier descriptions are not translated.

## Use an isolated data directory

The default location is the current user's local application-data directory under `QuoteMatch`. SQLite, preferences and protected credential files use this application directory.

An absolute `QUOTEMATCH_DATA_DIRECTORY` value selects another location. For example, in PowerShell:

```powershell
$env:QUOTEMATCH_DATA_DIRECTORY = Join-Path ([System.IO.Path]::GetTempPath()) 'QuoteMatch-Demo'
dotnet run --project src/QuoteMatch.Desktop
```

Use synthetic documents in a demonstration workspace. This override changes local storage; it does not install an integration or grant external-system permissions. Unset the environment variable to return to the default location.

## Import and review

1. Create a quotation with a short name.
2. Create a document template from a sample file. Choose an explicit numeric culture and commercial unit.
3. For PDFs, define the identifier, record boundaries and named capture groups. For spreadsheets, map worksheet, rows and columns.
4. Choose or drop a document in the quotation import window. Recognizable PDFs start extraction automatically; spreadsheet mapping and unmatched PDFs remain manual.
5. Review values and visible warnings, then add the proposal explicitly.

Original bytes, source hashes, template versions, captured fields and reviewed corrections are saved separately. A source quotation cannot be confirmed when its review is incomplete or blocked. Unconfirmed reviews are in memory and must be added before closing to persist them.

See [PDF recognition](automatic-pdf-import.md) and [spreadsheet mapping](excel-import.md) for supported formats and limits.

## Compare and prepare purchases

Identify products manually or through a configured catalog, confirm numeric packaging factors and enter demand when available. Analysis explains normalized prices and whole-lot costs. Preparing a purchase saves local choices and does not send an order.

The shared review grid manages product links, conversions, optional desired quantities and order references. A first preparation with one supplier selects eligible unambiguous offers; subsequent explicit choices and removals remain saved. [Business rules](business-rules.md) explain eligibility and historical revisions.

## Add external integration when needed

The current settings screen configures a SQL Server connection and named query operations. Saving settings does not execute a query. Test the displayed connection, save it, then validate read contracts using a read-only account before configuring writes.

[ERP integration](erp-integration.md) distinguishes SQL mapping from implementing a new adapter. [Purchase order contracts](purchase-order-scripts.md) describe confirmed writes and recovery. Credentials and installation SQL belong to local configuration, not to public fixtures.

## Verify a checkout

```powershell
./scripts/Test-Coverage.ps1
dotnet format QuoteMatch.slnx --no-restore --verify-no-changes
```

The script generates synthetic inputs and isolated test stores. See [testing](testing.md) for report locations, measured assemblies and platform limits.

## Backups

Before applying migrations to an existing SQLite database, the application creates a consistent backup in the data directory's `backups` folder. Close the application before restoring a backup. A dedicated backup/restore interface is not implemented; keep the complete data directory and its protected credentials scoped to the appropriate user account.
