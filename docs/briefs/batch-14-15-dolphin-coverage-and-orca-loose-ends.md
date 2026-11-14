# Batches 14 and 15: Dolphin coverage, then Orca's loose ends

From the planning session (`git-ee`). The rules and report form of `batch-08-09-settings-tests-model-reviewer.md` apply. These
follow batch 13.

## Batch 14 (Dolphin): finish what batches 11 and 12 opened

Specification: the open boxes in `C:\Git\split\OpenTail.Dolphin\docs\design\ingestion-ideas-2026-11.md`, section 6. The rule
about Airbyte's source stands.

1. **A2 and A1 on the remaining connectors**, one connector at a time, each with fetch-level tests on a fake handler or fake
   client (a refused sign-in stops the run; an item-level refusal does not; throttling does not):
   the Gmail, Graph and IMAP inbox adapters; Dropbox; the object-store clients; the web connectors; the social connectors.
   - Read each connector first and write down, in the report, how it tells a refused sign-in from a refused item. Where the
     provider's answer cannot tell them apart, do **not** guess: leave that connector throwing nothing new, and say so.
   - The object-store clients are shared with exporters: the exception is thrown where the read path can tell, and an exporter's
     behaviour does not change. If that cannot be done without changing the exporter, stop on that connector and report.
   - IMAP: an authentication failure at connect is a configuration failure; a failure on one message is not.
   - A connection check makes one cheap read, never a write, and never fetches content.
2. **`TrackRetry` takes the error directly**, not from the last line of the run's errors.
3. **Index text on the SQLite and files backends.** They must index `IndexText ?? Content` and show `Content`, and then declare
   `UsesIndexText`. A schema change in the SQLite backend follows that backend's existing migration pattern; existing stores
   must keep working without re-ingesting (a missing index text means the content is indexed, as today).
4. **A score threshold for the VectorData backend**, so "no answer" is possible: an option, off by default if the quality gate
   gets worse with any value you try, on by default with the value you measured if it fixes the two abstention scenarios at a
   chunk limit of 160 without breaking any scenario at 480. Report the numbers for each value tried.
5. **Tie order.** Results with equal scores come back in a stable order (by record id) on every backend, so two runs of the gate
   give the same output. Add it to the conformance suite.

## Batch 15 (Orca): loose ends of the reviewer gate

Specification: `docs/design/reviewer-gate.md`, the boxes still open in section 3.

1. **A real two-process test of the AI budgets.** A small console project in the solution's test folder that opens the same
   SQLite database and makes one AI decision when told to; the test starts two of them against one remaining unit and expects
   exactly one success, for approvals and for refusals. Model it on how `OpenTail.Orca.Tests.Worker` is built and started. If
   the test is slow or flaky on a loaded machine, keep it out of the default run behind a trait and say so.
2. **Reviewer counters that survive archive rotation.** The AI reviewers panel counts from the live audit trail, so its totals
   drop when old entries are archived. Read `AiReviewerPanel` and `AuditArchive` first. Keep running totals per reviewer that an
   archive cut does not reduce (written in the same transaction as the cut), and show "since <date>" beside them. Test: totals
   are the same before and after a cut.
3. **MCP, A2A and Teams in the parity record.** For each channel that has no review route of its own, add a test that shows it
   cannot decide: an MCP tool call, an A2A message and a Teams message from a non-reviewer cannot approve, reject or pass an
   action. Teams with a fake connector, as the existing Teams tests do.
4. **`--explain` for the model reviewer:** for a tool, say whether an AI reviewer would be offered it, which reviewers, whether
   it has guidance, and each limit that would stop an approval, in the plain form `--explain` already uses.

Not in this batch: starting the model reviewer (no provider is chosen), dual review, the block proposal, measurement.

## Report

One per batch, to `git-ee`, at most 40 lines, in the usual form.
