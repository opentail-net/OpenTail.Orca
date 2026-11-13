# Design: execute with an undo window

**Status:** built on 2026-10-18: `DelayFor` and `Delay` on policy entries and Execute rules, `TrustRamp.DelayFor`,
`Limits.ElevatedDelay`, `DelayUntilWorkingHours` with policy `WorkingHours`; the run-at time on actions (a Pending action with
`RunAt`, so no new status or schema change was needed); the sweep that runs due actions and holds a stale backlog (more than its
delay late); Stop and run-now on the review page and the Teams card; a "Going out soon" headline in notifications. First written 2026-10-17. Written from reading
[`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca) at `230cdd0`. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here. The owner's decisions of 2026-10-18 are under [decisions](#decisions). Corrected on 2026-10-18: Delay is a `DelayFor` on `Execute`, not a fourth `ToolMode` value ([why](#where-it-sits-in-policy)).

**Where it fits:** Orca work. A new step between running a call now and waiting for a reviewer. It fits the
[adaptive limits ladder](adaptive-limits.md#the-ladder) as the step an Elevated agent can drop to before everything queues.

## The problem

Each tool call is `Execute` (run now), `Queue` (wait for a reviewer) or `Deny` (`ToolMode`). For many actions neither of the
first two is right:

- **Queue costs people.** A reviewer approving the 200th ordinary reply of the week stops reading. Approval fatigue is the weak
  point of every approval system, and an attacker benefits from it.
- **Execute gives nobody a chance.** A wrong mail is sent, a ticket closed, a record changed, before anyone could see it.

Most of these actions would be approved anyway. What people want is to **see them go by and be able to stop the odd one**, as with
Gmail's undo send.

## The idea

**Delay: the action is announced, runs after a set time unless someone stops it, and is audited either way.** In policy it
is `Execute` with a `DelayFor`; this page calls it Delay for short.

```json
"mail.send":     { "Mode": "Execute", "DelayFor": "10m", "ReviewerGroup": "support" },
"ticket.update": { "Mode": "Execute", "DelayFor": "2m" }
```

### How it runs

1. The call passes policy as today and is stored as an action with a **run-at time** (`now + DelayFor`).
2. The agent gets an answer straight away: "accepted, runs at 14:32 unless stopped", with the action id. It does not wait; the
   call is not held open.
3. People are told: a line on the review page under **Going out soon**, and a Teams card with the preview and one button,
   **Stop**. Notifications follow `ReviewerGroup` as for queued actions, but nobody has to act.
4. At the run-at time, the action runs exactly as an `Execute` would have, through the same executor, rate limits and audit.
5. **Stop** cancels it (the existing `Cancelled` status), audited with who stopped it. Stopping is open to anyone in the reviewer
   group; it needs no second approval, because stopping is always the safe direction.
6. A reviewer can also **run now** (skip the wait) or **hold** it, which turns it into an ordinary queued action needing approval.

### What a delay is good for

| Tool | Typical delay | Why |
|---|---|---|
| Replies to customers | 5 to 15 min | Most are fine; a wrong one is embarrassing and cannot be unsent |
| Ticket updates and closures | 1 to 5 min | Cheap to stop, annoying to reopen |
| Calendar invitations | 5 min | Reach other people |
| Bulk changes the agent proposes | Hours (overnight) | Someone looks in the morning |

Not for: destructive actions (they keep Queue), anything time-critical, and Read tools (nothing to stop).

### Where it sits in policy

- **Not a new `ToolMode` value.** `ToolMode` is `Deny = 0`, `Queue = 1`, `Execute = 2`, and `ToolPolicy.Stricter` combines
  modes with `Math.Min` on those numbers. A `Delay` value cannot go between `Queue` and `Execute` without renumbering
  `Execute`, which is stored in actions and policy files and compiled into modules against contract version 1. So Delay is a
  property: `DelayFor` on a policy entry or rule, meaningful only with `Execute`. That is an additive contract change (a new
  optional field), and existing modules and stored actions are untouched.
- **Ordering holds:** `Deny` < `Queue` < Execute with a delay < `Execute`. Combining only tightens: a Queue rule still beats an
  Execute mode with a delay, and when two entries both execute, the longer `DelayFor` wins.
- **Rules can choose it:** a rule can set a `DelayFor` for some arguments (`mail.send` to a new recipient: delay; to a known
  one: execute).
- **Taint forces Queue, not Delay.** A tainted session's high-impact calls still wait for a reviewer: an announcement nobody reads
  is not a review. This is the line that keeps Delay from weakening anything that exists today.
- **Trust ramp:** a tool can be promoted from `Queue` to `Delay` after clean approvals, before (or instead of) promotion to
  `Execute`. A natural middle rung.
- **Adaptive limits:** an Elevated agent's `Execute` calls can drop to `Delay` instead of `Queue`, if the operator prefers less
  friction. Suspended still denies.

## Rules that make it hold

1. **What runs is what was announced.** Arguments are fixed when the action is accepted; there is no editing during the delay
   (stop and let the agent resubmit instead). The preview shown is the preview that runs.
2. **The run-at time survives restarts.** Delayed actions live in the action store like queued ones; on start Orca runs any
   whose time has passed (or, if they are much older than `DelayFor`, holds them for a person, so an outage does not fire a backlog
   of stale actions at once).
3. **Stopping is never blocked.** Rate limits, budgets and approvals apply to running, never to stopping.
4. **The agent cannot shorten its own delay.** `DelayFor` is configuration, never an argument.
5. **Audited as both:** announced (`action.delayed`), then `action.executed` or `action.stopped` with who stopped it.

## Metrics to record

Delayed, stopped and run-now counts by tool; time from announcement to stop (do people stop things early or at the last minute?);
stop rate per tool (a high stop rate says the tool should be Queue; a zero stop rate over months says it could be Execute). These
feed the trust ramp and the [reviewer watch](more-extensions.md#watching-the-reviewers).

## Order

1. **`DelayFor`** on policy entries and rules (an additive contract field; `ToolMode` is unchanged), the action schema
   version bump for the run-at time, policy validation, the run-at time on actions, the scheduler, Stop on the review page.
2. **The Teams card** with Stop, and the **Going out soon** list.
3. **Rules and trust ramp** choosing Delay.
4. **Adaptive limits** using Delay for Elevated.

## Decisions

Decided by the owner on 2026-10-18.

- **Default delay:** 10 minutes, where a policy asks for a delay without a length.
- **Who may stop:** anyone in the reviewer group. Stopping should be easy for anyone who can see it.
- **Outside working hours:** stretching a delay to the next working morning is an opt-in per tool (`DelayUntilWorkingHours`).

Still open: **stale backlog after an outage**, how much older than `DelayFor` before an action is held instead of run.
Proposed: twice the delay.
