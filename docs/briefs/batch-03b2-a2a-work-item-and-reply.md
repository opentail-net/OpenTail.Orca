# Batch 03b-2: A2A thin slice, second half (work item, reply, completion)

From the planning session (git-fa). Batch 03b-1 is accepted and pushed (fae2725). Read `docs/design/a2a.md` sections 4, 5, 6 and 10,
`OpenTail.Orca/Work/WorkItems.cs` (WorkService) and `OpenTail.Orca.A2A/` first. The design below is fixed by the planner; where it
differs from a2a.md this brief wins and you list the difference. Same rules: no commit/push/git state; no public renames;
`contract-v1.txt` unchanged (additive public members are allowed if the contract test allows them; if it does not, STOP that part);
follow the code where the brief is wrong and say so; STOP a part and write options when it needs a design decision not made here.
Baseline: Abstractions 71 of 71, Host 1267 of 1267. Blocking sends stay refused: they are batch 03b-3, do not build them.

## Contracts read by the planner (verify each before relying on it)

- `WorkService.CreateAsync(goal, forWhom, from, inputsJson, ..., tainted, carried, ..., thread)` creates an Open item and audits it.
- `WorkService.CompleteAsync(id, caller, summary)` sets Done and Summary. It raises no event and does **not** record what the
  completing agent's session holds: `WorkItem.Labels` are only the labels carried at creation.
- `WorkService.CancelAsync(id, by)` cancels Open **and Claimed** items. It reads the status and then transitions from that status.
- `WorkService.Carry(subject, sessionId)` gives a caller's taint and labels; `DataLabels.GainCarried(session, subject, labels)` adds them.
- `complete_work` in `Mcp/BuiltInTools.cs` calls `CompleteAsync(workId, Caller(), summary)` with no session.
- `OpenTailHost.cs:123`: the work service exists only when a profile delegates or `Work.Enabled` is set.

## Design (fixed)

E1. **Completion carries what the completer holds (a change to work items, needed by E5).** `CompleteAsync` gains an optional
    `string? sessionId = null`. On Done it merges `Carry(caller.Subject, sessionId)` into the item: `Labels` becomes the union and
    `Tainted` is or-ed. `complete_work` passes the caller's session when BuiltInTools has one (find out how `claim`/`read` get theirs).
    Reason: a summary written by an agent that read labelled data must carry those labels to whoever reads it, and to the reply.
    If an existing test fails because of this, do NOT change the test: STOP E1, E5 and E6, build the rest, and report the failing
    tests with their names. Add tests: a completer holding label L leaves L on the Done item; a reader of that item gains L.
E2. **A `Completed` event on `WorkService`**, raised after an item becomes Done, in the same style as `Ready` (handlers must not
    throw; a throwing handler never fails the completion). Additive.
E3. **`a2a.submit` creates the work item.** Its body, when it runs:
    - reads the requester from the call context (`HostContract.RequesterContextKey`) and refuses unless it equals
      `partner:<partner argument>`, and unless `a2a_tasks` has a row for (that partner, `taskId`) whose `submit_action_id` is null or
      equals this action's id (`HostContract.ActionIdContextKey`). So an MCP agent whose profile happens to list `a2a.submit` (or `*`)
      cannot forge a partner request. Refusal is an exception with a generic message; test it through the MCP path or the queue;
    - if the row already has `work_item_id`, returns it (idempotent);
    - otherwise looks for an existing item on thread `a2a:<contextId>` from this partner (heals a crash between create and store),
      else calls `CreateAsync(goal, ForProfile(skill.Inbox), from: "partner:<id>", inputsJson (null when empty), tainted: true,
      carried: Carry(partner subject, the action's session).Labels, thread: "a2a:<contextId>")`, then stores `work_item_id` on the row.
    - Result: `{ "accepted": true }` only. The work item id is never returned to the partner.
    Startup check added when A2A is enabled: the work service must exist (`Work.Enabled`, or say what the real condition is).
E4. **`a2a.reply`**: a second tool in the same module. Risk `ExternalEffect`. Arguments `partner`, `taskId`, `text`. Preview: the
    partner's display name and the exact text. Body: the same requester and row check as E3 (row's `reply_action_id` null or this
    action), returns `{ "delivered": true }`. The text a partner receives is read from this action's stored arguments once it has
    Succeeded; it is stored nowhere else. Startup checks when enabled: the policy has an entry for `a2a.reply`; its mode is not
    `Execute` (error: a reply is an exit and is always reviewed or denied); if the policy model has "reviewer must be a person" and
    "no edits" settings for a tool or rule, they must be set so for `a2a.reply` (say what exists; do not invent settings); every
    profile named by an `a2a:call` key lists both `a2a.submit` and `a2a.reply`.
E5. **Reconcile: one function, called from GetTask, CancelTask, a repeated SendMessage and the `Completed` handler.** For a row whose
    work item is Done and whose `reply_action_id` is null:
    1. take the session and profile from the submit action's record (if the session no longer exists for the partner, start a new one);
    2. apply the item's taint and labels to that session (`GainCarried`, and taint) so the standard exit check sees them;
    3. submit `a2a.reply` as `partner:<id>` (`RequesterKind.AiSession`, that profile, that session), idempotency key
       `<taskId>:reply`, text = the item's Summary, or "Done." when it is null or blank;
    4. store `reply_action_id`; audit `a2a.reply_queued` (task, partner, text hash, bytes; no text).
    It must be safe to run twice at once (the idempotency key and a conditional update on the row). An exception in it never fails
    GetTask: the task then reads as WORKING and the next call tries again.
E6. **States (replaces D11 for tasks that have a work item).** In this order:
    - reply action Succeeded -> COMPLETED, with one artifact holding one text part (the reply text) and a status message "Completed.";
    - reply action Denied, Rejected, ChangesRequested, Expired, Cancelled or Failed -> FAILED, generic text (the work was done but
      the answer may not be sent; no reason is given);
    - reply action Pending, Approved or Executing, or item Done with no reply yet -> WORKING;
    - item Open or Claimed -> WORKING; item Dead or Expired -> FAILED (generic); item Cancelled -> CANCELED;
    - no work item yet: as D11 today.
    No artifact, history or text ever comes from the work item itself. Nothing but an executed reply's text leaves.
E7. **CancelTask.** Submit Pending: as today. Work item Open: cancel it and answer CANCELED. Claimed, or anything later:
    TaskNotCancelable. The cancel must not be able to stop a claimed item through a race: add an additive way to cancel only from
    Open (for example an optional `onlyIfOpen` parameter on `CancelAsync`), audited as `work.cancelled` by the partner subject.
E8. **Docs.** `OPERATIONS.md` (what is served now; an example policy fragment for `a2a.submit`, `a2a.reply` and a `DataFlow` rule
    naming `a2a.reply:partner=<id>`; verify that destination syntax in `DataLabels.Check` and correct a2a.md if it differs).
    Checklist C.1 and a2a.md "What is built". `check-host-deployments.ps1`: update it if it lists projects or config sections.

## Tests (extend A2aSliceTests or add A2aReplyTests; SDK client over a socket as before)

- Approve submit -> a work item exists for `profile:<inbox>`, tainted, from the partner; GetTask is WORKING. Approving twice or
  re-running the body makes one item. A row with no `work_item_id` but an existing item on the thread is healed, with one item.
- An agent of the inbox profile claims and completes with a summary -> a pending `a2a.reply` whose preview shows that text; GetTask
  is still WORKING and has no artifact. Approve -> COMPLETED with exactly that text. Reject -> FAILED, and no summary text anywhere
  in the response.
- GetTask alone (the `Completed` handler removed or throwing) still creates the reply. Two concurrent GetTask calls make one reply.
- Labels: the completer's session holds a label whose `DataFlow` rule denies `a2a.reply` for this partner -> the reply is refused by
  the exit check, the task is FAILED, and the text is not in the response. With an Allow for this partner it is queued normally.
- A non-partner caller submitting `a2a.submit` or `a2a.reply` with a real partner id and task id: no work item, no reply delivered.
- Another partner sees TaskNotFound at every stage, COMPLETED included.
- CancelTask: open item -> CANCELED and the item is cancelled; claimed item -> TaskNotCancelable and the item is still claimed.
- Item dead (runs used up) -> FAILED; expired -> FAILED. A restart between completion and GetTask still ends COMPLETED.
- Startup: A2A on without work items refuses; `a2a.reply` missing from the policy, or `Execute`, refuses; a partner profile without
  `a2a.reply` refuses.
- Mutation check, one at a time, restore after each: (a) skip step 2 of E5 (labels) -> the label test fails; (b) remove the
  requester check in the `a2a.submit` body -> the forging test fails; (c) take the artifact from the item's Summary instead of the
  reply action -> the "pending reply has no artifact" test fails. Report it.

## Report

Message `git-fa`, at most 45 lines: per design item done / changed / stopped; existing tests changed and why (none is expected);
differences from a2a.md; files changed; full-run counts for both suites; the mutation check result; anything you saw that looks wrong
in 03b-1 now that you have built on it.
