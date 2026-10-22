# Design: work items and continuations

**Status:** proposed 2026-10-18. Steps 1 (continuations), 2 (work items) and 3 (wake-up) built 2026-10-18; steps 4 and 5 in part. Written from reading [`opentail-host`](https://github.com/opentail-net/opentail-host)
at `2537674` and from the state of the protocols in October 2026 (MCP Tasks extension, A2A). The owner's position of 2026-10-18 is
under [decisions](#decisions); the rest is open for discussion.

**Where it fits:** Host work, after [roles and permissions](roles-and-permissions.md) (who may do what) and alongside
[where data goes](data-flow.md) (labels must survive a wait). It gives the Host a memory of **work owed and work waited on**; the
tools stay the agent's hands.

## The problem

The Host is good at one call at a time. A queued action already outlives the agent's turn: the decision is kept as a notice and
handed over on the agent's next call or through `get_updates`, and `wait_for_action` waits up to 25 seconds. Three things are
missing:

1. **Giving an agent work.** There is no way for a person, a system or another agent to say "do this" and have it picked up later
   by whichever agent is suited, with a fresh context per run.
2. **Coming back to something.** An agent that sends off a request (an approval, a licence check, a company-registration lookup)
   has to stop. When the answer arrives days later, nothing connects it to what the agent was doing or why. Today the agent must
   happen to call `get_updates` and reconstruct the rest from nothing.
3. **Waking the agent.** The Host cannot call into an agent whose chat is closed. This is not a gap in our code but in the
   protocol: MCP no longer allows requests from server to client that the client did not ask for (SEP-2260). Whatever wakes an
   agent has to sit beside the protocol.

## What the Host is, and is not

**The Host keeps the ledger; the agent's harness does the thinking.** A harness runs the model: it builds prompts, chooses
context, runs the loop of tool calls, calls the model and decides the next step. The Host does none of these, before or after this
design. A work item is a ticket; delegating is putting a ticket in an inbox, as Jira does for people without being their brain.

The line, written down so later work does not drift over it. The Host never:

- calls a model to do the work (the existing AI reviewer, which judges an action, is unchanged);
- builds prompts or chooses what goes into an agent's context;
- stores or replays transcripts;
- decides an agent's next step.

What the Host does keep: records of work, what is being waited on, outcomes, and a short **note** the agent writes for its future
self, stored opaquely like an attachment on a ticket. The Host never reads the note to make a decision.

## The idea

Three pieces, each useful alone, built in this order: continuations, work items, wake-up.

### Continuations: "wake me when this comes back"

An agent that has sent something off parks its place:

```
park(
  note:       "Checking ACME Ltd for the supplier onboarding. Approval a-81f3 for the bank-detail change is pending;
               licence check lc-2207 sent. Next: if both pass, create the vendor in the ERP; if the licence fails, mail
               procurement@ with the reason.",
  waitingOn:  ["a-81f3", "ext:licence/lc-2207"],
  wakeWhen:   "all",            // or "any"
  until:      "2026-10-25"      // give up, and wake with what is known
)
→ { "continuationId": "c-4410" }
```

When the condition is met (or `until` passes), the Host turns the continuation into a **resume work item**. It carries the note
and the outcomes: the approval or rejection with the reviewer's reason, the licence check's result, and which waits timed out.
The agent that picks it up, in a fresh context, has everything it needs to carry on.

- **What can be waited on:** the Host's own actions (queued, delayed, or simply long-running), timers, and
  [external waits](#external-waits). Waiting on another work item is how delegation reports back.
- **The note is the agent's, not the Host's.** It may be a summary in prose, structured state, or just a pointer into the
  agent's own session store (for harnesses that can resume sessions). It is size-capped (proposed 8 KB) so it holds a summary,
  not a dump.
- **Notices stay.** `get_updates` keeps working for agents that never park. A continuation is the richer form of the same idea.

### Work items: the inbox

A work item is a unit of work owed by an agent:

| Field | Meaning |
|---|---|
| `goal` | What is wanted, in words, written by whoever created it |
| `inputs` | Structured inputs (JSON, size-capped) |
| `for` | Who it is for: a profile (any agent holding it may claim) or one named identity |
| `from` | Who created it: a person, a system (a webhook, a schedule) or an agent, with the chain if delegated |
| `thread` | Groups related items (a delegation and its sub-tasks, a resume and what it resumes) |
| `due`, `ttl` | When it should be done; when it is abandoned |
| `budget` | Optional caps: calls, units, wall time |
| `labels`, `tainted` | Carried from whatever created it (see [rules](#rules-that-make-it-hold)) |
| `note` | For resume items, the parked note |

Life of an item: **open → claimed → done | failed | handed back**, plus **expired** and **dead** (too many failed attempts).

- **Claiming takes a lease** (proposed 10 minutes, renewable). An agent that dies loses the lease and the item reopens. This means
  an item can be worked twice; see [run twice](#rules-that-make-it-hold).
- **Each run starts with a fresh context.** What carries over between runs on one thread is the **thread note**: a short,
  size-capped text the agent rewrites as it goes ("what I know, what I tried, what is next"). Agents built for long tasks already
  work this way, with progress files. It is cheaper than replaying a transcript, model-neutral, and an operator can read it.
- **Creating work** comes from three places: a person (review page, CLI, API, later chat: see
  [Slack as a front door](slack-front-door.md), deferred), a system (an inbound webhook, a schedule), or an agent through `delegate`. Policy governs each like any tool call: `delegate` is a tool with a policy entry, so delegating to a
  production-capable profile can be made to queue.

Built-in tools, added to the existing set (`get_updates`, `wait_for_action` and the rest stay):

| Tool | Does |
|---|---|
| `next_work` | Claim the next item this caller may take (optional long wait, like `wait_for_action`) |
| `get_work` | Read one item, with its outcomes and thread note |
| `complete_work` / `fail_work` / `hand_back_work` | End a claim, with an optional summary for whoever is waiting |
| `park` | Create a continuation (above) |
| `delegate` | Create a work item for another profile, optionally waited on by a continuation |
| `update_thread_note` | Rewrite the thread note |

The operator gets `--work` (list, filter by state, profile, thread), `--work <id>` (the item, its note, its history) and
`--requeue` / `--cancel-work`.

### Wake-up: three interchangeable ways

An item becoming ready has to reach an agent. The Host offers three ways and is neutral between them:

1. **Pull.** The agent calls `next_work`. Works for any MCP client, including chat apps a person opens in the morning.
2. **Push to a URL.** A signed webhook to an agent platform, one per profile or identity: "work ready: w-512". The platform starts
   its agent, which then pulls. The webhook carries ids only, never the goal or note, so a leaked URL leaks nothing.
3. **Host-started.** The Host runs a configured command that starts a headless agent with a work id, as `ReviewerRunner` already
   starts reviewers. This starts *someone else's* harness; the Host does not become one. Optional, off by default.

### External waits

A licence or registration check is usually: start it, get a reference, hear back later. The module owns this:

- The module's start tool returns an **external reference** (`ext:licence/lc-2207`) the Host records as a wait.
- Completion arrives by a **signed inbound callback** to the Host (`/api/callbacks/{module}/{ref}`, signature checked by the
  module) or by the module **polling** on the maintenance sweep.
- The outcome is stored as outside content: whatever the agent reads from it taints like any other outside read.

The Host gains one generic thing, the wait record and the callback route; each check's specifics stay in its module.

### Standard protocols

The internal model maps onto the protocols as they stand, so clients that speak them need nothing OpenTail-specific:

- **MCP Tasks extension** (SEP-2663, Final, 2026). A queued or delayed action, and a delegated item a client waits on, can be
  returned as a task handle. The client checks it with `tasks/get` and can cancel it. "Waiting for a reviewer" is reported as
  `working` with a status message, not `input_required`: that status is for questions the *client* must answer, and a reviewer's
  decision is not one. The task's `ttlMs` is the item's `ttl`.
- **A2A**, later and only if asked for: work items for another organisation's agents map onto A2A tasks, `thread` onto
  `contextId`, and push-to-URL onto its push notifications.

## Rules that make it hold

1. **Waiting does not clean a session.** A continuation records its session's taint and labels at the moment of parking, and the
   resume item carries them. Taint today lasts `TaintWindow` on the subject; a resume after a week would otherwise come back clean,
   and "park, then resume" would launder both taint and labels.
2. **Notes count as what the session read.** The note was written by a model that may have read an injection, and it goes back
   into a model later. Reading a note (or a thread note) applies its session's taint and labels. Size caps keep it a summary;
   operators can read every note.
3. **Everything is checked again on resume.** Policy, profile, identity level and grants are evaluated when the resumed agent
   acts, not when it parked. An approval covers the one action it was given for, never the continuation. A key suspended during
   the wait stays suspended; a grant that expired stays expired.
4. **Delegation only narrows.** A delegated agent acts under its own identity and profile; the item records the delegator, and the
   audit trail shows the chain. An agent can delegate only to profiles its own profile lists (`MayDelegateTo`), and only tools in
   both profiles count as available to the work (intersection). The chain is capped (proposed depth 3) so agents cannot bounce work
   between themselves forever.
5. **Assume anything can run twice.** Leases expire mid-run; callbacks repeat. Writes made while working an item carry an
   idempotency key derived from the item and step (the Host already honours `IdempotencyKey` on actions), and the agent is told
   so in the tool descriptions.
6. **Every item ends.** `ttl` on every item and continuation (proposed default 14 days); after a set number of failed claims an
   item goes to **dead** for a person to look at. Nothing waits forever unseen. What kinds of failure there are, how far each is
   retried, and how a dead item is inspected and redriven: [failures, retries and inspection](failures-and-inspection.md).
7. **Budgets are per item,** as well as per identity under [adaptive limits](adaptive-limits.md): a runaway item is stopped
   without suspending the agent's other work.

## How it fits with the other designs

- **Undo window, approvals:** these are the most common things to wait on. A continuation waiting on a delayed action wakes when
  it runs or is stopped.
- **Temporary access:** `access.request` is a natural wait: park until the grant is decided. The grant is checked on resume, not
  carried in the continuation.
- **Data flow:** labels on the item, rule 1; delegation to a profile with outbound tools is a flow destination like any other.
- **Adaptive limits:** a dead item or many failed claims are soft signals; delegation depth hitting the cap is one too.
- **Skills:** a skill describes how to do a kind of work; a work item is one instance of it. An item may name the skill it expects.

## Metrics to record

From the start: items created by source, claimed, done, failed, expired and dead, by profile; time from open to claim and from
claim to done; continuations parked and woken, by wait kind and outcome (met, timed out); leases lost; delegation depth reached.
Audit events: `work.created`, `work.claimed`, `work.done`, `work.failed`, `work.expired`, `continuation.parked`,
`continuation.woken`. Notes and goals never go into telemetry.

## Order

1. **Continuations on existing actions.** `park` waiting on actions and timers, resume items delivered through `next_work` and
   `get_updates`, taint and labels carried (rules 1 to 3). Schema bump. This alone covers "send an approval, come back to it".
2. **Work items.** The inbox, claiming with leases, `delegate` with `MayDelegateTo`, thread notes, `--work`, ttl and dead items.

*Steps 1 and 2 built 2026-10-18, together (a resume is a work item): `WorkService` with an in-memory and a SQLite store (schema 10);
the tools `park`, `next_work`, `get_work`, `complete_work`, `fail_work`, `hand_back_work`, `update_thread_note`, offered when the
policy sets `Work.Enabled`; `work.delegate` as an ordinary tool in a built-in `work` module (registered when a profile lists
`MayDelegateTo`), so policy governs it; taint and labels recorded at park, creation and delegation and applied to whoever reads the
item or the thread note; outcomes carry statuses and reasons, never results; a lost lease counts as a failed run; `--work`,
`--work-item`, `--requeue`, `--cancel-work`; `workReady` in `get_updates`. Provisional defaults (the proposals, the owner's to
change): work tools off by default; lease 10 minutes; 3 runs; ttl 14 days; notes 8 KB, thread notes 16 KB; depth 3. Not built: the
tool intersection for delegated work (only tools in both profiles), per-item budgets (rule 7), work created from outside (webhooks,
schedules: still open), and putting dead items into inspection (failures design, step 3).*
3. **Wake-up by URL and Host-started runner.** *Built 2026-10-18: `WorkService.Ready` fires whenever an item is open and waiting
   (created, handed back, reopened, requeued, redriven, moved to the operators); a `work_ready` notification event (webhook channels
   only, a route's `Work` patterns pick who it is for) sends ids only; `Work.Runners` start a command with the work id, no shell, up
   to `MaxConcurrent`, audited as `work.runner_started`. Built, unverified: tested with a scripted webhook and a fake process start,
   not against a real agent platform.*
4. **External waits:** the callback route and polling, with one real module first (the licence or registration check a customer
   needs).
5. **MCP Tasks extension** for actions and waited-on items ([its own design](mcp-tasks.md); the part for actions needs nothing
   from work items); A2A only if a customer asks.

## Work items and `IWorkQueue` (2026-10-22, for the owner)

The owner asked to move work items onto Connectivity's `IWorkQueue`. On reading `WorkService`, I did not make the move, because it
would take away things work items need and add nothing the Host can use yet:

- **A work item is a record, not just a job.** `get_work` reads one by id; `--work` lists by state, profile and thread;
  `--work-item` shows its history; inspection reads dead items and their claimants. A queue can only hand out what is at its
  head. RabbitMQ cannot even look a message up by id.
- **Claiming filters.** `next_work` takes the first item *this caller* may claim (`For` is a profile or one identity;
  `Excluded` keeps failed claimants off an item for inspection). `IWorkQueue.LeaseAsync` takes the head of a named queue. One
  queue per `For` value covers the first; `Excluded` would mean leasing an item and handing it straight back, possibly in a loop.
- **Two sources of truth.** Keeping the table for the record and a queue for delivery means two writes that can disagree.
  It can be made safe (the table stays the truth, the queue only says "look at w-512", a claim is compare-and-set on the row,
  so a duplicate delivery is dropped, and a sweep re-sends open items the queue lost), but that is real work and new failure modes.
- **No gain today.** The Host keeps all its state in one SQLite file, so it runs as one node either way; the table already gives
  leases, runs, a dead state and redrive. A broker would only pay off once the Host runs as several nodes, and then the whole
  store needs a server database first, not only work items.

What I would do instead, when there is a reason:

1. **Now, if wanted:** use `IWorkQueue` with `WorkQueueWorker` for the Host's own retry-shaped jobs, where a queue is the natural
   fit: webhook deliveries (`work_ready` and the other notification events) and the external-wait polling in step 4 of the order
   above. Those are fire, retry with backoff, dead-letter; nobody queries them by id.
2. **When the Host goes multi-node:** the "delivery hint" shape above: the work table (on a server database) stays the record,
   `IWorkQueue` carries "item ready" per `For` value, and claims are compare-and-set on the row.

The Connectivity side is ready for either: `IWorkQueue`, the SQLite and RabbitMQ queues, the conformance suite and the worker
loop are built and pushed. The queue packages are not published yet; the Host would need them first.

## Decisions

Decided by the owner on 2026-10-18:

- **Not a harness.** The Host stores records and an opaque, size-capped note; it never stores transcripts and never calls a
  model to do the work.
- **The note is stored by the Host,** not only a pointer into the harness, so agents without a session store of their own can
  resume. It carries its session's taint and labels and is visible to operators.

Still open:

- **Note sizes:** 8 KB for a continuation note and 16 KB for a thread note, or other limits?
- **Who may create work from outside:** inbound webhooks creating items need their own credential and profile; is a schedule
  (cron-like) in scope, or left to external schedulers calling the API?
- **`MayDelegateTo` and depth:** a list per profile and a depth cap of 3, as proposed?
- **Default ttl:** 14 days for items and continuations?
- **Which external check first:** the licence or registration check a customer actually needs, to build external waits against.
