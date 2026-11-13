# Design: adaptive limits and intrusion detection

**Status:** the [groundwork](#groundwork-get-the-metrics-right-first) is built (2026-10-18): result stats, usage rollups and
telemetry, with modules declaring their counts on the descriptor rather than returning them. Order step 2 is built too: the
out-of-profile tripwire (Suspended) and reviewer rejections (Elevated), each `ReportOnly` by default and `Enforce` when set in
policy `Limits` (a change from "enforcing" in the order below: report only first, as the owner's decision for soft signals said).
Soft signals from step 5 (breadth and repeated tainted high-impact attempts; report only by default; volume and time of day wait
for baselines). Decoy records from step 6 too (policy `Decoys` markers and `Limits.Decoys`: read Elevated, sent Suspended, report only by
default). Order step 3 too: capacity backoff per tool or module, all callers (policy `Capacity`, a token bucket, a wait up to `MaxDelay`,
then a retryable `slow_down` with a retry time; in memory per process). Not built: per-identity budgets and Throttled,
the doubling delay (the wait is simply the time to the next token), smaller pages, notifications on level changes, automatic
proof of key rotation. First
written 2026-10-17. The point of
writing it now is the [metrics groundwork](#groundwork-get-the-metrics-right-first): what the Host must record from today so that
limits, backoff and detection can be built later on real data. Written from reading
[`opentail-orca`](https://github.com/opentail-net/opentail-orca) at `230cdd0`. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here. The owner's decisions of 2026-10-18 are under [decisions](#decisions).

**Where it fits:** Host work, next to [roles and permissions](roles-and-permissions.md). Profiles decide **what** an agent may
do; this design watches **how it behaves** while doing it, and tightens as the behaviour turns suspicious. Profiles come first:
they give each agent a stable identity to measure and the strongest [tripwire](#tripwires).

## The problem

A compromised agent does not need a forbidden tool to do harm. It can use the tools it legitimately holds, fast and at volume:

- **Data leaves through reads.** Once records are read they are in the model's context, and from there they can leak. Sending is
  guarded (taint holds high-impact tools for a reviewer), but reading is not: Read tools run straight through under
  `DefaultReadMode`. An agent can page through a whole customer table and nothing slows it.
- **Fast attacks finish before anyone looks.** Every guard the Host has acts per call. None sees that one identity has made 4,000
  reads in ten minutes when it usually makes 40 a day.
- **Volume costs the system.** A runaway or hostile agent hogs module workers, downstream API quotas and the database, at the
  expense of every other agent.

## What the Host has today

| Mechanism | What it does | Limitation |
|---|---|---|
| Taint | A session that read untrusted content gets high-impact tools queued | Binary; keyed on what was read, not on behaviour |
| `Budget` on an Execute rule | N runs per requester per window, then the call falls back to Queue | Closest to this design, but counts calls only, per tool, and only where an Execute rule exists |
| `RateLimit` | Refuses a tool beyond N per hour or day (`ActionExecutor.ExceedsAsync`) | Counts all callers together; fails the call instead of slowing or escalating |
| `MaxPendingPerRequester` | Caps one requester's queue | Queue size only |
| `AiMaxRejections` | Stops an AI reviewer after rejections | Per action, not per requester |
| Telemetry (`HostTelemetry`) | Action counts, pending, time to decision, execution time | Tagged by tool, module, status, mode, taint; nothing per identity, nothing about result size |

Nothing combines these into a view of one identity over time, nothing measures how much data a call returned, and nothing ever
moves an identity to "slowed" or "denied because it looks compromised".

## The idea

**Measure what each request touched, total it per identity over time, and tighten in steps: slow down first, then ask a person,
and only deny when the evidence is strong. Only a person loosens.**

### The ladder

| Level | Entered when, for example | Effect | Left when |
|---|---|---|---|
| **Normal** | Default | Policy as configured | — |
| **Throttled** | Volume above the identity's soft budget | [Incremental backoff](#incremental-backoff): calls are slowed, larger pages refused with a retry time; nothing is denied | Volume falls back under budget (automatic: backoff is not a judgement) |
| **Elevated** | Sustained throttling, a reviewer rejection, taint followed by high-impact attempts, a burst across many tools | Everything above Read is queued; reads above a size are queued | A person clears it |
| **Suspended** | A [tripwire](#tripwires) fires, or Elevated keeps climbing | Every call denied, pending actions frozen, operator notified | A person clears it, usually after rotating the key |

Throttled is the only level the Host leaves on its own, because it is about rate, not trust. Elevated and Suspended are judgements
and follow the Host's existing rule: automatic changes only tighten; loosening is a human act, audited.

### Incremental backoff

The first step is slowing, not refusing. It does two jobs at once:

- **Protects the system.** A flood is paced instead of hogging workers and downstream quotas.
- **Tames data throughput without killing it.** A legitimate agent that hit a busy patch still finishes, a little later. An
  attacker who needed ten minutes to copy a table now needs days, which is long enough for the volume windows, the daily report
  or a person to notice. Fast attacks become slow ones, and slow ones are what the other levels catch.

How it works, per identity:

1. **Cost per call.** Each completed call costs units: one for the call, plus its records and bytes returned
   ([result stats](#result-stats)). A read that returned 5,000 rows costs far more than one that returned 5.
2. **A budget that refills.** A token bucket per identity: a burst allowance and a steady refill rate, taken from the identity's
   [baseline](#baselines) once there is one, from the profile's configured default until then.
3. **Over budget, the delay grows.** The first calls over budget wait a little before running (say 250 ms), and each further
   window over budget doubles it, up to a cap (say 30 s). Quiet time halves it back. This is the shape TCP and well-behaved
   clients use: back off fast, recover gradually.
4. **Beyond the cap, refuse with a retry time** instead of holding the call open: a retryable error (`slow_down`, with
   `retryAfter`), the MCP equivalent of HTTP 429. Holding thousands of delayed calls open would itself hog the system.
5. **Optionally, smaller pages.** Where a module declares a page-size argument, the Host may clamp it while Throttled, so each
   call returns less. Modules opt in; the Host never rewrites arguments it does not understand.

Backoff is never shown to the model as a security signal: it sees only "slow down, retry after N seconds", the same message a busy
API gives.

### Two kinds of limit, kept apart

| | Capacity limit | Security limit |
|---|---|---|
| Protects | The system and downstream quotas | The data |
| Scope | All callers, per tool or module | Per identity |
| Response | Backoff only | The ladder |
| Configured by | Operator, from capacity | Baselines plus operator defaults |

Both use the same backoff machinery. Keeping them apart means a busy afternoon never marks an agent as suspicious, and a
suspicious agent is caught even on a quiet one.

### Tripwires

High-precision signals that justify Suspended directly:

- **A call outside the agent's profile.** A healthy agent never sees those tools (they are not advertised), so it never names
  them. An agent that calls one was told about it by someone. This is why profiles come first.
- **Decoy records (honeytokens).** A fake customer, invoice or file that no real task needs, registered with the Host. A read of
  one moves the identity to Elevated; an attempt to send one out (its marker found in an outbound tool's arguments) suspends it.
  Cheap to plant, almost no false positives.
- **Repeated attempts at denied actions** within a short window.

### Softer signals, which raise the level but never suspend alone

- Reviewer rejections of the identity's actions.
- Volume against baseline: records, bytes, distinct entities read, per hour, day and week.
- Breadth: many tools or modules touched in a short time (enumeration).
- Taint followed by attempts at high-impact tools.
- Time of day against baseline.

### Baselines

Agents suit this far better than people. A person's work varies by day and task; an invoice assistant does nearly the same thing
every day. Tight baselines mean anomalies stand out and false alarms are rarer, which is a real advantage of the Host over
behaviour analytics built for people.

A baseline is per identity (or per profile, when several keys share one): typical calls, records and distinct entities per hour
and per day, by tool. Learned from the [daily rollups](#rollups-that-outlive-retention), shown to the operator, and adjustable.
Until an identity has two weeks of history, the profile's configured defaults apply.

## Groundwork: get the metrics right first

Everything above depends on data the Host does not record today. This part is worth building early, even with no enforcement,
because baselines need weeks of history and the history must exist before the feature does.

### Result stats

A new, optional part of a tool's result, reported by the module (an additive contract change, like `AuditResult`):

```csharp
public sealed record ResultStats(int? Records = null, int? Recipients = null, IReadOnlyList<string>? EntityKeys = null);
```

- **Records:** rows, messages, files or pages returned.
- **Recipients:** for outbound tools, how many addresses.
- **EntityKeys:** stable ids of the business records touched (customer id, invoice number), so "distinct customers read this week"
  can be counted. The Host never stores them as given: it keeps a salted hash per deployment, and only to count distinct values.
- **Bytes** need no module help: the Host measures the result it already stores (`ActionRecord.ResultJson`).

Modules that do not report stats still get call counts and bytes. The Sql, Files, Csv, inbox and OpenAPI modules should report
records first: they are where bulk reads happen.

### On every action record

`ActionRecord` gains `ResultBytes` and `ResultStats` (without entity keys; their hashes go to the counters). With the requester,
session, tool, risk, taint and duration it already has, that is everything needed to replay a day and ask "what would the ladder
have done?".

### Per-identity counters

Exact, in the Host's own store, not in telemetry: sliding windows (hour, day, week) per identity of calls, cost units, records,
bytes and distinct entity hashes. These are what enforcement will read, so they must be transactional with the action, like the
existing `Admissions`.

### Rollups that outlive retention

`RetentionDays` deletes finished actions, and baselines need longer. A daily rollup per identity and tool (counts, records, bytes,
distinct entities, rejections, level changes, no argument values or results) is kept like the audit trail. This is the one piece
that cannot be added later without losing history: **build it with the stats.**

### Telemetry

`HostTelemetry` gains counters for result records and bytes, tagged by tool, module and profile, and a counter of level changes
by level and reason. Never tagged by identity: per-identity series would explode in number and belong in the Host's store, not in
the metrics backend.

### Audit

New audit events: `level.changed` (identity, from, to, reason), `tripwire` (which one), `backoff` (once per episode, not per
call), `level.cleared` (by whom). No argument values, as for all audit entries.

### Report-only mode

Before anything enforces, the Host evaluates the ladder and writes what it **would** have done, with the reason, to the audit
trail and a daily report. Operators tune budgets on that report; enforcement is switched on per level once the report is quiet.
The Host already works this way for skills (shadow mode); this is the same idea for limits.

## Rules that make it hold

1. **Only a person loosens** Elevated or Suspended. Throttled recovers by itself.
2. **The model never sees its level or its risk score,** only "slow down, retry after N". Anything it can see, an injection can
   steer around. (The same reasoning that gives taint no opt-out.)
3. **Every change explains itself:** in `--explain --caller`, on the review page next to a queued action ("queued because this
   agent is Elevated: 3 rejections today"), and in the audit trail. An unexplained block gets switched off.
4. **Suspension freezes, it does not delete.** Pending actions stay for a person to look at.
5. **Counters fail closed for enforcement, open for metrics.** If the store cannot be read, an enforcing Host treats the identity
   as Throttled; report-only mode just records the gap.

## Order

1. **Groundwork, early:** `ResultStats` in the module contract, result bytes and stats on `ActionRecord`, the daily rollups, the
   telemetry counters. No enforcement. Sql, Files, Csv, inbox and OpenAPI modules report records.
2. **After profiles:** the out-of-profile tripwire (Suspended) and reviewer rejections raising the level, enforcing, since both are
   high precision.
3. **Capacity backoff:** per tool or module, all callers. Useful on its own and exercises the backoff machinery.
4. **Per-identity budgets and backoff in report-only mode,** with the daily report; then enforcing Throttled.
5. **Elevated from soft signals,** report-only first.
6. **Decoy records** and **learned baselines.**

## Decisions

Decided by the owner on 2026-10-18.

- **Delay or refuse first:** a short server-side delay up to a cap, then refuse with `retryAfter`.
- **Clearing Suspended:** the key must be rotated first; a suspended agent comes back with a fresh key.
- **Entity keys:** stored as salted hashes, never as the business id.
- **Retention of rollups:** daily summaries are kept for 13 months, long enough to see yearly patterns.

Still open:

- Is any delay acceptable for interactive agents (a person waiting in Teams)? The cap should be small for them.
- **Who is notified** on Elevated and Suspended, and through which channel (the existing notifications, Teams)?
- **Shared profiles:** baselines per key or per profile when several keys share one?
