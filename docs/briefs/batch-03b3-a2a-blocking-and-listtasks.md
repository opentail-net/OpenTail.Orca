# Batch 03b-3: A2A blocking sends and ListTasks

From the planning session (git-fa). Batch 03b-2 is accepted and pushed (23020b1). Read `docs/design/a2a.md` section 4 ("Blocking and
`returnImmediately`", "ListTasks"), corrections 2 and 5, and `docs/briefs/a2a-sdk-facts.md` sections 3 and 4. The design below is fixed
by the planner; where it differs from a2a.md this brief wins and you list the difference. Same rules: no commit/push/git state; no
public renames; `contract-v1.txt` unchanged; follow the code where the brief is wrong and say so; STOP a part and write options when
it needs a design decision not made here. Baseline: 1363 passed, 0 failed, 32 skipped across the solution.

Answers to your 03b-2 report: (1) the failing test: option (a), done by the planner (it now asserts the startup refusal; the handler's
profile check stays). (2) A completion without a session carrying `SessionlessTainted`: intended, it is what `Carry` means everywhere.
(3) The facts file's nullability line is corrected. (4) Whether `OpenTail.Host.A2A` ships as a package is the owner's; leave it.

## Why

`returnImmediately` defaults to false, so a client that uses the SDK's defaults gets UnsupportedOperation today. Approval takes
minutes or hours, so a blocking send cannot simply hold the connection until the end either.

## Design (fixed)

F1. **Config** (A2A section, validated at startup when enabled): `BlockingWait` (TimeSpan, default 00:00:30, allowed 1 second to
    5 minutes) and `MaxBlockingWaitsPerPartner` (int, default 4, allowed 1 to 64).
F2. **A blocking send (false or omitted) creates the task exactly as a non-blocking one** (same validation, row, submit, idempotency),
    then waits until the task is in a terminal state (COMPLETED, FAILED, CANCELED, REJECTED) or `BlockingWait` has passed.
    - Terminal in time: return the task, as GetTask would (artifact included when COMPLETED).
    - Not terminal in time: answer a JSON-RPC error, never a task in SUBMITTED or WORKING. Use the SDK error code that fits best
      (say which and why; if none fits, InternalError) with the text "The task <taskId> is still in progress. Read it with GetTask,
      or send with returnImmediately set to true." The task id is the caller's own, so it may appear. The task is not cancelled.
    - The client disconnects (request aborted): stop waiting; the task is not cancelled or changed.
    - A retry with the same `messageId` and content finds the same task and waits again; different content is refused as today.
    - Waiting: poll the task's state (reconcile included, as GetTask does) about every 500 ms through an injectable delay or the
      Host's `TimeProvider`, so tests do not sleep for real seconds. No new event plumbing.
F3. **The cap.** A partner holding `MaxBlockingWaitsPerPartner` waits gets the next blocking send refused **before any row is
    created** (so the refusal leaves nothing behind), with a generic "too many requests are waiting; retry, or send with
    returnImmediately set to true" error, audited `a2a.refused` reason `too_many_waits`. The counter is in memory, per partner id,
    and is released in a `finally`. It never changes what a waiting request returns.
F4. **ListTasks.** Only the caller's tasks, ever: the query is by `partner_id`, and no parameter can widen it.
    - `pageSize` 1 to 100, default 50; outside the range InvalidParams (as the SDK's own server does).
    - Order: newest first by `created_at`, then `task_id`. The spec asks for status update time; that time lives in three stores, so
      this slice orders by creation and a2a.md records the difference.
    - `pageToken`: opaque (base64url of the last row's `created_at` and `task_id`), keyset paging, no offsets. A token that does not
      parse is InvalidParams. `nextPageToken` is "" on the last page. `totalSize` is the caller's task count under the same
      `contextId` filter.
    - `contextId`: served as a filter (it matches only the caller's rows). `status` and `statusTimestampAfter`: InvalidParams with
      "this filter is not served yet" (state is derived, not stored). `historyLength`: ignored, there is no history.
    - `includeArtifacts`: the answer's artifact is included only when it is true.
    - Each returned task is built the same way GetTask builds it, reconcile included.
    - `tenant` is already cleared by the SDK; do nothing about it.
F5. **Audit the silent refusal.** The handler's `Caller() == null` path (a signed-in `a2a:call` credential with no partner) writes
    `a2a.refused` with reason `no_partner` and the credential's subject before it throws, in all three served methods and ListTasks.
F6. **Docs.** `OPERATIONS.md` (the two settings, what a blocking client sees, ListTasks and its limits). Checklist C.1: tick or
    annotate correction 2 and correction 5, and record under "Decided" (planner's delegated decisions, 2026-11-13): the wait ends in
    an error, not a non-terminal task; the cap default of 4; ListTasks orders by creation. `a2a.md` "What is built".

## Tests (SDK client over a socket, as before; use the injectable time so no test waits real seconds)

- Blocking send, reviewer approves submit, agent completes, reviewer approves reply, all inside the wait: one response, COMPLETED,
  with the reply text. Both `ReturnImmediately = false` and the property left unset.
- Blocking send that outlives `BlockingWait`: the error of F2, naming the task id; the row and the pending action exist; GetTask on
  that id answers SUBMITTED; a retry with the same `messageId` waits on the same task (one row, one action).
- Rejected at submit during the wait: the response is the task in REJECTED.
- The client aborts mid-wait: the task is unchanged and the wait counter is back to zero.
- The cap: with the cap at 1, a second concurrent blocking send is refused, leaves no row, is audited; after the first ends, a new
  one is accepted. Another partner is not affected by the first partner's waits.
- ListTasks: partner A with 5 tasks and page size 2 gets 2, 2, 1 with tokens and then ""; newest first; partner B's tasks never
  appear for A, including when A passes B's `contextId` (empty result, not an error). `totalSize` is right. A bad token, page size 0
  and 101, a `status` filter: InvalidParams. `includeArtifacts` false hides a completed task's artifact; true shows it.
- Startup: `BlockingWait` of 0 or 10 minutes, and a cap of 0, refuse.
- Mutation checks, one at a time, restored: (a) drop the partner filter from the ListTasks query -> the foreign-task test fails;
  (b) return the task instead of the error on timeout -> the timeout test fails; (c) skip the `finally` release -> the cap test
  fails. Report it.

## Report

Message `git-fa`, at most 40 lines: per design item done / changed / stopped; the error code chosen in F2 and why; differences from
a2a.md; existing tests changed and why (none is expected); files changed; full-run counts; the mutation result.
