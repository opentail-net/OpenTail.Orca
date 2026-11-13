# Design: queued actions as MCP Tasks

**Status:** proposed 2026-10-18. Steps 1 (the SDK upgrade), 2 (tasks for queued and delayed actions) and 3 (long-running calls) built 2026-10-18; step 4
waits on its prerequisites. Written from reading [`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca)
at `8d87b95`, the Tasks extension ([SEP-2663](https://modelcontextprotocol.io/seps/2663-tasks-extension.md), Final), the
2026-07-28 MCP specification it depends on, and the C# SDK v2 documentation. Facts about the protocol and SDK were checked on
2026-10-18; re-check before building.

**Where it fits:** step 5 of [work items and continuations](work-and-continuations.md#order) (work plan section 17), brought
forward to design now because its first part needs nothing from work items: every queued or delayed action can already be a task.
It changes how a result *reaches* a client that supports Tasks; it changes nothing about how an action is decided.

## The problem

When a call cannot finish at once (it waits for a reviewer, runs after an undo window, or takes a long time), the Host returns
ordinary tool output, `{"status": "pending_approval", "actionId": ...}`, and teaches the model through server instructions to call
`get_action_status`, `wait_for_action` or `get_updates`. That works with every client, but:

- **The model does the bookkeeping.** It must remember the action id, decide when to check, and not poll in a loop. Models forget,
  or poll anyway; the instructions spend tokens on every conversation saying so.
- **The client cannot help.** A pending action looks like any other tool output, so a client that could track and poll it for the
  model, or show "waiting for approval" in its own interface, has nothing to go on.
- **It is OpenTail-specific.** Every agent has to learn our tools; none of what it learns carries over to other servers.

The 2026-07-28 MCP revision added a standard for exactly this: the **Tasks extension**. A server answers `tools/call` with a task
handle instead of a result; the client checks it with `tasks/get`, can cancel it with `tasks/cancel`, and answers questions with
`tasks/update`. The client, not the model, keeps the handle and polls.

## What the standard says (as of 2026-10-18)

- **Extension** `io.modelcontextprotocol/tasks`, moved out of the core protocol in the 2026-07-28 revision. The experimental tasks
  of 2025-11-25 are gone and not wire-compatible.
- **The client opts in on each request** (`_meta` client capabilities); the **server decides per request** whether to return a task
  (`resultType: "task"`) or an ordinary result. A server must never return a task to a client that did not opt in.
- **Only `tools/call`** can return a task so far.
- **Statuses:** `working`, `input_required`, `completed`, `cancelled`, `failed`. `failed` is only for protocol (JSON-RPC) errors;
  a tool result with `isError: true` is `completed`.
- **`tasks/get`** is a pure read; **`tasks/update`** carries answers to `inputRequests` (elicitation or sampling, addressed to the
  user, not the model); **`tasks/cancel`** is an acknowledgement, and the task may still end some other way.
- **There is no `tasks/list`,** on purpose, so one caller can never learn of another's tasks. Clients are expected to store task
  ids durably.
- **A task must be durable before the handle is returned,** carries `ttlMs` (after which the server may forget it) and a suggested
  `pollIntervalMs`.
- **Push** (`notifications/tasks` over `subscriptions/listen`) is optional and not yet implemented in the C# SDK.
- **Security:** task ids must be unguessable; every task request must be authorised; `inputRequests` get the same trust as any
  elicitation, never more.

**C# SDK:** Tasks need `ModelContextProtocol` 2.x (2.2.0 is current) and the `ModelContextProtocol.Extensions.Tasks` package. The
Host is on **1.4.1**, which speaks the 2025-11-25 revision only. SDK 2.x still accepts 2025-11-25 clients, so upgrading does not
break today's clients.

**Clients:** support varies and I found no authoritative list of which major clients (Claude Code, VS Code, Cursor, Codex) speak
Tasks today. Check before building; it decides how much this is worth (see [when](#when-it-is-worth-it)).

## The idea

**An action is a task.** The Host already keeps exactly what the standard asks of a task server: a durable record created before
the response is sent, an unguessable id, an owner checked on every read, a status, a result, and an expiry. Tasks become a second
way to *see* actions, beside the built-in tools, for clients that ask for it.

### When a call becomes a task

Only when the client opted in on that request, and only when the action does not finish inline:

| What happens to the call | Client opted in | Client did not |
|---|---|---|
| Runs and finishes inline | ordinary result (unchanged) | ordinary result (unchanged) |
| Denied, refused by a profile, a flow rule or a limit | ordinary result with the error (unchanged) | unchanged |
| Queued for a reviewer | **task**, `working` | `pending_approval` JSON (unchanged) |
| Delayed by an undo window | **task**, `working` | `scheduled` JSON (unchanged) |
| Runs longer than the inline limit (proposed 20 s) | **task**, `working` | waits, as today |

A client that did not opt in sees no change at all, and the Host never answers "missing capability" (`-32021`): there is always the
old way.

### How the statuses map

| Action status | Task status | What the client gets |
|---|---|---|
| Pending (awaiting review) | `working` | `statusMessage`: "Waiting for a reviewer; expires at …" |
| Pending with a run-at time | `working` | "Runs at 14:20 UTC unless a person stops it." |
| Approved, Executing (including a retry waiting its turn) | `working` | "Running." |
| Succeeded | `completed` | the same `CallToolResult` an inline success returns |
| Rejected, ChangesRequested, Expired, Denied | `completed`, `isError: true` | the same JSON `get_action_status` gives, with the reviewer's reason |
| Cancelled because a person pressed Stop on a delayed action | `completed`, `isError: true` | "Stopped by a person before it ran." |
| Failed (the tool failed) | `completed`, `isError: true` | the error, as today |
| Cancelled by the requester | `cancelled` | — |
| The Host itself broke (store error, crash) | `failed` | JSON-RPC error `-32603` |

A person's decision (rejection, stop, expiry) is a *result*, not a cancellation: the model needs to read the reason. `cancelled`
is kept for the client's own cancel. **Changes requested** completes the task with the reviewer's reason and the usual hint to call
`resubmit_action`; the resubmission is a new action, and so a new task.

### The three methods

- **`tasks/get`** goes through the same code as `get_action_status`, not a copy of it. Reading a result therefore applies taint (a
  tool that reads outside content taints the session recorded on the action and its owner), adds data labels, runs output guards,
  and marks the notice as seen, exactly as today. This is the part most easily got wrong: a second path to results that skipped
  taint would undo the protection the Host exists for.
- **`tasks/cancel`** is `cancel_action`: the requester may cancel a pending action. A running or finished action cannot be
  cancelled; the Host acknowledges anyway (the standard allows it) and the task ends as it ends.
- **`tasks/update`**: not used at first; the Host has nothing to ask the *user* mid-task. A reviewer's decision is not client
  input, which is why waiting for review is `working`, not `input_required`. [Later uses](#later) are listed below.

Ownership: every task request resolves the caller at the protocol edge, as tool calls do. Someone else's task id and an unknown id
give the same answer (`-32602`, "not found"), so ids cannot be probed. Task ids are the action ids (`act_` plus a random GUID, 122
random bits), so there is no second id to map.

### Timing fields

- **`ttlMs`:** for a pending action, until its expiry plus the result retention period; for a finished one, until it is purged.
  The Host purges on its own schedule, so the value is honest rather than fixed.
- **`pollIntervalMs`:** chosen by what the action waits for. Awaiting a person: 60 s (people are slow; checking every second helps
  nobody). Delayed: the time until it runs, capped at 60 s. Running: 2 s. Polling much faster than suggested counts towards the
  caller's limits under [adaptive limits](adaptive-limits.md), report-only like the rest.

### What stays

`get_action_status`, `wait_for_action`, `list_my_actions`, `get_updates` and notices all stay: most clients do not speak Tasks yet,
and `list_my_actions` covers what the standard deliberately leaves out (there is no `tasks/list`). The server instructions get a
shorter variant for clients that opted in, since the client now does the tracking.

## How it is built

- **Upgrade the SDK first,** 1.4.1 to 2.x, as its own step with the full test suite. The Host already runs Streamable HTTP
  stateless and keeps its own session handles, which is what the 2026-07-28 revision moves everyone towards, so the upgrade should
  be mostly mechanical. Only `OpenTail.Orca` and `OpenTail.Orca.Http` reference the SDK; `OpenTail.Orca.Abstractions` does not, so
  module authors and the frozen contract are unaffected. Note the licence change to MIT in SDK 2.0 for the notices file.
- **Use the SDK's alternate-result handler, not its task store.** `WithTasks` would run every tool call in its own background task
  with its own store, beside ours. Instead, `CallToolWithAlternateHandler` (which replaces our `CallToolHandler`) returns either the
  ordinary result or a `CreateTaskResult` built from the action, and the Host serves `tasks/get`, `tasks/update` and `tasks/cancel`
  from the action store. One source of truth.
- **Several Host instances:** the standard has clients send the task id in an `Mcp-Name` header so a load balancer can route to the
  instance that holds the task. Host instances sharing one action store do not need that; a deployment that does not share a store
  must route on it.

## Later

- **Push:** `notifications/tasks` once the SDK supports `subscriptions/listen`, fed by the Host's existing action events. Polling
  stays the baseline.
- **Work items** ([section 17](work-and-continuations.md)): `delegate` returns a task the delegating agent can poll; it completes
  when the item is done, with the completion summary as its result. Tasks and continuations complement each other: a task suits a
  client that keeps the handle and polls; a continuation suits an agent that will lose its context and needs waking with a note.
- **`input_required`, carefully:** URL elicitation to send the *user* to sign in to a downstream service (per-user sign-in, work
  plan section 3), or to the review page when they are themselves the approver. Elicitation reaches a person through the client, so
  it is never used to ask the model anything, and never as a way for a reviewer's decision to arrive.

## Rules that make it hold

1. **One path to results.** `tasks/get` and `get_action_status` share the code that applies taint, labels, guards and notices.
   Tests prove a tainting result read through `tasks/get` taints, and that a labelled one labels.
2. **Never a task without the opt-in** on that very request; never `-32021`.
3. **Ownership on every request;** unknown and foreign ids look the same.
4. **No new decisions.** Returning a task changes nothing about policy, review, delays or limits: the same action is created either
   way, and the audit trail does not depend on how the client watches it.
5. **Durable before returned:** the action is stored before the handle goes out (it already is).

## Metrics to record

Calls answered with a task versus the old JSON, by client (from client info) and reason (queued, delayed, long-running);
`tasks/get` calls per task and how often clients poll faster than suggested; tasks cancelled through `tasks/cancel`; time from task
creation to the client's first read of the result.

## When it is worth it

The value depends on clients. With no major client speaking Tasks, this is groundwork; with one, every queued action in that client
gets better at no cost to others. Proposed order:

1. **SDK upgrade to 2.x** on its own: worth doing anyway, to stay on a supported SDK and the current protocol revision. *Done
   2026-10-18: `ModelContextProtocol` and `.AspNetCore` 1.4.1 to 2.2.0 (with `Microsoft.Extensions.AI.Abstractions` 10.6.0 to
   10.8.3, which it needs). No code changes were needed; all tests pass, plus two new ones: a current client negotiates 2026-07-28,
   and a 2025-11-25 client still initialises, lists and calls tools over HTTP.*
2. **Tasks for queued and delayed actions:** small once the upgrade is in (mapping, three methods, tests), and harmless to clients
   that do not opt in. *Built 2026-10-18 (owner's go-ahead to build the plan): `ModelContextProtocol.Extensions.Tasks` 2.2.0 for
   its types only (not its task store); `CallToolWithAlternateHandler` returns a `CreateTaskResult` when the call left a pending,
   approved or running action and the client opted in; `tasks/get` reads through the same code as `get_action_status`
   (`BuiltInTools.ReadOwnAsync`: ownership, taint, notices); `tasks/cancel` cancels a pending action; `tasks/update` answers that
   the task asks for no input; poll intervals 60 s and 2 s as proposed (provisional); `opentail.host.tasks` counts tasks by reason.
   The SDK marks these APIs experimental (MCPEXP002, suppressed where used). Not built: the shorter server instructions for
   clients that opted in (the instructions are per server, not per client). Tested with the SDK's own client over in-memory
   streams; no real client yet.*
3. **Long-running calls as tasks** (the inline limit). *Built 2026-10-18: for a client that opted in, the call runs detached from
   its request (on the Host's lifetime, so a stateless HTTP request ending does not cancel it); past `TaskInlineLimit` (provisional
   default 20 s, in the policy) the client gets a task for the running action, polled every 2 s, and the action finishes in the
   background. The queue hands the handler the running action as soon as it is stored. Other clients wait as before.*
4. **Push, delegation and `input_required`,** each when its prerequisite exists. *Not built: push needs `subscriptions/listen` in
   the C# SDK; a task for a delegated work item needs task ids that are not action ids (`work.delegate`'s own action finishes at
   once); `input_required` needs per-user sign-in (work plan section 3).*

## Decisions

Decided by the owner on 2026-10-18: upgrade the SDK now, as its own step (done).

Still open:

- **Inline limit** of 20 seconds before a long call becomes a task, or another value?
- **Poll intervals:** 60 s while a person decides, 2 s while running, as proposed?
- **Do clients matter first?** Build step 2 now as groundwork, or wait until a client our customers use supports Tasks?
