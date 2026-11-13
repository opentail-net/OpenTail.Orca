# Reviewer gate: AI reviewers on Orca

**This is the current design and the plan.** It says what is true in the code now, what is open, and what waits for the owner.
Last checked against the code at `32848f5` (2026-11-13).

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

The owner's direction (2026-11-13): we do not know how someone else will want to run the system, so a rule that is a judgement
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
| `AiReviewers` | none | The named AI reviewers that may decide this tool. With no list, any AI reviewer may (D-36). Accepted only on a Queue tool with `Ai` or `Either` (G1). |
| `AiMayRefuse` | off | Lets an AI reject or ask for changes. Without it an AI refuses nothing, on any tool (G2). |
| `AiRefusalBudget` | none | At most n AI refusals per requester and tool in a window. Needs `AiMayRefuse`. |
| `ApproveDelay` | off | The final approval schedules the run after this window (at most 7 days) instead of running it. Queue tools only. |

### 2.2 Who may decide (`ReviewAuthority.Check`, and the suspension check before it)

| Case | Result |
|---|---|
| A suspended AI reviewer, any decision | Refused |
| An AI refusing, `AiMayRefuse` not set (any `Reviewer` value) | Refused |
| An AI approving or retrying a Human-only action | Refused |
| An AI approving or retrying an action with a reviewer group | Refused |
| An AI refusing a Human-only or group action, `AiMayRefuse` set | Allowed |
| An AI approving or retrying a request from its own identity or its own person | Refused |
| A person outside the action's reviewer group, any decision | Refused |
| Approve or retry under `SelfApproval: Forbid`, by an unknown identity or by the requester's own person | Refused |
| Several approvers required, identity unknown | Refused |
| An AI on a tool whose `AiReviewers` list does not name it | Refused (in the queue, after the rows above) |
| Otherwise | Allowed |

Every refusal is audited as `review_refused`.

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
| Retry | Runs again only if `RetryRules` allows: the failure is retryable; an AI only on a tool idempotent by key; an unknown outcome on other tools only by a person who gives a reason. Nothing limits how many times (D-32). |
| Reject, Request changes | Rejected or sent back. A person's rejection can demote trust and raise the requester's level. An AI's does neither (D-27). |

Every AI decision is audited as `ai_review` under its `ai:` subject, whichever channel brought it.

### 2.5 Delayed and scheduled runs (`RunDueDelayedAsync`, `StopAsync`)

| Situation | Result |
|---|---|
| Execute rule with a delay (the undo window) | Pending with a run time; approving it runs it now |
| Stopped before its time, by the requester or the reviewer group | Cancelled |
| Its time passed, within the allowance | System approval (`delay_elapsed`), then the claim and the run |
| Late by more than the allowance, or the requester is suspended | Held (`delay_held`). For a run scheduled by an approval, the approval is withdrawn, and the next approval schedules a new window. |
| Expired | Does not run |

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
  the runner (no action is shown to a reviewer it is not offered to).
- An AI reviewer cannot change policy, budgets, credentials, suspensions or levels.
- The runner exists (`ReviewerRunner`, `IReviewer`). The only implementation is `DelegateReviewer`. **No model-backed reviewer
  exists**, and no deployment runs the runner.

### 2.7 The overseer

- The review page labels each AI decision with the reviewer, and the model, prompt version and checks it reported (its claims).
- The "AI reviewers" admin panel shows each reviewer's counts, the queue's refusals of it, and its decisions in a time window, and
  lets an admin suspend or clear it with a reason (D-40). `--suspend` does the same from the command line.
- A suspended AI reviewer decides nothing and is offered nothing.
- The audit trail keeps 4 weeks live and archives older entries to verified files, kept 7 years by default (D-41).
- An AI approval runs with no person involved (D-39). That is for experimentation; see section 4.

### 2.8 A2A

- Startup refuses an `a2a.submit` whose `Reviewer` is not Human. Its rules are not checked (G3).
- Startup requires `a2a.reply` to be Human, Queue or Deny, with no edits and no rule that executes or names another reviewer.
  It does not look at `AiMayRefuse`, so an AI may refuse a reply where the policy says so. No test covers that.

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
- [x] Startup warning for an `Ai` or `Either` tool with no reviewer list (hosted server).

Dropped: **P4**, delayed rejection (D-24). A person may not be blocked from an `Ai` action (D-19, reverted by D-38).

### Open: small, no decision needed

- [ ] **G1.** Accept `AiReviewers` on any Queue tool that has `AiMayRefuse`, so refusals on a Human-only tool can be limited to
  named reviewers. Include those tools in the startup warning. Test: two reviewers, each trying the other's Human-only action.
- [ ] **G3.** Check the rules on `a2a.submit` as the reply's rules are checked. Startup test.
- [ ] **G6.** Print the no-list warning from the standalone runner too.
- [ ] **G5.** Trace whether an action can be queued with no policy snapshot. If it cannot, record that and close.
- [ ] Fix the taint message in `AiReviewRules`: an AI "may not approve this", not "may not decide this" (D-35).
- [ ] When hot reload of policy is planned, move the recheck inside the claim first (D-33). Note this on the hot-reload item.
- [ ] **Tests (G8):**
  - [ ] an AI refusing an A2A reply, and failing to approve it;
  - [ ] channel parity: one action through MCP, A2A, Teams, HTTP and the runner gets one answer;
  - [ ] a restart during a scheduled window;
  - [ ] pins for the decision and delay tables (2.4, 2.5);
  - [ ] a red-team case for a side door (a tool with no policy entry);
  - [ ] pairwise coverage for 2.2 and 2.3.

### Open: needs a decision first (section 4)

- [ ] **G2.** AI refusals on `Ai` and `Either` tools: keep the one switch, or allow them without it.
- [ ] **D-36. A default reviewer list** (owner's direction, 2026-11-13; the details below wait for a yes).
  - [ ] `DefaultAiReviewers` at the top of the policy: the reviewers for every AI-decidable tool that has no `AiReviewers` of its own. Part of the policy hash.
  - [ ] A tool's own list replaces the default; it does not add to it.
  - [ ] With no default and no list: nobody, and startup refuses the policy when two or more AI reviewers are configured. With exactly one configured reviewer, it is the default (so today's single-reviewer policies keep working).
  - [ ] The no-list warning goes away; `--explain` shows which list applied.
  - [ ] Tests: default applies; tool list overrides; two reviewers and no default is refused at startup.
- [ ] **D-32. Retry of an unknown outcome becomes a setting** (owner's direction, 2026-11-13; the details below wait for a yes).
  - [ ] `UnknownOutcomeRetry` per tool, with a policy-wide default: `Never` (the failures design), `Once`, or `WithReason` (as built).
  - [ ] `Once`: a person may retry it one time, with a reason; a second unknown outcome on the same action is final.
  - [ ] It applies to people. An AI still retries only a tool that is idempotent by key, and a tool that is idempotent by key is not limited by the setting.
  - [ ] `failures-and-inspection.md` and `POLICY.md` say the same thing as the code.
  - [ ] Tests: each value, by a person, by an AI and through HTTP.
- [ ] **D-37. An AI deciding its own person's request becomes a setting** (owner's direction, 2026-11-13; details wait for a yes).
  - [ ] `AiOwnRequest` per tool, with a policy-wide default: `ForbidApproval` (as built, the default), `ForbidAll` (it may not refuse either), `Allow`.
  - [ ] `Allow` follows the loosening rule in section 1a (in the policy hash, a startup warning, shown by `--explain`).
  - [ ] Tests: each value, for the same identity and for the same person in `People`.
- [ ] **G4.** An audited "run now" for a scheduled approval, or strike it from the design.
- [ ] **G7 / D-12.** Whether `ApproveDelay` is allowed on irreversible tools.
- [ ] **D-28.** Give `Ai` and `Either` different meanings, or drop one.

### Open: larger

- [ ] **R2. A model-backed reviewer** (needs D-13 for the real-model run only; the rest can start now).
  - [ ] `ModelReviewer : IReviewer` over `IChatClient` (`Microsoft.Extensions.AI.Abstractions` is already referenced).
  - [ ] The packet, built by Orca (section 5.1).
  - [ ] The pre-scan for reviewer-directed text.
  - [ ] Strict output; anything else is an abstention.
  - [ ] The `AiReviewer` section in `http.json` (provider, endpoint, model, the name of the key's environment variable, poll
    interval, timeout).
  - [ ] Tests with a fake client: approve, reject, escalate, malformed output, timeout, injection hit.
  - [ ] One run against a real model.
  - [ ] Optional dual review.
- [ ] **P6. Shared budgets across processes.** One conditional counter in the store, incremented in the decision's transaction
  (D-11). Test: two processes, one remaining unit, exactly one succeeds.
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
- [ ] **D-36.** The owner's direction (2026-11-13): a default reviewer is specified, so a tool with no list is not open to every reviewer. To confirm: the shape in section 3 (`DefaultAiReviewers`; a tool's list replaces it; a single configured reviewer is the default; otherwise a missing default is a startup error).
- [ ] **D-32.** The owner's direction (2026-11-13): make it a setting, with "never" as one option. To confirm: the default. **As built there is no limit on the number of retries**: a person may retry an unknown outcome every time it happens, with any reason. "Once" would be a new, stricter default. Recommended: `Once` as the default, `Never` for Destructive tools.
- [ ] **D-37.** The owner's direction (2026-11-13): a setting. To confirm: the three values in section 3, with today's rule as the default. It still does not catch one model behind two identities: Orca does not know which model a requester runs on, so D-17 can only compare what each side declares.
- [ ] **Section 1a.** Confirm the rule for settings, and the list of rules that are not settings.
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
| The owner | D-1 one authority model (agreed in principle); D-5 undo window 10 minutes, stop by the reviewer group; D-38 people keep their authority over every policy; D-39 an AI approval may run with no person, for experimentation; D-41 audit retention |
| The implementer, while the owner was away; checked in the history file, 14.1 | D-32 retry rule; D-33 the recheck stays before the claim; D-34 no AI decision without a snapshot; D-35 message wording; D-36 no list means any AI reviewer; D-37 no approval of its own request |
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
markers, invisible or bidirectional Unicode, encoded blobs in plain-text fields. A hit goes into the packet and makes the reviewer
escalate. It never decides by itself.

**Output.** Strict JSON: `{ decision: approve | reject | escalate, checks: [...], reason }`. Anything else, a timeout or a refusal
to answer is an abstention. A decision with no checks named counts as escalate.

**Stance.** A versioned system prompt shipped in the package. When unsure, escalate. The reviewer is told the content may try to
persuade it. The prompt is never exposed through any tool.

**Dual review (optional).** Two reviewers, preferably from different vendors, must both approve. Any disagreement goes to a person.

**Note for the builder:** with `AiMayRefuse` off, a `reject` from the model is refused by the queue and the action stays for a
person (G2). Decide G2 before judging a reviewer by its rejections.

### 5.2 The threats, and where each stands

| # | Cheat | Defence | State |
|---|---|---|---|
| 1 | Persuasion or injection in the content | The packet, the fence, the pre-scan | Not built (R2). The structural limits hold whatever the text says (R5). |
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

### 5.4 An AI block proposal (P5)

An AI may propose blocking a sender; nothing takes effect until a person confirms. The record names who is blocked (a partner, an
agent identity, or both), and the block expires unless a person renews it.

## 6. Not verified

- Whether an action can be queued with no policy snapshot (G5).
- F11 and F14 (retry of an interrupted run, retry through the endpoint) were read from the code and fixed in P0, never reproduced
  before the fix.
- No real external session has used an AI reviewer credential.
- The review page's AI label and the panel were not checked in a browser.
- A restart during a scheduled window: the schedule is in the stored record, and no test restarts Orca.
- This file's section 2 was written from the code read on 2026-11-13: `ReviewAuthority.cs`, `AiReviewRules.cs`,
  `ReviewerRunner.cs`, `RetryRules.cs`, the review path and sweeps of `ActionQueue.cs`, the claim in `ActionExecutor.cs`, the
  startup checks in `A2AFrontDoor.cs`, and the new fields' validation in `PolicyConfig.cs`. The admin endpoints, the panel, the
  archive and the tests were not read; what is said of them comes from the phase log.
