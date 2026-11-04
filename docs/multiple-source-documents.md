# Multiple documents when creating a purchase order

A quotation can contain several supplier documents of any supported format. Existing analysis and purchase preparation continue selecting one offer per product/comparison group. Creating a new order adds an explicit decision when at least two active, nonempty documents have the same source supplier as a selected offer.

The question appears only for creation. One document, documents from different suppliers, suppliers with no selected purchase item and updates to existing orders do not ask to combine quantities. Mixed quotations ask only for the applicable supplier groups. Supplier labels use the saved import-template supplier name, with case and repeated whitespace normalized for comparison; different names are not guessed to be the same ERP supplier. ERP buying-supplier resolution and the one-brand-per-order rule remain separate.

Each applicable supplier requires a choice before building the creation preview:

- **Keep only selected offers** preserves the existing prepared lots.
- **Add quantities** includes matching, already confirmed source rows for the selected ERP products and variants. It does not add unrelated, unselected products, unlinked rows, different variants or another supplier's offers. Individual additional lots need not cover the complete demand alone.

Quantities combine only for the same catalog, exact ERP product, confirmed variant, commercial unit, base unit and package factor. Different packages/units stay on separate order lines. Similarity-group membership never authorizes replacing a selected product with a different one.

If quantities are being added and a compatible product has different unit prices, a second choice becomes required. It stays hidden when prices are equal or the operator keeps selected offers:

| Choice | Result for compatible rows with different prices |
| --- | --- |
| Highest price | Sum quantities, use the highest source unit price, calculate quantity × chosen price. |
| Quantity-weighted average | Sum quantities; price = sum(quantity × unit price) / sum(quantity); total retains that summed extended cost. |
| Separate prices | Combine rows sharing a price; retain different prices on separate order lines. |

For example, 10 units at 5 and 20 units at 8 produce 30 units at 8 (total 240), 30 units at 7 (total 210), or two lines totaling 210. Products without a price conflict retain their reviewed reported totals. Decimal calculations do not change source values; a repeating weighted average retains decimal division precision and is subject to the installed ERP adapter's supported precision and explicit financial/rounding policy. Unsupported precision must be resolved rather than silently rounded for an ERP write.

The difference preview shows proposed quantities/prices; the final review shows actual resulting lines and totals before confirmation. Changing a document decision invalidates the preview and clears write approval. Original captures, reviewed rows, links, demand and saved purchase preparations remain unchanged. Combining quantities is not a new demand source.

## Frozen inputs and integration

`SynchronizationPreview.CreationDocuments` is null for legacy/single-document creation and updates. Otherwise it contains:

- `Choices`: normalized supplier key, explicit quantity choice and the price choice when required.
- `Groups`: supplier labels and the source document IDs, original filenames and SHA-256 values.
- `Sources`: anchor comparison-group keys and complete frozen `PurchaseLine` values for contributing reviewed rows, including proposal/capture identity, source hash and template version.
- `FinancialSources`: document-level financial values once per contributing proposal, with complete source item counts. Original raw capture text is excluded.

`SynchronizationPreview.FinancialSources` exposes the effective financial sources, including contributing additional documents. For combination-aware financial preview queries, read this collection and `CreationDocuments.Sources`, rather than assuming `Preview.Proposal.Lines` contains every source row. The proposal retains its original saved-preparation identity for validation; `Selection.Lines` contains the confirmed final order lines. Tax/freight allocation still belongs to the configured queries and must not copy each document footer to every resulting line.

The application rebuilds the combination from saved inputs before approving and dispatching. The SQLite journal repeats this validation inside its transaction before recording Pending. Forged sources, changed quotation/analysis/preparation versions and changed supplier provenance cannot silently alter an approved combination. The journal freezes the quantity/price choices and all contributing sources before reservation or dispatch. Unknown outcomes retain those decisions for reconciliation without repeating creation.

Public verification uses synthetic documents, actual SQLite and headless desktop interactions. Creation/reservation behavior, repeated-product lines, numeric precision, financial policies and write permissions require validation in the intended installation. Combining documents remains local until an external operation is explicitly confirmed. See [order integration](purchase-order-scripts.md) and [testing](testing.md).
