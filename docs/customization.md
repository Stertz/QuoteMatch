# Customizing QuoteMatch with a contributor or coding agent

This guide is self-contained for a clean checkout. Start with [AGENTS.md](../AGENTS.md), [architecture](architecture.md), [business rules](business-rules.md) and [integration contracts](erp-integration.md). Private notes and local installation profiles are not execution authority or prerequisites.

## Locate the change

| Change | Start here |
| --- | --- |
| PDF layout/identifier | Template editor and [automatic PDF import](automatic-pdf-import.md); use synthetic sample text/PDFs |
| Spreadsheet columns, rows or culture | Template mapping and [spreadsheet import](excel-import.md) |
| Another SQL Server catalog schema | Saved product/order query contracts and Infrastructure ERP readers |
| Another database/API transport | Application boundary interfaces, new Infrastructure adapter and Desktop composition/configuration |
| Unit, identity, grouping or lot-selection semantics | Domain rules, observable behavior tests and serialized-input compatibility |
| Tax/charge calculation or order defaults | Installation queries and [financial policies](financial-capture-and-policies.md); extend typed fields only when required |
| A screen or localized explanation | Desktop ViewModels/views and resource files; preserve UI-thread responsiveness and keyboard use |
| Local persistence | Existing repository methods, Fluent API mappings, migrations and real SQLite tests |

## Give the agent a bounded brief

Specify the intended behavior, target transport, read/write scope, provided schema or API documentation, affected workflows and explicit verification limits. Use synthetic examples and credential placeholders. Supply real secrets only through the application's supported protected configuration.

An example brief:

```text
Adapt read-only catalog integration for TARGET_SYSTEM.
Read AGENTS.md and docs/erp-integration.md first.
Use the provided synthetic schema/API examples and existing application interfaces.
Preserve exact supplier/internal codes, explicit units and decimal conversion factors.
Do not add external writes or query unrelated projects/services.
Add behavior tests for missing, ambiguous, inactive and canceled results.
Keep the local quotation workflow usable without a connection.
Report changed boundaries, executed checks and unverified target behavior.
```

For order writes, define a separate concrete scope with a disposable environment and permitted effects. Query implementation does not authorize dispatching a real order.

## Follow existing boundaries

Search current implementations and signatures before proposing an interface. Reuse the current repositories, extraction stages, product review and operation journals when their semantics fit. Keep Domain free of UI, database and document-library dependencies. Do not introduce a generic connector framework merely to support one new transport.

The current order model assumes whole lots, explicit brands and history-backed buying suppliers. Decide whether the target can truthfully provide those semantics. If it cannot, describe and test the required model change instead of returning invented values or embedding target-specific tables/status codes in Domain.

Existing enum values, JSON fields and SQL result aliases are compatibility contracts. Renaming them to improve terminology can break saved profiles and journals. Add versioned behavior or a migration when a change is necessary, and retain historical consultation/recovery.

## Work and verify

1. Reproduce the current behavior and identify the relevant tests.
2. Define the smallest verifiable change and its expected results.
3. For a confirmed bug, demonstrate the behavioral failure before fixing it.
4. Implement through the existing boundaries and add meaningful synthetic tests.
5. Use actual SQLite for persistence behavior; do not replace decimal/transaction semantics with mocks.
6. Run the applicable build, formatting and test/coverage checks from [testing](testing.md).
7. Review the diff for unrelated changes and sensitive material; update public documentation for the resulting behavior.

A final report should distinguish implemented behavior, tests actually executed and target/platform behavior that remains unverified. A fake adapter passing tests is not a live ERP certification.

## Public material

Code, identifiers, comments, tests and public technical documentation use English. User-facing text belongs in localized resources; imported descriptions remain verbatim. Examples and screenshots must be clearly synthetic.

Never publish local databases, credentials, private SQL, source quotations or derived business fixtures. `.local/` is ignored private development material; a new contributor must be able to run and understand the public project without it. See [contributing](../CONTRIBUTING.md) and [security](../SECURITY.md).
