# Business rules

These rules describe implemented behavior. Examples are synthetic. External database names and status codes are adapter concerns; [integration contracts](erp-integration.md) define the boundary.

## Source evidence and review

A quotation owns one or more supplier proposals. Original file bytes, SHA-256, template identity/version, extraction strategy, source page/row and captured values remain separate from reviewed corrections and exclusions.

A reviewed included item needs a code, description, unit, positive quantity and positive unit price, with a nonnegative reported total. Every captured item must be reviewed or explicitly excluded with a reason. Blocking extraction errors, incomplete reviews and empty/all-excluded proposals prevent confirmation. Missing values are not zero and are never borrowed from another record.

Automatic PDF recognition selects a template and creates a review. It does not confirm a proposal. The source's numeric culture controls parsing; the UI culture controls display. See [automatic PDF import](automatic-pdf-import.md) and [spreadsheets](excel-import.md).

## Product identity and grouping

Supplier-code documents use an exact supplier-code catalog lookup. Internal-code documents use an exact internal product-code lookup. Both preserve punctuation and leading zeros; trimming surrounding whitespace and case-insensitive comparison do not make `000.01`, `00001` and `1` equivalent.

Several candidates may share a supplier code because packaging or variants differ. A name search or comparison family supplies candidates, not authorization to substitute a product. Manual selection remains traceable to the original source item and selected catalog snapshot. Inactive products cannot be linked.

Grouping is versioned and scoped to the catalog:

- Supplier-code offers use explicit comparison-family metadata when supplied.
- Under the current name-fallback rule, an absent family can use the exact catalog product name and compatible base unit. Conflicting reviewed variants remain pending.
- Internal-code offers group by the exact product, even when family metadata is shared.
- Older saved grouping versions retain their product-code fallback behavior.
- Manual groups are an explicit offline alternative and remain distinct from catalog groups.

Grouping a set of offers does not itself choose a purchase or rewrite their identities. An adapter must expose truthful group metadata; target-schema field names are not domain rules.

## Reusable mappings and review

A reusable supplier reference includes supplier, catalog, code kind, source code, reviewed description, color/variant and quoted unit. Reference keys normalize case and whitespace while retaining punctuation, accents and variant distinctions. A supplier code alone is insufficient to share mappings between variants.

Mappings retain the exact product snapshot, base unit and confirmed decimal factor. Updates protect creation metadata, advance a version and reject stale writes. A new import may reuse a previously confirmed active mapping with a complete compatible identity without a remote lookup; other suggestions still need review. Failure of this optional step preserves the saved source proposal.

The shared review grid can save selected links, optional mappings, quantities and staged order references in one SQLite transaction. Invalid selected rows, conflicting duplicate references or stale versions prevent the batch from partially saving. A repeated reference can share a mapping only when product, unit and factor agree.

The individual editor saves an item link before its optional reusable mapping. If that later mapping operation fails, the item remains linked and the pending mapping is reported. Remember-mappings preferences suggest a choice; they do not authorize a new save automatically.

## Units, factors and prices

All quantities, conversion factors and money use `decimal`. The adapter supplies the catalog base unit and, when known, base units per commercial alternative. Labels such as `RL`, `BOB` or `KM` never imply a factor.

For three packages containing 100 base units each:

```text
Base quantity = quoted quantity × confirmed factor = 3 × 100 = 300
Base unit price = quoted unit price ÷ confirmed factor
```

Factor 1 is explicit when the source already uses base units. A known base unit cannot be relabeled as an unrelated unit. Factors must be positive and representable in decimal; an unrepresentable repeating quantity/factor ratio requires review rather than silent rounding.

The adapter's last-purchase price is already per base unit. Do not divide it again by a packaging factor. Missing history or incompatible units leave that reference unavailable. Refreshing product/history data previews a new snapshot and requires confirmation; it does not rewrite past analyses.

Display precision does not replace calculation inputs. The supplier's reported whole-lot total remains the source cost; it is not silently recalculated from rounded display values. Financial write precision and rounding belong to the validated integration policy.

## Requested quantities and price references

Desired quantity is a separate input from the quoted quantity and packaging factor. It may be entered manually or adopted explicitly from one document or a linked order reference. Document provenance includes source hash, item and factor; order provenance includes exact catalog/order/revision/line identities.

Linked-order demand uses complete active ordered quantities normalized to the base unit. Received quantities are shown separately and do not reduce that original demand. Family demand can combine different identified brands without choosing substitutes. Repeated refreshes must not count a source line twice. Manual quantities remain unless replacement is explicitly chosen.

The selected price reference is persisted as last purchase or linked order. A missing order reference never silently falls back to last purchase. When several order lines contribute, the normalized price reference is:

```text
Weighted reference price = Σ(base quantity × base unit price) ÷ Σ(base quantity)
```

Unknown values and a zero denominator leave the result unavailable. Inputs and reference snapshots are versioned so historical prices and quantities remain consultable offline.

## Comparison and recommendations

Analysis distinguishes normalized unit price from the reported cost of an entire lot that covers demand. A recommendation needs identified compatible offers, a positive known demand and one unique eligible lowest-cost lot. Missing demand, conflicting units/variants, insufficient lots and tied lowest costs leave the recommendation pending.

| Supplier | Offered quantity | Base unit price | Reported lot cost | Excess over demand |
| --- | ---: | ---: | ---: | ---: |
| A | 1,500 m | 8.20 | 12,300.00 | 0 m |
| B | 2,000 m | 7.80 | 15,600.00 | 500 m |
| C | 1,500 m | 8.00 | 12,000.00 | 0 m |

For demand of 1,500 m, B has the lowest unit price and C has the lowest eligible whole-lot cost. Analysis suggestions remain separate from saved purchase choices.

The displayed metrics use explicit reference inputs:

| Metric | Formula |
| --- | --- |
| Unit-price premium | `(offer base price - lowest base price) / lowest base price × 100` |
| Maximum unit-price savings | `(highest base price - lowest base price) / highest base price × 100` |
| Change from purchase/order reference | `(quoted base price - reference base price) / reference base price × 100` |

Missing/zero reference values, incompatible units and overflow do not produce a percentage. Calculation help exposes the actual inputs. Filters change presentation, not totals or identity.

Supplier quoted totals remain scoped to source proposal IDs. A common package comparison includes only eligible unambiguous groups. Equal normalized quantities permit comparison before demand; unequal quantities need demand coverage. Missing or conflicting groups remain visible as pending. Package comparison does not set demand, select products or imply a complete landed cost.

## Purchase preparation and continuity

An explicit purchase can choose any eligible whole lot, including a higher-priced or tied offer. Desired quantity is optional for that explicit choice; if supplied, the lot must cover it. Before external creation with missing demand, the UI offers review, an order reference, continuing without demand or canceling.

Each choice/removal appends a local preparation version. Applying analysis suggestions explicitly replaces choices with the eligible unique recommendations. Partial preparations are allowed, and removing the last choice records an empty version.

With one source supplier and no saved preparation, the initial preparation selects each eligible group with exactly one offer. Ambiguous, unidentified and insufficient items remain pending. Saved choices, deliberate removals and empty preparations take precedence; multiple source documents do not imply adding quantities.

When quotation/analysis inputs change, opening a draft preparation appends a version retaining exact previously chosen source rows that remain eligible with the same product/catalog, variant, units and factor. Invalidated choices and merged groups with competing old choices remain pending. Another supplier is not selected automatically. Historical preparation snapshots remain unchanged. Finalized quotations are read-only until explicitly reopened.

The item subtotal sums selected reported lot totals. It excludes unresolved groups and is not a universal landed-cost calculation. Saved preparation records pin quotation-input/analysis versions, source hashes, template versions, identities, factors, demand and financial provenance. Repository transactions reconstruct and validate those records from owned inputs.

## Combining source documents

Creation can explicitly keep selected offers or add compatible quantities from multiple documents belonging to the same source supplier. Same-product additions require confirmed identity, variant, units and factor. Different prices require a highest-price, weighted-average or separate-line decision.

These decisions do not rewrite original offers, desired quantities or saved purchase choices. [Multiple source documents](multiple-source-documents.md) specifies frozen sources and financial provenance.

## External order synchronization

The active synchronization model partitions proposals by explicit brand metadata and resolves a buying supplier with history provenance. Brand and buying supplier are distinct. One resulting order must contain one brand. Missing metadata/history and incompatible retained extras block the operation rather than inventing a supplier relationship.

The preview shows actual supplier, product/package, quantity, price, inclusion and removal differences. Update differences begin unselected; creation builds its selected insertion set from the prepared lots. Removals require separate confirmation. Received/non-editable lines and stale inputs remain protected.

A valid reviewed plan enables the final create/update action. No additional general write checkbox is required. Initial order status is supplied by the installation script; the date is explicit. The last branch used in a valid review is suggested locally and remains editable.

Before dispatch, the operation is saved as pending. Creation saves its reserved number and internal controls before insertion. Confirmed receipts provide the generated order number/code/branch after creation and offline reopening. Pending or uncertain reservations are not labeled as created orders.

Unknown outcomes block another submission until reconciled. Reconciliation checks the original identity and result without resending. No transaction spans SQLite and the external database. Installation adapters own permitted states, required/dependent fields, numbering, totals and durable receipts. See [order contracts](purchase-order-scripts.md) and [financial policies](financial-capture-and-policies.md).

## History, deletion and credentials

Saved captures, links, references, analyses, preparations and operation journals remain available offline. Comparisons can preserve multiple-order coverage and accepted-shortage decisions without rewriting demand. Quotations with comparison/operation history are protected from deletion to preserve consultation and recovery.

Code changes and migrations must preserve serialized identities and old journal readability. Stale local writes fail; they do not replace a newer result. Local finalization does not create an external order.

Credentials use the platform protection described in [security](../SECURITY.md). Connection testing, query validation and live operation validation are separate checks. Diagnostics expose sanitized stages/counts, never credentials, private SQL or business records.
