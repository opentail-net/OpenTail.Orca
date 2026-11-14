# Batches 10 and 11: shared budgets in Orca, then failure kinds in Dolphin

From the planning session (`git-ee`). The rules and the report form of `batch-08-09-settings-tests-model-reviewer.md` apply: the
plan is the specification, you own the tests, the records and a self-review, no commit and no push, and you go on to the next
batch without waiting unless you STOPPED.

## Batch 10 (Orca): budgets that hold across processes (P6), and the rest of the tests

Specification: `docs/design/reviewer-gate.md`, the P6 box in section 3 and section 5.3. Decision D-11 is taken by the planner for
the owner to confirm: **a counter in the store**, not "one runner per Orca".

Fixed points:

- One mechanism for the approval budget (`AiBudget`) and the refusal budget (`AiRefusalBudget`): a conditional increment in the
  store, in the same transaction that records the decision. If the increment is refused, the decision is not recorded.
- Read `IActionStore`, the SQLite store and the in-memory store first, and list in your report the members you add and how each
  store meets them. The in-memory store must give the same answers as SQLite. Add the cases to the store's existing shared tests
  if there are any.
- A window is a time window, as today (`Per`). Say in the report how the counter expires or rolls, and what happens to decisions
  recorded before the counter existed (they must still count within their window: do not let an upgrade reset a budget).
- Keep the in-process gate only if it is still needed; if you remove it, say why it is safe.
- A schema change is one bump, with the migration and its backup as the other bumps do.
- Acceptance: two processes (two stores on one database file) and one remaining unit: exactly one decision succeeds, for
  approvals and for refusals. The existing in-process concurrency tests still pass.

Then the tests left open in section 3 under G8, as far as they need no production change:

- channel parity for the channels not yet covered (MCP, A2A, Teams, HTTP);
- the rest of the pins for the delay table (plan 2.5);
- pairwise coverage for plan 2.2 and 2.3: generate the covering array in the test code from the factors listed in
  `reviewer-gate-history.md` section 7; write the expected result of each row from the plan's tables, not from the code's output.
  A row where the code disagrees with the plan is a finding: report it, do not adjust either.

## Batch 11 (Dolphin, `C:\Git\split\OpenTail.Dolphin`): what an operator sees when a credential breaks

Specification: `docs/design/ingestion-ideas-2026-11.md`, section 6, the first group (A2, B4, A1), with section 9's corrections.
Read that document's rule about Airbyte first: **do not open Airbyte's source code, anywhere**. Build from our document and
Dolphin's own contracts.

Fixed points:

- A2: `SourceConfigurationException` in `OpenTail.Dolphin.Files`. The pipeline stops the run on it, from the fetch branch and from
  the change stream; nothing is added to the retry list and the cursor is not saved past the last commit. `IngestionReport` says
  so in a field of its own, not only in `Errors`. Each connector that can tell a 401 or 403 (or its provider's equivalent) throws
  it; list in the report which connectors you changed and which cannot tell.
- A throttling answer (429, or a provider's quota error) is never a configuration failure.
- B4: a warnings list on `TextExtractionResult`; `TransformationsTextExtractor` passes the extraction library's warnings through;
  the pipeline records a content-type mismatch as a validation warning with its own code. No new detection code.
- A1: `IConnectionCheck` and its result type, using A2's kinds; implemented on the connectors that take a credential; a
  conformance check for it; read-only (the conformance suite's write-verb check must still pass). Orca's use of it is not in this
  batch.
- Public contracts change here (`TextExtractionResult`, `IngestionReport`). Keep them source-compatible for existing callers
  (optional parameters or new members with defaults), and list every public change in the report.
- Records in Dolphin: tick the boxes in the ideas document, update `docs/architecture.md` where it describes the pipeline, and
  `docs/writing-a-connector.md` if it tells authors how to report failures.
- Run the Dolphin test project (not the ones that need a server) and report the counts, with the baseline you measured first.

## Report

One per batch, to `git-ee`, at most 40 lines, in the form of the earlier brief.
