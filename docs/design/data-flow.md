# Design: where data goes

**Status:** steps 1 to 3 of the [order](#order) built on 2026-10-18 in a first form: labels on descriptors and policy entries,
labels on sessions and callers (persisted), the flow check at `ExternalEffect` tools (`DataFlowMode`: report only by default,
or enforced), the reviewer line on the review page and the Teams card, `--explain --labels`, module labels per instance, argument
conditions (`Destinations` with a `When`), and value matching with `RelaxWithoutMatch` (fingerprints in memory, per process). Not
built: telemetry by label. First written 2026-10-17. Written from reading
[`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca) at `230cdd0`. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here. The owner's decisions of 2026-10-18 are under [decisions](#decisions).

**Where it fits:** Orca work, alongside [roles and permissions](roles-and-permissions.md) (who may call a tool) and
[adaptive limits](adaptive-limits.md) (how much). This one decides **where data may go**. It is the other half of taint.

## The problem

Taint tracks **untrusted content coming in**: once a session has read mail or the web, its high-impact calls wait for a reviewer.
Nothing tracks **sensitive data going out**. Orca does not know that the text in a `mail.send` body came from the ERP, so:

- A session that never read anything untrusted (no taint) can still read customer records and mail them anywhere `mail.send`
  policy allows.
- A tainted session's outbound call is queued, but the reviewer is not told "this body contains rows from the finance database";
  they have to spot it.
- An operator cannot write the rule they actually mean: "finance data never leaves for an outside domain", "personal data stays in
  the EU tenant", "nothing from HR goes into a ticket".

Research on prompt injection has converged on the same point: the model cannot be made unfoolable, so the system must constrain
what data can flow where, regardless of what the model was told. This design brings that idea into Orca in a form operators can
read.

## The idea

**Label data where it enters, carry the labels through the session, and check them where data leaves.**

### Labels at the source

A label names a kind of data: `finance`, `personal`, `hr`, `customer`, `secret`. Labels come from two places:

- **The module** declares what a tool returns: `erp.query` returns `finance`; `hr.lookup` returns `hr, personal`. A new descriptor
  field, like `ReadsUntrusted`: `Labels = ["finance"]`.
- **The operator** adds or narrows by configuration: `sql.query` against the `payroll` database returns `hr`; files under
  `/contracts` return `customer`. Matching on the call's arguments uses the same condition language as policy rules.

Operators can add labels, never remove a module's. As everywhere in Orca, configuration only tightens.

### Labels on the session

When a call returns labelled data, the session gains those labels, the way it gains taint today (`sessions.Taint`). A session's
labels are the union of everything it has read. Like taint, they spread to every session of the same caller within a window, so a
new session cannot launder them.

This is deliberately coarse: Orca does not try to follow individual values through the model's reasoning (it cannot see
inside the model). "This session has seen finance data" is the honest statement. A [finer check](#finer-checks-where-they-are-cheap)
adds precision where it is cheap.

### Checks at the exit

An outbound tool (any `ExternalEffect` tool, and writes to shared places: tickets, wiki pages, files in shared folders) is checked
against the session's labels before policy decides:

```json
"DataFlow": {
  "finance":  { "Allow": [ "mail.send:to=*@ourcompany.com", "erp.*" ], "Otherwise": "Queue" },
  "personal": { "Allow": [ "mail.send:to=*@ourcompany.com", "crm.update" ], "Otherwise": "Queue" },
  "secret":   { "Allow": [], "Otherwise": "Deny" }
}
```

- A label's rule lists the destinations its data may reach without review. Anything else gets `Otherwise`: `Queue` (a person
  decides) or `Deny`.
- Destinations name a tool and, where it matters, a condition on its arguments (recipient domain, ticket project, folder),
  compiled with the existing rule compiler.
- An unlabelled session is unaffected. A session with several labels must satisfy every label's rule.
- The flow check only tightens policy, never loosens it: a tool policy of Deny stays Deny.

### What the reviewer sees

A queued action gains a line: **"This session has read: finance (erp.query, 3 calls), personal (crm.get)."** and, when the
[finer check](#finer-checks-where-they-are-cheap) found something, **"The body contains values returned by erp.query."** The
reviewer no longer has to guess where the content came from.

### Finer checks, where they are cheap

Coarse session labels will over-queue: a session that read one invoice and then sends an unrelated internal note gets checked.
Two refinements cut that down without pretending to see inside the model:

1. **Value matching.** Orca keeps salted fingerprints of distinctive values returned by labelled calls (ids, account numbers,
   e-mail addresses, longer strings), for the session's lifetime. An outbound call's arguments are scanned for them. A match is
   strong evidence and is shown to the reviewer; no match on a low-risk destination may relax `Otherwise` from Queue to Execute,
   if the operator allows it (`"RelaxWithoutMatch": true`). Fingerprints only, never the values, and gone with the session.
2. **Decoy records** from the [adaptive limits design](adaptive-limits.md#tripwires) are the extreme case: a decoy's marker in an
   outbound argument suspends the identity.

Value matching can be fooled by an attacker who re-encodes data (base64, paraphrase). That is why it only ever **relaxes** a check
the operator opted into, or **adds** evidence; it never is the only thing between labelled data and the outside.

## Rules that make it hold

1. **Labels only accumulate within a session.** Nothing the model says removes one. A new session starts clean only after the
   caller's label window passes, as for taint.
2. **Module labels cannot be removed by configuration,** only added to.
3. **The model is never told its labels** or why a call was queued beyond the usual "waiting for a reviewer". Anything it can see,
   an injection can steer around.
4. **No values are stored for the check,** only salted fingerprints, held for the session and dropped with it.
5. **Explainable:** `--explain --caller` and the simulator show which labels a session holds, where they came from, and which rule
   decided an outbound call.

## How it fits with the other designs

- **Taint** stays as it is: untrusted in. Labels are sensitive out. A call can be held for either reason, and the reviewer sees
  both.
- **Profiles** decide whether an agent has `erp.query` and `mail.send` at all. Flow rules decide what happens when one agent has
  both. The cleanest deployments need few flow rules because their profiles already keep readers of sensitive data away from
  outbound tools.
- **Adaptive limits:** labelled reads cost more units, and a flow rule hit (`Otherwise` triggered) is a soft signal; a decoy match
  is a tripwire.

## Metrics to record

From the start, even before enforcement: labels gained per session (by label and tool), flow checks by label, destination and
outcome, value-match hits. Telemetry by label and tool, never by identity. Audit events: `flow.checked` (label, destination,
outcome), `flow.matched` (label, source tool). Like adaptive limits, run report-only first: "this would have been queued, because".

## Order

1. **Labels on descriptors and in configuration;** labels on sessions; `--explain` shows them. Report-only: audit what flow rules
   would have done.
2. **The reviewer line** ("this session has read …") on the review page and the Teams card. Useful even with no rules.
3. **Flow rules enforcing** `Queue` and `Deny`.
4. **Value matching** and `RelaxWithoutMatch`.
5. **Module labels** shipped for Sql, Files, inbox, Microsoft 365, Google, ServiceNow, Jira and Confluence.

## Decisions

Decided by the owner on 2026-10-18.

- **Labels:** five fixed labels ship (`finance`, `personal`, `hr`, `customer`, `secret`), plus labels operators define. Few
  labels means people actually write rules.
- **Label window:** reuse `TaintWindow`; no separate setting.

Still open:

- **Which tools count as an exit?** Proposed: every `ExternalEffect` tool, plus writes that a module marks as reaching other people
  (tickets, wiki pages, shared files).
- **Value matching cost:** how many fingerprints per session before it becomes slow or memory-heavy, and is the privacy review
  happy with salted fingerprints held for a session?
- **Regions:** is "personal data stays in the EU tenant" a label rule (destinations by region), or a separate deployment concern?
