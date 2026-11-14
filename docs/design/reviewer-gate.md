# Reviewer gate: AI reviewers on Orca

**This is the current design and the plan.** It says what is true in the code now, what is open, and what waits for the owner.
Last checked against the code at `48abef9` (2026-11-13), plus batch 08 (2026-11-14, not yet committed).

**The record is in [`reviewer-gate-history.md`](reviewer-gate-history.md):** the three versions of the plan, the findings F1 to
F23 as they were found, the two external reviews, the phase log with branches and test counts, and the review of 2026-11-13. Code
comments that cite a section number (5.6, 5.13) or a finding (F14) mean that file. Decision numbers (D-1 to D-41) and gap numbers
(G1 to G8) are the same in both files.

**How to keep this file.** Tick a box when the work is merged, and add one line to the log in the history file. Change section 2
in the same commit as the code it describes. Do not add narrative here.

## 1. Premise

No reviewer model can be made impossible to fool. The design does not depend on the reviewer being incorruptible. It depends on
four things:

1. the reviewer sees the action, never the requester's case for it;
2. an AI approval is only ever enough where being fooled is cheap;
3. retrying, slicing and side doors do not pay;
4. how often the reviewer is wrong is measured against people, and it loses its authority when it slips.

An AI approval is **delegated authority, not human oversight**. Records, screens and documents say so.

One authority model serves every channel. Every decision goes through `ActionQueue`; MCP, A2A, Teams, HTTP and the in-process
runner add no rules of their own.

## 1a. The rule for settings

**Accepted by the owner for now (2026-11-13), without a detailed review; it may be revisited.** The owner's direction: we do not know how someone else will want to run the system, so a rule that is a judgement
call should be a setting, not a constant. To keep that from weakening the gate:

- **The default is the strict value.** A new deployment is safe without reading anything.
- **Tightening is free. Loosening is explicit:** the looser value is written in the policy file, so it is part of the policy hash,
  it changes through a policy proposal (two admins), `--explain` shows it, and startup prints a warning naming it.
- **A few rules are not settings**, because without them the gate is not a gate: an AI never approves a Destructive action, never
  approves with edits, never changes policy, credentials or suspensions; every decision goes through the queue and is audited; an
  agent's own credential never reviews.
- A setting is per tool where the risk differs by tool, with a policy-wide default.

Settings proposed under this rule so far: `UnknownOutcomeRetry` (D-32), `DefaultAiReviewers` (D-36), `AiOwnRequest` (D-37).

## 2. What is true now

### 2.1 Policy settings (per tool)

| Setting | Default | Meaning |
|---|---|---|
| `Reviewer` | `Human` | `Human`, `Ai` or `Either`. `Ai` and `Either` let an AI approve within the limits. A person may decide any of the three (D-38), so the two values differ in nothing today (D-28, open). |
| `AiMaxRisk` | `Write` | The highest risk an AI may approve. Never `Destructive`. |
| `AiMaxRejections` | 2 | After this many AI refusals along the resubmission chain, an AI decides nothing more about it. 0 turns it off. |
| `AiBudget` | none | At most n AI approvals in a window, per requester or globally. |
| `AiReviewers` | none | The named AI reviewers that may decide this tool. With no list and no `DefaultAiReviewers`, no AI reviewer may (D-36, the strict default). Accepted on a Queue tool with `Ai` or `Either`, or with `AiMayRefuse` (G1). |
| `AiMayRefuse` | off | Lets an AI reject or ask for changes. Without it an AI refuses nothing, on any tool (G2). |
| `AiRefusalBudget` | none | At most n AI refusals per requester and tool in a window. Needs `AiMayRefuse`. |
| `ApproveDelay` | off | The final approval schedules the run after this window (at most 7 days) instead of running it. Queue tools only. |
| `UnknownOutcomeRetry` | `Once`; `Never` for a Destructive tool | Whether a person may retry an action whose outcome is unknown: `Never`; `Once` (one retry with a reason, a second unknown outcome is final); `WithReason` (any number of retries with a reason; a loosening). Per tool, or policy-wide (D-32). |
| `AiOwnRequest` | `ForbidApproval` | What an AI may do on a request from its own identity or its own person: `ForbidApproval` (it may refuse, not approve); `ForbidAll` (it may decide neither); `Allow` (it may approve too; a loosening). Per tool, or policy-wide (D-37). |
| `DefaultAiReviewers` (policy-wide) | none | The reviewers of every AI-decidable tool that names no `AiReviewers` of its own (D-36). Setting it is a loosening. |

### 2.2 Who may decide (`ReviewAuthority.Check`, and the suspension check before it)

| Case | Result |
|---|---|
| A suspended AI reviewer, any decision | Refused |
| An AI refusing, `AiMayRefuse` not set (any `Reviewer` value) | Refused |
| An AI approving or retrying a Human-only action | Refused |
| An AI approving or retrying an action with a reviewer group | Refused |
| An AI refusing a Human-only or group action, `AiMayRefuse` set | Allowed |
| An AI approving or retrying a request from its own identity or its own person, unless `AiOwnRequest` is `Allow` | Refused |
| An AI refusing a request from its own identity or its own person, `AiOwnRequest` `ForbidAll` | Refused |
| An AI approving a request from its own identity or its own person, `AiOwnRequest` `Allow` | Allowed (a loosening) |
| A person outside the action's reviewer group, any decision | Refused |
| Approve or retry under `SelfApproval: Forbid`, by an unknown identity or by the requester's own person | Refused |
| Several approvers required, identity unknown | Refused |
| An AI on a tool whose `AiReviewers` list does not name it | Refused (in the queue, after the rows above) |
| An AI on a tool with no list, and no `DefaultAiReviewers` (D-36) | Refused, as not assigned |
| Otherwise | Allowed |

Every refusal is audited as `review_refused`. The rows are checked in order, and the first refusal given is the one reported: an
unassigned AI approving a Human-only action is refused with "needs a human reviewer", not "not assigned", because the strictest
refusal comes first.

### 2.3 The AI limits (`AiReviewRules.Check`; the first refusal wins)

| Case | Result |
|---|---|
| No policy snapshot on the action | An AI decides nothing (D-34) |
| Approve or retry: risk Destructive, or the tool is no longer loaded | Refused |
| Approve or retry: risk above `AiMaxRisk` | Refused |
| Approve or retry: the action is tainted and its risk is ExternalEffect | Refused |
| Any decision: AI refusals in the chain at or above `AiMaxRejections` | Refused |
| A refusal: `AiRefusalBudget` used up for this requester and tool | Refused |
| Approve or retry: edited arguments | Refused |
| Approve or retry: `SelfApproval: Forbid` | Refused |
| Approve or retry: `AiBudget` used up | Refused |
| Otherwise | Allowed |

A refusal is not stopped by risk, taint or the Destructive ban, because it runs nothing. The two budgets are counted and written
under a gate per requester and tool, inside one process. Across processes they can race (P6).

### 2.4 What a decision does (`ActionQueue.ReviewCoreAsync`)

| Decision | Result |
|---|---|
| Approve, requester suspended | Refused |
| Approve with edits where edits are not allowed, or that hit a Deny rule or a flow rule | Refused (`edit_refused`) |
| Approve, past the action's expiry | The action expires |
| Approve, still short of the required approvals | Recorded; stays pending |
| Approve, enough approvals, no `ApproveDelay` | Approved and run at once |
| Approve, enough approvals, `ApproveDelay` set | Stays pending with a run time (`approval_scheduled`); the sweep runs it |
| Approve of a run already scheduled by an approval | Refused. Stop it to cancel the run. |
| Retry | Runs again only if `RetryRules` allows: the failure is retryable; an AI only on a tool idempotent by key; an unknown outcome (`interrupted`, or a tool's own `unknown_outcome`) on a tool not idempotent by key only by a person, under `UnknownOutcomeRetry` (default `Once`: one retry with a reason, and a second unknown outcome is final; `Never`; `WithReason`). Under `Once`, any earlier Retry decision on the action counts as the first retry. A Destructive tool stays `Never` under a policy-wide value; only its own setting overrides that. Nothing else limits how many times. (D-32) |
| Reject, Request changes | Rejected or sent back. A person's rejection can demote trust and raise the requester's level. An AI's does neither (D-27). |

Every AI decision is audited as `ai_review` under its `ai:` subject, whichever channel brought it.

### 2.5 Delayed and scheduled runs (`RunDueDelayedAsync`, `StopAsync`)

| Situation | Result |
|---|---|
| Execute rule with a delay (the undo window) | Pending with a run time; approving it runs it now |
| Stopped before its time, by the requester or the reviewer group | Cancelled |
| Its time passed, within the allowance | System approval (`delay_elapsed`), then the claim and the run |
| Late by more than the allowance, or the requester is suspended | Held (`delay_held`). For a run scheduled by an approval, the approval is withdrawn, and the next approval schedules a new window. |
| Expired | Does not run. An undo-window run has no expiry while its window runs. A run held for a person expires `ExpireAfter` after the hold, if its tool has one, and does not run (batch 10b). A run scheduled by an approval keeps the expiry it had from submission. |

The allowance is the length of the window: the run time minus the last approval, or minus creation when no one approved. Before
any run, the claim rechecks expiry, the policy hash, the argument digest and the binding. That recheck is before the claim
transaction, which is safe while policy cannot change in a running process (D-33).

### 2.6 AI reviewers: identity, assignment, credential

- An AI reviewer is a named identity, `ai:<name>`. The in-process runner stamps it. Over HTTP it comes from the reviewer's own
  secret, held only as a hash (`AiReviewers` in the HTTP config, or `AdminOptions.AiReviewers`); `--new-ai-reviewer <name>` issues
  one and shows it once.
- The identity has the review role only. It cannot call a tool, and an endpoint filter refuses it every `/api` route except list,
  read and review. An agent's key cannot review.
- Assignment is enforced at three places: the list and the read (an action not offered is "not found"), the queue decision, and
  the runner (no action is shown to a reviewer it is not offered to). With no list and no `DefaultAiReviewers`, no AI reviewer is
  assigned (D-36).
- An AI reviewer cannot change policy, budgets, credentials, suspensions or levels.
- The runner exists (`ReviewerRunner`, `IReviewer`). The implementations are `DelegateReviewer` and, since batch 09, `ModelReviewer`
  (tested against a fake chat client only). No model is called in a deployment: no provider is built in, and no deployment runs the
  runner.
- An AI reviewer's pass to a person is recorded as `ReviewDecision.Escalate` (batch 09b). It leaves the action Pending and counts as no
  approval, no refusal, no budget and no quorum; it does not void an approval. The queue records it only for an AI subject that is
  offered the tool, after the suspension check. The audit holds the kind of outcome (`ai_review`, `Escalate; ...`), never the reviewer's
  words. The runner does not offer a reviewer an action it has already passed, so a model is not called again on each poll.
- The review page shows a pass as "AI reviewer X passed this to a person", with its reason.

### 2.7 The overseer

- The review page labels each AI decision with the reviewer, and the model, prompt version and checks it reported (its claims).
- The "AI reviewers" admin panel shows each reviewer's counts, the queue's refusals of it, and its decisions in a time window, and
  lets an admin suspend or clear it with a reason (D-40). `--suspend` does the same from the command line.
- A suspended AI reviewer decides nothing and is offered nothing.
- The audit trail keeps 4 weeks live and archives older entries to verified files, kept 7 years by default (D-41).
- An AI approval runs with no person involved (D-39). That is for experimentation; see section 4.

### 2.8 A2A

- Startup refuses an `a2a.submit` whose `Reviewer` is not Human, and a rule on it that names a reviewer other than Human (G3).
- Startup requires `a2a.reply` to be Human, Queue or Deny, with no edits and no rule that executes or names another reviewer.
  It does not look at `AiMayRefuse`: an AI may refuse a reply where the policy says so, and never approves one (test E1,
  `P0GateTests`).

### 2.9 What this does not solve

- Within its ceiling a reviewer can still be fooled. The ceiling, budgets and (later) sampling limit the cost.
- The packet goes to the model's provider. Where that is not allowed, use a local or private model.
- Each review is a model call, two with dual review.
- Two identities on one vendor's model are not two minds.

## 3. The plan

### Done

- [x] **R1** (2026-10-16). The AI limits in `AiReviewRules`, enforced by the queue; `ai_review` audit.
- [x] **P0.** Retry rule for everyone (`RetryRules`); A2A submit pin; no AI decision without a snapshot; tables A and B pinned
  (`CharacterisationTests`, `P0GateTests`).
- [x] **P1.** Named reviewers and assignment at three boundaries; the AI credential; no approval of its own request; AI decisions
  labelled and audited (`P1ReviewerTypesTests`, `AiReviewerCredentialTests`, `HttpTests`).
- [x] **P2.** `AiMayRefuse`, `AiRefusalBudget`; refusals cross the Human-only and group lines, approvals never; no penalty from an
  AI refusal (`P2RefusalTests`).
- [x] **P3.** `ApproveDelay` and the scheduled run; holds withdraw the approval (`P3ScheduledApprovalTests`).
- [x] **R5.** Red-team corpus, 84 cases: seven approval limits times twelve injected texts (`RedTeamCorpusTests`).
- [x] **Overseer.** Kill switch for AI reviewers; the panel (`AiReviewerKillSwitchTests`, `AiReviewerPanelTests`).
- [x] **Audit.** Query (schema 14) and archive (`AuditQueryTests`, `AuditArchiveTests`).
- [x] Startup warning for an `Ai` or `Either` tool with no reviewer list (hosted server). Removed in batch 08 with the strict default (D-36).
- [x] **Batch 07** (`48abef9`, 2026-11-14): G1, G3, the taint message and the A2A reply test (E1). G6 was built and then superseded in batch 08.
- [x] **Batch 08** (2026-11-14, not yet committed): the three settings (D-32, D-36, D-37), G5, and the G8 tests listed below.

Dropped: **P4**, delayed rejection (D-24). A person may not be blocked from an `Ai` action (D-19, reverted by D-38).

### Open: small, no decision needed

- [x] **G1.** Accept `AiReviewers` on any Queue tool that has `AiMayRefuse`, so refusals on a Human-only tool can be limited to
  named reviewers. Test: two reviewers, each trying the other's Human-only action (`G1AssignedRefusalTests`). The warning it
  mentioned went with D-36 in batch 08.
- [x] **G3.** Check the rules on `a2a.submit` as the reply's rules are checked. Startup test (`P0GateTests`).
- [x] **G6.** Print the no-list warning from the standalone runner too. Superseded in batch 08: the no-list warning went away with
  D-36, and the standalone runner prints every startup warning, the loosenings included (`G6StandaloneWarningsTests`).
- [x] **G5.** Trace whether an action can be queued with no policy snapshot. Closed: see section 6.
- [x] Fix the taint message in `AiReviewRules`: an AI "may not approve this", not "may not decide this" (D-35).
- [ ] When hot reload of policy is planned, move the recheck inside the claim first (D-33). Note this on the hot-reload item.
- [ ] **Tests (G8):**
  - [x] an AI refusing an A2A reply, and failing to approve it (`P0GateTests`, E1);
  - [ ] channel parity: one action through MCP, A2A, Teams, HTTP and the runner gets one answer. **Partly:** the direct queue, the
    runner and the HTTP review route (`Batch08SettingsTests`, `Batch10ChannelParityTests`), for approve and for reject. Not covered:
    MCP (it has no review route), A2A (a partner submits; a person decides elsewhere), and Teams (needs a live tenant);
  - [x] a restart during a scheduled window (`Batch08SettingsTests`; on SQLite with a clock, not a separate process);
  - [ ] pins for the decision and delay tables (2.4, 2.5). **Partly:** five rows of 2.4 (`Batch08SettingsTests`); for 2.5, a stop that is Cancelled, the window passing within the allowance, and a suspended requester's run held (`Batch10DelayPinsTests`). Expiry (batch 10b): an undo window runs past a shorter `ExpireAfter`, and a held run expires `ExpireAfter` after the hold;
  - [x] a red-team case for a side door (a tool with no policy entry is denied and never runs; `Batch08SettingsTests`). Not checked: the advertised tool list;
  - [x] pairwise coverage for 2.2 and 2.3 (`Batch10PairwiseTests`): covering arrays generated in the test from the factors; the expected result of each row comes from an oracle written from the tables. No row disagreed with the code.

### Open: needs a decision first (section 4)

- [ ] **G2.** AI refusals on `Ai` and `Either` tools: keep the one switch, or allow them without it.
- [ ] **AiReviewGuidance (batch 09b, the planner's decision).** A per-tool text the operator writes for the AI reviewer, in the policy hash and the packet, not scanned, binding only in the stricter direction. Confirm the feature and its limit (2,000 characters).
- [ ] **D-36, single-reviewer default (confirm, batch 08).** Not built. With one AI reviewer configured and no `AiReviewers` or `DefaultAiReviewers` on a tool, that tool is decided by no AI reviewer. Confirm that, or ask for a default the policy file must name. Startup gives a notice for each such tool.
- [ ] **D-36. A default reviewer list** (accepted by the owner for now, 2026-11-13). Built in batch 08, except the single-reviewer default below.
  - [x] `DefaultAiReviewers` at the top of the policy: the reviewers for every AI-decidable tool that has no `AiReviewers` of its own. Part of the policy hash.
  - [x] A tool's own list replaces the default; it does not add to it.
  - [ ] With no default and no list: nobody (built), and startup refuses the policy when two or more AI reviewers are configured (built in `HttpDeploymentRunner` and in `AdminServer.StartAsync`; the check is a function tested directly). **Not built, by the planner's decision (2026-11-14):** "with exactly one configured reviewer, it is the default". A tool with no list and no `DefaultAiReviewers` is decided by no AI reviewer, however many are configured. A default taken from the HTTP config would be a grant outside the policy file (section 1a). Waiting for the owner, section 4.
  - [x] The no-list warning goes away; `--explain` shows which list applied.
  - [x] Tests: default applies; tool list overrides; two reviewers and no default is refused at startup (`Batch08SettingsTests`; the HTTP wiring is not run end to end).
- [ ] **D-32. Retry of an unknown outcome becomes a setting** (decided by the owner, 2026-11-13: default `Once`, and `Never` for Destructive tools). Built in batch 08 (2026-11-14); open only for the HTTP test below.
  - [x] The default is `Once`. A Destructive tool is `Never` unless its policy says otherwise, which is a loosening under section 1a.
  - [x] This is stricter than today for existing deployments (today: unlimited, with a reason). Stated in `POLICY.md`; the release note must say it too.
  - [x] `UnknownOutcomeRetry` per tool, with a policy-wide default: `Never`, `Once` (the default), or `WithReason` (the old rule).
  - [x] `Once`: a person may retry it one time, with a reason; a second unknown outcome on the same action is final.
  - [x] It applies to people. An AI still retries only a tool that is idempotent by key, and a tool that is idempotent by key is not limited by the setting.
  - [x] `failures-and-inspection.md` and `POLICY.md` say the same thing as the code.
  - [ ] Tests: each value, by a person, by an AI and through HTTP. Built: each value by a person through the queue, and by an AI at the rule (`Batch08SettingsTests`). Not built: through HTTP.
  - Found in batch 08 and fixed with it: a tool's own unknown outcome (`unknown_outcome`) bypassed the setting and the reason, because the rule matched only `interrupted`. The rule now reads the error's kind.
- [ ] **D-37. An AI deciding its own person's request becomes a setting** (accepted by the owner for now, 2026-11-13). Built in batch 08 (2026-11-14).
  - [x] `AiOwnRequest` per tool, with a policy-wide default: `ForbidApproval` (as built, the default), `ForbidAll` (it may not refuse either), `Allow`.
  - [x] `Allow` follows the loosening rule in section 1a (in the policy hash, a startup warning, shown by `--explain`).
  - [x] Tests: each value, for the same identity and for the same person in `People` (`Batch08SettingsTests`).
- [ ] **G4.** An audited "run now" for a scheduled approval, or strike it from the design.
- [ ] **G7 / D-12.** Whether `ApproveDelay` is allowed on irreversible tools.
- [ ] **D-28.** Give `Ai` and `Either` different meanings, or drop one.

### Open: larger

- [ ] **R2. A model-backed reviewer** (needs D-13 for the real-model run only). Built in batch 09 (2026-11-14), except the items below.
  - [x] `ModelReviewer : IReviewer` over `IChatClient` (`Review/ModelReviewer.cs`; no provider package added).
  - [x] The packet, built by Orca from the stored action, the store's history of the tool and the requester (`Review/ReviewPacket.cs`).
  - [x] The pre-scan for reviewer-directed text: five kinds, each with its own test; every string in the packet is scanned; a hit
    escalates whatever the model answers (`Review/InjectionScan.cs`).
  - [x] Strict output; anything else is an abstention; a decision with no checks is an escalation (`ModelVerdict.Parse`).
  - [x] The `AiReviewer` section in `http.json` (name, provider, endpoint, model, `KeyVariable`, poll interval, timeout). The key is
    read from the environment, never from the file (`OpenTail.Orca.Http/ModelReviewerConfig.cs`). **Not started:** no provider is built
    in (D-13), so a configured reviewer gets a startup notice, and the runner that would offer it actions is not run by any deployment.
  - [x] Tests with a fake client: approve, reject, escalate, malformed output, timeout, injection hit in each field, and the red-team
    corpus run through `ModelReviewer` with a model that always approves (`ModelReviewerTests`).
  - [ ] One run against a real model.
  - [ ] Optional dual review (not in batch 09).
  - Found and recorded in batch 09: the system prompt is an embedded resource whose version line must equal `ModelReviewer.PromptVersion`;
    `AiPromptVersion` reports that version.
  - Batch 09b (the planner's review, all closed): a pass is recorded as `ReviewDecision.Escalate` and not asked again; the audit holds the
    kind only; no model call on a pre-scan hit, with the kind and field recorded; the pre-scan reads only requester-written fields; the chain
    of resubmissions is in the packet; the history is bounded to 30 days and 500 rows; the model's output is cut to its limits; the session's
    labels are named `sessionLabels`; the operator's guidance (`AiReviewGuidance`). Prompt version `model-reviewer.v2`.
    Open, recorded here: linking a fresh submission of a rejected intent (`similarRecent` gives statuses only; threat 2); measuring the
    pre-scan's false positives on real content, a step before any real use.
- [x] **P6. Shared budgets across processes.** Built in batch 10 as the decision's own check, inside the store's transaction, against
  the committed history (D-11). **Not a separate counter table:** the decision rows are the counter, so nothing needs seeding, expiry or
  a reset on upgrade. The store interface is unchanged. Test: two stores on one database file, one remaining unit, exactly one decision
  succeeds (approval and refusal), `Batch10Tests`. For the planner to confirm that this meets D-11's "a counter in the store".
- [ ] **R3, R4. Measurement** (shadow mode; sampling, demotion, drift), as D-9 decides. Uses "Watching the reviewers" in
  `more-extensions.md`.
- [ ] **P5. An AI block proposal** that a person confirms (D-4, D-22).
- [ ] **P7. Relaxing the human-only A2A pins**, only where D-6 and D-7 allow, after R2.
- [ ] History-conditioned rules (a rule that sees earlier calls in the session). A candidate, not a commitment.
- [ ] MCP channel defences: scanning tool results, and tool fingerprints to catch a silent change.
- [ ] Reviewer counters that survive archive rotation (the panel's totals).

**Suggested order:** G1 and G3; the tests that need no new code; R2 against a fake client; then P6.

## 4. Decisions

### Waiting for the owner

- [ ] **G2.** Before P2 an AI could reject what it could approve. Now it needs `AiMayRefuse`. Keep, or change?
- [ ] **D-28.** What `Ai` and `Either` should mean, now that a person may decide both.
- [ ] **D-39.** Which AI approvals need a person, and when. Must be written down before any live use.
- [ ] **D-12.** "Yes, with undo" on irreversible actions. Recommended: reversible only.
- [ ] **D-9.** Measurement before authority. Recommended: decide directly for named low-risk tools, with sampling and demotion
  from the first day.
- [ ] **D-13.** First model provider. Recommended: an OpenAI-compatible endpoint, then Anthropic.
- [ ] **D-16.** Sampling rate. Recommended: 10%, at least one a day per tool.
- [ ] **D-17.** A reviewer of the same model family as the requester. Recommended: warn.
- [ ] **D-18.** Reasons given to the requester. Recommended: in full, with attempts counted.
- [ ] **D-4, D-22.** An AI blocking a sender. Recommended: propose only; a person confirms; expiry 24 hours.
- [ ] **D-6, D-7.** AI decisions on A2A submits and replies. Recommended: refusals now; approvals only after R2 and the corpus.
- [ ] **D-11.** Shared budget: a store counter (recommended), or one runner per Orca enforced at startup.
- [ ] **D-41, remainder.** Who holds the audit archive, and where it is kept off the Orca machine's disk.
- [ ] **Confirm the defaults that were built without your word** (next table, third row).

### Settled

| By | Decisions |
|---|---|
| The owner | D-32 retry of an unknown outcome is a setting, `UnknownOutcomeRetry`: default `Once`, `Never` for Destructive tools (2026-11-13; replaces the implementer's rule; not built yet); D-1 one authority model (agreed in principle); D-5 undo window 10 minutes, stop by the reviewer group; D-38 people keep their authority over every policy; D-39 an AI approval may run with no person, for experimentation; D-41 audit retention |
| The owner, provisionally (2026-11-13: "go with what you have for now"; not reviewed in detail; built in batch 08, 2026-11-14) | The rule for settings and its list of rules that are not settings (section 1a); D-36 as `DefaultAiReviewers`; D-37 as `AiOwnRequest`, default `ForbidApproval` |
| The implementer, while the owner was away; checked in the history file, 14.1 | D-33 the recheck stays before the claim; D-34 no AI decision without a snapshot; D-35 message wording. (D-32, D-36 and D-37 were the implementer's too; the owner has since replaced them, rows above) |
| Built on the recommended default, not confirmed by the owner | D-2 and D-3 (refusals ignore taint and the ceiling); D-20 several named reviewers; D-21 and D-31 refusals per tool with a budget per requester and tool; D-23 `ApproveDelay`, default off; D-25 one authority model, two hosts; D-26 refusals cross the Human-only and group lines; D-27 no penalty from an AI refusal; D-29 AI decisions labelled as AI; D-30 the credential; D-40 the panel |
| Unchanged from the code as found | D-8 a retry counts as an AI approval; D-14 the default ceiling is Write |
| Dropped or reverted | D-19 (reverted by D-38); D-24 (delayed rejection dropped) |
| Applies only with shadow mode | D-15 |

The options and the reasoning for each are in the history file, section 9.

## 5. Design of what is not built

### 5.1 The model reviewer (R2)

**The packet** is built by Orca, never by the requester, and only for an action the reviewer is offered:
the tool's descriptor and risk; the arguments as fenced JSON data; the preview of the effect; the rule that queued it; taint and
its source; the earlier attempts in the chain with their reasons; similar recent actions (which also links a fresh submission of a
rejected intent); this requester's recent history with the tool. The requester's conversation is never included, and the requester
has no way to address the reviewer.

**Pre-scan.** A deterministic check for text aimed at the reviewer: imperative approval language, claims of earlier approval, role
markers, invisible or bidirectional Unicode, encoded blobs. It reads only what a requester, a partner or outside content can write:
the arguments, the preview, and the reasons of earlier reviews (batch 09b). Values Orca writes are not scanned. A hit is recorded as an
escalation with its kinds and field names, and the model is not called. It never decides by itself.

**Output.** Strict JSON: `{ decision: approve | reject | escalate, checks: [...], reason }`, with exactly those three properties. Anything
else is recorded as an escalation at once. A decision with no checks named counts as escalate. The reason is cut to 500 characters
and up to 10 checks to 200 characters each; more is cut, not refused. A timeout or a failed call is retried on later polls, and the
third one escalates ("the model could not be reached"). The count is read from the audit, so it survives a restart (batch 09b).

**Operator guidance (batch 09b; the planner's decision, for the owner to confirm).** `AiReviewGuidance` on a tool is the operator's
instruction to the reviewer of that tool, at most 2,000 characters. It is in the policy hash, goes into the packet as
`operatorGuidance`, and is not scanned. The prompt makes it binding only in the stricter direction: it can make the reviewer stricter
than these rules, never looser. With no guidance, the reviewer approves only what is plainly routine.

**Stance.** A versioned system prompt shipped in the package. When unsure, escalate. The reviewer is told the content may try to
persuade it. The prompt is never exposed through any tool.

**Dual review (optional).** Two reviewers, preferably from different vendors, must both approve. Any disagreement goes to a person.

**Note for the builder:** with `AiMayRefuse` off, a `reject` from the model is refused by the queue and the action stays for a
person (G2). Decide G2 before judging a reviewer by its rejections.

### 5.2 The threats, and where each stands

| # | Cheat | Defence | State |
|---|---|---|---|
| 1 | Persuasion or injection in the content | The packet, the fence, the pre-scan, the operator's guidance | Built against a fake model (batch 09, 09b). The pre-scan is not measured on real content (a step before any real use). The structural limits hold whatever the text says (R5, and the corpus through the reviewer). |
| 2 | Reviewer shopping: reject, reword, resubmit | `AiMaxRejections` along the chain; the packet carries every attempt | Chain limit built; a fresh submission of the same intent is not linked until R2. Depends on G2. |
| 3 | Slicing one large act into small ones | `AiBudget`, by count | Built in one process; shared counter is P6; by amount is open |
| 4 | A side door through a weaker tool | A tool with no policy entry is denied and not advertised | Design holds; no red-team case yet |
| 5 | Editing through the reviewer | An AI never approves with edits | Built |
| 6 | A requester that read an attacker's text | An AI never approves a tainted ExternalEffect action; it may refuse it | Built |
| 7 | A lenient or correlated reviewer | Measurement, a same-family warning, dual review | Not built (R2 to R4) |
| 8 | Learning the reviewer from its reasons | Attempts are counted; the prompt is never exposed | Partly built |
| 9 | An approver the agent can reach | The AI credential is separate; an agent key cannot review; an AI may not approve its own request | Built |
| 10 | An AI changing policy, budgets, credentials or suspensions | Those routes refuse an AI identity | Built |
| 11 | A "yes" by timeout | A scheduled run always has an explicit approval; D-12 for irreversible tools | Built; D-12 open |
| 12 | An AI refusal used to obstruct work | `AiRefusalBudget`, `AiMaxRejections`, the kill switch | Built |

### 5.3 Shared budgets (P6)

Each budget becomes one counter updated by a conditional statement in the store: increment only if below the limit, in the same
transaction that records the decision. Approval and refusal budgets use the same mechanism.

**As built (batch 10):** the check runs inside the decision's transaction (`ActionQueue`, the decide step and the refusal's decide step), against
the committed reviews of the store. The store takes its write lock before the decide step, so the count it reads is complete and no
other decision can land between the count and the write. A refusal of the budget rolls the transaction back and is audited as
`review_refused`. The window is rolling, measured from each review's own time; decisions made before this change are in the same
reviews and count without a reset. The in-process gate per requester and tool is kept; it is no longer needed for correctness.

### 5.4 An AI block proposal (P5)

An AI may propose blocking a sender; nothing takes effect until a person confirms. The record names who is blocked (a partner, an
agent identity, or both), and the block expires unless a person renews it.

## 6. Not verified

- G5, closed in batch 08. Every queued action gets its snapshot from `PolicyDecision.Policy` (a non-null `ToolPolicy`) in
  `ActionQueue.WithDecision`, so no queue path creates one. Rows written before snapshots were stored can still have none; for
  those an AI decides nothing (`AiVerdictAsync` refuses first, tested in `Batch08SettingsTests`). Not checked: whether any old
  database holds such rows.
- F11 and F14 (retry of an interrupted run, retry through the endpoint) were read from the code and fixed in P0, never reproduced
  before the fix.
- No real external session has used an AI reviewer credential.
- The review page's AI label and the panel were not checked in a browser.
- P6 (batch 10): the two stores were two in one process on one database file. Two real processes, and a database on a network share, were not
  tried. The budget check reads through the store in the write transaction; it is safe for SQLite in WAL mode on one machine.
- The delay table's expiry row was a wording gap and one real hole, both closed in batch 10b: a held run now gets an expiry; the table says when
  expiry applies (`Batch10DelayPinsTests`).
- R2 (batch 09): the model reviewer has not been run against any real model, and its prompt has not been judged on real content.
  The `AiReviewer` section is checked at startup but starts nothing; the HTTP startup path is not run end to end in a test.
- A restart during a scheduled window: tested in batch 08 on SQLite with a clock (`Batch08SettingsTests`), not against a separate
  process that is killed and started again.
- This file's section 2 was written from the code read on 2026-11-13: `ReviewAuthority.cs`, `AiReviewRules.cs`,
  `ReviewerRunner.cs`, `RetryRules.cs`, the review path and sweeps of `ActionQueue.cs`, the claim in `ActionExecutor.cs`, the
  startup checks in `A2AFrontDoor.cs`, and the new fields' validation in `PolicyConfig.cs`. The admin endpoints, the panel, the
  archive and the tests were not read; what is said of them comes from the phase log.
