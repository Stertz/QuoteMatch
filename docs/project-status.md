# Project status and scope

QuoteMatch is a working desktop application under active development. It is suitable for evaluating the implemented workflows, architecture and public verification from source. A configured installation is separate from the application repository.

## Implemented

| Area | Current capability |
| --- | --- |
| Documents | Text-based PDF extraction, automatic template suggestion, spreadsheet formats and explicit review |
| Traceability | Original file bytes/hash, versioned templates, capture evidence and reviewed corrections |
| Analysis | Confirmed product identity/conversion, optional demand, price references and whole-lot comparisons |
| Purchasing | Versioned choices, initial single-supplier defaults, retained eligible choices and document-combination decisions |
| Local persistence | SQLite, migrations/backups, exact decimal snapshots and offline reloads |
| Integration | Configurable SQL Server reads, order previews, confirmed dispatch boundaries and reconciliation contracts |
| Presentation | Brazilian Portuguese/English resources, themes, keyboard interactions and accessible calculation help |
| Verification | Synthetic fixtures, real SQLite and headless desktop suites with aggregate coverage enforcement |
| Distribution | Source-derived README badges, native release packaging and an explicit client update/restart workflow |

## Installation-specific work

Every integration must establish product-code semantics, base units/factors, grouping, brand/buying-supplier metadata, permitted order states, allocation/numbering, totals, precision and durable receipt storage. Financial policies need compatible preview, read, write and reconciliation implementations.

The public repository includes no production SQL, credentials or compatibility certification for a named ERP. Connecting a different SQL Server schema may require query mapping; a different transport or different business semantics may require application and adapter changes. Follow [customization](customization.md).

## Current limits

- No OCR for image-only PDFs and no formula recalculation for spreadsheets.
- Unconfirmed import reviews are not persisted as drafts.
- Purchase comparison operates on complete quoted lots. Divisible purchases and supplier-wide minimum conditions are not modeled.
- The active order workflow requires one brand per resulting order and buying-supplier provenance from order history.
- Financial fields are finite, with several named tax fields used in Brazilian quotations. They do not form a universal tax engine.
- Monetary display currently uses Brazilian reais (`R$`); configurable multi-currency support is not implemented.
- Payment installments, cost-center allocation and other policies outside the modeled fields require further integration work.
- SQL Server is the only included external-system transport. No generic API connector or runtime plugin framework is provided.
- Windows DPAPI persists SQL passwords. Native persistent keychains for other platforms are not implemented.
- The data store has migration backups, but there is no dedicated backup/recovery UI.
- Release packaging and the client updater are implemented; hosted CI execution, signing/notarization and native first-install/version-update acceptance still need target-environment validation. See [releases and updates](releases-and-updates.md).

## Verification boundaries

The CI definition targets Windows, Linux and macOS. A configured matrix is not evidence that a particular run, native desktop workflow, protected credential store or release package passed on every platform.

Public tests do not execute production ERP writes or supplier-specific private documents. A green test run establishes the tested contracts and synthetic scenarios; it does not certify installation SQL, fiscal layouts or operational workflows.

## Repository readiness

Documentation, contribution instructions and agent directives are self-contained. Private development material remains ignored. The repository owner has not selected a project license file; maintain that status explicitly until one is added.
