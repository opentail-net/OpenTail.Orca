# Design: three smaller extensions

**Status:** initial plan, 2026-10-17. Nothing here is implemented. Written from reading
[`opentail-orca`](https://github.com/opentail-net/opentail-orca) at `230cdd0`. Each is smaller than the
[roles](roles-and-permissions.md), [adaptive limits](adaptive-limits.md), [data flow](data-flow.md) and
[undo window](undo-window.md) designs, and each builds on one of them. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here.

## Temporary access for one task

**The problem.** [Profiles](roles-and-permissions.md) should be tight, but legitimate work is sometimes unusual: the invoice
assistant needs `crm.update` once, for a customer whose address changed. Today the choice is to widen the profile for good or
block the work.

**The idea.** Grants that expire, as in Entra's just-in-time access (Privileged Identity Management):

1. The agent calls a built-in tool, `access.request`, naming the tool and a reason. Its profile allows requests for a configured
   list of tools (`"Requestable": [ "crm.update" ]`), never administration tools.
2. A person in the approver group grants it **for a time or a number of calls** ("1 hour", "3 calls"), on the review page or Teams.
3. The tool appears in the agent's `tools/list` until the grant ends (MCP's tools-changed notification tells the client), then
   disappears. The grant is bound to the identity, and optionally to the session.
4. Every call under a grant is marked as such in the audit trail.

**Rules.** Grants never include administration tools. A grant can only narrow the tool's policy (a granted `mail.send` is still
queued if policy says so). A person can revoke a grant early. Requests are a [soft signal](adaptive-limits.md#softer-signals-which-raise-the-level-but-never-suspend-alone):
an agent asking for many tools is unusual.

**Metrics.** Requests, grants and refusals by tool and profile; calls made under grants. A tool granted every week to the same
profile should be in the profile.

*Built 2026-10-18: `Requestable` on profiles, the `access.request` tool (a built-in module, present only when a profile can request),
grants by time or calls whichever first (persisted, schema 8), the granted tool in the caller's `tools/list`, `granted_call` audit,
`--grants` and `--revoke-grant`. Not built: binding a grant to one session, the tools-changed notification (the Host's tool list is
static per connection; clients see it on their next `tools/list`), metrics by tool and profile, requests as a soft signal.*

## A tamper-evident audit trail

**The problem.** The audit trail (`IAuditSink`; SQLite in `SqliteAuditSink`) is append-only by contract, but nothing proves it.
Anyone with access to the database can delete or edit an entry and nobody can tell. For an enterprise buyer, and for audits (SOC 2;
the EU AI Act's record-keeping duty for high-risk systems), "the log shows it" is only worth as much as the log is trusted.

**The idea.** A hash chain:

- Each entry stores the hash of the entry before it and its own hash (over its fields and the previous hash). Editing or removing
  any entry breaks every hash after it.
- **Checkpoints:** periodically (hourly, and at shutdown) the Host publishes the latest hash somewhere the database's owner cannot
  rewrite: the operator's notification channel, a write-once storage bucket, or a signed file. A chain is only as trustworthy as
  its last checkpoint someone else holds.
- `--verify-audit` walks the chain and the checkpoints and reports the first break, if any.

**Rules.** Entries still hold no argument values (as today), so the chain can be shared with an auditor. Retention deletes
actions, never audit entries, so the chain is never cut by design. If the Host ever needs to drop old audit entries, it writes a
signed "truncated up to hash X" entry first.

**Metrics.** Checkpoints written, verification runs and results.

**Order.** Chain fields on new entries (a schema version bump; old entries are the chain's unverified prefix), `--verify-audit`,
then checkpoints to the notification channel, then write-once storage. *Built 2026-10-18: the chain (schema 5), `--verify-audit`
with `--checkpoint`, and `--audit-checkpoint` to print one. Also built the same day: hourly and shutdown
checkpoints to the notification channel (`audit_checkpoint`). Not built: write-once storage, and the signed truncation entry
(nothing truncates the audit trail today).*

## Watching the reviewers

**The problem.** Every safeguard that ends in "a person approves" is only as good as the person's attention. Approval rates near
100%, decisions in two seconds, a whole queue approved in one minute: these say people have stopped reading, and an attacker who
gets a proposal in front of them benefits.

**The idea.** Measure review quality and say so, gently:

- **Per reviewer and per tool:** approval rate, time to decision (the Host already records `opentail.host.time_to_decision`),
  approvals per minute in bursts, how often an approved action was later reverted or flagged.
- **Seeded checks, optional:** now and then, with the operator's consent, a known-bad sample action (clearly marked as a check
  once decided) is placed in the queue. Approving it is recorded and the reviewer is told immediately. It is the decoy record idea
  applied to people. Sensitive to culture: off by default, and the results go to the reviewer first.
- **What the Host does with it:** a weekly report to the operator; a nudge on the review page ("you approved 40 in 3 minutes");
  and evidence for policy changes: a tool approved 100% of the time over months is a candidate for [Delay](undo-window.md) or the
  trust ramp, and one with frequent rejections should stay Queue.

**Rules.** Reported to the reviewer and the operator, never used to block a reviewer automatically. No reviewer's figures are
shown to other reviewers.

**Order.** The report from data the Host already has, then the review-page nudge, then seeded checks if a customer wants them.
*Report built 2026-10-18: `--review-report [--since 7d]`. Not built: sending it weekly, the review-page nudge, seeded checks
(designed only, by decision).*

## Decisions

Decided by the owner on 2026-10-18.

- **Temporary access:** a grant ends after a time or a number of calls, whichever comes first.
- **Audit checkpoints:** the notification channel first (already configured), then write-once storage such as S3 Object Lock.
- **Seeded checks:** designed only; built only if a customer asks. They carry a cultural cost.
