# Briefs and reports

The planning sessions wrote the briefs in this folder, and the implementing sessions wrote the reports. The state of each item is in
[`docs/design/governance-checklist.md`](../design/governance-checklist.md) and [`docs/design/a2a.md`](../design/a2a.md). This file is the
record of what each brief asked, and where the decisions are.

## Briefs and reports

- `batch-01-a2a-phase0-and-teams-inventory.md`: A2A phase 0 and the Teams inventory. Closed by `f24f8c5`. The inventory is `teams-inventory.md`.
- `batch-02-teams.md`: Teams bot fixes (chat risk order), Request changes, typed `waiting` and `help`. Closed by `fef5b4f`.
- `batch-03a-a2a-sdk-facts.md`: A2A SDK facts from probes. Closed by `c8e50e9`. The facts are in `a2a-sdk-facts.md`.
- `batch-03b1-a2a-slice-foundation.md`: the thin slice, first half. Closed by `fae2725`.
- `batch-03b2-a2a-work-item-and-reply.md`: the thin slice, second half (work item, reviewed reply, completion). Closed by `23020b1`.
- `batch-03b3-a2a-blocking-and-listtasks.md`: blocking sends and ListTasks. Closed by `00b52ce`.
- `batch-04-a2a-card-and-reviewer-view.md`: the agent card; partner text on the review page and in Teams; the trust, suspension and cap
  tests. Closed by `c159639`. Its report is `a2a-reviewer-view.md`.
- `batch-05-a2a-try-it-kit-and-teams-status.md`: the A2A kit; Teams `work` and `tasks`. Closed by `7b6003c`.
- `batch-06-records.md`: records brought in line with the code, and one test. Closed by the commit that added this file.
- `batch-07-gate-gaps.md`: reviewer gate gaps G1, G3 and G6, the taint message, and a test of an AI refusing an A2A reply. Closed by `48abef9` (2026-11-14).
- `batch-08-09-settings-tests-model-reviewer.md`: the three settings (D-32, D-36, D-37), G5, the missing tests, then the model reviewer against a fake client (R2). From this brief on the coder also owns the tests and the records. Batch 08 built, not committed (2026-11-14); batch 09 open.
- `teams-inventory.md`, `a2a-sdk-facts.md`, `a2a-reviewer-view.md`: the inventory, the facts and the batch 04 report. They are not briefs.

## Decisions the planner made for the owner

Each line gives the decision, where it is recorded, and how to change it.

1. **A2A-Version.** Missing is 1.0; `1.0` is accepted; empty, `0.3` and other values are refused with VersionNotSupported. Recorded:
   checklist, Decided (D6); `a2a-sdk-facts.md` section 5. Change: `OpenTail.Orca.A2A/A2AVersionGate.cs`, and the version matrix test in
   `A2aSliceTests`.
2. **`a2a.reply` is never `Execute`.** Recorded: the batch 03b-2 brief; `a2a.md`, item-level checklist, C.1; `OPERATIONS.md`, A2A startup checks. Change: the
   check in `A2AFrontDoor.Check`. Not recommended.
3. **A completion carries the completer's labels and taint onto the work item.** Recorded: the batch 03b-2 brief (E1). Change:
   `WorkService.CompleteAsync` in `OpenTail.Orca/Work/WorkItems.cs`.
4. **A blocking send waits 30 seconds, then answers an error if the task is still going** (InternalError, naming the task; the task keeps
   running). Recorded: checklist, Decided (2026-11-13); `OPERATIONS.md`. Change: `A2A.BlockingWait` in the HTTP config (1 second to 5 minutes,
   checked at startup); the error is in `A2ARequestHandler.WaitForTerminalAsync`.
5. **The cap is 4 blocking waits per partner** (1 to 64). Recorded: checklist, Decided. Change: `A2A.MaxBlockingWaitsPerPartner` in the
   HTTP config.
6. **ListTasks is ordered by creation time, then task id.** Recorded: checklist, Decided; `a2a.md` section 4. Change: the `ORDER BY` and the
   page token in `A2ATaskTable.ListPage`.
7. **An anonymous caller gets 401 for the card with A2A on, and 404 with it off.** Recorded: `a2a.md` section 8 (option c). Change: option
   (a) in `docs/briefs/a2a-reviewer-view.md`. The 401 comes from the default challenge scheme in `HostHttp.cs`.
8. **A cut Teams preview (over 400 characters) cannot be approved from chat, on the card or on the click.** Recorded: the batch 04 brief,
   B3; `OPERATIONS.md`. Change: `TeamsBot.CardPreviewMax` and the check in `TeamsBot.InvokeAsync`. **Owner accepted 400 on 2026-11-13.**
9. **A2A ships inside `OpenTail.Orca.Server`, not as its own package.** One server hosts A2A and MCP; the server already references
   `OpenTail.Orca.A2A`. Decided by the owner on 2026-11-13. Change: remove that project reference and pack the A2A project separately.
10. **Teams may approve a partner reply by default.** The switch `ApprovePartnerRepliesInChat` (Teams options, default `true`) turns this
    off: a reply then waits for the review page. Decided by the owner on 2026-11-13 (default yes). Change: the setting in
    `TeamsBotOptions.cs`. Status: built and tested on 2026-11-13 (full suite: 1441 total, 0 failed, 32 skipped).

## Waiting for the owner

- A live check in a real Teams tenant: whether a bare `@name` in a card text becomes a mention. The report on B2 is from reading the code.
- The missing-header rule of `A2A-Version` (`a2a.md`, item-level checklist, C.1: recommended 1.0, owner to confirm).
- Which profiles may use an inbox skill (`a2a.md`, item-level checklist, C.3).
- The plan defaults in `a2a.md`, item-level checklist, C.3: JSON-RPC first; a generic error for an unavailable skill; configuration-only cards and skills;
  reviewer reasons kept internal; file parts refused.
- Whether the partner id is generated by the server (`a2a.md` section 2) or by the operator (the phase 0 brief). Built as the brief says.
