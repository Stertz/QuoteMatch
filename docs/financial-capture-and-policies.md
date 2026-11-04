# Financial capture and integration policies

Financial capture is optional. Existing templates, documents, preparations and journals load with unavailable financial values. Missing values remain `null`; zero is explicit. Original PDFs, capture text and original numeric strings remain unchanged when an operator reviews an item.

The current field set includes named taxes used in Brazilian quotations. It is a finite capture model, not a universal tax calculation engine. Target schemas map these values through the [integration contracts](erp-integration.md); additional financial concepts need explicit typed/versioned changes.

## PDF mapping

Use optional named groups in the item record expression for item values, or the separate document financial expression for footer/header values:

| Group | Meaning |
| --- | --- |
| `freight` | Freight amount |
| `discount` | Discount amount, represented as a positive deduction |
| `otherCharges` | Other charge amount |
| `ipiAmount`, `ipiRate` | IPI amount and percentage |
| `icmsAmount`, `icmsRate` | ICMS amount and percentage |
| `substitutionTaxAmount`, `substitutionTaxRate` | Substitution tax amount and percentage |

A synthetic line `Freight 12,30` can use `^Freight (?<freight>[\d.,]+)$` with a `pt-BR` template. A separate `en-US` template parses `1,234.56` using that culture. Rates are percentage units: 5 means 5%, not 0.05. Amounts must be nonnegative; supported rates are 0–100. The application does not derive an amount from a rate or assume a tax-inclusive price basis.

Each item is independent; missing fields never inherit the previous item's value. Document captures retain page, matched text, original number and decimal evidence. Identical repeated footer values are not added together. Conflicting captures, invalid values and a configured document expression with no capture block confirmation until the mapping is corrected and extraction repeated. Expressions share the finite regex and overall processing budgets.

Item financial values can be reviewed separately from their immutable capture. Document captures are read only. Saving/restarting preserves both. Preparations freeze document values and provenance once per source proposal in `FinancialSources`, including source hash, template identity/version and included item count. Raw capture evidence stays in the local import record and is not sent to ERP queries. Selecting part of a PDF does not authorize copying its entire freight/taxes into every resulting order.

## Order policies

Options belong to the confirmed operation. Changing one clears the reviewed result and confirmation.

| Category | Choices |
| --- | --- |
| Prices | Selected PDF price changes; keep existing; last purchase order; zero |
| Freight, discount, other charges, taxes | Installed query policy; PDF; last purchase order; zero; keep existing |

The update-price switch selects keep-existing mode while off and disables PDF price-change choices. Keeping a price requires an existing item; it cannot invent a price for an included item. Keep-existing modes are unavailable for creation. Zero never means missing. Legacy profiles preserve their installed default behavior and reject explicit financial policies without the new contract.

Queries define eligible history, package-price conversion, allocation, tax bases, totals, precision and rounding. The application does not implement installation tax formulas. Copying a rate does not necessarily mean copying an old absolute amount to a different quantity. Missing values/history must be reported, not silently replaced by zero or a different source.

## Financial preview contract

Saved operation `PreviewPurchaseFinancials` is read only and uses the bounded, parameterized SQL execution boundary. `@RequestJson` contains the draft `SynchronizationWritePlan`, including `FinancialPolicy`, frozen selected PDF lines, proposal `FinancialSources`, status/date and target snapshot. `FinancialPreview` and `Allocation` are cleared: this query must not allocate identities, update data, depend on reserved numbers or cause external effects through a called routine.

Adding this query enables financial preview for every new operation under that profile, including default policies. Compatible read, write and reconciliation queries are also required. Its definition is pinned in the operation's script identity.

Return exactly one string column `ResultJson`. A synthetic example, not a calculation or production SQL script:

```json
{
  "Status": "Ready",
  "Revision": "synthetic-input-and-source-revision",
  "HasChanges": true,
  "Header": { "Freight": 0 },
  "OrderTotal": 420,
  "Lines": [
    {
      "Position": 0,
      "Control": "synthetic-line",
      "ProductCode": "SYN.001",
      "Unit": "RL",
      "BaseUnit": "M",
      "Quantity": 2,
      "Factor": 100,
      "UnitPrice": 210,
      "ItemTotal": 420,
      "Values": {}
    }
  ],
  "Sources": []
}
```

`Header` and line `Values` use the optional PascalCase `FinancialAmounts` fields from the mapping table. Prices/totals are the query's approved ERP values. Do not modify line identity, product, unit, quantity or factor. Position refers to the final selection order, including retained extras.

Status may be `Ready`, `MissingDocumentValues`, `MissingHistory` or `UnsupportedPolicy`. Unresolved results block submission. Revision must deterministically cover relevant inputs, approved results and source changes; exclude fetch timestamps. Historical policies require Sources with `CatalogIdentity`, `OrderId`, `Revision` and ISO `Date`. Multiple source orders are supported.

Financial-only operations need no manufactured quantity/price difference, but must return `HasChanges=true`. No-change results are not dispatched. Selected PDF-price mode preserves the selected unit prices; zero policies return explicit zero for applicable fields. The application checks identity, quantity, units, price intent, source provenance and valid decimals. Queries own ERP calculations.

## Readback and recovery

`ReadPurchaseOrder` must expose optional root `Financials`/`OrderTotal` and `Financials` on each active line for a financial-enabled profile. Legacy snapshots may omit them; a legacy read cannot authorize a new financial operation.

The preview displays the approved prices, item values, order total, charges/taxes and history references, including exact-price detail. The local journal freezes this result before dispatch. The adapter reruns the query inside the reservation/write transaction and rejects changed values/revisions before effects. Line/source ordering does not change the comparison.

Create/update queries must honor `FinancialPolicy` and `FinancialPreview`, preserve unselected fields, and return the standard receipt with `FinancialRevision` equal to the approved revision. Before commit, the adapter reads the order in the same transaction and compares its supplier, prices, conversions, line totals, header values and order total. An old script cannot silently ignore the new options.

Reconciliation verifies the durable marker and approved financial values, then includes FinancialRevision in a committed receipt. Missing/mismatched acknowledgment remains uncertain and never authorizes replay. Existing journals without financial policies retain their legacy recovery contract.

Public verification uses synthetic PDFs/results, actual SQLite persistence and desktop interactions. Fiscal layouts, installation calculations and rounding require validation for the intended environment. Financial mapping does not grant external write permissions or install target schema/trigger/index changes. See [testing](testing.md) and [project status](project-status.md).
