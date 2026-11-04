# External catalog and ERP integration

QuoteMatch's local workflow is independent of an external system. The included integration uses SQL Server and configurable queries to translate a target schema into typed application contracts. Table names, status values, numbering and financial formulas are installation details.

## Choose the extension point

| Target | Expected work |
| --- | --- |
| Another SQL Server schema with compatible business semantics | Map saved queries into the current result contracts; validate permissions, values and recovery |
| Another database engine | Implement infrastructure readers/writers and connection/credential configuration for that provider |
| An HTTP API or another transport | Implement the application boundary interfaces, including cancellation, typed results, identity and recovery; add appropriate configuration/composition |
| A different purchasing model | Review Domain/Application contracts and version compatibility as well as the adapter |

Settings currently configure the SQL Server implementation. There is no runtime adapter/plugin loader. A new provider is a source-code change, not a connection-string substitution.

## Read-only catalog operations

| Saved operation | Bound parameter | Purpose |
| --- | --- | --- |
| `FindProduct` | `@SupplierProductCode` | Exact supplier-code identification; multiple commercial alternatives may be returned |
| `GetEquivalentProducts` | `@SimilarityCode` | Investigate an already known comparison family |
| `FindProductById` | `@ProductCode` | Exact internal catalog-code lookup |
| `SearchProducts` | `@SearchText` | Ordered product-name terms; candidates still require selection |

Supplier-code identification never starts with a fuzzy description search. Preserve code punctuation and leading zeros. The application rejects supplier-code results that do not correspond to the requested code. Internal-code imports remain exact-product groups even when comparison-family metadata is shared.

Product queries return one tabular result, with at most 1,000 rows. Values use named parameters; code/search inputs are bounded. The configured connection timeout defaults to 30 seconds, the product command timeout is 15 seconds and execution/result consumption has a 60-second cancellation budget.

Single-query mode accepts a bounded `SELECT` or CTE shape. Script mode explicitly permits read procedures/batches while retaining parameters, one typed result set, limits and cancellation. Both modes require database-enforced read-only permissions. Query validation and `ApplicationIntent=ReadOnly` do not make trusted SQL safe to run with unrestricted credentials.

## Product result aliases

The SQL adapter currently accepts the following compatibility aliases. They are output names, not required names for the target database's tables or columns. A query may select, join, calculate or alias a target schema into them. Keep these names stable unless implementing a deliberate compatibility migration.

| Alias | Application meaning | Value expectations |
| --- | --- | --- |
| `Codigo` | Internal product code | Nonempty text; preserve identity |
| `Nome` | Catalog product name | Nonempty text |
| `Semelhante` | Comparison-family identifier | Text; null/empty means no supplied family |
| `Inativo` | Inactive state | Boolean or integer 0/1 |
| `FornecedorID` | Supplier-reference identity on the candidate | Text; not authority to choose the order's buying supplier |
| `Fornecedor` | Candidate supplier-reference label | Text |
| `CodigoFornecedor` | Supplier product code | Exact requested code for supplier-code identification |
| `Quantidade` | Base units per commercial alternative | Decimal/integer; null when not supplied |
| `UnForcli` | Commercial-alternative unit label | Text; a label does not imply a factor |
| `Data` | Last-purchase date | Date/date-time or null |
| `VlrUnit` | Last-purchase price per base unit | Decimal/integer or null; do not divide it by the alternative factor |
| `UnidadeBase` | Catalog base unit | Optional result column; unknown unit requires explicit review |
| `BrandId`, `BrandName` | Explicit product brand metadata | Optional pair; supply both or neither |

The first eleven aliases must exist even when their permitted values are empty/null. Decimal fields reject binary floating-point results; return an appropriate exact numeric type. Direct internal-code and name searches can leave supplier-alternative fields empty and the alternative factor null. Missing purchase history remains unknown.

This synthetic contract example uses no external tables and returns a row only for a demonstration supplier code:

```sql
SELECT
    N'CAT-001' AS Codigo,
    N'Synthetic cable' AS Nome,
    N'SYN-FAMILY' AS Semelhante,
    CAST(0 AS bit) AS Inativo,
    N'SYN-SUPPLIER' AS FornecedorID,
    N'Synthetic supplier' AS Fornecedor,
    @SupplierProductCode AS CodigoFornecedor,
    CAST(100 AS decimal(19, 4)) AS Quantidade,
    N'RL' AS UnForcli,
    CAST(NULL AS date) AS Data,
    CAST(NULL AS decimal(19, 4)) AS VlrUnit,
    N'M' AS UnidadeBase,
    N'SYN-BRAND' AS BrandId,
    N'Synthetic brand' AS BrandName
WHERE @SupplierProductCode = N'SYN-001';
```

It demonstrates result shape, null history and conversion metadata. It is not a production catalog or an order integration.

For name search, the application escapes literal wildcard characters and inserts `%` between terms. SQL Server search scripts must honor the escaped pattern using the configured contract's `LIKE`/escape behavior, bound values, deterministic ordering and row limit. See [ProductNameSearch](../src/QuoteMatch.Infrastructure/Erp/ProductNameSearch.cs).

## Orders, brands and buying suppliers

`SearchPurchaseOrders` and `ReadPurchaseOrder` provide typed, versioned snapshots. `FindBuyingSupplier` uses `@BrandId`, `@Branch` and `@RequestJson` to return zero or one `BuyingSupplierSnapshot` in `ResultJson`.

The current buying-supplier model includes source order identity, revision, date and branch. Brand identity and the supplier receiving an order are separate. An installation with only a direct master-data relationship cannot fabricate historical order provenance to satisfy this interface; adapting that model requires a reviewed contract/rule change.

The active synchronization workflow creates one-brand proposals and requires a resulting order to contain one brand. Unknown metadata, incompatible units, received items and non-editable orders remain blocking. These are current product rules, not universal ERP assumptions.

[Purchase order contracts](purchase-order-scripts.md) define reads, reservation, confirmed writes, receipts and reconciliation. [Financial policies](financial-capture-and-policies.md) define the optional financial preview/readback boundary.

## Implement a different adapter

The existing boundaries are:

| Interface | Contract source | Responsibility |
| --- | --- | --- |
| `IErpProductLookup` | [ErpContracts.cs](../src/QuoteMatch.Application/Integration/ErpContracts.cs) | Supplier-code and comparison-family reads |
| `IErpProductSearch` | [ProductMappingContracts.cs](../src/QuoteMatch.Application/Integration/ProductMappingContracts.cs) | Catalog identity, internal-code and name searches |
| `IErpBuyingSupplierLookup` | [ErpContracts.cs](../src/QuoteMatch.Application/Integration/ErpContracts.cs) | Typed buying-supplier resolution with provenance |
| `IPurchaseOrderReader` | [PurchaseOrderService.cs](../src/QuoteMatch.Application/Purchasing/PurchaseOrderService.cs) | Order searches and versioned snapshots |
| `IPurchaseSynchronizationWriter` | [SynchronizationWriteService.cs](../src/QuoteMatch.Application/Purchasing/SynchronizationWriteService.cs) | Profile identity, reservation, execution, reconciliation and optional financial preview |
| `IErpCredentials` | [ErpContracts.cs](../src/QuoteMatch.Application/Integration/ErpContracts.cs) | Protected connection credentials for the existing configuration model |

Implement transport code in Infrastructure and wire it through [WorkspaceViewModel](../src/QuoteMatch.Desktop/ViewModels/WorkspaceViewModel.cs), the current composition location. Configuration for a new transport may need its own typed model and UI. Do not store API tokens in SQL fields or add plaintext credential fallback.

Preserve stable string identities, catalog scoping, exact numeric semantics, cancellation, bounded results and sanitized errors. A writer must meet the operation-journal/recovery contract; implementing a successful request alone is insufficient. Existing legacy writer interfaces support saved historical operations and should not be removed casually.

## Validate an installation

1. Establish the target's product, unit, family, brand, supplier and order semantics from authorized schema/API documentation.
2. Develop mappings/adapters against synthetic fixtures and an isolated local data directory.
3. Validate read-only lookups, missing/ambiguous results, exact conversions and offline snapshots.
4. Establish order defaults, state transitions, numbering, dependent fields, financial rules and durable receipt storage in a disposable target environment.
5. Exercise concurrency, stale previews, partial failures, lost acknowledgment and reconciliation before enabling writes.

Public tests and examples never require production access. Keep installation queries, credentials and private business records outside published repository content. [Customization](customization.md) provides a bounded workflow for contributors and coding agents.
