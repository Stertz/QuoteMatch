# Spreadsheet import and product code mapping

Excel, LibreOffice and delimited text quotations use the same proposal review, analysis, last-purchase comparison, purchase preparation and confirmed external order synchronization as PDF quotations. No Excel or LibreOffice installation is required. The [catalog contract](erp-integration.md) is independent of the target system's table names.

Several files can feed one quotation. New-order creation conditionally asks whether same-supplier documents should add quantities, with explicit treatment of different prices; see [multiple source documents](multiple-source-documents.md).

## Configure a template

In **Templates**, create a template and select **Spreadsheet (Excel, LibreOffice, CSV/TSV)**. Set its supplier, numeric culture and explicit default commercial unit. Choose a sample to inspect worksheet names, column letters, headers and sample values. Existing templates serialized with `SourceFormat: Excel` remain compatible with this spreadsheet category.

| Format | Supported extensions |
| --- | --- |
| Excel | `.xls`, `.xlsx`, `.xlsm`, `.xlsb` |
| LibreOffice/OpenDocument | `.ods`, `.fods` |
| Delimited text | `.csv`, `.tsv` |

CSV/TSV use the stable worksheet name `Data`. Under **CSV / TSV settings**, choose automatic detection or an explicit semicolon, comma, tab or vertical bar separator. TSV defaults to tabs. Quoted fields preserve escaped quotes, delimiters and embedded line breaks. Leading/trailing field whitespace and text product codes remain captured verbatim.

Automatic encoding recognizes UTF-8 and UTF-16 byte-order marks; without one, it tries strict UTF-8 and then Windows-1252. Explicit UTF-8, Windows-1252, UTF-16 LE and UTF-16 BE are available, including UTF-16 without a byte-order mark. Separator and encoding choices are saved with each template/proposal. After changing them, load the sample again and review its headers. CSV numeric conversion still uses the template culture, independently of the separator/encoding.

ODS/FODS retain named worksheets, cached display text, invariant numeric values, row positions and explicitly repeated cells/rows. Covered cells remain empty; merged-cell values are never copied into missing fields. Large trailing empty sheet regions do not allocate millions of cells. Populated repetitions outside import limits are rejected. ODS and FODS use the same mappings and review as the Excel formats.

Select the worksheet, header row and first item row. An optional last item row excludes footer totals or other sections. Map different column letters to product code, description, quantity and unit price. Total and unit columns are optional.

For example, a synthetic workbook may map code to A, description to B, quantity to C, price to D and total to E. Column references use letters, including AA and AB. Header and item rows start at 1. The selected worksheet name must match exactly. Mapped sample headers are saved with the template; changed or missing headers block confirmation until the mapping is reviewed.

Choose the product code type explicitly:

| Code type | Identification | Grouping |
| --- | --- | --- |
| Supplier code (default) | Exact supplier-code lookup through `FindProduct` and `@SupplierProductCode` | Versioned comparison-family/name fallback rules |
| Internal catalog code | Exact product-code lookup through `FindProductById` and `@ProductCode` | Exact product only; shared family metadata does not merge different codes |

Internal identification never falls back to supplier code, description or similarity lookup. Saved mappings and batch lookup caches distinguish the two code types. A saved internal-code mapping cannot point to a different product code. Conversion and product confirmation remain explicit. This code-type option is also available to PDF templates.

Save and test the template. Templates can be exported/imported as JSON. Each proposal freezes its template version and code type; later template edits do not rewrite saved proposals or analyses. Legacy templates without the new fields default to PDF and supplier-code identification.

## Review and purchase

Open a quotation, choose **Import supplier document**, select the spreadsheet and its template, extract and review. The original file, source hash, worksheet, source row, captured fields, reader version and reviewed corrections remain available offline. Exporting the original preserves the file bytes, encoding and extension. CSV row evidence refers to logical records; quoted line breaks stay within one record.

Text numbers use the template's culture. Numeric Excel cells use their stored value, independently of visual currency/decimal formatting. Numeric product codes use the workbook's display format to retain intentional leading zeros and punctuation; text codes remain unchanged. All quantities, prices and totals enter calculation and SQLite storage as decimals. Reading the Excel numeric representation does not change the application's decimal arithmetic.

Without a total column, the captured total is `quantity × unit price`, without additional rounding, and is labeled as calculated. With a total column, missing totals require correction or exclusion. Without a unit column, the explicit template default applies; a missing cell in a mapped unit column requires review. Missing quantities or prices never become zero and never inherit previous rows.

Every nonempty row in the selected range is captured, including incomplete rows and unexpected footer content. Correct incomplete records or exclude them with a reason. Fully empty rows are skipped. Empty extraction, header mismatches and invalid reviewed values cannot be confirmed. Changing the selected file or template invalidates an existing review preview.

**Review items** searches pending codes through the selected code-type query and opens the shared review grid. **Only pending** filters out unchanged confirmed rows; clearing it allows reviewing all items. The grid shows document values, captured color, ERP product, factor, system quantity, normalized unit price and compatible last purchase. Edit either the factor or system quantity; the other updates in decimal. Missing or ambiguous products remain visible and can be selected or searched within the grid. Invalid selected rows prevent saving; unselected rows remain pending.

**Save review** saves selected links, optional reusable mappings, desired quantities and staged order references together in one SQLite transaction, then closes the grid. Source quantities, prices, reviewed fields and documents remain unchanged. Different conversions for the same reusable reference require correction before saving mappings. Repeating quantity ratios that cannot be represented exactly by the decimal factor remain invalid rather than being silently rounded. A stored factor takes precedence over an alternative-code factor; internal-code imports ignore supplier-alternative conversion fields. A same-unit factor of one is a review suggestion.

After a PDF or spreadsheet import is confirmed, previously saved active mappings with complete supplier/catalog/code-kind/description/color/unit identity and a confirmed factor are applied locally. This uses saved product snapshots, without ERP lookup or rewriting mapping revisions. Missing, disabled, incompatible or concurrently changed mappings remain pending. Failure in this optional step keeps the imported document saved and reports pending review.

**Desired quantity** shows an optional editable column. Choose one document or one linked reference order to supply demand; quantities from competing documents are not added automatically. Document demand records the source hash, row and conversion; selected-order demand records exact order revisions and lines. The **References** tab manages linked orders and the price comparison source. The history tab retains coverage, comparisons and reconciliation. Display options are remembered; closing without saving does not confirm products or quantities. Escape closes owned dialogs when no guarded operation is running.

**Remember mappings** applies to the selected rows. Its last saved choice is suggested in subsequent batch and individual reviews, without automatically saving. Language/theme updates preserve this preference. Preference-file failure after the database commit reports a warning and does not replay the saved batch. Saving a mapping also refreshes the suggestion for identical references still pending in the open quotation.

Double-click or F2 still opens the individual editor; successful individual confirmation closes it. After confirmation, the analysis shows its last-purchase reference and variation when available. Purchase choices remain explicit and versioned. Missing desired quantity allows an explicit whole-lot choice; before creating an order, a warning offers reviewing quantities, linking a reference, continuing without demand or canceling. Known insufficient lots remain blocked. **Preview purchase order** prepares the selected new-order items and validates the write plan together; **Create purchase order** requires explicit confirmation and is the action that writes to ERP. Item exclusions remain available and another preview preserves them. The order date is explicit; the installation creation query defines the initial ERP status. The final create button confirms the write without a separate checkbox. Missing write configuration is shown before preview. Importing or preparing a quotation does not create an ERP order or expand configured write permissions.

Purchase preparation retains saved item and supplier-package choices across reopening and analysis revisions. Only invalidated lots need another choice. A first preparation with one supplier selects eligible, unambiguous offers automatically; existing exclusions remain respected. The synchronization screen suggests the last branch used in a validated preview. Confirmed creations show the saved order number, internal code and branch, including after offline reopening or successful reconciliation.

## Limits and verification

Formula values are read from the saved workbook cache; formulas are not recalculated. Save/recalculate the workbook in its spreadsheet application when its cached results are missing or outdated. ODS missing/error formula caches stay unavailable for review. Macros, external links and XML external entities are not executed or resolved. Encrypted or malformed workbooks return a localized error.

Import limits are 32 MB per file, 64 MB of expanded package entries, 2,048 ZIP entries, 50 worksheets, 10,000 populated/source rows per sheet, 256 populated columns, 500,000 cells, 2,000,000 captured characters and 5,000 imported items. ODS trailing empty repeated rows/columns are skipped; later populated cells must still fit the source row/column bounds. XML document characters are bounded and DTDs are prohibited. Reading has a finite time budget and supports cancellation between records.

Public tests generate synthetic BIFF `.xls`, Open XML `.xlsx`/`.xlsm`, BIFF12 `.xlsb`, ODS/FODS and CSV/TSV files. They exercise actual readers, cached totals, formatted codes, encoding, quotes, culture, repeated/covered cells, missing values, header checks, original-file recovery, SQLite template/proposal versions, internal versus supplier identification, exact-product grouping, linked-order demand, synchronization pairing and the shared desktop workflow. Private supplier files remain outside public fixtures. See [testing](testing.md) for the CI matrix and the distinction between headless checks and native runtime verification.
