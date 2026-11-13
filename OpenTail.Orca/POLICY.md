# Writing a policy

The policy file is the only place that decides what Orca does with a call. It is written by the operator, read once at startup, and
never influenced by arguments, model output or tool results. A mistake anywhere in it stops Orca from starting, with a message that
names the tool, the rule and where in the condition the problem is.

Check a change before you ship it:

```
my-host --policy policy.json --explain files.write '{"path":"drafts/a.txt","content":"x"}'
my-host --replay-policy new-policy.json --db queue.db --since 7d
```

`--explain` shows how each rule evaluated for one call. `--replay-policy` re-evaluates the actions already in the database against the
new file and reports what would have been handled differently. `--test-policy cases.json` checks a list of expected decisions and exits 1
on a mismatch (put it in CI). `--lint` lists what will be denied, what runs without review, and risky combinations. None of them executes
anything; all accept `--modules modules.json`.

## The file

```json
{
  "DefaultReadMode": "Execute",
  "MaxPendingPerRequester": 10,
  "TaintWindow": "30m",
  "SessionIdleTtl": "14d",
  "RetentionDays": 90,
  "People": { "alice": ["admin:alice", "key:alices-agent"] },
  "Tools": {
    "files.write": { "Mode": "Queue", "Reviewer": "Human", "ExpireAfter": "24:00:00" }
  },
  "Agents": {
    "invoice-assistant": { "Tools": [ "mail.read", "erp.query" ] },
    "reply-assistant":   { "Tools": [ "mail.*" ] }
  }
}
```

| Setting | Meaning |
|---|---|
| `DefaultReadMode` | What a `Read`-risk tool with no entry does (`Execute`, `Queue`, `Deny`). Everything else with no entry is **denied**, and not even offered to the model. Default `Deny`. Modules enabled from `modules.json` do not follow it unless their entry sets `AllowDefaultRead` (the module labels its own tools as Read). |
| `MaxPendingPerRequester` | How many actions one caller may have waiting. The count is made in the transaction that stores the action, so it holds across processes. `0` = no cap. |
| `TaintWindow` | After a caller reads untrusted content (mail, web, files, API responses, database rows), **every** session of that caller counts as tainted for this long, so starting a new session does not help. A tainted caller never gets a high-impact tool run without a reviewer. Default `30m`. `0` = per session only. |
| `SessionIdleTtl` | Idle sessions are deleted after this. Default `14d`. |
| `RetentionDays` | Finished actions (they hold argument values) are deleted after this many days. The audit trail, which holds no argument values, is kept. `0` = keep. |
| `People` | Which sign-in subjects are the same human (used by `SelfApproval`). Subjects are namespaced: `admin:name` (reviewer token), `key:name` (API key), `jwt:issuer|sub` (JWT), `ai:name` (AI reviewer), `local` (stdio). |
| `Tools` | Per tool, see below. |
| `Agents` | Agent profiles: the tools each agent identity may call. See [agent profiles](#agent-profiles). |
| `DataFlow`, `DataFlowMode` | Where labelled data may go without review, per label; `ReportOnly` (default), `Enforce` or `Off`. See [data flow](#data-flow). |
| `Capacity` | Calls per minute across all callers, by tool or `module.*`: `{ "crm.*": { "PerMinute": 60, "Burst": 10, "MaxDelay": "2s" } }`. Over it a call waits for room up to `MaxDelay` (default 2 s), else fails as retryable `slow_down` with a retry time. Protects the system and downstream quotas; never a trust signal. |
| `Limits` | Adaptive limits: `{ "OutOfProfile": "ReportOnly", "Rejections": "ReportOnly", "Decoys": "ReportOnly" }`, each `Off`, `ReportOnly` (default) or `Enforce`. See [identity levels](#identity-levels). |
| `Decoys` | Markers of decoy records you planted (at least 8 characters each). See [identity levels](#identity-levels). |
| `Include` | Policy fragments, relative to this file: a directory (`"policy.d"`, every `*.json` in name order) or a file. A fragment holds only `Tools`, only for the module it is named after (`policy.d/github.json` holds `github.*`). One tool, one place. `--adopt-policy <module>` writes fragments; see [MODULES.md](MODULES.md#policy-for-a-module). |

**Durations** are written the way people write them: `"24:00:00"`, `"1.00:00:00"`, `"90s"`, `"30m"`, `"8h"`, `"2d"`.

### A tool entry

| Setting | Meaning |
|---|---|
| `Mode` | `Execute` (run now, audited), `Queue` (wait for a reviewer), `Deny`. |
| `Reviewer` | `Human` (a person decides), `Ai` (an AI decides within its limits; a person may refuse the action but not approve or retry it) or `Either` (an AI or a person may approve). An AI reviewer only ever sees actions handed to it, and can never satisfy a group or count towards several approvals. Its limits are under *AI reviewers* below. |
| `AiMaxRisk` | The highest risk an AI reviewer may decide for this tool: `Read`, `Write` (the default) or `ExternalEffect`. `Destructive` is refused: an AI never decides those. |
| `AiMaxRejections` | After this many AI rejections or change requests along one resubmission chain, the AI may not decide it again (default 2; 0 turns it off). See *AI reviewers* for who may decide then. |
| `AiBudget` | `{ "Per": "01:00:00", "MaxCount": n, "Scope": "Requester" }`: at most n AI approvals of this tool per requester (or `Global`) in the window; past it, the AI may not approve more in that window. |
| `AiReviewers` | `["bot", "second"]`: the AI reviewers, by name (the part after `ai:`), that may decide this tool. A reviewer not named may not decide it, and is never shown it. Left out: any AI reviewer may decide it. Needs Mode `Queue` and Reviewer `Ai` or `Either`. |
| `ExpireAfter` | A pending action that nobody decides in this time expires. It can no longer be approved, even before the sweep notices. |
| `ReviewerGroup` | Only reviewers carrying this group may decide it. |
| `RequiredApprovals` | 1-5 distinct approvers. An edit of the arguments voids approvals given before it; one eligible rejection ends the action. |
| `SelfApproval` | `Allow` (default: the person using the assistant is normally the right approver) or `Forbid` (four eyes, judged through `People`). |
| `DelayFor`, `Delay` | Execute with an undo window: the call is announced and runs after `DelayFor` (`"10m"`), or the default 10 minutes with `"Delay": true`, unless someone stops it. Only with `Mode: Execute` and tools above Read; taint and Queue rules still queue. See [undo window](#undo-window). |
| `AllowEdits` | `true` (default) lets a reviewer change the arguments when approving; `false` means approve or reject exactly what was asked (or request changes). Required `false` for administration tools. |
| `Labels` | Labels the operator adds to what this tool returns (`["finance"]`); a module's own labels stay. See [data flow](#data-flow). |
| `Administers` | `true` makes the tool an [administration tool](#administration-tools) although its module does not mark it. It only adds the mark; `false` never removes a module's. |
| `RateLimit` | `{ "PerHour": n, "PerDay": n }`, enforced at execution, so it binds approved actions too. |
| `TrustRamp` | `{ "AfterCleanApprovals": n }`: after n approvals with no edit and no failure the tool becomes eligible for promotion to `Execute`, by an operator, never automatically. With `"DelayFor": "5m"` a promoted tool runs after that undo window instead of at once. A rule or the taint override always beats a promotion. Not with several approvers. |
| `Rules` | Conditional refinements, below. |

## Rules

A rule refines a tool's policy for calls whose arguments, time or volume match a condition.

```json
"mail.send": {
  "Mode": "Queue",
  "Rules": [
    { "Name": "never-to-competitors", "Mode": "Deny",
      "When": { "arg": "/to", "any": { "domainIn": ["rival.example"] } } },

    { "Name": "internal-small", "Mode": "Execute", "IgnoreArgs": ["subject", "body"],
      "When": { "all": [
        { "arg": "/to", "all": { "domainIn": ["corp.example"] } },
        { "arg": "/bcc", "absentOrEmpty": true },
        { "arg": "/attachments", "countAtMost": 0 },
        { "time": { "days": ["Mon","Tue","Wed","Thu","Fri"], "from": "08:00", "to": "19:00", "zone": "Europe/Amsterdam" } } ] },
      "Budget": { "Per": "24h", "MaxCount": 50, "Scope": "Requester" } },

    { "Name": "money-talk", "Mode": "Queue", "ReviewerGroup": "finance", "RequiredApprovals": 2, "ExpireAfter": "4h",
      "When": { "arg": "/body", "matches": "invoice|payment", "ignoreCase": true } }
  ]
}
```

### What a rule can do

| Rule `Mode` | Effect | Applies when the condition is |
|---|---|---|
| `Deny` | Refuse the call. | true **or unknown** |
| `Queue` | Hold it for a reviewer; may set `Reviewer`, `ReviewerGroup`, `RequiredApprovals`, `ExpireAfter` for that queue. | true **or unknown** |
| `Execute` | Run it now. Only on a tool whose own `Mode` is `Queue`: a rule can relax a queue, it cannot create access. | **true** and every argument accounted for |

Among the rules that apply: any `Deny` wins, then any `Queue`, then `Execute`. A tool that is `Deny` stays denied. The caller being
tainted always turns an `Execute` of a high-impact tool back into `Queue`; there is no rule-level way around it.

**High-impact means `Destructive` or `ExternalEffect`.** Taint does not hold `Write` tools (they change only what the deployment owns and
can restore). An `Execute` rule on a `Write` tool runs it for a tainted caller too, and `--lint` warns when that tool's module returns
outside content. Modules mark anything other people can see (a comment, an issue, a message) `ExternalEffect` for this reason.

### Unknown is not false

Every test is **true**, **false** or **unknown**. A test is unknown when the argument is missing, has the wrong type, an address does not
parse, a regex times out, or anything throws. `not` of unknown is unknown, so a missing field can never turn a condition on. That is why
`Deny` and `Queue` rules also apply on unknown (when in doubt, be careful) and `Execute` rules need a definite true.

A consequence you will notice: a **misspelt path in a Deny rule denies every call** (the path is always missing, so the rule is always
unknown). That is deliberate: you find out on day one, not after an incident.

### Every argument must be accounted for

An `Execute` rule only applies if **every top-level argument in the call** is either looked at by the rule's condition or listed in
`IgnoreArgs`. Otherwise a model could add a field the rule never inspected (a `bcc`) and ride it. `--explain` says which argument was not
accounted for.

### Conditions

```
{ "all": [ ... ] }  { "any": [ ... ] }  { "not": { ... } }
{ "time": { "days": [...], "from": "HH:mm", "to": "HH:mm", "zone": "Europe/Amsterdam" } }
{ "arg": "<JSON Pointer>", <one test> }
{ "arg": "<JSON Pointer>", "all"|"any": { <one test> } }     over each item of an array (a single value counts as one item)
```

Tests (strings compare exactly; add `"ignoreCase": true` to ignore case):

| Test | True when |
|---|---|
| `equals` | the value equals a string, number, boolean or null |
| `in` | the value equals one of a list |
| `startsWith`, `endsWith`, `contains` | string checks |
| `matches` | the whole-value regex matches (linear-time engine, no backreferences or lookarounds, 50 ms limit) |
| `domainIn` | the value is a **bare** email address whose domain is in the list |
| `lt`, `lte`, `gt`, `gte` | numeric comparison (a number in quotes is not a number) |
| `exists` | the argument is present and not null (`true`/`false`) |
| `absentOrEmpty` | missing, null, `""`, `[]` or `{}` |
| `countAtMost`, `countAtLeast` | array length (a missing array counts as zero) |

`domainIn` is strict on purpose: exactly one `@`, no display names (`Bob <b@x>`), no lists, no quoted local parts, no trailing dot; the
domain is lower-cased and converted to punycode and compared **exactly** (`corp.example.evil.com` and `sub.corp.example` do not match
`corp.example`). Anything it will not parse is unknown.

**Paths:** `startsWith` on a path is a trap (`"drafts/../secret"` starts with `drafts/`). Prefer an anchored `matches`, such as
`^drafts/[A-Za-z0-9._-]+$`. The module's own root check is the last line of defence, not the rule.

### Budgets

`"Budget": { "Per": "24h", "MaxCount": 50, "Scope": "Requester" }` on an `Execute` rule: once the tool has been executed `MaxCount` times
within `Per` (by that requester, or by anyone with `"Scope": "Global"`), the rule stops matching and the call waits for a reviewer. The
count and the insert are one transaction, so concurrent callers (and several Orca processes on one database) cannot overshoot. Only
executions count (`Succeeded` or `Executing`), including ones a human approved.

### Reviewer edits

When a reviewer edits arguments, the rules run again on the edited arguments. An edit that now matches a `Deny` rule is refused. With
`AllowEdits: false` (and always for administration tools) no edit is accepted: the reviewer approves, rejects or sends it back.

## Agent profiles

A profile names the tools one agent may call: exact names, `module.*` (every tool whose name starts with `module.`) or `*`. An API key
names its profile in the HTTP config (`{ "Name": "invoice-bot", "Hash": "sha256:...", "Profile": "invoice-assistant" }`), so the
profile comes from the credential and the agent can never choose or change it; a prompt injection would need a different key.

* **Profiles only narrow.** A tool in the profile is still `Execute`, `Queue` or `Deny` by its entry, rules, taint and trust, as
  without profiles. A tool outside it is not in that caller's `tools/list`; a call to it by name is refused (`not_in_profile`)
  and audited.
* **Fail closed.** The server refuses to start when a key names a profile `Agents` does not define, and a profile naming an unknown
  tool or module is a policy error.
* **Keys without a profile** keep every tool policy allows, and the server warns at startup for each one. A later release will refuse
  them: give every key a profile.
* **Check one:** `--explain <tool> '<args>' --profile <name>` says whether the profile admits the tool and, if so, what policy decides.
* Profiles apply to API keys. Signed-in JWT callers and the local stdio operator have none yet; app roles mapped to profiles come
  with the first Entra ID customer. The design is in [roles and permissions](../docs/design/roles-and-permissions.md).
* **See and change them on the review page:** *Admin → Agents* lists every profile with each tool's mode; *Propose a change*
  makes a policy proposal that is checked, replayed against stored actions and approved by a second admin before you deploy it
  ([SOP C1](SOP.md#c1-changing-the-policy)).

### Temporary access

A profile can list tools its agent may **ask for** for one task: `"reader": { "Tools": [ ... ], "Requestable": [ "crm.update" ] }`.
The agent then has `access.request` (tool, reason, minutes, calls). It is decided by people like any queued action, so give it an
entry: `"access.request": { "Mode": "Queue", "Reviewer": "Human", "ReviewerGroup": "approvers", "SelfApproval": "Forbid", "AllowEdits":
false }` (anything looser stops startup). Approving grants the tool to that identity for the minutes or the calls asked for (up to 8 h
and 50 calls), whichever ends first; it appears in that caller's `tools/list` until then. The granted tool's own policy still decides
each call, and every call under a grant is audited (`granted_call`). Administration tools can never be requested. `--grants` lists
grants in force; `--revoke-grant <id> --by <name>` ends one early.

### Administration tools

Some tools change how the agents behave instead of doing the business's work: `skills.publish` is one (its module marks it). Orca
holds them to fixed rules, whatever the configuration says:

1. **Only an exact name grants one.** `*` and `skills.*` never include `skills.publish`; a profile must name it. A signed-in caller
   without a profile never reaches one; only the local stdio operator does.
2. **Always a proposal that people decide.** If its entry is not `Deny`, it must be `Mode: Queue`, `Reviewer: Human`, with a
   `ReviewerGroup`, `SelfApproval: Forbid` and `AllowEdits: false`, with no `Execute` rule and no trust ramp. A trust promotion never
   applies to it. Otherwise Orca refuses to start, naming the tool.
3. **Never next to business tools with effects.** A profile naming an administration tool and any other tool above `Read` stops
   startup, except on a sandbox or shadow Orca. The workbench proposes skills; it does not send mail.
4. **Listed at startup:** every profile holding one is named on stderr.

The `skills` module's suggested policy (`--adopt-policy skills`) meets these rules, with the reviewer group `skill-approvers`: one
approval to move the `staging` label, two different people for `production`.

## Failures: retries and paused routes

Every failed action has a kind (transient, environment, contract, auth, policy, unknown outcome, bug), from the module when it knows
and `bug` when it does not. What Orca does about it:

```json
"Failures": { "AutoRetries": 3, "RetryBase": "5s", "RetryMax": "5m",
              "CircuitMode": "ReportOnly", "CircuitThreshold": 5, "CircuitWindow": "5m", "CircuitCooldown": "1m",
              "CircuitMaxCooldown": "30m", "ReleasePerSweep": 5 }
```

* **Automatic retries** only for a **transient** failure of a tool **idempotent by key**, at most `AutoRetries` times, waiting
  `RetryBase` doubled each time with jitter (up to `RetryMax`, never less than the tool's `Retry-After`). Anything else waits for a
  person (a reviewer can retry transient, environment, auth and unknown-outcome failures). A non-idempotent effect is never re-run
  by Orca.
* **Paused routes:** `CircuitThreshold` failures of one kind (transient, environment or auth) on one tool within `CircuitWindow`
  pause that tool. One audit entry names the fingerprint (tool, kind, code), not one per call. Report only by default
  (`circuit.would_open`). With `Enforce`, calls on a paused tool are held without reaching it (`circuit_open`); after
  `CircuitCooldown` one call is let through as a probe; if it succeeds the tool reopens and held calls run, `ReleasePerSweep` per
  maintenance sweep; if it fails, the pause doubles (up to `CircuitMaxCooldown`). Held calls never reached the tool, so running them
  later is safe, and it does not count as an attempt. Circuits are per Orca process.

## Work items and continuations

Orca can keep a ledger of **work owed** (an inbox of work items for agents) and **work waited on** (an agent's place, parked
while it waits). It never calls a model, builds prompts or keeps transcripts: it keeps records, leases and outcomes, and a note the
agent writes for itself. See [the design](../docs/design/work-and-continuations.md).

```json
"Work": { "Enabled": true, "Lease": "10m", "MaxRuns": 3, "DefaultTtl": "14d", "NoteMaxBytes": 8192, "ThreadNoteMaxBytes": 16384,
          "MaxDelegationDepth": 3 }
```

* `Enabled` (default false) offers the work tools to callers: `park`, `next_work`, `get_work`, `complete_work`, `fail_work`,
  `hand_back_work`, `update_thread_note`. They are Orca's own bookkeeping, like `get_updates`, not business tools.
* **Park:** an agent waiting on its actions, on work it delegated or on a time saves a note. When the waits are met, or the time is
  up, it gets a resume item (through `next_work`, listed by `get_updates`) carrying the note and what each wait came to: statuses and
  reviewers' reasons, never results (it reads a result with `get_action_status`, which taints as usual).
* **Waiting does not clean a session.** The session's taint and labels are recorded when it parks, or creates or delegates work, and
  whoever reads the resume item, the delegated item or the thread note takes them on.
* **Leases and runs:** claiming takes a lease (`Lease`, renewed by working on the item). A run that fails, or a lease that runs out,
  counts against the item; after `MaxRuns` it is dead, for a person to look at. Handing an item back does not count.
* **Every item ends:** `DefaultTtl` on items and continuations. `--work`, `--work-item <id>`, `--requeue <id> --by <name>` and
  `--cancel-work <id> --by <name>` are the operator's view.

**Waking an agent:** work that becomes ready reaches an agent in one of three ways, and Orca is neutral between them:

* **Pull:** the agent calls `next_work` (any MCP client).
* **A signed URL:** a notification route with `"On": ["work_ready"]` and optional `"Work": [ "profile:clerk" ]` sends the agent
  platform's webhook the work id, who it is for and its kind, never the goal or a note, so a leaked URL leaks nothing. The platform
  starts its agent, which pulls.
* **Host-started:** `"Work": { "Runners": [ { "For": "profile:clerk", "Command": "agent-run", "Arguments": [ "--work", "{workId}" ],
  "MaxConcurrent": 1 } ] }` starts the command (no shell; the id also in `OPENTAIL_WORK_ID`) when work is ready for that profile, up
  to its limit; work that arrives while it is busy waits in the inbox. Off unless listed. It starts someone else's harness; Orca
  does not become one.

**Inspection:** an item that dies (too many failed runs) or expires before it is done goes to inspection: Orca collects the
evidence from its own records (the item and its runs, the actions its claimants made while it was open with their failure kinds and
fingerprints, the audit entries naming it, the claimants' identity levels; never arguments or results) and opens an inspection item
for the `Work.InspectorProfile` profile (default `inspector`; without one, for the operators). The identities that failed it may not
claim the inspection. An inspection nobody claims within `Work.InspectorFallback` (default 1 hour), or that fails, goes to the
operators; it never opens another inspection. A parent waiting on the dead item wakes with `stalled` and the inspection's id.

**Credential failures** in the evidence carry Orca's own assessment, from facts it holds: *expected* (the tool never worked here;
the policy changed in the last week; other identities fail the same way), *suspicious* (it worked for this identity in the last week;
the session had read outside content; the identity is not Normal; several tools refused it in a day; it touched a decoy; it had just
asked for more access) or *unclear*. Suspicion wins over any expected reason, is audited as `credential.suspicious`, and is a soft
signal for identity levels (report only unless soft signals are enforced).

Inspection proposes; a person decides. The inspector's remedy tools `work.redrive` and `work.request_rotation` (ask a credential's
owner to rotate it; Orca never touches the secret) must be `Mode: Queue`, `Reviewer: Human`,
`SelfApproval: Forbid` (anything looser stops startup), and the operator can redrive directly with `--redrive <id> --by <name>
[--for profile:<name>] [--exclude-failed] [--drop-note]`. A redrive reopens the item with its runs reset and the inspection's findings
attached; `--exclude-failed` keeps the identities that failed it from claiming it again, `--drop-note` replaces the old thread note
(written by a possibly compromised agent) with the findings.

```json
"Agents": { "inspector": { "Tools": [ "work.redrive" ] } },
"Tools": { "work.redrive": { "Mode": "Queue", "Reviewer": "Human", "SelfApproval": "Forbid" } }
```

**Delegation** is the tool `work.delegate`, present when a profile lists `MayDelegateTo`, and governed by its policy entry like any
tool (so delegating to a production-capable profile can be made to queue). An agent may delegate only to the profiles its own profile
lists; the delegated agent acts under its own identity and profile, the item records the chain, and the chain is capped at
`MaxDelegationDepth`.

```json
"Agents": { "clerk": { "Tools": [ "mail.*", "work.*" ], "MayDelegateTo": [ "researcher" ] }, "researcher": { "Tools": [ "web.*" ] } },
"Tools": { "work.delegate": { "Mode": "Execute" } }
```

## Undo window

`"mail.send": { "Mode": "Execute", "DelayFor": "10m", "ReviewerGroup": "support" }` runs a send ten minutes after it is made unless
someone presses **Stop**. The agent is told `scheduled` with `runsAt` and carries on; reviewers see it under *Going out soon* on the
review page (and in notifications), where **Stop** cancels it and **Approve** runs it at once. Anyone in the action's reviewer group,
or the requester, may stop it; stopping needs no reason and no second person. What runs is what was announced (arguments are fixed;
stop it and let the agent resubmit to change them). The run-at time survives restarts; an action found more than its delay past its
time (after an outage) is held for a person instead of firing (`delay_held`). Audit: `delayed`, `delay_elapsed`, `stopped`.
Taint still queues: an announcement nobody reads is not a review.

An **Execute rule** can carry `DelayFor` too, so only the calls it matches run after a window (`"internal": { "Mode": "Execute",
"DelayFor": "5m", ... }`); not together with a `Budget`. `Limits.ElevatedDelay` (for example `"15m"`) gives Elevated identities a
delay instead of a queue for their Execute calls, for operators who prefer less friction. The Teams bot's card shows *Going out
soon* with **Stop** (any configured reviewer, whatever the risk) and **Run now**. A promoted tool can run after a delay too
(`TrustRamp.DelayFor`). And a tool with `"DelayUntilWorkingHours": true` whose window would end outside the policy's
`"WorkingHours": { "Days": ["Mon", ...], "From": "08:00", "To": "18:00", "Zone": "Europe/London" }` runs at the next opening plus
its window, so someone is around to stop it.

## Data flow

The other half of taint ([design](../docs/design/data-flow.md)). A **label** names a kind of data: the shipped ones are `finance`,
`personal`, `hr`, `customer` and `secret`; you may use your own. Modules label what their tools return (the email module's reads are
`personal`); a module instance in `modules.json` can label everything it reads (`{ "Type": "sql", "Id": "erp", "Labels": ["finance"], ... }`,
because only you know what that database holds); and a tool's policy entry can add labels (`"crm.get": { "Mode": "Execute", "Labels":
["customer"] }`). A session that calls a labelled tool **gains** its labels, and
so does the caller for `TaintWindow`, so a new session does not wash them off.

At every outbound (`ExternalEffect`) tool, the labels held are checked against `DataFlow`:

```json
"DataFlow": {
  "finance":  { "Allow": [ "mail.internal" ],
                "Destinations": [ { "Tool": "mail.send", "When": { "arg": "/to", "all": { "domainIn": [ "ourcompany.com" ] } } } ],
                "Otherwise": "Queue" },
  "secret":   { "Allow": [], "Otherwise": "Deny" }
},
"DataFlowMode": "ReportOnly"
```

`Allow` lists destinations whatever the arguments; `Destinations` allows a tool only when its arguments match a condition in the
[rules' language](#conditions) (here: every recipient in our own domain). An unknown condition does not allow.

**Value matching.** Orca keeps salted fingerprints (never the values) of distinctive values that labelled reads returned (strings
of 8+ characters, numbers of 6+ digits), for the label window. When a flow rule is broken and an outbound call's arguments contain
one, the reviewer sees "the arguments contain values returned by these reads" and the audit trail `flow_matched`. A rule with
`"RelaxWithoutMatch": true` does not queue a call that contains none of them; it never relaxes a `Deny`, because re-encoded data
(base64, a paraphrase) is not caught.

A destination not in `Allow` gets `Otherwise` (`Queue` or `Deny`); a session with several labels must satisfy each. The check only
tightens. With `ReportOnly` (the default) it writes `flow_would_check` to the audit trail and changes nothing; with `Enforce` it
applies (`flow_checked`). Queued actions show the reviewer "This session has read: finance (erp.query)". `--explain <tool> '<args>'
--labels finance` shows the outcome. The check is coarse on purpose: it knows what the session read, not which values the model
copied.

## Identity levels

Two high-precision signals move a signed-in identity up a ladder ([design](../docs/design/adaptive-limits.md)):

* **A call outside its agent profile** (`OutOfProfile`): **Suspended**. A healthy agent never sees those tools, so naming one means
  someone told it to. Every call is refused (`suspended`) and its pending actions are frozen: they can be rejected, not approved.
* **A decoy record** (`Decoys`, with markers in the policy's `"Decoys": ["C-DECOY-7731"]`): plant a fake customer, invoice or
  file that no real task needs, and list its marker (8 characters or more). A result containing it moves the identity to
  **Elevated**; an outbound call carrying it **suspends** the identity and, when enforced, that call is refused before it runs.
* **Softer signals** (`SoftSignals`): **Elevated**, never Suspended. Breadth: `BreadthTools` (15) different tools within
  `SignalWindow` (10 minutes), which looks like enumeration. And `TaintedHighImpactAttempts` (3) high-impact attempts within the
  window after reading outside content. Volume and time of day against a baseline come once the usage rollups have history.
* **A reviewer rejecting one of its actions** (`Rejections`): **Elevated**. Everything above `Read` waits for a person, whatever
  policy, rules or trust say; reads still run. Requests for changes do not count.

`ReportOnly` (the default for both) records the level and writes `level_would_change` to the audit trail with no effect, so you can
see what enforcing would do; `Enforce` applies it (`level_changed`). Automatic changes only tighten. Only a person returns an identity
to Normal: `--clear-level <subject> --by <name>`, and for a suspended API key only after rotating it (`--key-rotated`). `--levels` lists
them. The local stdio operator and in-process callers are never levelled.

## Reviewers, groups and several approvers

* Reviewer **groups** and **roles** come from the credential the server authenticated (a reviewer token's `|review,admin,group:finance`
  suffix, or the JWT's scopes and app roles), never from the request.
* `review` may decide actions; `admin` may also promote tools to auto-execute. An **API key can never review**, and a reviewer token can
  never call tools.
* With `RequiredApprovals: 2` the first approval is recorded and the action stays pending. The same person approving again counts once
  (two names for one person count once if `People` says so). An AI reviewer's approval never counts towards a several-approver action.

## AI reviewers

An AI reviewer can be talked into "yes" by the content it reviews (an email that says "approved by finance"). So whatever the policy says,
an AI reviewer (any reviewer signed in as `ai:<name>`, such as one run by `ReviewerRunner`) **cannot**:

* decide a `Destructive` action, or anything above the tool's `AiMaxRisk` (default `Write`, so sending mail needs `"AiMaxRisk": "ExternalEffect"`);
* decide an `ExternalEffect` action whose requester had read outside content (tainted); with `Either` a person may approve it;
* approve with edited arguments (it may reject, or ask for changes with a reason);
* be the second pair of eyes: with `SelfApproval: Forbid` only a person approves;
* keep deciding a request it has turned down `AiMaxRejections` times along its resubmission chain;
* approve more than `AiBudget` allows;
* decide a tool it is not named for in `AiReviewers`, when the tool has that list. Such an action is never shown to that reviewer.
* approve a request from its own identity, or from its own person in `People`: four eyes apply to an AI as well (a refusal is not restricted).

The queue enforces these, so they hold for any reviewer; a refusal is audited as `review_refused` with the reason, and the action waits.
Who may decide it then depends on the policy: with `Either` a person may approve it. With `Ai` a person may refuse it but not approve or
retry it (D-19), so an action the AI may not approve waits until it expires or is refused. An operator who needs a person to be able to
approve such an action sets `Either`. The runner does not offer an AI what it may not decide. Each AI decision is audited as `ai_review` with the model and
prompt version the reviewer reported; the checks it says it made are stored with the review (not in the audit trail, as they may
quote arguments). The design and what comes next (a model-backed reviewer, shadow mode, sampling) are in
[`../docs/MCP-HOST-V6-PLAN.md`](../docs/MCP-HOST-V6-PLAN.md).

## When something is not allowed

Call `--explain` with the arguments. The first line says the decision and the rule; the lines under it say, rule by rule, true / false /
unknown and why, and whether the arguments were all accounted for. If you changed the policy and want to know what it would have done to
last week, use `--replay-policy`.
