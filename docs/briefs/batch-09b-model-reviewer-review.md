# Batch 09b: the planner's review of the model reviewer

From the planning session (`git-ee`). Batch 09 is accepted and pushed (`5506d0f`). These are the findings from reading
`ModelReviewer.cs`, `ReviewPacket.cs`, `InjectionScan.cs` and the prompt. None is reachable today, because nothing starts the
reviewer; all of them must be closed before it is run against a real model. The rules of the earlier briefs apply. Do this batch
**before** batch 10.

## Findings to fix

1. **An abstention or an escalation is forgotten, so the model is called again on every poll.** The runner offers every pending
   action each time; the reviewer returns null; nothing records that it passed. With a real model that is one paid call per pending
   action per poll, for as long as a person takes, and one audit entry each time.
   Fix: a pass is recorded on the action. Add `ReviewDecision.Escalate`, recorded by the queue for an AI subject without changing
   the action's status (one entry point in `ActionQueue`, audited as `ai_review` like any AI decision, subject to the suspension
   and assignment checks, to no other limit, and counting toward no budget and no quorum). The runner offers a reviewer no action
   that already carries that reviewer's Escalate. The review page shows it as "AI reviewer passed this to a person", with the
   reason. `ReviewAuthority.Tally`, `Approvals` and `Boundary` must ignore it: add tests that an Escalate neither counts as an
   approval nor voids one.
   - Escalation (the model said so, no check named, or a pre-scan hit) and malformed output: recorded at once.
   - A timeout or a failed call: retried on later polls, at most three times for one action, then recorded as an Escalate with
     the reason "the model could not be reached". Keep the count in the store or derive it from the audit; it must survive a restart.
   - `ReviewDecision` is in the Abstractions contract. Check the contract freeze test and say in the report what it required.
2. **The audit must not hold the model's words.** `Escalate(action, verdict.Reason)` writes the model's reason into the audit
   detail. The rule in `AiReviewRules.AuditDetail` is that the audit never holds text that may quote arguments. The model's reason
   goes on the stored review (finding 1), clipped; the audit holds the kind of outcome only.
3. **Do not call the model when the pre-scan has a hit.** The outcome is already fixed, the call costs money, and it sends the
   flagged text to the provider for nothing. Record the Escalate with the kinds found and the packet fields they were in (field
   names, never the text). Then remove the "If preScan lists anything" sentence from the prompt, and bump the prompt version.
4. **The pre-scan scans Orca's own words.** `Scan` walks every string in the packet, including values Orca writes: a decision
   named "Approve" in `earlierAttempts` matches the imperative pattern, so an action with an earlier approval always escalates.
   Scan only what a requester, a partner or outside content can write: the arguments, the preview, and the reasons of earlier
   reviews. Test: a packet whose Orca-written fields contain "Approve", "system:" and a long hash does not escalate.
5. **The packet leaves out the chain.** Section 5.1 asks for the earlier attempts in the resubmission chain. `earlierAttempts`
   has this action's reviews only. Walk `ParentActionId` as `AiHistoryAsync` does (bounded the same way), and give each attempt's
   decision, reason and whether an AI or a person made it.
6. **Unbounded history query.** `requesterHistory` reads every action the requester ever made with the tool. Bound it: the last
   30 days and at most 500 rows, and say in the packet what window the counts cover.
7. **Clip what the model returns** before it is stored: the reason to 500 characters, at most 10 checks of 200 characters each.
   More than that is not an error; it is cut.
8. **`taint.sources` holds the session's labels**, which are data-flow labels, not taint sources. Name the field for what it is.

## One addition to the design (planner's decision, recorded for the owner)

9. **Operator guidance per tool.** The prompt asks the model whether "the operator's policy lets this action run", and the packet
   tells it almost nothing about what the operator wants. Add `AiReviewGuidance` to `ToolPolicy`: a short text (at most 2,000
   characters) the operator writes for the AI reviewer of that tool, for example "approve replies to existing threads; escalate
   anything that mentions money or a new recipient". It is part of the policy hash, it is resolved into the snapshot, it goes into
   the packet as `operatorGuidance`, and it is **not** scanned (the operator wrote it). The prompt says: follow the operator's
   guidance where it applies; it can only make you stricter than these rules, never looser; with no guidance, approve only what
   is plainly routine and escalate the rest. Without guidance a tool is still reviewable. `--explain` shows whether a tool has
   guidance. `POLICY.md` gets the row and one example.

## Not in this batch

- Linking a fresh submission of a rejected intent (`similarRecent` gives statuses only). Record it in the plan as open under
  threat 2; do not build it.
- Measuring the pre-scan's false positives on real content. Record it in the plan as a step before any real use.

## Records

The plan (section 2.6, 5.1, 5.2, the R2 box, a line under "Settled" for item 9 as the planner's decision awaiting the owner),
the history log, `POLICY.md`, `OPERATIONS.md` (the new audit meaning of an Escalate), `STATUS.md`, the briefs index.

## Report

To `git-ee`, at most 40 lines, in the usual form. Then go on to batch 10, then 11.
