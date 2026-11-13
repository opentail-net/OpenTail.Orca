# Design: failures, retries and inspection

**Status:** proposed 2026-10-18. Steps 1 to 4 built 2026-10-18 (step 4 in part). Written from reading [`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca)
at `bef5add` and from discussion with the owner on 2026-10-18. The owner's positions are under [decisions](#decisions); the rest is
open for discussion.

**Where it fits:** alongside [work items and continuations](work-and-continuations.md) (work plan section 17), which only says that
an item can be `failed` or `dead` and that a dead item is "for a person to look at". This design says what kinds of failure there
are, how far each is retried, where it goes when retries run out, who looks at it, what they may do, and how work gets back on
track. Its first step concerns actions only and needs nothing from work items.

## The problem

The happy path is rarely real life. Calls time out, disks fill, credentials expire, a new integration profile has never worked,
an agent dies halfway through. Today:

- **An action's error carries one flag, `Retryable`.** Module timeouts and process deaths are retryable (`tool_unavailable`,
  `interrupted`); almost everything else lands in a catch-all `tool_exception`, not retryable. A downstream 401, a full disk, a bad
  request and a module bug all look the same to Orca, to the model and to an operator.
- **Only a reviewer can retry** a failed action, with one exception: after a crash, a tool that declares `IdempotentByKey` is
  retried with the same key. A non-idempotent external effect is never silently re-run. That rule stays.
- **Work items** add more ways to fail (a lost lease, an agent that gives up, a budget used up, a wait that never comes back) and
  the design caps them only with "dead after N failed claims".

Two dangers sit on either side. Retrying without limit, at several layers at once, hammers a broken system and multiplies effects.
Escalating to something that "makes it work by any means" turns a failure into a security incident: a missing credential is not a
reason to grant one.

## What Orca is, and is not

The line from [work items](work-and-continuations.md#what-the-host-is-and-is-not) holds here too. Orca classifies failures,
counts attempts, collects evidence and keeps the queue, all with plain rules. It never calls a model to diagnose a failure. An
inspector that is an agent is just another agent claiming work items, under its own profile.

## The idea

### 1. Every failure has a kind

The module knows what went wrong (it saw the HTTP status, the exception, the protocol error), so the module says so. Orca
applies policy to the kind.

| Kind | Examples | Automatic handling | Where it goes when that is not enough |
|---|---|---|---|
| `transient` | timeout, 429, 502/503/504, module restart | Retry with backoff and jitter, capped, only if the tool is idempotent by key | Dead item after the cap |
| `environment` | disk full, quota used up, storage or database unreachable | No retry; the route is paused ([below](#3-pause-the-route-not-every-item)) | Ops ticket |
| `contract` | 400, 404, 409, 422, schema mismatch, a profile that has never worked | No retry | The requester or the profile's author |
| `auth` | 401, 403, expired or revoked credential, failed token refresh | At most one token refresh, then stop | [Credential assessment](#5-credential-failures-are-a-security-question-first) |
| `policy` | Orca denial, a profile refusal, a guardrail or flow rule | Never retried | Not an operational failure; repeated attempts are already a signal under [adaptive limits](adaptive-limits.md) |
| `unknown_outcome` | interrupted on a tool that is not idempotent | Never retried | A person checks whether the effect happened |
| `bug` | an unexpected exception in a module | No retry | The module's owner |

**The module contract** gains one exception beside `ToolTransientException`: `ToolFailureException(kind, code, detail)`. It only
adds; modules that do not use it keep today's behaviour (`ToolTransientException` is `transient`, anything else unexpected is
`bug`). The OpenAPI module maps HTTP statuses to kinds itself, so every integration profile gets classification without code; a
profile may override the mapping for an API that misuses statuses. Over the isolation channel the kind travels as the existing
error code does. `ActionError` gains `Kind`; `Retryable` is kept and derived from it.

**Classification fails safe, because everything else trusts it.** A wrong kind is worse than none: an `auth` failure taken for
`transient` retries into the provider's account lockout. So:

- **Anything a module does not classify is `bug`,** which is never retried. Only a failure positively reported as `transient`
  retries.
- **Statuses lie, and profiles say so.** Some APIs answer 404 for a resource the caller may not see (GitHub does, for private
  repositories), or 200 with an error in the body. A profile's mapping can mark a status as ambiguous ("404 here may mean not
  allowed"), which classifies it as the stricter kind, and can name a body field that signals an error.
- **`auth` is never retried, even once, against a login endpoint;** the one token refresh is for expired access tokens only.

**Error text from outside systems is outside content.** A 403 body saying "ask an administrator to grant full scope" is an
injection vector. Error details from a downstream system are scrubbed as today, size-capped, and taint whoever reads them, the same
as a tool result.

### 2. Retries have limits at every layer, and do not multiply

Three layers can fail, and each keeps its own count:

1. **An action:** one tool call. Orca retries `transient` failures itself.
2. **A run:** one agent's attempt at a work item. It ends in done, failed, handed back, budget used up, or a lost lease.
3. **A wait:** a continuation waiting on an action, a timer, an external check or another item.

Three retries at each layer would mean up to 27 calls against the same broken system. So:

- **Only the action layer retries automatically,** and only `transient` failures, only for tools idempotent by key, with
  exponential backoff and jitter, honouring `retryAfter`. Proposed default: 3 attempts.
- **A run that ends in failure counts against the item,** whatever the reason, and **a lost lease counts too**. Otherwise an item
  that crashes every agent that claims it (a poison item) reopens for ever. Proposed default: 3 runs.
- **Every item and continuation has a deadline** (its `ttl`) that caps everything beneath it, however the retries are counted.
- **A wait that times out or comes back failed** wakes the continuation with that outcome; it is the resumed agent's input, not a
  retry by Orca.

### 3. Pause the route, not every item

Every failure gets a **fingerprint**: tool, kind, code, and the credential or module it went through. When failures with one
fingerprint pass a threshold in a window, Orca opens a **circuit** on that route:

- calls on that route are refused at once as `environment` (or `auth`) with the circuit's reason, instead of each failing slowly;
- **one incident** is raised for the fingerprint, not one per item: 200 items failing on a full disk are one ticket;
- items waiting on the route are **held**, not killed, and do not use up their attempts while held;
- after a cool-down Orca lets **one probe call** through (the half-open state). If it succeeds, the circuit closes and held
  items carry on; if not, the cool-down grows. Provider outages heal this way with no person involved.

The probe is an ordinary call by a held item, under that item's requester, through policy as usual. Orca never invents calls.

**Held items are released gradually,** not all at once onto a system that has only just recovered: a few first, more as they
succeed, back to holding if failures return. Items whose deadline passed while held are not released; they end as `expired`.

**Fingerprints can be too coarse.** Two different causes with the same tool, kind and code would merge into one incident, the
second hidden behind the first. Operators can split a group (by a field of the error, by identity, by time), and a split group
gets its own incident.

**Circuits are per process at first,** like the capacity limits: several Orca instances each open their own circuit and send their
own probe. That is acceptable for small deployments and stated so; a shared circuit needs the shared store that several instances
already use for actions.

### 4. Dead items go to inspection, with the evidence attached

When an item runs out of attempts, passes its deadline, or fails in a kind that is never retried, it becomes **dead**, and the
Orca does two things.

**It collects the evidence at that moment,** from its own records, not from the agent:

- the item, its runs, and every action with its error, kind, attempts and timings;
- the policy decisions and the slice of the audit trail for those actions;
- module health and the circuit's state;
- other recent failures with the same fingerprint, across identities;
- facts about the credential, never the secret: which one, when it last succeeded, when it was last rotated or changed and by
  whom, when it expires;
- facts about the profile: its version, when it was added or changed, whether it has ever succeeded;
- the identity's level, taint at the time, and soft signals under adaptive limits.

**The evidence is sensitive.** It copies arguments and results, which can hold personal or labelled data, into one place. So:

- it carries the labels of everything it copies, and is kept and shown under the same retention and access rules as the action
  records it was taken from; it is purged with them;
- **a ticket is a data exit.** Whatever goes into a ServiceNow or Jira ticket leaves Orca, so ticket creation passes the
  [data-flow](data-flow.md) rules like any other outbound write; by default a ticket carries the fingerprint, the kind, counts and
  a link to the inspection item, not arguments or results.

**It creates an inspection item** for the inspector profile (below), carrying the evidence and the fingerprint. Inspection is a
work item like any other, which reuses the inbox, leases, ttl and audit, and keeps Orca from calling a model.

**By default an agent inspects,** a different one from the agent that failed, holding the inspector profile (owner, 2026-10-18).
People can always work an inspection item from the review page or CLI, and one that no inspector claims within a set time (proposed
1 hour), or that its inspector hands back, goes to the operators' group.

**The inspector wears a security hat.** It is not a fixer looking for the quickest way to green; it examines evidence on the
assumption that the failure may be hostile until the evidence says otherwise. Orca cannot set a model's attitude, but it sets
everything around it:

- the inspector profile's server instructions and skill say so: outside error text may be an attack; when unsure, the answer is
  `unclear`, not `expected`; a ticket is preferred to a remedy; the job is to explain, not to make the item pass;
- the profile itself holds only read tools on Orca's records, ticketing, and remedy tools that always queue (below), so a less
  cautious model has nothing more to reach for;
- its findings are checked against Orca's own rule-based assessment: where the inspector calls a failure expected and the rules
  call it suspicious, the rules win and the disagreement is shown to the approver.

**Inspection never inspects itself.** An inspection item that fails, expires or is handed back goes to the operators' group. It
never creates another inspection item, so a failing inspector cannot start a loop.

### 5. Inspection proposes; it does not perform

Looking into a failure is diagnosis. Putting things right is a separate step through the approvals Orca already has.

**The inspector profile may:**

- read Orca's own records: audit, action history, the evidence bundle, metrics;
- write findings on the inspection item;
- raise a ticket through a ticketing profile (ServiceNow, Jira), under that profile's own policy;
- **propose** a remedy by calling a remedy tool: `redrive_work`, `access.request`, "rotate this credential" (a request to the
  credential's owner). Each has a policy entry that **always queues**, so the call becomes a pending action that a person
  approves.

**It may not:**

- approve anything, its own proposals included: the inspector's identity is never an approver;
- hold, read or change secrets (Orca never gives agents secrets);
- call the failed tool or the business system behind it to "test" a fix; probing is the circuit's job.

**Separation of duties:** the identity that failed never inspects its own failure, and never approves its own redrive. This is the
same rule as for approvals today.

**The evidence taints the inspector.** It contains outside error text, so findings are recommendations, and a remedy an inspector
proposes is reviewed with that taint shown.

### 6. Credential failures are a security question first

An `auth` failure is never given an automatic green light, and never an automatic red one. The inspection item carries an
**assessment**, made by Orca rules from facts it already has, with the evidence for each:

**Points to expected:**

- the credential or profile is new and has never succeeded (a new, untested contract);
- its known expiry date has passed;
- an administrator changed or rotated it recently (audit shows who);
- every identity using that endpoint is failing (likely the provider).

**Points to suspicious:**

- a credential that worked recently is now refused for one specific resource or operation;
- the session was tainted before the call;
- the arguments reached outside this identity's usual pattern (the baselines in adaptive limits);
- several credentials or scopes were tried in a short time;
- a decoy record was read;
- the refusal followed an attempt to widen access.

The result is **expected**, **unclear** or **suspicious**. Expected comes with a recommendation (rotate, consent again, contact the
provider). Suspicious feeds the identity-level ladder (report-only, as the owner decided for soft signals) and notifies the security
group; nothing on that thread is redriven until security clears it.

**No working around a refusal.** After an `auth` failure, the agent that failed may not open an access request on its own for the
same resource; that is exactly "make it work by any means". The inspector may propose one, which queues for a person.

## Getting back on track

Most failures never reach inspection: transient retries succeed, and paused routes reopen on their own after a probe. For the rest:

| Kind | Who fixes the cause | How the work resumes |
|---|---|---|
| `environment` | Ops, through the ticket | The inspection item waits on the ticket ([external wait](work-and-continuations.md#external-waits)). When the ticket closes, Orca proposes a redrive of every item with that fingerprint. |
| `contract` | The requester, or the profile's author | A profile fix is an audited change, then redrive is proposed; or the requester submits a corrected item. |
| `auth`, expected | The credential's owner rotates it or consents again | Orca sees the credential change in the audit trail and proposes a redrive. |
| `auth`, suspicious | Security decides first | Redrive only after security clears it, with two approvals. |
| `unknown_outcome` | A person checks the target system | They mark it happened (the item completes) or did not (redrive). |
| `bug` | The module's owner ships a fix | Redrive after the deploy. |

**Approval effort follows risk.**

- **Automatic** (owner, 2026-10-18, on by default, can be turned off): an `environment` redrive runs without approval when the
  ticket for its fingerprint is closed **and** the circuit's probe has succeeded. The closed ticket says a person believes it is
  fixed; the probe shows it is. Items flagged by the inspector or by the rules as anything other than plain `environment` are left
  out and wait for a person.
- **One approval:** `transient` and `bug` redrives, `contract` after a profile fix, and `environment` when the ticket closed but the
  probe failed or there is no probe to make.
- **Two approvals:** `auth`.
- **A person's check:** `unknown_outcome`, always.

An automatic redrive is audited as `redrive.automatic` with the ticket and the probe that justified it.

**The ticket's closure carries a steer: retry (the default) or do not retry** (owner, 2026-10-18). Whoever closes the ticket knows
whether the waiting work still makes sense; a disk being cleared does not mean last Tuesday's export is still wanted.

- **How it is read:** the ticketing profile maps the steer from the ticket system: a resolution or close code, or a field or tag
  where the system has no suitable code. A closure with no steer means retry.
- **Do not retry:** the items for that fingerprint are not redriven. They are abandoned with the closer's note as the reason, their
  requesters and anything waiting on them are told, and an operator can still redrive any of them by hand.
- **The steer can only hold back, never widen.** It switches off an automatic redrive that the rules above would allow; it cannot
  make one happen for `auth`, `unknown_outcome` or anything else outside them. Ticket text is outside content.
- **Only a person's closure counts.** If the ticket was closed through OpenTail by an agent (Orca sees its own calls to the
  ticketing tools), there is no automatic redrive; the items wait for one approval. Orca cannot see a closure made by the
  ticket system's own automation, which looks like a person's. The risk is accepted: the steer can only hold back, and the only
  automatic redrive is `environment`, released gradually and checked again.

**Giving up is a valid end.** If the work is no longer wanted, the item is cancelled and its requester, and anything waiting on it,
are told why. What is not allowed is failing quietly or waiting for ever.

### What a redrive does

**A failed action:** Orca runs the stored call again: same tool, arguments and requester, as `Retry` does today. No agent and
no model take part. Policy, profile, level and grants are checked again for the requester at that moment, so a key suspended in the
meantime stops it.

**A work item:** it goes back to the inbox. Its `for` is unchanged unless the approver retargets it: an item for a profile can be
claimed by any agent holding that profile, the original one included; an item for one named identity goes back to that identity.
The new run starts with a fresh context and reads the thread note and the inspector's findings.

**Whoever approves the redrive may retarget it**, and the kind suggests the default:

| Failure | Default target | Why |
|---|---|---|
| `transient`, `environment`, `bug`, `auth` expected | Same `for` | The agent was not the problem |
| `contract` | The requester to correct it, or same `for` after a profile fix | The request or the profile was the problem |
| `auth` suspicious, repeated `policy` hits, or taint before the failure | Same profile, **excluding the identity that failed** | Do not hand the work back to a possibly compromised session |

- **Retargeting may narrow freely.** Pointing an item at a profile with more rights is privilege escalation and needs the same
  approval as delegating to that profile under `MayDelegateTo`.
- **On a suspicious redrive the thread note does not go along by default.** A possibly compromised agent wrote it, and handing it to
  a fresh agent could carry an injection over. The approver chooses: drop it, keep it, or attach only the inspector's findings
  (assembled by Orca from its own records).
- **Labels and taint travel with the item** ([rule 1](work-and-continuations.md#rules-that-make-it-hold)), whoever claims it.

### Partial work, and failure travelling up

- **Part done, then failed:** step 3 of 5 succeeded and step 4 failed. Undoing steps 1 to 3 is new work items, each through
  approval, not an automatic rollback. The thread note records which steps completed; idempotency keys make a redriven run skip
  what already happened.
- **A parent waits on a dead child:** the parent's continuation wakes with the outcome. While the child is under inspection, the
  parent wakes with `stalled` and the inspection item's id, so its agent can tell its requester; it wakes again when the child is
  redriven to completion or abandoned (proposed; see [open questions](#decisions)).
- **Expiry is a failure too:** an item that passes its `ttl` takes the same path, with kind `expired`.

## Rules that make it hold

1. **The module says what went wrong; Orca decides what to do.** Kinds come from the module; retry, pause and escalation are
   Orca policy.
2. **Only transient failures of idempotent tools retry automatically,** and only at the action layer.
3. **Every attempt counts,** lost leases included, and every item has a deadline above all counts.
4. **One incident per fingerprint,** not one per item.
5. **Inspection proposes, people approve.** No inspector identity approves, and the identity that failed neither inspects nor
   approves its own redrive. The one automatic redrive (`environment`, ticket closed by a person, probe succeeded) rests on a
   person's closing of the ticket and Orca's own probe, never on an agent's say-so.
6. **Nothing is retried to get past a refusal.** `auth` and `policy` failures are never retried automatically, and the failing agent
   cannot request access around them.
7. **Everything is checked again on redrive,** for whoever runs it.
8. **Outside error text is outside content:** scrubbed, capped, tainting.
9. **Unclassified means no retry,** and an ambiguous status takes the stricter kind.
10. **Evidence keeps its labels,** and a ticket is a data exit.
11. **Inspection ends with people,** never with another inspection.

## How it fits with the other designs

- **Work items and continuations:** `dead` gains a cause and a destination; lost leases count as attempts; inspection is a work
  item; redrive is `--requeue` with retargeting and approval.
- **Adaptive limits:** fingerprints and circuits use the same counters; a suspicious credential assessment is a soft signal; many
  failures of kind `policy` from one identity already are.
- **Temporary access:** `access.request` is a remedy an inspector may propose, never one a failing agent files for itself.
- **MCP Tasks:** a failed action is `completed` with `isError: true` and its kind in the result; `failed` stays for Orca's own
  errors.
- **Roles and permissions:** the inspector is a profile like any other, with read tools on Orca's records and the remedy tools,
  all of which queue.

## Metrics to record

Failures by kind, code, tool and profile; automatic retries and their outcome; runs per item and lost leases; circuits opened,
probes, time a circuit stayed open, items held; items dead by cause; inspection items opened and how they ended (redriven,
retargeted, abandoned); time from dead to redrive; credential assessments by result. Audit events: `action.failed` (with kind),
`circuit.opened`, `circuit.closed`, `work.dead`, `inspection.opened`, `inspection.finding`, `redrive.proposed`, `redrive.automatic`,
`redrive.approved`. Error text and evidence never go into telemetry.

## Order

1. **Failure kinds on actions:** `ToolFailureException`, `ActionError.Kind`, the OpenAPI status mapping, kinds in notices and
   `get_action_status`. Useful at once and independent of work items. *Built 2026-10-18: `ToolFailureKind` and `ToolFailureException` in
   the module contract (over the isolation channel too); `ActionError.Kind`, with `EffectiveKind` inferring one from the code for
   older records; unclassified exceptions are `Bug`, an `ArgumentException` while running is `Contract`; the OpenAPI module maps
   statuses (`Errors.Kinds` overrides per status) with code `http_<status>`; the kind appears in action status, notices and the
   review page. Provisional: `Retryable` keeps its meaning (a reviewer may retry) and is true for transient, environment, auth and
   unknown-outcome failures, false for contract, policy and bug. A profile field naming an error in a 200 body is not built.*
2. **Retry policy per kind, fingerprints and circuits,** with held items and the probe. *Built 2026-10-18: `Failures` in the
   policy; automatic retries for transient failures of tools idempotent by key, scheduled with `ActionRecord.RetryAt` and run by the
   maintenance sweep (3 retries, 5 s doubling with jitter up to 5 min, `Retry-After` honoured); circuits per tool for transient,
   environment and auth failures (5 in 5 min), one audit entry per episode with the fingerprint, held calls (`circuit_open`, not
   counted as attempts), one probe after 1 min doubling to 30 min, release of 5 held calls per sweep. Provisional: circuits are
   report only by default, like the other new checks; switch `CircuitMode` to `Enforce` once the entries look right. The route is
   the tool (not yet the credential), circuits are per process, and the operator cannot yet split a fingerprint group: work items
   are where held items will wait in step 3.*
3. **Dead items, the evidence bundle, inspection items and redrive with retargeting,** built with work items (section 17, step 2).
   *Built 2026-10-18: dead or expired items open an inspection work item (`WorkKind.Inspection`) carrying the evidence as inputs,
   tainted (error text is outside content), for `Work.InspectorProfile` with the failing identities excluded; unclaimed after 1 hour
   (provisional) or failed, it goes to the operators (`people:operators`) and never opens another inspection; parents wake with
   `stalled`; `work.redrive` (held to Queue, Human, SelfApproval Forbid at load) and `--redrive` with `--for`, `--exclude-failed`,
   `--drop-note`, attaching the findings. Not built: the evidence's credential facts (Orca does not see when a module's
   credential last succeeded or was rotated), the automatic redrive after a closed ticket and the steer (they need a ticketing
   integration), retargeting checks against `MayDelegateTo` (a person approves every redrive), and inspections of actions outside
   work items.*
4. **The inspector profile, credential assessment and remedy tools.** *Built 2026-10-18, in part: `CredentialAssessment` reads
   Orca's facts into expected, unclear or suspicious with the evidence for each (suspicion wins), carried in the inspection's
   evidence; suspicious results are audited (`credential.suspicious`) and raise a soft signal through identity levels, report only
   by default (the owner's open question; provisional answer: the same report-only default as the other soft signals); the remedy
   tools `work.redrive` and `work.request_rotation` are held to Queue, Human, SelfApproval Forbid at load; the inspection's goal
   carries the security-hat instructions. Not built: the inspector profile's own server instructions and skill (instructions are
   per server today), comparing the inspector's written verdict with the rules' (findings are free text), and facts only modules
   hold (when a credential last succeeded, was rotated, expires).*

**Build step 1 now and let its data decide the rest.** Failure kinds are cheap and useful today. The metrics they produce show what
actually fails, how often and in which groups, which is what circuits and inspection should be tuned on. The inspector agent comes
last: for most dead items the evidence bundle and Orca's rule-based assessment already tell the story, and the agent adds a
written account and a ticket draft. Build it when the evidence alone has proved not to be enough.

## Decisions

Decided by the owner on 2026-10-18:

- **Separation of duties:** the agent that failed does not inspect its own failure.
- **Recovery goes through approval:** the inspector may propose a remedy (redrive, access request, credential rotation), which
  queues for a person; it never carries one out itself.
- **Who inspects:** by default a different agent, with a cautious security hat on (the inspector profile); people as fallback.
- **Automatic redrive** after a closed environment ticket: yes, most likely. Proposed as on by default, only when the probe has also
  succeeded.
- **The ticket's closure steers it:** retry (the default) or do not retry.

Still open:

- **Unclaimed inspection:** hand it to the operators' group after 1 hour?
- **Caps:** 3 automatic action retries and 3 runs per item, under the item's ttl?
- **Suspicious credential assessments:** raise the identity's level automatically, or stay report-only like the other soft signals?
- **A parent waiting on a child under inspection:** wake it with `stalled`, as proposed, or keep it waiting until the child is
  redriven or abandoned?
