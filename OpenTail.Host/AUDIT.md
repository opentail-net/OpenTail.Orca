# OpenTail Host for auditors

This page is for an auditor, a regulator or a customer's security team. It explains how the Host decides what an AI agent may do,
what it records, how to check those records without trusting the Host or its makers, and which controls are the Host's and which
stay with the organisation running it.

The Host is a component, not an organisation: it cannot be certified on its own. It produces the records and enforces the controls an
organisation needs for its own audit (SOC 2, ISO/IEC 27001, the EU AI Act and others; see [the mapping](#standards)).

## What the Host does

Agents (AI models acting for people) do not reach business systems directly. They call tools on the Host, and for each call the Host
decides one of three things:

* **run it now**;
* **hold it for a person** (or for an automated reviewer, where policy allows), who approves, rejects or sends it back; or run it after
  an *undo window* during which a person can stop it;
* **refuse it.**

The rules are in a policy file written by the organisation. No argument, prompt or model output can change them.

## How a decision is made

Every call passes the same checks, in this order. Each check can only make the outcome stricter; the strictest answer wins.

| Check | Question it answers |
|---|---|
| Authentication | Who is calling? Taken from the credential (an API key or a signed-in user), never from the request. |
| Profile | Does the caller's agent profile include this tool? The profile is bound to the credential; outside it, the call is refused. |
| Identity level | Is this identity under suspicion (Elevated) or suspended? Suspended identities are refused everything. |
| Policy and rules | What does the policy say for this tool, and does a conditional rule on its arguments apply? |
| Trust ramp | Has a person promoted this tool to run without review after enough clean approvals? Never beats a rule that holds or refuses. |
| Taint | Has the caller read outside content (email, web pages) that might contain instructions? Then high-impact tools wait for a person. |
| Data flow | Is labelled data (finance, personal, ...) the session has read going somewhere it is not allowed to go? |
| Delay | Should it run after an undo window instead of at once? |
| Queue | Who must approve, and how many people? |

**Every action records this as its decision trace:** each check that ran, what it concluded, the evidence it used (rule names,
labels, grant ids, never argument values) and whether it changed the outcome. The trace is written once, by the code that makes the
decision, and never reconstructed later. The review page shows it under *How this was decided*; the export carries it.

Each action also carries the **hash of the whole policy** it was decided under, so "which rules applied" is a lookup.

## What is recorded

| Record | Holds | Never holds | Kept |
|---|---|---|---|
| **Action** (one per call) | Requester, profile, session, tool, module version, arguments and their hash, the decision trace, policy hash, reviews, outcome, failure kind, result | Secrets | Until `RetentionDays` (organisation's choice; 0 keeps them) |
| **Review** (on the action) | Decision, reason, the authenticated reviewer and their groups; for an automated reviewer, its model, instruction version and the checks it reported | | With the action |
| **Decision record** (when an action is purged) | Everything above except arguments and result: requester, profile, trace, policy hash, reviews, outcome, the arguments' hash, result counts | Arguments, results | Until `AuditRetentionDays` (0 keeps them; never shorter than action retention) |
| **Audit entry** | One line per event: decisions, approvals, denials, policy loads, access changes, purges, level changes, grants, work items | Argument values, secrets | Always |
| **Policy version** | The whole effective policy, by its hash, with when it was first and last loaded | | Always |

**Access changes** are audited: API keys added, removed, rotated or moved to another profile (by name and a prefix of the key's hash,
never the key); agent profiles and people added, changed or removed with each new policy version; temporary grants; identity levels.
`--access-report --at <time>` rebuilds who could do what at any moment.

**Reads and exports** are audited: who opened an action on the review page, and every export with the account that ran it. Someone
reading the database file directly is not seen by the Host; that is server access control.

**Automated reviewers:** each AI review records the model, its instruction version, the checks it reported and the SHA-256 of the
action exactly as it was handed to it (optionally the input itself).

**Human oversight** is recorded on every action that waited for a person: who decided, in which group, when, why, and what they were
shown (the preview and the trace).

## How to check the records

The audit trail is protected in layers:

1. **Append-only.** Nothing in the Host edits or deletes audit entries; retention never touches them.
2. **A hash chain.** Each entry carries the SHA-256 of its own fields and of the previous entry's hash. Editing, removing or reordering
   any entry breaks every hash after it.
3. **Decision records are bound into the chain.** Each purge writes one audit entry carrying the digest of the decision records it
   kept, so the records cannot be altered either, though the chain never holds their content.
4. **A copy streamed out** (when configured): every audit entry, with its chain hashes, sent to the organisation's log store within
   a minute of being written.
5. **Checkpoints kept elsewhere.** Every hour and at shutdown the Host sends the newest entry's hash to a channel outside its database
   (a webhook, Teams or Slack). A chain rewritten end to end would no longer match a checkpoint already sent.

**On the server:** `--verify-audit [--checkpoint <seq:hash> ...]` walks the chain, checks the decision records against their batches,
and compares checkpoints. It names the first problem and exits 1 if there is one.

**Without the Host:** `--export-audit <folder> [--from <time>] [--until <time>]` writes a folder with the audit trail, the decision
records for the period (complete purge batches), every policy version in force, the chain's head, a manifest of file hashes, and
`verify.py`. Run `python3 verify.py <folder>`: it uses only Python's standard library and none of OpenTail's code. Each hash is checked
over the exact text it covers, and that text is parsed to confirm it says what the record says. Then compare `head.txt` with the
checkpoints the organisation kept outside the Host.

**What this does not protect against:** a Host that is compromised can write false entries from that moment on. The layers above make
sure it cannot rewrite what was recorded before. Detecting the compromise is the job of the Host's identity levels, the organisation's
monitoring, and the checkpoints and copies held elsewhere. Someone with direct access to the database file can read what actions hold;
access to the server is the organisation's control.

## Which controls are whose

| The Host provides | The organisation provides |
|---|---|
| Authentication of every caller; profiles bound to credentials | Who gets a credential, and reviewing that regularly |
| Policy enforcement on every call; refusal by default | Writing the policy, keeping it under version control, reviewing changes (`--lint`, `--replay-policy`) |
| Human approval where policy requires it, with separation of duties (no self-approval where forbidden, groups, several approvers) | Choosing the reviewers and training them |
| The decision trace, decision records, audit trail, policy versions, access-change entries | Keeping checkpoints outside the server; choosing retention periods to match obligations |
| Tamper evidence (chain, digests, checkpoints, export with an independent verifier) | Access control to the server and its database; backups; clock synchronisation (NTP) |
| Monitoring signals (identity levels, decoys, review-quality report) | Responding to them; incident response |

## Standards

The Host produces records that answer these requirements; meeting them is the organisation's.

| Standard | Requirement | Where the Host answers it |
|---|---|---|
| SOC 2 CC6 | Logical access controlled and reviewed | Authentication, profiles, access-change entries, `--access-report` |
| SOC 2 CC7 | Activity monitored, anomalies detected | Audit trail, identity levels, decoys, review-quality report |
| SOC 2 CC8 | Changes authorised and tracked | Policy versions, `policy_loaded` entries, `--policy-diff` |
| ISO/IEC 27001 A.8.15, A.8.16 | Logging and monitoring; logs protected | Decision trace, audit trail, hash chain, checkpoints |
| ISO/IEC 27001 A.8.17 | Clock synchronisation | UTC timestamps; the server's clock is the organisation's |
| ISO/IEC 27001 A.5.28 | Collection of evidence | `--export-audit` with `verify.py` |
| NIST SP 800-53 AU-3, AU-9, AU-10, AU-11 | Record content, protection, non-repudiation, retention | Record contents above; tamper layers; authenticated reviewers; `AuditRetentionDays` |
| EU AI Act Art. 12, 14, 26(6) | Automatic logging; human oversight; deployers keep logs at least six months | Every call recorded; approvals with who decided and what they saw; retention set by the organisation |
| ISO/IEC 42001 | AI operated under defined controls with oversight and monitoring | Policy as the control, approvals as oversight, review-quality report |
| GDPR Art. 5 | Accountability; data minimisation and storage limits | No argument values in audit entries or decision records; separate retention for actions, which hold personal data |

The procedures that produce and protect these records day to day (retention, verification, answering an auditor, incidents) are in
[SOP.md](SOP.md). Regulatory references are summaries; check the official texts for what applies to you. See also the
[design behind this page](../docs/design/audit-and-evidence.md).
