# Design: audit and evidence

**Status:** proposed 2026-10-18. D1 to D7 and D10 built 2026-10-18, D8 and D9 in part. Written from reading [`opentail-host`](https://github.com/opentail-net/opentail-host)
at `ef471f1`. Regulatory facts were checked on 2026-10-18 from secondary sources; re-check the official texts before telling a
customer what a regulation requires.

**Where it fits:** completes the [tamper-evident audit trail](more-extensions.md#a-tamper-evident-audit-trail) (plan section 16,
partly built) and records what the newer designs decide ([adaptive limits](adaptive-limits.md), [data flow](data-flow.md),
[undo window](undo-window.md), [failures](failures-and-inspection.md)). Work plan section 18.

## Why this is needed

The Host exists to make decisions about what agents may do: run a call now, wait for a person, or refuse. A customer who puts the
Host between agents and their business systems will be asked, by an auditor, a regulator, a security team or a court:

- **What happened?** Which agent did what, on whose behalf, when, and with what result.
- **How was it decided?** Which rules applied, which checks ran, what each concluded, and why the outcome was what it was.
- **Who decided?** Which person or automated reviewer approved, with what authority, and what they were shown.
- **Under which rules?** What the policy said on that day, and who changed it since.
- **Can we trust the record?** Whether anyone could have altered or deleted it, and how we would know.

A governance product whose own records cannot answer these is not a governance product. The Host already answers the first and
third well, the second only in part, and the fourth and fifth not fully. This design closes the gaps.

It is also a selling point: evidence an auditor can verify without trusting us is rare among agent tools, and buyers in finance,
health and the public sector ask for it first.

## What standards we are trying to meet, and how

The Host is a component, not an organisation, so it cannot be certified by itself. What it can do is **produce the records and
controls a customer needs to pass their own audit**, and make them easy to hand over. The standards that matter:

| Standard | What it asks for, in short | How the Host answers |
|---|---|---|
| **SOC 2** (AICPA Trust Services Criteria) | Logical access controlled and reviewed (CC6); system activity monitored, anomalies detected and acted on (CC7); changes authorised and tracked (CC8) | Every call authenticated and decided by policy; access changes audited ([D4](#d4-access-changes-are-audited)); policy changes versioned and attributed ([D2](#d2-policy-versions)); adaptive limits and identity levels as monitoring |
| **ISO/IEC 27001:2022**, Annex A | Logging (8.15) and monitoring (8.16); protection of logs from tampering and unauthorised access; clock synchronisation (8.17); change management (8.32); collection of evidence (5.28) | The decision trace and records ([D1](#d1-a-decision-trace-on-every-action), [D3](#d3-decision-records-that-outlive-the-action)); the tamper protection [below](#keeping-the-audit-log-safe-from-tampering); UTC timestamps with the instance's identity ([D9](#d9-time-and-origin)); the auditor's export ([D7](#d7-an-export-for-auditors)) |
| **NIST SP 800-53**, AU family (used by US public sector, and as a reference elsewhere) | What a record must contain (AU-3), protection of audit information (AU-9), non-repudiation (AU-10), retention (AU-11) | Record contents in D1 and D3; tamper protection; authenticated subjects on every decision; a separate audit retention setting |
| **EU AI Act** (Regulation 2024/1689), for customers using agents in high-risk uses | Automatic logging over the system's life (Art. 12); human oversight (Art. 14); deployers keep the logs under their control for at least six months (Art. 26(6)) | The Host is where the logs of an agent's actions are made; approvals are the human oversight, recorded with who decided and what they saw ([D6](#d6-what-the-automated-reviewer-saw)); audit retention can be set to six months or more. Dates for high-risk obligations have been subject to change; check before quoting them |
| **ISO/IEC 42001** (AI management systems) | Records showing AI systems are operated under defined controls, with human oversight and monitoring | The same records: policy as the defined control, approvals as oversight, the review-quality report as monitoring of the overseers |
| **GDPR** | Accountability: being able to show compliance (Art. 5(2)); data minimisation and storage limits (Art. 5(1)(c), (e)); safeguards around automated decisions (Art. 22) | Audit entries and decision records hold no argument values; retention for actions (which hold personal data) is separate from retention for decision records (which do not); reads of personal data are logged ([D5](#d5-who-read-what)) |

Sector rules (DORA for EU finance, HIPAA for US health) ask for the same things in their own words: logs of access and changes,
kept safe and kept long enough. Meeting the table above covers most of them.

**What the Host does not cover:** how the customer runs it (who has access to the server, backups, staff training, incident
response). The Host supports those controls; it does not replace them. An [evidence guide](#d10-a-guide-for-auditors) says which is
which.

## What the Host records today

- **An action record for every call:** requester, agent profile, session, tool, module version, arguments as submitted with their
  hash (reviewer edits kept beside them, never overwriting), status, error, attempts, timestamps.
- **Each review:** decision, reason, the authenticated subject (never taken from the request) and their groups; for the AI reviewer,
  its model, its instruction version and the checks it says it made.
- **Part of the reasoning:** the policy mode, the conditional rule that decided it if one did, a snapshot of that tool's policy,
  whether the session was tainted, and the data labels it held.
- **An audit trail:** one-line events (approved, denied, flow_matched, level changes, grants, trust promotions, retention purges and
  more), never argument values, chained with SHA-256 since schema 5; hourly and shutdown checkpoints to the notification channel;
  `--verify-audit` reports the first break.
- **Tools:** `--explain` (how a tool would be decided now), `--replay-policy` (how a new policy would have decided past actions),
  `--review-report` (review quality).

**The gaps** an auditor would find: no full trace of the checks behind a decision; no record of which policy was in force, or who
changed it; when retention is configured, the purge deletes the reasoning with the action; creating or changing an API key leaves
no audit entry (keys live in the HTTP config); no log of who read sensitive data; no single export an auditor can verify; the AI
reviewer's input is not kept.

## What we propose to deliver

### D1. A decision trace on every action

Every call passes a fixed sequence of checks: authentication, profile, policy mode and rules, taint, flow rules (labels), identity
level, delay. Each can only tighten. Today only the final mode and the one matching rule are stored, so "queued because the session
was tainted, though the rule said run" cannot be rebuilt.

Each action gets an ordered **decision trace**: for every check that ran, its name, what it concluded, the evidence it used (rule
name, label, level, grant id; never argument values), and whether it changed the outcome. Example:

```
profile        allowed      profile=finance-agent
policy         Execute      tool default
taint          Queue        session read web.fetch at 10:41   ← changed
flow           Queue        label finance → mail.send: rule finance-outbound   (no further change)
level          Normal
delay          none
→ Queue, reviewer group finance, 1 approval
```

The trace is written once, when the decision is made, by the code that makes it; nothing reconstructs it later. `--explain` and the
review page show the same trace, so the reviewer, the operator and the auditor see one explanation.

### D2. Policy versions

- **Every policy the Host loads is recorded:** a `policy_loaded` audit entry with the SHA-256 of the whole effective policy (all
  files merged), the Host version, the instance, and who started or reloaded it when known.
- **Every action carries the policy hash** it was decided under, beside today's per-tool snapshot.
- **Every version is kept,** content-addressed by its hash, so "what did the policy say on 3 March" is a lookup, and
  `--policy-diff <hash> <hash>` shows what changed.
- **Attribution:** the Host cannot know who edited a file. It records the hash and the operator who loaded it; the recommended
  practice (in the evidence guide) is to keep policy in version control, where the commit names the author and the hash links the
  two. `--lint` and `--replay-policy` before a change are the change-review evidence.

### D3. Decision records that outlive the action

Actions hold argument values and results, which may be personal data, so they should be purged after a short time. The reasoning
should not go with them.

- When an action is purged, a **decision record** is kept: tool, module version, requester, profile, session id, timestamps, the
  decision trace, the policy hash, each review (who, groups, decision, reason, AI model and instruction version), the outcome and
  error kind, the arguments hash, result counts. **No argument values, no results.**
- **Separate retention:** `AuditRetentionDays` for decision records and audit entries, apart from `RetentionDays` for actions.
  Proposed default: keep (no purge), as audit entries are kept today; customers set it to their obligation (six months minimum
  under the AI Act for high-risk use; often years in finance).
- **Bound into the chain:** the audit entry for an action's final state carries the decision record's hash, so the chain proves the
  record unaltered without holding its content.
- The arguments hash lets a customer who kept the arguments elsewhere prove which arguments a decision was about.

### D4. Access changes are audited

The access review an auditor runs starts with "who could do what, and who changed it". Grants, identity levels, trust promotions
and administration tools are audited today. Added:

- **API keys:** keys are in the HTTP config, so the Host compares the key list at each load with the last one it saw and audits
  `key_added`, `key_removed` and `key_profile_changed` (by key name and hash prefix, never the key).
- **Agent profiles and reviewer groups:** additions, removals and changes to what a profile allows, recorded as part of the policy
  version (D2) and summarised as audit entries.
- **A point-in-time access report:** `--access-report --at <date>`: every key, its profile, what that profile allowed, active grants
  and identity levels, as of that date, rebuilt from the audit trail and the policy versions.

### D5. Who read what

When actions hold personal data, auditors ask who looked at it.

- **Audited:** opening an action's arguments or result on the review page, exports (D7, review outcomes, shadow cases), and reads
  through the Host's API, with the reader's subject.
- **Not covered, and stated so:** someone with direct access to the database file. That is the customer's server access control,
  covered by the evidence guide.

### D6. What the automated reviewer saw

Human oversight rules ask what a decision-maker was shown. For people, the review page shows the action, its preview and its
trace, all recorded. For the AI reviewer:

- **Always:** a hash of its full input (instructions and the action as presented) on the review.
- **Optional, per policy:** the full input, stored with the action, with its labels, purged with it.

### D7. An export for auditors

`--export-audit --since <date> --until <date>` writes one bundle:

- decision records and audit entries for the period (JSON Lines);
- every policy version in force during it;
- the checkpoints that cover it;
- a manifest with the hash of each file and how to verify them.

**Verification does not need our code.** The chain's algorithm is documented (SHA-256 over a JSON array of the entry's fields and
the previous hash), and the bundle includes a short script in plain Python so an auditor can check it independently.
`--verify-audit` does the same from the Host.

### D8. Tamper protection completed

See [below](#keeping-the-audit-log-safe-from-tampering): write-once checkpoints, streaming to the customer's log store, and the
signed truncation entry.

### D9. Time and origin

Every entry records UTC time and the Host instance that wrote it, and the Host version is in `policy_loaded`. The evidence guide
asks for clock synchronisation (ISO 27001 8.17) on the server; the Host warns at start-up when its clock is far from the checkpoint
target's.

### D10. A guide for auditors

A short document, `AUDIT.md` beside the Host's README, written for an auditor rather than a developer:

- how a decision is made, check by check, and what each check means;
- what is recorded, where, for how long, and what is never recorded;
- how to verify the trail and the export;
- which controls the Host provides and which remain the customer's (server access, backups, clocks, staff);
- a mapping from the standards above to the records that answer them.

"How are decisions made?" is the auditor's first question; this answers it before it is asked.

## Keeping the audit log safe from tampering

The threat: someone with access to the Host's database (an administrator, an attacker who got in, or the Host itself if
compromised) edits or deletes entries to hide what happened. Each layer below narrows what they could get away with.

1. **Append-only by contract.** `IAuditSink` only appends; nothing in the Host updates or deletes audit entries, and retention never
   deletes them (built).
2. **A hash chain.** Each entry carries the SHA-256 of its own fields and of the previous entry's hash. Editing or deleting any entry
   breaks every hash after it; `--verify-audit` finds the first break (built, schema 5).
3. **Decision records bound into the chain.** The chain carries each decision record's hash (D3), so the records cannot be altered
   either, even though the chain never holds their content.
4. **Checkpoints held by someone else.** A chain alone can be rewritten end to end by someone who recomputes every hash. So the Host
   publishes the latest hash hourly and at shutdown somewhere the database's owner cannot rewrite: the notification channel today
   (built); a rewrite would no longer match a checkpoint already sent. The window an attacker has is the time since the last
   checkpoint.
5. **Write-once storage for checkpoints.** Checkpoints also go to storage that cannot be overwritten or deleted until a retention
   date, even by its owner: S3 Object Lock in compliance mode first, Azure immutable blob storage next. A separate small writer,
   not the storage module (which has deliberately no write path).
6. **Streaming to the customer's log store.** Audit entries are also sent as they are written, over OpenTelemetry logs or to a
   syslog or SIEM endpoint (Splunk, Microsoft Sentinel, Elastic). A copy outside the Host's control, written within seconds, shrinks
   the window from an hour to seconds and is where a customer's security team already looks.
7. **No silent truncation.** If audit entries must ever be dropped (retention), the Host writes a signed truncation entry first,
   naming the range and the hash it ended on, so the chain continues from a known point and the cut is visible.
8. **Independent verification.** The documented algorithm and the script in the export (D7) mean an auditor checks the trail
   without trusting the Host or us.

Optional, for customers who ask: trusted timestamps on checkpoints (RFC 3161), proving each checkpoint existed by a given time.

**What this does not protect against,** stated plainly in the evidence guide: a compromised Host can write false entries from the
moment it is compromised. Layers 4 to 6 make sure it cannot rewrite what was recorded before; detecting the compromise itself is the
job of adaptive limits, the customer's monitoring and the streamed copy.

## Rules that make it hold

1. **Written once, by the deciding code.** Traces and decision records are recorded when the decision is made, never rebuilt.
2. **No argument values or results in audit entries, decision records or exports.** Hashes and counts only; values stay in actions,
   under action retention.
3. **Audit retention is separate from action retention,** and never shorter by default.
4. **Every change of rules or access leaves an entry.**
5. **Verifiable without trusting the Host.**
6. **One explanation.** The review page, `--explain`, the export and the auditor's guide show the same trace.

## How it fits with the other designs

- **Adaptive limits, data flow, undo window, temporary access:** each is a check in the trace (D1), and their report-only results
  are recorded as "would have changed" so an auditor sees what enforcement would have done.
- **Failures and inspection:** failure kinds, circuits, inspection findings and redrives (automatic ones with their ticket and probe)
  are audit events and appear in decision records.
- **Work items:** creation, claims, delegation chains and continuations are audit events; a decision record names the work item it
  served.
- **Telemetry:** metrics stay counts and durations; the audit trail is the record. Telemetry never carries what the audit rules keep
  out.

## Order

1. **D1 decision trace and D2 policy versions.** Together they answer "how was it decided, under which rules". The checks already run
   in one place; they start writing down what they concluded. Schema bump. *Built 2026-10-18: `ActionRecord.DecisionTrace` (check, result,
   evidence, changed) written by the queue as each check runs and shown on the review page; `ActionRecord.PolicyHash` (SHA-256 of
   the canonical policy, keys sorted); policy versions in schema 9 (`policy_versions`), recorded with a `policy_loaded` audit entry
   at start-up; `--policy-versions` and `--policy-diff`. Not yet: attribution of who loaded a policy beyond `StartedBy` on the
   builder (the runners do not pass a name yet), and `--explain` printing the same trace.*
2. **D3 decision records** with separate audit retention, bound into the chain. *Built 2026-10-18: the purge writes a decision
   record per deleted action in the same transaction (schema 11), and the `retention_purge` audit entry carries the batch and the
   digest of its records (SHA-256 over their hashes in action-id order), rather than one audit entry per record; `--verify-audit`
   checks each record against its hash and each batch against its digest; `AuditRetentionDays` (provisional default 0: keep) deletes
   old records and must be zero or at least `RetentionDays`. Records are written at purge, not at the action's final state: until
   then the action itself is the record. Audit entries are still never deleted (that needs the signed truncation entry, D8).*
3. **D4 access changes** and the access report. *Built 2026-10-18: the HTTP server audits key changes at start against a snapshot
   in the settings table (`key_added`, `key_removed`, `key_rotated`, `key_profile_changed`); `policy_loaded` of a new version audits
   `profile_added`, `profile_changed`, `profile_removed` and `people_changed` against the version loaded before it;
   `--access-report --at` rebuilds keys, profiles, grants still stored and identity levels as of a time. Reviewer groups are part of
   each tool's policy, so their changes show in `--policy-diff`, not as separate entries. Revoked grants are deleted, so the report
   shows only grants still stored (their `grant_revoked` entries remain in the trail).*
4. **D7 export** with the verification script, and **D10 the auditor's guide.** *Built 2026-10-18: `--export-audit` writes the
   chain up to the end of the period (it can only be checked from its start) with each entry's preimage, so `verify.py` needs only
   SHA-256 and a JSON parser, not .NET's serializer; decision records in complete purge batches, plus records built from actions not
   yet purged (`batch: live`); policy versions in force; `head.txt`; `manifest.json`. Tests run `verify.py` with real Python when it
   is installed and check it catches an edit even when the manifest is fixed. `OpenTail.Host/AUDIT.md` is the guide.*
5. **D8 tamper protection:** streaming to a log store, then write-once checkpoints, then the signed truncation entry. *Streaming
   built 2026-10-18: an `audit_entry` notification event, webhook channels only (signed, through the outbox with retries), picked
   up every scheduler pass (30 s) from a cursor kept across restarts. Format provisional: the Host's own JSON over a signed webhook,
   which HTTP collectors (Splunk HEC, Sentinel, Elastic) can take; OpenTelemetry logs and syslog are not built. Not built:
   write-once checkpoints (S3 Object Lock needs a real bucket with Object Lock to verify, and a choice of which package carries the
   AWS dependency: the design says a separate small writer, not the storage module) and the signed truncation entry (nothing
   truncates the trail, so it has no use yet).*
6. **D5 read logging, D6 reviewer input, D9 origin.** *Built 2026-10-18: `action_viewed` when the review page opens an action, with
   the reader; `export` for each CLI export, with the OS account; `AiInputHash` (SHA-256 of the action as handed over) on every AI
   review, set by the runner, and `AiInput` kept when the policy says `AiReviewInput: Full` (provisional default: hash only); the
   hash also goes into decision records. D9 in part: the Host version and instance are on `policy_loaded`; per-entry instance would
   change the chain's format, and the clock check against the checkpoint target is not built.*

## Decisions

Still open:

- **Default audit retention:** keep everything until the customer sets a period (as today), or a default such as one year?
- **Streaming format first:** OpenTelemetry logs (fits the Host's telemetry), syslog, or a specific SIEM?
- **Write-once target first:** S3 Object Lock, as proposed, or Azure immutable storage for Microsoft 365 customers?
- **AI reviewer input:** the hash always and the full input optional, as proposed, or the full input always?
- **Read logging scope:** review page, exports and API, as proposed, or narrower to start?

Sources consulted for the regulatory facts: [EU AI Act Article 26](https://artificialintelligenceact.eu/article/26/);
[AI logging under the EU AI Act](https://www.datenschutz-notizen.de/ai-logging-under-the-eu-ai-act-the-compliance-infrastructure-behind-high-risk-systems-4458904/);
[What the EU AI Act requires for AI agent logging](https://www.helpnetsecurity.com/?p=366232).
