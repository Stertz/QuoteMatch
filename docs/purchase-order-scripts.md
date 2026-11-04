# Purchase order integration contracts

This document specifies the included SQL Server adapter's order boundary. The remote schema is installation-specific. Start with [ERP integration](erp-integration.md) for product aliases, adapter interfaces and the distinction between mapping SQL and implementing another transport.

Saving an integration query never runs it. Production queries, credentials, status codes, numbering and fiscal rules are not bundled with the repository. Public examples are synthetic.

## Execution boundary

Order operations accept SQL batches and stored-procedure calls through `Microsoft.Data.SqlClient`. `GO` is a client-side separator and is unsupported. Batches may use declarations, control flow, temporary tables and cursors, but must respect the caller's transaction and produce only their documented result.

Every batch receives `@RequestJson` as `nvarchar(max)`. Values are bound parameters. JSON uses case-sensitive English property names, ISO dates, decimal numbers and string enum names. Product codes, order IDs and line IDs remain strings. Any conversion from a target's numeric identity representation must preserve exact identity and validate range.

Order batches must return exactly one tabular result, one row and one string column named `ResultJson`. Use `SET NOCOUNT ON` and suppress incidental result sets. Empty searches return the JSON array `[]`, not an absent row. Missing required constructor data, unknown JSON properties, extra rows/result sets and invalid values are rejected.

SQL commands have a 60-second command timeout and execution/result-consumption cancellation budget. The synchronization execution adapter also applies a 120-second budget around its write/revalidation phase; this is not a single end-to-end deadline covering connection, preview and reservation. Search results are limited to 1,000 headers, snapshots to 10,000 lines and returned JSON to four million characters.

Use a deliberately configured write connection. A separate SQL login is available; explicit configuration can reuse the current SQL/Windows connection. `ApplicationIntent` does not grant or restrict permissions. Scripts are trusted integration code, and database permissions remain the authorization boundary.

`@OperatorCode` is a typed integer containing optional audit metadata, not authentication. When configured it must be positive; an installation may require it. The effective write identity, operator and relevant scripts are pinned in the reviewed profile identity, so changes require another preview. Passwords never enter request JSON or SQLite configuration.

## Read contracts

| Operation | Request | Additional parameters | Result |
| --- | --- | --- | --- |
| `SearchPurchaseOrders` | `Number`, `Supplier`, `Branch`, `Status`, `From`, `To` | `@FromDate`, `@ToDate`: nullable SQL dates | Array of headers |
| `ReadPurchaseOrder` | `OrderId` | `@OrderId`: `nvarchar(200)` | Object with `Header`, `Revision`, `Lines` |
| `FindBuyingSupplier` | `BrandId`, `Branch` | `@BrandId`, `@Branch`: `nvarchar(200)` | Array containing zero or one buying-supplier snapshot |

A header contains `Id`, `Number`, `SupplierId`, `SupplierName`, `Branch`, `Status`, `Date`, `CanEdit`. The internal ID is the identity; the displayed number need not be globally unique. `CanEdit` must reflect the target's actual policy.

A line contains `Id`, `ProductCode`, `Description`, `Unit`, `BaseUnit`, `Quantity`, `BaseUnitsPerUnit`, `UnitPrice`, `Total`, `ReceivedQuantity`, `IsCancelled`. It may also contain `SimilarityCode`, `Brand` and `Financials`.

Quantities, prices and received quantities use the commercial `Unit`. Multiplying quantity by the confirmed positive factor gives base quantity; dividing price by it gives base-unit price. Reported totals need not equal a universal quantity-times-price formula. Preserve repeated products as distinct line IDs and report actual receiving/cancellation state.

`Revision` must change whenever relevant header, item, financial, receiving or dependency data changes. Use a target revision or a deterministic fingerprint excluding fetch time. The adapter assigns the root catalog identity and UTC retrieval timestamp locally; nested brand/supplier identities must agree with that catalog. [ProtectedErpCredentials.CatalogIdentity](../src/QuoteMatch.Infrastructure/Erp/ProtectedErpCredentials.cs) defines the current profile identity. Do not invent an undocumented SQL parameter for it.

Buying-supplier snapshots include `Brand`, `Supplier`, `SourceOrderId`, `SourceOrderRevision`, `Branch`, `OrderDate`, `FetchedAtUtc`. Brand and supplier are typed identities containing `CatalogIdentity`, `Id`, `Name`. The current use case requires genuine order-history provenance and a single coherent brand. A different supplier-resolution model needs an explicit contract change.

Read scripts used within a write must honor the supplied transaction. Avoid `NOLOCK`, isolation changes, alternate connections and external effects. For JSON dates use explicit ISO conversion; use native bound dates where available. Return exact numeric types for money and factors.

## Brand synchronization contracts

The primary purchase screen uses `SynchronizationWritePlan`, independently from older `OrderWritePlan` history.

| Operation | Responsibility | `ResultJson` |
| --- | --- | --- |
| `ReservePurchaseSynchronization` | Reserve final order identity, displayed number and one line identity per selected line; do not insert the order | `OrderId`, `Number`, `LineControls` |
| `CreatePurchaseSynchronization` | Insert the saved allocation and reviewed final selection using validated installation defaults | `OperationId`, `State`, `OrderId` |
| `UpdatePurchaseSynchronization` | Apply only reviewed coherent changes and preserve unselected stored fields | `OperationId`, `State`, `OrderId` |
| `ReconcilePurchaseSynchronization` | Check the original operation, reserved target, receipt and actual result without replay | `OperationId`, `State`, `OrderId` |

These operations receive `@RequestJson`, `@OperationId` (`uniqueidentifier`) and `@OperatorCode` (`int`, nullable when unconfigured). Creation/update profile identities are distinct and pin the relevant reservation, write, read, supplier and reconciliation queries.

A write plan includes `OperationId`, `Selection`, `Profile`, optional `Status`, `Date`, optional `Allocation`, `FinancialPolicy` and optional `FinancialPreview`. `Kind` distinguishes creation/update. `TargetId` refers to the existing target or saved allocation. The desktop sends null `Status` for new orders; the installation creation script defines its initial state. The date remains explicit.

The selection includes its versioned preview, `SelectedChanges`, `SupplierId`, final `Lines` and `RemovedControls`. Each final line has `Control`, `ProductCode`, `Description`, `Unit`, `BaseUnit`, `Quantity`, `Factor`, `UnitPrice`, `Total`, `Brand`. `Control` may be null for an insertion. Creation allocation provides the exact new controls in final-selection order.

A synthetic reservation result:

```json
{
  "OrderId": "synthetic-order-1",
  "Number": "DEMO-001",
  "LineControls": ["synthetic-line-1"]
}
```

The active model requires one resulting brand. Supplier replacement, product/package replacement, quantity, price, inclusion and removal are separate reviewed decisions. Ambiguous pairing uses exact line controls. Received/non-editable lines, stale source or target data, unknown required metadata and incompatible retained extras remain blocking. Removals require explicit additional consent.

Validate selected sources and the actual target inside the transaction. Preserve unselected fields, original notes and dependent business data. Reject unsupported taxes/charges, receiving, installments, allocation or precision rather than silently discarding them. [Document-combination inputs](multiple-source-documents.md) may contain additional selected sources, and [financial policies](financial-capture-and-policies.md) require compatible preview/readback contracts.

## Numbering, receipts and recovery

SQLite and the external database do not commit atomically. The application saves the reviewed plan as `Pending` before reservation or dispatch. Creation then saves its returned allocation locally before sending the insertion. Consumed controls/numbers are not reused or blindly reserved again after an uncertain result.

Use an existing concurrency-safe allocation mechanism. An unprotected `MAX(number)+1` is insufficient. Do not assume a particular sequence/counter schema or alter the target schema as an incidental installation step. A transport that only assigns final IDs during insertion needs a deliberate creation/recovery contract adaptation instead of fabricated reservations.

The write script must use `OperationId` as an idempotency key and persist a durable marker/receipt in the same external transaction as the reviewed effects. The application owns commit/rollback; scripts and called procedures must not commit the caller's transaction or perform effects on another connection.

A synthetic successful write receipt:

```json
{
  "OperationId": "00000000-0000-0000-0000-000000000001",
  "State": "Committed",
  "OrderId": "synthetic-order-1"
}
```

The returned target must match the reviewed existing order or reserved creation identity. A financial-enabled operation also returns its approved `FinancialRevision`. The adapter reports success only after commit acknowledgment and required readback validation.

Exceptions, cancellation after dispatch, invalid receipts or lost acknowledgment leave an uncertain result. Pending/unknown work blocks another operation for the quotation or known target, including the older workflow. Operation IDs cannot be reused across journals even after commitment.

Reconciliation is read-only and returns `Committed` or `Unknown` for synchronization. An absent marker does not prove that an in-flight write cannot still commit. Do not reserve or resend the original operation while uncertain. If an existing annotation field stores a marker, preserve its contents, validate encoded capacity and verify the resulting values; a marker alone is insufficient proof.

Committed local creation journals provide order number, internal code and branch after restart, without requiring another live query merely to display the receipt. Historical versions do not undo an external write.

## Historical comparison and compatibility

Earlier saved histories use `PurchaseOrderReference`, `OrderWritePlan`, `CreatePurchaseOrder`, `UpdatePurchaseOrder` and `ReconcilePurchaseOperation`. Preserve serialized enum values, old settings and journal readability; these operations are not the primary synchronization screen's contracts.

Legacy write operations receive `@RequestJson`, `@OperationId`, `@OrderDate` and `@OperatorCode`. The plan contains `OperationId`, `Kind`, `Reference`, `ExpectedOrder`, `Branch`, `Status`, `Date`, `ScriptIdentity`, `Changes`, `CoverageOrders`, `CoveragePairs`.

Each legacy change contains `LineId`, `ReferenceLineId`, nullable `Quantity`/`UnitPrice` and `Cancel`. Existing lines use exact `LineId`; null fields preserve values. New lines use a confirmed reference identity. Cancellation needs consent and must implement the target's validated cancellation behavior rather than assuming physical deletion.

Saved comparisons allocate coverage once per external line in linked-order priority. Exact product/base-unit matches and explicit comparison-only pairings remain separate from reusable product mappings. Repeated items, excess, shortages and individual price references are preserved. Accepting a shortage does not alter original demand.

Legacy reconciliation can return `NotApplied` only after proving no committed or in-flight operation can still take effect under the same operation-key synchronization. Synchronization reconciliation deliberately accepts only `Committed` or `Unknown`.

## Verification responsibilities

Public tests cover deterministic plans, real SQLite histories/journals and synthetic adapter behavior. They do not execute production scripts or establish the target's numbering, required fields, fiscal formulas, triggers, permissions or native workflow.

Validate a disposable installation's reads, coherent changes, reservation, exact readback, concurrency, stale inputs, lost acknowledgment and restart reconciliation before operational use. Keep private queries, records and credentials outside public fixtures and reports. See [testing](testing.md) and [security](../SECURITY.md).
