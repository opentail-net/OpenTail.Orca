# Batch 03b-1: A2A thin slice, first half (endpoint, identity, task table, submit, GetTask)

From the planning session (git-fa). Batch 03a is accepted; `docs/briefs/a2a-sdk-facts.md` is your reference for every SDK name.
Read `docs/design/a2a.md` (sections 1, 2, 4, 5, 7, 10) and checklist C.0. The design below is fixed by the planner; where it differs
from a2a.md, this brief wins and you list the difference in the report. Same rules: no commit/push/git state; no public renames;
`contract-v1.txt` unchanged; follow the code where the brief is wrong and say so; STOP a part and write options when it needs a design
decision not made here. Baseline: Abstractions 70 of 70, Host 1245 of 1245. The second half (work item, reply, completion, blocking
waits) is batch 03b-2: do not build it now.

## Design (fixed)

D1. **Packages.** Add `A2A` and `A2A.AspNetCore` 1.0.0-preview4 to `Directory.Packages.props`, and raise
    `Microsoft.Extensions.AI.Abstractions` to the lowest version that restores (10.11.0 per your probe). Run both full suites right
    after the bump, before any other change. If anything fails or any other pin must move, STOP and report.
D2. **A new project `OpenTail.Host.A2A`** (package id the same; licence and packaging like `OpenTail.Host.Http`). It references
    `OpenTail.Host.Http`. Nothing references it except `OpenTail.Host.Server` and the tests, so deployments that do not want A2A do
    not carry the SDK. Add it to `OpenTail.Host.slnx`, to the architecture tests' layering rules (Http -> A2A, one way), and to the
    versioning and packaging lists the architecture tests check. If `HttpDeploymentRunner` has no seam through which another package
    can map endpoints and add a module, add the smallest one (for example an optional callback on the options) and describe it.
D3. **Served only when `A2A.Enabled` is true.** Otherwise no route exists (404) and no A2A tool is registered.
D4. **Endpoint.** JSON-RPC at `/a2a`, mapped with the SDK's `MapA2A` over our own `IA2ARequestHandler` (not `A2AServer`), behind an
    authorization policy that requires the role `a2a:call`. No agent card route in this batch. The existing allowed-host, rate-limit,
    header and body-size middleware must apply to it (test the body limit and the rate limit, or show they are app-wide).
D5. **Caller.** Through `IHttpContextAccessor`: the authenticated API key gives the profile and the partner id (batch 01). A request
    whose key has no partner is refused. The partner id, never the key name, owns tasks.
D6. **`A2A-Version`, enforced by the Host before the SDK sees the request:** `1.0` accepted; header missing accepted as 1.0; header
    present but empty refused; `0.3` refused; anything else refused. Refusals use the SDK's own VersionNotSupported error shape
    (-32009, HTTP 200). This is the owner's delegated decision: empty and 0.3 follow the spec, missing follows the SDK.
D7. **Task table.** SQLite schema 13 (follow the existing migration pattern; one bump): `a2a_tasks` with `task_id` (UUID made by the
    Host, primary key), `partner_id`, `context_id` (made by the Host), `message_id`, `content_hash`, `skill_id`, `submit_action_id`
    (nullable), `work_item_id` (nullable), `reply_action_id` (nullable), `created_at`, `updated_at`; unique on
    (`partner_id`, `message_id`). No message text in this table.
D8. **SendMessage (new task only).** Steps, in this order, crash-safe:
    1. Validate: exactly one inbox skill is configured and usable by the caller's profile (D10); parts are `text` (the goal) and
       optionally `data` (inputs JSON); a `file` part, a `taskId` or `contextId` naming an existing task, or a non-empty `tenant` is
       refused (UnsupportedOperation or InvalidParams, whichever the SDK type fits; say which). Size caps as a2a.md section 4.
    2. In one transaction: look up (`partner_id`, `message_id`). Found with the same `content_hash`: go to step 4 with that row.
       Found with a different hash: refuse (InvalidParams, generic text). Not found: insert a row with a new task id.
    3. If the row has no `submit_action_id`: submit `a2a.submit` as the partner with idempotency key = the task id, then store the
       action id on the row. A crash between the two is healed by the retry reaching this step again with the same key (verify the
       action store returns the existing action for the same key and arguments; test it by simulating the crash).
    4. Return the task. `returnImmediately=true`: return now. `false` or omitted: in THIS batch return UnsupportedOperation with a
       message that blocking sends are not served yet; do not block, and do not return SUBMITTED for a blocking request. Check for
       this before step 2, so a refused blocking send leaves no row.
D9. **Tools, in a Host module `a2a` inside the new project, registered only when A2A is enabled:**
    - `a2a.submit`: `ReadsUntrusted`, risk `Write`, with a preview showing the partner display name, the skill and the goal as quoted
      outside text. Arguments: `partner` (set by the adapter from the credential, never from the request), `taskId`, `skill`, `goal`,
      `inputs`. In this batch its body only records that it ran (returns `{ "accepted": true }`); the work item comes in 03b-2.
      Startup refuses A2A enabled unless the policy has an entry for `a2a.submit`. If its mode is plain `Execute` with no rule, that is
      a startup warning, not an error (the operator may trust all partners; say so in the warning).
    - `a2a.reply` is NOT built in this batch.
D10. **Skills config.** `A2A.Skills`: id -> `{ Name, Description, Inbox }` where `Inbox` names a profile in the policy's `Agents`.
    Startup checks when enabled: at least one skill; every `Inbox` exists. A partner may use a skill when its profile lists
    `a2a.submit`. Skill selection by extension is not in the slice: with exactly one usable skill it is chosen; with none or several
    the request is refused with a generic "not available" error.
D11. **GetTask.** Only the owner partner's tasks; foreign and unknown ids give the same TaskNotFound. State from the submit action:
    Pending -> SUBMITTED; Rejected, ChangesRequested, Denied, Expired, Cancelled-by-someone-else -> REJECTED (generic text, no
    reviewer reason); Failed -> FAILED (generic); Succeeded -> WORKING ("accepted, in progress") until 03b-2 adds the rest. No
    artifacts yet.
D12. **CancelTask.** Submit action Pending: cancel it as the requester; state CANCELED. Anything else: TaskNotCancelable.
    **Everything else on the handler** (ListTasks, streaming, push config, extended card, resubscribe): UnsupportedOperation.
D13. **Audit.** `a2a.received` (task id, partner id, skill id, content hash, size; no text) and `a2a.refused` (a reason code), written
    through the Host's audit sink. Audit detail never holds argument values.

## Tests (new files in OpenTail.Host.Tests; drive the endpoint with the SDK's client in-process, as your facts file example does)

- returnImmediately=true creates a task: SUBMITTED. The queued `a2a.submit` carries the partner id from the credential even when the
  message metadata or data claims another partner. The session is tainted by it.
- A reviewer approves -> WORKING; rejects -> REJECTED with no reviewer reason in the response; the partner cancels while pending ->
  CANCELED.
- Same `messageId`, same content -> the same task id and no second action. Same `messageId`, different content -> refused.
- A crash between the row insert and the action submit (simulate it by inserting the row without an action): the retry completes it,
  with one action.
- Another partner's key: GetTask and CancelTask give TaskNotFound, identical to an unknown id.
- A key without `a2a:call`: refused. An `mcp:tools` key: refused. No key: 401. A2A disabled: 404, and no `a2a.submit` tool.
- The version matrix (missing, empty, `0.3`, `1.0`, `2.0`) with the outcomes in D6.
- A blocking send (false, and omitted): UnsupportedOperation, and no task row is left behind.
- A file part, a non-empty tenant, an oversized goal: refused, and audited as `a2a.refused`.
- A schema 12 database migrates to 13 with existing actions intact. A restart finds existing tasks.
- Mutation check: switch off (a) the owner check in GetTask and (b) the version filter, one at a time. The matching tests must fail.
  Restore both. Report it.

## Docs

`OPERATIONS.md`: replace "A2A (not yet served)" with what is served now and what is not (no card, no blocking sends, no replies yet).
Checklist C.0, C.1, C.2: tick what this batch really completes; note the SDK facts that close C.2 items; record D6 under "Decided" as
the planner's delegated decision. `a2a.md`, "What is built": one paragraph.

## Report

Message `git-fa`, at most 45 lines: per design item done / changed / stopped; the seam you added in D2; differences from a2a.md; files
changed; full-run counts for both suites (after D1 alone, and at the end); the mutation check result; existing tests changed and why.
