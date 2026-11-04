# QuoteMatch — development and agent directives

These directives apply to the entire repository. Explicit task requirements and system/environment instructions take precedence. Existing code, private notes, and memory do not override these rules.

## Language and public content

- Write all code, identifiers, filenames, comments, XML documentation, logs, technical exception messages, tests, commits, and public technical documentation in English.
- User-facing text defaults to Brazilian Portuguese. Keep translated text in localization resources rather than embedding it in business logic or views.
- Preserve original supplier documents and product descriptions verbatim; do not translate imported data.
- Support localization through stable English resource keys. Use the selected UI culture for display and the document template's explicit culture for parsing.
- Never expose stack traces, connection strings, credentials, private SQL, or raw documents in user-facing errors or logs.

## Product and architecture

QuoteMatch is an independent, offline-first desktop application using C#/.NET, Avalonia, Fluent Theme, and MVVM. It has no runtime dependency on another application or proprietary UI license.

- Start with `docs/README.md` for public guides, `docs/architecture.md` for code boundaries, and `docs/customization.md` for contributor/agent workflows. Public setup and verification must work without private notes or installation profiles.
- Keep the product independent of a named ERP vendor. The included SQL Server adapter maps an installation's schema into application contracts; other transports require concrete adapters and deliberate composition/configuration changes. Do not claim universal compatibility from the existence of interfaces.
- Keep installation table/column names, status codes, numbering and financial policies in adapter configuration or implementation. Preserve existing public result aliases and serialized identities for compatibility; their names do not require matching remote schema names.

- Views and code-behind handle presentation only; ViewModels manage screen state and delegate use cases.
- Application coordinates use cases and validation. Domain owns deterministic business rules. Infrastructure implements persistence, document extraction, filesystem access, and optional ERP integration.
- Domain must not reference Avalonia, Entity Framework Core, Dapper, or PDF libraries.
- Only persistence repositories access DbContext during application operations. Bootstrap, migrations, design-time factories, and integration tests may construct contexts for their own responsibilities.
- Keep interfaces at real boundaries. Do not introduce speculative strategies, factories, managers, generic repositories, message buses, distributed workers, or plugin frameworks.
- Check existing implementations and actual API signatures before adding abstractions or calling methods. Search → evaluate → reuse → adapt → create only when necessary.

## Data and calculations

- Keep source capture, reviewed offer, product mapping, requested demand, and calculated analysis separate.
- Preserve original documents and field values. Corrections and reprocessing must remain traceable.
- Use decimal for money, quantities, and conversion factors. Never use binary floating point to work around financial persistence limitations.
- Use explicit units, price bases, package conversion, rounding policies, and purchase constraints. Missing data is not zero.
- Product codes are strings. A supplier code alone may not identify color, packaging, or another product variant.
- Identify supplier-code document items through an exact supplier-code lookup in the configured catalog. Group linked offers using the adapter's explicit comparison-family metadata under the saved grouping version. Internal-code imports retain exact-product grouping. Never replace code identification with similarity/description lookup or remove code punctuation and leading zeros. See `docs/erp-integration.md` for the current result contracts.
- Similarity groups and text matches suggest candidates; they do not authorize substitutions automatically.
- Generate local entity IDs in the application. Store audit timestamps in UTC and protect creation metadata during updates.
- Use enums for domain states and explicit transitions. Preserve compatibility when changing serialized values.
- Analyses and purchase references use versioned inputs. Changes must not silently rewrite historical results.

## Persistence and external integrations

- Use Fluent API for EF mappings, not persistence annotations on domain entities.
- Use AsNoTracking for read-only queries when appropriate. Keep DbContext short-lived, never shared across concurrent operations.
- DbContext constructors accept DbContextOptions only; do not inject UI state or user/session services.
- Validate behavior using the actual SQLite provider, including decimals, migrations, transactions, and ordering.
- Database and filesystem writes are not one atomic transaction; define recovery for partial operations.
- ERP access is optional. Losing a connection must not prevent local work or remove saved snapshots.
- Parameterize SQL values and validate typed result contracts. Regex filtering does not make arbitrary SQL safe; use read-only database credentials for read operations.
- Keep ERP writes behind a separate adapter and explicit user confirmation. Handle uncertain results and reconciliation before retrying external effects.
- Store credentials in platform-provided secure storage; never use embedded keys or plaintext fallback.

## Desktop experience

- Use semantic theme resources and support light, dark, and system themes. Persist preferences outside the repository.
- Keep UI responsive. Long operations expose progress and cancellation and run away from the UI thread as appropriate.
- Use reusable presentation components and accessible keyboard focus, contrast, and non-color status labels.
- Explain calculations with a compact help button beside the metric. Tooltips provide a short explanation; a keyboard/click-accessible details panel shows the formula, inputs, assumptions, and reference values.
- Never place required instructions exclusively in a hover tooltip. Invalid conversions and purchase restrictions remain visible when they affect a decision.
- Keep UI copy short and direct: labels, values, actions and actionable validation. Avoid generic notices or paragraphs that repeat what the controls already convey. Put optional explanations in compact, accessible help.
- Use synthetic, clearly labeled demonstration data. Never embed private quotation contents in screenshots, application resources, tests, or examples intended for publication.

## PDF and regex

- Separate extraction, layout reconstruction, record segmentation, field capture, conversion, and validation.
- Version templates and preserve the extraction strategy, culture, source page, and capture evidence.
- Validate regex syntax and capture groups. Set finite timeouts and an overall input/processing budget.
- Do not fill a missing item's quantity, price, or identity from the previous item. Page continuation must be explicit and scoped.
- Do not report empty or partial extraction as successful confirmation. Surface unmatched candidates and ambiguous fields for review.

## Private development material

- `.local/` is strictly local and must remain ignored by Git. It contains private planning, original development PDFs, derived text, reference results, and local previews.
- Never commit, stage, upload, publish, or send private PDFs, planning documents, extracted content, or derived sensitive fixtures to GitHub or another external service.
- Do not use git add with force on ignored material or silently weaken ignore rules. Check ignored paths before preparing a commit.
- Public fixtures must be synthetic. Do not change a private fixture into a public fixture by renaming it.
- A request to implement the application does not authorize publishing repository changes. Follow explicit commit/push instructions when provided.

## Change discipline

- Investigate before implementing. Distinguish a confirmed defect, a suspicion, and an environment limitation using evidence.
- Make focused changes related to the task. Preserve the user's existing work; do not opportunistically reformat or refactor unrelated files.
- Refactor only when required for correctness, architecture, or the requested scope.
- Prefer simple, concrete implementations. Do not invent API contracts, guarantees, or requirements to avoid investigation.
- Resolve routine choices autonomously. Ask only when missing information materially changes behavior, data, security, or scope.

## Testing and verification

- For every confirmed behavioral bug, add or extend an automated regression test: reproduce → verify failure caused by the defect → fix → verify the same test passes → run relevant regressions.
- Compilation errors in the test, invalid fixtures, or unavailable dependencies do not prove a behavioral defect.
- Test observable behavior with explicit expected results. Do not duplicate production logic in tests or mock away the semantics that caused the issue.
- Choose the smallest faithful test level: domain unit tests, real-provider integration tests, or desktop interaction tests as appropriate.
- Keep tests deterministic and independent. Control clocks and concurrency when relevant. Do not require production services or private PDFs for public CI.
- Do not ignore tests, weaken assertions, or accept incorrect results to make checks pass.
- Run build, formatting checks, and tests appropriate to the change. Documentation-only changes need reference/diff review, not artificial tests.
- Maintain at least 80% aggregate line coverage for the core and the measured application assemblies using scripts/Test-Coverage.ps1. Add meaningful behavior tests; do not exclude business code or inflate duplicate coverage to meet the threshold.
- Before committing, run dotnet format QuoteMatch.slnx and inspect its changes. Avoid new compiler/analyzer warnings; enforce warnings as errors in CI.
- Report what changed, what was actually verified, and what remains unverified or blocked. Never equate a Windows build with verified Linux/macOS runtime support.
- Keep SDK and package versions in global.json and Directory.Packages.props. Verify actual compatibility rather than assuming the newest version is suitable.

## Continuity

Permanent execution rules belong in this file. Private design and execution plans belong under `.local/planning/`. Public documentation must be self-contained and must not depend on private notes or sibling repositories.

Memory is historical context, not execution authority. Validate recovered information against the current checkout; do not query unrelated projects without user authorization.

Implementation sequence: understand → investigate → identify existing conventions → define the smallest verifiable change → implement → verify → review the diff.
