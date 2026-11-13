# Reviewer gate: the plan for AI reviewers on the Host

Status: **the one plan for the Host's reviewer gate.** It merges the earlier gate proposal (2026-11-13) with `docs/MCP-HOST-V6-PLAN.md`, which is now a pointer to this file. It has been revised against an external review of the merged draft (section 13). Phase R1 of V6 is done; everything else is proposed and nothing else is built (section 8). Owner decisions are in section 9, and three of them block implementation (section 0). Line numbers are from reads on 2026-11-13. Section numbers are kept, because other documents cite them.

## 0. The three questions that must be settled first

The external review asked three questions, and implementation should not start until they are answered. The answers below are this plan's recommendations. The owner confirms them in section 9.

1. **What can an AI reviewer reject, including actions configured as Human-only?** Any action the AI is *assigned* to, when the policy allows AI refusals (`AiMayRefuse`, D-21). An AI may never approve a Human-only action, and never approve a reviewer-group action. A refusal and an approval are two separate authorities, and this plan treats them that way (5.4).
2. **What distinguishes the five verdicts?** For the first release: yes now (approve), no now (refuse), and yes with undo (approve after a window). "No with undo" is deferred, and so is "no and block the sender", which needs a human to confirm it (5.3). Each scheduled outcome is typed, so a rejection can never run as an approval (5.3, 5.6).
3. **How is an AI reviewer restricted to exactly the actions it may review?** Each AI reviewer is a named identity with an explicit list of assigned policies or tools. It can list, read and decide only assigned actions. Several named reviewers are allowed, each with its own assignment (5.2, D-20).

## 1. What is asked, and the premise

**Premise (from V6, section 1).** No reviewer model can be made impossible to fool. The design therefore does not depend on the reviewer being incorruptible. It depends on four things:

1. the reviewer sees the action, never the requester's case for it;
2. an AI approval is only ever enough where being fooled is cheap;
3. retrying, slicing and side doors do not pay;
4. how often the reviewer is wrong is measured against people, continuously, and it loses its authority when it slips.

**What the owner asks (2026-11-13).**
- One reviewer gate for every tool and every channel (MCP, A2A, and any later channel). Agreed in principle.
- AI reviewers may decide actions, within limits. The owner chooses, per action, whether to approve directly or to oversee the AI.
- Five verdicts for an AI reviewer: yes now, yes with undo, no now, no with undo, and no and block the sender. Section 0 says which ones ship first.
- The owner's time is the scarce resource. Approval by a person should be the exception, not each step.

**The one tension to settle.** V6 puts measurement before authority: shadow mode, then sampling, then demotion. The owner's stated view is "AI saves time" and "I am not training AI". Decision D-9 settles this.

**Scope.** The Host's reviewer gate. The Teams bot's own behaviour, the pair of sessions and the A2A kit are out of scope, except where they touch the gate.

## 2. Contracts this must satisfy

| # | Member | Where | Promise | Who relies on it |
|---|---|---|---|---|
| 1 | `ReviewAuthority.Check(r, decision, subject, groups, people)` | `OpenTail.Orca/Policy/ReviewAuthority.cs:52-80` | Says who may make this decision. Refuses an AI on a Human-only action (57-59), **whatever the decision**. Refuses an AI on a group action (61-63), **whatever the decision**. Refuses a non-member of the group (64). Refuses self-approval under `Forbid` (67-74). Refuses an unknown identity on a several-approver action (76-77). | `ActionQueue.ReviewAsync`, before any state change |
| 2 | `AiReviewRules.Check(r, decision, risk, editsArguments, history)` | `OpenTail.Orca/Policy/AiReviewRules.cs:22-48` | The AI limits. Pure. Order: destructive, risk ceiling, taint, refusals, then edits, four eyes and budget for approvals. The ceiling and taint apply to **refusals too** (27-32). | `ActionQueue.AiVerdictAsync`; `ReviewerRunner` |
| 3 | `AiReviewRules.IsAiRefusal`, `IsAiApproval` | `AiReviewRules.cs:60-64` | Which past reviews count as AI refusals and AI approvals. `Retry` counts as an approval. | `AiHistoryAsync` |
| 4 | `ActionQueue.ReviewAsync(actionId, review)` | `OpenTail.Orca/Runtime/ActionQueue.cs`, from about line 360 | The only path that changes an action's decision. Stamps edit digests, runs `ReviewAuthority` and then the AI check, then switches on the decision. The Retry branch (497-501) does **not** check `Retryable` or the failure kind (F14). | `AdminServer` `/actions/{id}/review` (`AdminServer.cs:241`, which accepts `retry`, 260); `TeamsBot.cs:281`; `ReviewerRunner.cs:60` |
| 5 | `ActionQueue.AiVerdictAsync`, `AiHistoryAsync` | `ActionQueue.cs:529-560` | Adds store history to `AiReviewRules.Check`: refusals up the `ParentActionId` chain, and approvals inside the `AiBudget` window. | `ReviewAsync`; `ReviewerRunner` |
| 6 | `ActionQueue.StopAsync`, `RunDueDelayedAsync`, `ExpireDueAsync` | `ActionQueue.cs` around 596-660 | A delayed action is `Pending` with `RunAt`. `RunDueDelayedAsync` computes the late allowance as `RunAt - CreatedAt` (about line 608), moves the action to Approved and calls `ClaimAndRunAsync` (about 624). Stop is open to the requester or the reviewer group. | Scheduler; `AdminServer` `/actions/{id}/stop` (`AdminServer.cs:307`) |
| 7 | `IReviewer` | `OpenTail.Orca.Abstractions/Actions/Interfaces.cs:124-128` | `Name`, and `ReviewAsync(action)` returning a review or null (abstain). | `ReviewerRunner` only. The only implementation in the projects read is `DelegateReviewer` (`ReviewerRunner.cs:13`). |
| 8 | `ReviewerRunner.RunOnceAsync` | `OpenTail.Orca/Runtime/ReviewerRunner.cs:28-62` | Offers only actions whose snapshot says `Ai` or `Either` (35) and that `AiVerdictAsync` allows (36). Stamps the identity as `ai:<name>` (54), and the input hash. Audits `ai_review`. | Runs in-process only |
| 9 | `Subjects.Ai`, `Subjects.IsAi` | `OpenTail.Orca.Abstractions/Identity/CallerIdentity.cs:28-38` | `ai:<name>` is the AI subject. `IsAi` is the prefix test the rules use. | Every AI rule |
| 10 | HTTP reviewer identity | `AdminServer.cs:141-160`, roles at `AdminServer.cs:51-60` | A reviewer token yields `admin:<name>` (155-157). Roles are `opentail.review`, `opentail.admin` and `group:*`. No HTTP credential yields `ai:`. | The review endpoint |
| 11 | `ToolPolicy` defaults | `OpenTail.Orca.Abstractions/Policy/ToolPolicy.cs` | `Reviewer` Human (46). `RequiredApprovals` 1 (55). `SelfApproval` Allow (57). `DefaultDelay` 10 minutes (71). `AiMaxRisk` null, which means Write (90, and `AiReviewRules.cs:16`). `AiMaxRejections` 2 (93). `AiBudget` null (96). | Everything above |
| 12 | `PolicyConfig` validation | `OpenTail.Orca/Policy/PolicyConfig.cs:215-258` | Reviewer, group, approvals and expiry only on Queue rules (223-224). A delay only on Execute (217, 246-247). `AiMaxRisk` not Destructive (254-255). `AiBudget` needs positive values (257-258). | Startup |
| 13 | `PolicyResolver` delays | `OpenTail.Orca/Policy/PolicyResolver.cs:196-206` | A rule's delay or a trust ramp's delay is applied to Execute decisions. | `ActionQueue` |
| 14 | A2A startup checks | `OpenTail.Orca.A2A/A2AFrontDoor.cs:56-84`, warning at 133-134 | The reply must be Human, Queue or Deny, with no edits, no Execute rule and no non-Human rule (68-79). An Execute submit gets a warning. **There is no reviewer check on the submit.** | Startup |
| 15 | A2A tool risks | `OpenTail.Orca.A2A/A2ASubmitModule.cs:28, 35` | `a2a.submit` is Write. `a2a.reply` is ExternalEffect. | Both gates |
| 16 | Teams chat decisions | `OpenTail.Orca.Http/Teams/TeamsBot.cs:223, 366-374` | A chat decision is allowed up to `ChatMaxRisk` (default ExternalEffect). Its subject is `teams:<id>`, a human. | The human path; must stay |
| 17 | One queue for every channel | `OpenTail.Orca/Mcp/QueuedToolFunction.cs`; `OpenTail.Orca.A2A/A2ARequestHandler.cs`, `A2ASubmitModule.cs`, `A2AWork.cs` | MCP and A2A calls both reach the queue. | Channel parity (5.1) |
| 18 | `ActionExecutor.ClaimAndRunAsync` and `RecheckRefusal` | `OpenTail.Orca/Runtime/ActionExecutor.cs:394` and `:400-443` | Every run, delayed or approved, goes through `ClaimAndRunAsync`, which calls `RecheckRefusal` (the only call site, 400). The recheck compares the policy hash and the binding digest. Whether it also checks suspension is **not verified**. | Delayed runs, approved runs |
| 19 | **Planned:** `ModelReviewer : IReviewer` over `Microsoft.Extensions.AI`'s `IChatClient` | V6 R2; not built | An in-process reviewer. The Host depends only on the abstractions (V6 section 4, R2). | Route A (5.2) |
| 20 | **Planned:** `AiReviewer` section in `http.json` | V6 R2; not built | Provider, endpoint, model, the name of the environment variable holding the key, poll interval and timeout. The key never appears in the file. | Deployment |
| 21 | **Planned:** AI reviewer assignment, `AiMayRefuse`, `AiRefusalBudget`, `ApproveDelay`, scheduled outcome | this plan, 5.2 to 5.6; not built | See those sections. Names are proposals. | Route B and the verdicts |

## 3. Existing behaviour, branch by branch

### Table A: `ReviewAuthority.Check`

| Row | Condition | Result | Audit |
|---|---|---|---|
| A1 | AI subject, `Reviewer = Human` | Refused, **any decision** | `review_refused` |
| A2 | `ReviewerGroup` set, AI subject | Refused, **any decision** | `review_refused` |
| A3 | `ReviewerGroup` set, subject not in the group | Refused | `review_refused` |
| A4 | Approve or Retry, `SelfApproval = Forbid`, subject unknown | Refused | `review_refused` |
| A5 | Approve or Retry, `Forbid`, subject is the requester's person (`People`) | Refused: cannot approve own | `review_refused` |
| A6 | `RequiredApprovals > 1`, subject unknown | Refused | `review_refused` |
| A7 | Otherwise | Allowed | none here |

**Reviewer modes, as the code enforces them (F15).** Human-policy with a human: allowed. Human-policy with an AI: refused (A1). `Ai`-policy with a human: allowed (no row refuses it). `Ai`-policy with an AI: allowed. `Either`: the same as `Ai`. So `Ai` and `Either` behave identically today, and humans can always decide an `Ai` action.

### Table B: `AiReviewRules.Check` (order matters; the first refusal wins)

| Row | Condition | Result, for any decision unless noted |
|---|---|---|
| B1 | Effective risk Destructive, or the tool is no longer loaded | Refused |
| B2 | Risk above the ceiling (`AiMaxRisk`, default Write) | Refused, **including reject** (V6 R1: intended; see F3) |
| B3 | Action tainted and risk ExternalEffect | Refused, **including reject** (V6 R1: intended; see F2) |
| B4 | Refusals in the chain at or above `AiMaxRejections` (default 2; 0 turns it off) | Refused, **including reject** |
| B5 | Approve or Retry with edited arguments | Refused |
| B6 | Approve or Retry, `SelfApproval = Forbid` | Refused |
| B7 | Approve or Retry, `AiBudget` used up in its window | Refused |
| B8 | Otherwise | Allowed |

### Table C: decision paths in `ActionQueue.ReviewAsync`

| Row | Decision | Result |
|---|---|---|
| C1 | Approve, requester suspended | Refused. `InvalidOperationException`. |
| C2 | Approve with edits, tool has `AllowEdits: false` | Refused: `edit_refused` |
| C3 | Approve with edits that hit a Deny rule | Refused: `edit_refused` |
| C4 | Approve, approvals still short of `RequiredApprovals` | Recorded. Stays Pending. |
| C5 | Approve, enough approvals | Approved, then `ExecuteAsync` **at once**. No delay on this path. |
| C6 | Retry | Failed becomes Approved and runs again at once. **No check of `Retryable` or the failure kind** (F14). |
| C7 | Reject | Rejected, resolved as Denied. Trust is demoted if it had failed. An AI session's requester is recorded with `levels`. |
| C8 | RequestChanges | ChangesRequested |

### Table D: delayed execution

| Row | Situation | Result |
|---|---|---|
| D1 | Execute rule with a delay | Pending, `RunAt` set, announced |
| D2 | Stopped before `RunAt` by the requester or the reviewer group | Cancelled. Recorded as a Reject by the stopper. |
| D3 | `RunAt` passed, within the allowance | System approval (`delay_elapsed`), then `ClaimAndRunAsync` |
| D4 | `RunAt` passed by more than the allowance | `delay_held`: `RunAt` cleared, a person decides |
| D5 | `ExpireAfter` passed | Expired |

**The allowance in D3 and D4 is `RunAt - CreatedAt` (F13).** For an Execute action with its delay set at creation, that is the delay. For a queued action approved later, `CreatedAt` is the time it entered the queue, so the allowance includes the queue wait.

## 4. Findings and threats

### 4.1 Findings from the code

Each was verified in the code cited, except where marked.

**F1. An approver session is treated as a human.** Only `ReviewerRunner` mints `ai:` subjects (`ReviewerRunner.cs:54`). An HTTP reviewer token yields `admin:<name>` (`AdminServer.cs:155-157`). An external approver session would therefore be audited as a person, and rows B1 to B7 would not apply to it. Route A (5.2) avoids this. Route B needs the identity model in 5.2.

**F2. The taint rule also stops AI refusals.** Row B3 runs before the decision is examined. V6 intends this (R1; cheat 6). It removes "no, immediately" for replies. D-2.

**F3. The ceiling also stops AI refusals.** Row B2 applies to any decision. V6 intends this ("not even reject"). D-3.

**F4. "Yes, with undo" has no path for queued approvals.** The undo window applies to Execute rules only (`PolicyConfig.cs:217, 247`; `docs/design/undo-window.md`). An approved queued action runs at once (C5). 5.3, 5.6.

**F5. "No, with undo" has no safe path.** A rejection is final at once (C7). A delayed rejection cannot reuse the delayed-run path, because `RunDueDelayedAsync` executes whatever is due: it approves and runs the action (verified; see F12 and 5.3). It is deferred (section 6, P4).

**F6. "No and block the sender" has no AI path.** Suspension is a human command (`--suspend`, in `OPERATIONS.md`) and an automatic level (`--levels`). An AI block would be new authority. 5.3, D-4, D-22.

**F7. The A2A submit pin is not in code.** The design says `a2a.submit` is `Reviewer: Human` (`docs/design/a2a.md:62`). `A2AFrontDoor.cs` checks only the reply (70-79). The submit gets only a warning for Execute (133-134). Fix in P0, with a regression test (5.5).

**F8. The AI budget is not atomic.** `AiHistoryAsync` counts approvals before the decision is written. V6 R1 records the same fact and accepts one runner per Host as the supported shape. Nothing enforces that shape. Multiple Host processes would race on the same budget. 5.8, D-11.

**F9. A missing policy snapshot falls back to defaults.** `AiReviewRules` treats a null policy as the defaults: ceiling Write, two refusals, no four eyes. `ReviewAuthority` guards on `policy is not null`. Whether a queued action can have a null `PolicySnapshot` is **not verified**. It matters at creation and also for an existing action an AI reviews. 5.9.

**F10. A retry counts as an AI approval** (`AiReviewRules.cs:38, 64`). An AI can re-run a failed action under the same limits. V6 does not mention it. D-8.

**F11. A retry of an interrupted action appears unguarded.** The recovery path marks an interrupted run as Failed and retryable, and retries it automatically only if the tool is idempotent by key (`ActionExecutor.cs:197-236`). The Retry branch (`ActionQueue.cs:497-501`) does not check that. **Read from code; not reproduced.** 5.9.

**F12. ReviewAuthority blocks AI refusals before AiReviewRules is consulted.** Row A1 and row A2 refuse an AI on Human-only and group actions, whatever the decision. So any change to `AiReviewRules` alone cannot let an AI refuse a Human-only action. The external review is right. 5.4 changes both.

**F13. The late allowance uses `CreatedAt`, not the start of the delay.** `RunDueDelayedAsync` uses `RunAt - CreatedAt` (about `ActionQueue.cs:608`). For a queued action approved after hours, the allowance is hours. The review is right. The delay start and duration must be stored separately. 5.6.

**F14. The Retry path does not check retryability, and the HTTP endpoint accepts a retry.** The HTTP review endpoint maps `retry` to `ReviewDecision.Retry` (`AdminServer.cs:260`). The queue's Retry branch (497-501) moves a Failed action to Approved and runs it, with no check of `Retryable` or the failure kind. The review page may hide the button, but the endpoint does not enforce that. **Read from code; not reproduced.** The failures design says only a reviewer retries, and an unknown outcome is never retried (`docs/design/failures-and-inspection.md`, lines 20-21, 49). The queue must enforce that, not the page. 5.9.

**F15. `Ai` and `Either` behave identically, and humans can decide `Ai` actions.** See section 3, Table A. The policy names suggest a difference the code does not enforce. D-19.

**F16. The reply pin and the AI refusal conflict today.** The A2A startup check requires `Reviewer: Human` on the reply (`A2AFrontDoor.cs:70-71`). So an AI cannot refuse a partner reply at all, even after P2. The fix separates approval authority from refusal authority, and the startup check must say so (5.4, 5.5).

### 4.2 Threats from V6 (eight cheats)

| # | Cheat | Defence | Status |
|---|---|---|---|
| 1 | Persuasion or injection: an email says "approved by finance" | The reviewer gets a packet the Host builds; content is fenced as data; reviewer-directed text is detected deterministically and escalates (5.12) | R2: not built |
| 2 | Reviewer shopping: reject, reword, resubmit | After `AiMaxRejections` AI refusals along the chain, only a person decides (R1, done). The packet carries every attempt and reason (R2). Linking a fresh submission of the same intent is R2 | R1 done; R2 open |
| 3 | Salami slicing: twenty small payments | `AiBudget` caps AI approvals by count (R1, done). By amount is open. The budget must be shared across processes (F8) | Partly done |
| 4 | Side doors: the same effect through a tool with weaker policy | A tool with no policy entry is denied and not advertised. R5 tests it | Design holds; R5 open |
| 5 | Edit through the reviewer | An AI may approve, reject, ask for changes or abstain, never approve with edits (R1, done) | Done |
| 6 | Tainted requester: it read an attacker's email, then asks to send money | A tainted `ExternalEffect` action is a person's (R1, done). D-2 may change this for refusals | Done; D-2 |
| 7 | Lenient or correlated reviewer | Shadow mode, sampling, demotion; a same-family warning; optional dual review across vendors (R2 to R4). The "watching the reviewers" design (`docs/design/more-extensions.md`) covers per-reviewer measurement | R2 to R4 open |
| 8 | Learning the reviewer from rejection reasons | Reasons are given, but attempts count (row 2). The reviewer prompt is never exposed through any tool | Partly done; R2 |

**Threats the V6 table does not cover**, found in this review:
- An approver that the agent can reach: an agent that can call the approve route can approve for itself (PortcullisMCP, Signet). The route must be one the requester cannot reach (5.2, 5.13).
- An AI that changes policy, budgets, credentials, suspensions or levels. Those stay human acts (5.2).
- A "yes" by timeout. An undo window that runs by itself is a timeout approval, which ADR-0030 calls unsuitable for a governance boundary (5.6, D-12).
- A retry after an interrupted run (F11), a retry through the endpoint (F14), and a missing policy snapshot (F9).
- An AI refusal used to obstruct legitimate work (the external review's point). Refusals need their own limit (5.4).

## 5. Target design

### 5.1 One authority model, keyed on risk, taint and assignment, not on channel

Every decision goes through `ActionQueue`. A channel adds no verdict rules of its own. The same action must receive the same governance decision whether it arrives through MCP, A2A, Teams, HTTP or the in-process runner (section 7).

### 5.2 AI reviewers: identity, assignment and two hosts

**One authority model, two hosts.** The external review asked whether the in-process reviewer and the externally authenticated reviewer are two integration models. This plan says they are one authority model with two places to run the reviewer logic:
- **Route A, in-process (V6 R2): the default.** `ModelReviewer : IReviewer` runs inside the Host. The runner stamps the identity as `ai:<name>`. No HTTP credential exists, so F1 does not arise. The provider key is an environment variable named in `http.json`.
- **Route B, external session.** The same named AI reviewer, authenticated over HTTP with a dedicated `ai` credential. The reviewer logic may run in an external session. The authority is the same: the same assignment, the same limits, the same audit. Route B is not built. It needs F1 fixed.

Two integration models that grant authority separately are not acceptable. There is one assignment table and one set of limits.

**Named AI reviewers and assignment (D-20).** Each AI reviewer is a named identity, `ai:<name>`, with an explicit assignment: a list of policy names or tool names it may review. Its rules:
- it lists, reads and decides only the actions assigned to it. The list endpoint filters to them; the decision endpoint refuses any other action (F12-style refusal, audited);
- it can read arguments only for assigned actions;
- several named reviewers may exist. A compromised reviewer then affects only its own assignments;
- it may not carry `opentail.admin`, a group role, or `mcp:tools`;
- a dedicated `ai` credential role is used. It is not an extension of the MCP API-key scopes, which keeps the existing separation between agent credentials and reviewer credentials.

**Under both routes**, an AI reviewer cannot change policy, budgets, credentials, suspensions or levels. Those stay human acts, as in Signet's split of approver powers.

**AI history on the review page.** Every AI decision shows the reviewer identity, model, prompt version and the checks it named. This is required for the owner's overseer role (5.13). Whether the page does this today is **not verified** (section 10).

### 5.3 The five verdicts, mapped to primitives and phased

| Verdict | Mechanism | First release? | Status |
|---|---|---|---|
| Yes, now | Approve (C5), under B1 to B8 | **Yes** | Exists |
| No, now | Refuse (5.4), under the refusal rules | **Yes**, once `AiMayRefuse` exists (P2) | New authority (F12) |
| Yes, with undo | Approve recorded; the run deferred with its own typed schedule and its own allowance (5.6) | **Yes**, after the typed schedule exists (P3) | New for queued approvals (F4) |
| No, with undo | A typed scheduled rejection | **No.** Deferred (P4), conditional on the human-override semantics. | New (F5). The external review recommends dropping it. |
| No, and block | A block proposal; a person confirms before it takes effect (5.3, D-4, D-22) | **No**, later (P5) | New (F6) |

**Typed scheduled outcomes.** A delayed action carries an explicit `ScheduledOutcome`: `Approve` or `Reject`. `RunDueDelayedAsync` must dispatch on it. A `Reject` outcome never goes through `ClaimAndRunAsync`. Today the path executes whatever is due (F5, F12), so the typing is a precondition for P4, and P3 must not start without it.

### 5.4 Positive and negative authority, separated

The external review's central point, adopted:

- **Approval authority** (positive): an AI may approve only what is untainted, within its risk ceiling, within its budget, with no edits, not under `SelfApproval: Forbid`, and never a Human-only or reviewer-group action. This is today's rule set, unchanged.
- **Refusal authority** (negative): an AI may reject or request changes on any action it is assigned, when the policy says `AiMayRefuse: true` (D-21). This includes Human-only actions, and tainted external actions (D-2 option B), and it ignores the risk ceiling (D-3). A refusal has no external effect.
- **Refusal limits** (the external review's point that a refusal can obstruct work): a refusal budget per requester and tool, `AiRefusalBudget` (proposed), plus `AiMaxRejections` along the chain. When either is used up, a person decides.
- **Human-only actions:** `ReviewAuthority` rows A1 and A2 must become: the AI may refuse; it may not approve. The check depends on the decision, not only on the subject. This changes `ReviewAuthority`, and `AiReviewRules` must change in the same step (F12). Changing `AiReviewRules` alone does not work.

**The A2A reply.** The startup check (`A2AFrontDoor.cs:70-71`) currently requires `Reviewer: Human` on the reply. Under this plan the check becomes: the reply's approval is human, and an AI may refuse it when `AiMayRefuse` is set. The startup check changes in P2, not in P7, so the P2 test (an AI refusing a tainted reply) can pass (F16).

### 5.5 A2A

- **P0: add the missing submit pin** (F7). `a2a.submit` must be `Reviewer: Human` by default, enforced at startup like the reply, with a regression test. It is added now, because it is a gap in the existing design. It is not a relaxation.
- **P2: the reply's startup check** changes to "approval is human; an AI may refuse" (5.4).
- **P7: relaxing the human-only approval** for submits or replies, only where D-6 and D-7 explicitly authorise it.

Which A2A actions can receive AI refusals, and which always need a human approval, is decided in D-6 and D-7.

### 5.6 Undo windows: separate accounting and recovery

The undo window is built as an Execute rule with a `DelayFor` (`docs/design/undo-window.md`, 2026-10-18). The owner decided on 2026-10-18: a default of 10 minutes, and anyone in the reviewer group may stop an action. This plan does not change those decisions.

A "yes, with undo" on a queued approval is new. Its design:

- **A per-tool setting, `ApproveDelay`** (proposed name). Today `DelayFor` is accepted on Execute rules only (`PolicyConfig.cs:217, 247`). The setting is allowed on Queue rules, and validated the same way.
- **Stored delay start and duration.** The action records `DelayStartedAt` and `DelayDuration` when the window opens. The late allowance is `DelayDuration`, not `RunAt - CreatedAt` (F13).
- **Recovery across restarts.** The undo-window design already says the run-at time survives restarts and due actions run on start (`undo-window.md`, lines 79-80). This plan keeps that.
- **The requester is suspended during the window.** The run is held for a person. It does not run. This is the rule to write and test; today `RunDueDelayedAsync` does not check suspension, and whether `ClaimAndRunAsync` does is **not verified**.
- **The action expires during the window.** `ExpireAfter` takes precedence: the action is Expired and does not run.
- **The policy changes during the window.** `ClaimAndRunAsync` calls `RecheckRefusal`, which compares the policy hash and the binding digest (`ActionExecutor.cs:400-443`). A changed policy refuses the run, and the refusal is audited. The action goes to a person.
- **Outside the allowance** (a long outage): held for a person, as D4 does.

Timeout approval: an undo window that runs by itself is a timeout approval. ADR-0030 calls that unsuitable for a governance boundary. For reversible actions the risk is small. For partner replies it is not, and D-12 decides it.

### 5.7 Default closed; channel parity

- New tools default to `Reviewer: Human`, as today. `AiMaxRisk` defaults to Write.
- Every new channel goes through `ActionQueue`. A test per channel checks that an AI cannot approve a Human-only action through it.
- The same action gets the same decision through MCP, A2A, Teams, HTTP and the runner (section 7).

### 5.8 Shared AI budget (F8)

A per-action transaction is not enough. The budget itself is shared across actions and across Host processes. It must be one counter updated with a conditional statement in the store: increment only if the count is below the limit, in one step. Two processes cannot both take the last approval. This is a shared-counter design, not a per-action transaction. D-11 chooses between it and a startup check that refuses more than one runner.

### 5.9 Fail-closed checks before execution (F9, F11, F14)

- **F14, retry enforcement in the queue, not the page.** The Retry branch refuses unless `Retryable` is set and the failure kind allows a retry. An AI reviewer can never retry an `unknown_outcome`, or a non-idempotent operation. The kinds and the rules are in `docs/design/failures-and-inspection.md` (lines 44-50). The endpoint and the queue must agree.
- **F11.** Reproduce it first. The fix is the same rule as F14: an interrupted, non-idempotent action is never retried silently or by an AI.
- **F9, missing snapshot.** A queued action with no policy snapshot is refused at creation, and an existing action with no snapshot is refused when an AI tries to review it. Verify first (section 10).

### 5.10 History-conditioned rules

Sequence rules, in the style of cordon: a rule can require approval for one tool after another in the same session, for example a send after a read of sensitive data. Rules add severity only. Today a rule sees only the arguments of the call in question. This is a candidate, not a commitment.

### 5.11 MCP channel defences

Two defences from the toolkit's MCP security spec, for the MCP channel only:
- **Response scanning** on tool results before they reach the agent (block, sanitise or log). A detected injection feeds the taint flag.
- **Tool fingerprints.** A hash of each tool's description and schema, checked against the approved one, to catch a tool that changes silently after approval. Drift is reported to a person.

### 5.12 The reviewer packet and its defences (V6 R2)

The packet is built by the Host, never by the requester:
- the tool's descriptor and risk;
- the arguments, as fenced JSON data;
- the preview of the effect;
- the rule or policy that queued it;
- taint and its source;
- the earlier attempts in the lineage, with their reasons;
- similar recent actions, which also links a fresh submission of a rejected intent (V6 V2 query);
- this requester's recent history with this tool.

The requester's conversation is never included, and there is no channel through which the requester can address the reviewer.

**Pre-scan:** a deterministic check for reviewer-directed text: imperative approval language, claims of prior approval, role markers, invisible or bidirectional Unicode, and encoded blobs in plain-text fields. A hit goes into the packet and makes the reviewer escalate. It never decides by itself.

**Output:** strict JSON, `{ decision: approve | reject | escalate, checks: [...], reason }`. Anything else, a timeout or a refusal means abstain. The reviewer must name the checks it made. A decision with no checks counts as escalate.

**Stance:** a versioned system prompt shipped in the package. When unsure, escalate. The reviewer is told that the content may try to persuade it.

**Dual review (optional):** two reviewers, preferably from different vendors, must both approve. Any disagreement means a person decides.

### 5.13 Overseer and verdict review (the owner's role)

The owner can be the approver or the overseer:
- **Approver:** `Reviewer: Either` lets a person decide any item the AI leaves. Its meaning is decided in D-19.
- **Overseer:** the owner sees each AI decision on the review page, with reviewer, model and prompt version (5.2). The owner can stop a delayed action, or hold it so it becomes an ordinary queued action. `--review-report` shows approval rate and time to decide per reviewer (`OPERATIONS.md`).
- **Measurement:** `docs/design/more-extensions.md` ("Watching the reviewers") already designs per-reviewer measurement. R3 and R4 should use it, not a second design.
- **Kill switch:** `--suspend <subject>` refuses every call from an identity until a person clears it. Whether it applies to `ai:` subjects is **not verified**.

A sent reply cannot be undone by a later review. For irreversible actions, the owner's control is the rule set, the hold and the kill switch.

### 5.14 What this does not solve (V6 section 5, kept)

- **Within its ceiling, a reviewer can still be fooled.** The ceiling, sampling and demotion limit the cost. They do not make it impossible.
- **Data leaves to the model's provider.** The packet contains the action's content. Where that is not allowed, a local or private-endpoint model is the answer, and quality varies.
- **Cost and latency.** Each review is a model call, two with dual review. Shadow mode doubles the work for a while.
- **Collusion through a shared vendor.** Different identities do not make different minds. Dual review across vendors is the mitigation, at a cost.

## 6. Phases

Order: the external review's order, with V6's R5 and R2 placed where they depend on it. Nothing is started until section 0 is settled.

**Done**
- **R1.** The AI path's limits (V6, done 2026-10-16). Verified present in code.

**P0. Immediate: characterise and close the existing gaps (no owner decision needed for the fixes)**
- Characterise tables A to D with tests that pin current behaviour.
- **F7:** add the A2A submit pin, with a regression test.
- **F14, F11:** queue-side retry enforcement, and the reproduction of F11 first.
- **F9:** verify whether a snapshot can be null, then fail closed at creation and at review.
- **R5, the red-team corpus** (V6 said it is next). It needs no model.

**P1. Reviewer types, credentials, assignment (blocking)**
- Decide D-19 (what `Ai` and `Either` mean), D-20 (named reviewers with assignments).
- The `ai` credential role, the assignment table, filtered list and decision endpoints, and the refusals for unassigned actions (5.2). Tests for each refusal.
- Show the AI reviewer, model and prompt version on the review page (5.2, 5.13).

**P2. Decision-aware refusals (blocking)**
- `AiMayRefuse` and `AiRefusalBudget` (D-21). `ReviewAuthority` and `AiReviewRules` change together (5.4, F12).
- The A2A reply's startup check changes to "approval is human; AI may refuse" (5.5). The P2 test, an AI refusing a tainted reply, now passes.
- Request-changes follows the same rules as reject.

**P3. Yes, with undo for queued approvals (high; only after P1 and P2 pass)**
- The typed `ScheduledOutcome` (5.3), `ApproveDelay` (5.6), the stored delay start and duration (F13), and the suspension, expiry and policy-change rules (5.6).
- Tests: restart during the window, late run held, suspended requester held, policy change refused, expiry precedence.

**P4. Delayed rejection (conditional; not in the first release)**
- Only after the human-override semantics are written down. The external review recommends dropping it.

**P5. AI block proposal (later)**
- The identity definition (D-22), a human confirmation, expiry, reversal, and audit (D-4).

**P6. Shared AI budget (high)**
- The conditional shared counter (5.8). D-11.

**R2. A model-backed reviewer (after P1)**
- `ModelReviewer` over a fake `IChatClient`: the packet, pre-scan, strict output, stance, optional dual review, and the `AiReviewer` config (V6 R2). Route A (5.2). Then one real-model run once the owner picks a provider (D-13).

**Measurement (after R2; decide per D-9)**
- **R3, shadow mode**, if D-9 chooses it (V6 R3).
- **R4, sampling, demotion, drift**, if D-9 chooses them (V6 R4), using `more-extensions.md`.

**P7. Relax the human-only A2A pins (after the security tests)**
- Only where D-6 and D-7 authorise it, and only after P1 to P3 pass.

**Overseer (any time after P1)**
- Verify the kill switch covers `ai:` subjects, and the review page labels AI decisions (section 10).

## 7. Test matrix

**The decision matrix.** For tables A and B, generate every combination of:
- reviewer mode: Human, Ai, Either;
- reviewer group: absent, present with the subject in it, present with the subject outside it;
- required approvals: one or several;
- SelfApproval: Allow or Forbid;
- edited or unedited arguments;
- risk (Read, Write, ExternalEffect, Destructive, unloaded), taint (yes or no), decision (approve, reject, request changes, retry), subject (human, AI, unknown);
- history: refusals at or below the maximum; budget used or not;
- failure state: none, retryable, non-retryable, unknown outcome;
- policy snapshot: present or missing;
- requester: active or suspended;
- reviewer identity: none, one AI reviewer, several AI reviewers with different assignments.

Each combination has one expected verdict, written down before the code changes.

**Additional tests:**
- simultaneous AI approvals consuming the same budget, across two processes (F8);
- expiry and restart during an undo window (5.6);
- a retry of an `unknown_outcome` by an AI is refused, through the queue and through the endpoint (F14);
- a double approval counts once;
- a delayed rejection can never execute an action (P4, when built);
- the red-team corpus (R5) runs in CI with the fake model.

**Channel parity.** The same action, reviewed or submitted through MCP, A2A, Teams, HTTP and the in-process runner, gets the same governance decision. This is a separate test set, not a sample.

## 8. Built and not built

**Built:** `ReviewAuthority` (A1 to A7); `AiReviewRules` (B1 to B8), which is V6's R1; the queue's decision paths (C1 to C8); delayed execution, stop and hold (D1 to D5, and the undo window); `ClaimAndRunAsync` with `RecheckRefusal` on every run; the `ReviewerRunner` framework for an in-process `IReviewer`; `ai:` subjects minted in-process only; the `ai_review` audit.

**Not built:** the retry guard (F14, F11); the missing-snapshot guard (F9); the A2A submit pin (F7); named AI reviewers with assignment (5.2); the `ai` credential role (F1, Route B); `AiMayRefuse` and the refusal budget (5.4, F12, F16); the typed scheduled outcome (5.3); `ApproveDelay` and approve-with-undo (F4); delayed rejection (F5); an AI block (F6); a shared AI budget (F8); the AI history on the review page (5.2); a model-backed reviewer (R2); the red-team corpus (R5); shadow mode and sampling (R3, R4); the MCP channel defences (5.11); history-conditioned rules (5.10). No model-backed `IReviewer` exists in the projects read.

## 9. Decisions for the owner

Recommended defaults are in bold. Items marked "decided" were settled earlier. Section 0 lists the three that block implementation.

| # | Decision | Options | Recommended |
|---|---|---|---|
| D-1 | One authority model for every channel | yes, no | **Yes.** Agreed in principle on 2026-11-13; record it. |
| D-2 | An AI refusal on a tainted external action | A: a person decides; **B: AI may refuse, never approve**; C: AI may approve with an added check | **B** |
| D-3 | AI refusals and the risk ceiling | V6: refusals are capped by the ceiling; **proposed: refusals ignore the ceiling** | **Ignore the ceiling** |
| D-4 | An AI blocking a sender | **propose only, a person confirms**; an automatic block that expires (then state the expiry) | **Propose only** |
| D-5 | Undo window default | **10 minutes, stop by the reviewer group** | **Decided 2026-10-18.** No change. |
| D-6 | AI decisions on the pair's submits | yes, no | **Yes, after P1 to P3 and R2 and the red-team corpus** |
| D-7 | AI decisions on partner replies | an AI refusal under D-2 = B; an AI approval stays with a person unless D-2 = C | **As stated** |
| D-8 | A retry counts as an AI approval (F10) | **keep counting it**; count separately | **Keep** |
| D-9 | Measurement before AI authority | (a) V6 as written: shadow (R3) then sampling (R4); **(b) decide directly for named low-risk tools, with sampling (R4) and demotion from day one**; (c) decide directly, no sampling | **(b).** The owner's view is against training-style shadowing. Without any measurement, a wrong AI approval has no measured basis. |
| D-10 | How an AI reviewer runs (route) | **(a) in-process model reviewer (Route A) as the default**; (b) external session (Route B) | **(a)**, with Route B allowed only under the one authority model in 5.2 |
| D-11 | AI budget atomicity (F8) | **(a) one shared conditional counter**; (b) one runner per Host, enforced at startup | **(a)** |
| D-12 | "Yes, with undo" on irreversible actions such as partner replies (a timeout approval, per ADR-0030) | reversible actions only; irreversible too, with the risk accepted in writing; hold for a person's confirmation | **Reversible only**, unless the owner accepts the risk in writing |
| D-13 | First model provider (V6) | **OpenAI-compatible endpoint first, Anthropic next**; both | **OpenAI-compatible first** |
| D-14 | Default AI ceiling (V6) | Read; **Write**; ExternalEffect | **Write**, with ExternalEffect per tool after evidence. The code default is Write. |
| D-15 | AI suggestion visible to the human during measurement (V6) | yes; **no, visible after** | **No.** Applies only if D-9 = (a). |
| D-16 | Sampling rate in Decide mode (V6) | 5%; **10%, at least one a day per tool**; 20% | **10%.** Applies if D-9 = (a) or (b). |
| D-17 | Same model family as the requester (V6) | refuse; **warn**; ignore | **Warn** |
| D-18 | Reasons to the requester (V6) | full; **full, attempts counted**; generic | **Full, attempts counted** |
| D-19 | What `Reviewer: Ai` and `Reviewer: Either` mean (F15) | (a) `Ai`: AI only, a person may still decide; `Either`: AI or a person, the same as `Ai` today; (b) `Ai`: AI only, a person may decide only by changing the policy; `Either`: AI or a person | **(a)**, written into POLICY.md, so the names match the behaviour |
| D-20 | One AI reviewer or several (5.2) | one global reviewer; **several named reviewers, each with an assignment** | **Several named reviewers** |
| D-21 | AI refusals (5.4) | **`AiMayRefuse` per tool, default false; a refusal budget per requester and tool**; refusals unlimited | **Per-tool opt-in with a budget** |
| D-22 | An AI-proposed block (D-4): who is blocked | an A2A partner; an agent identity; **both, with the identity recorded**; expiry default | **Identity recorded; expiry 24 hours, renewed by a person** |
| D-23 | Name and default of the queued-approval undo setting (`ApproveDelay`) | **`ApproveDelay`, default off, per tool**; a different name | **As stated, default off** |
| D-24 | "No with undo" in the first release (the external review's suggestion) | keep as P4; **drop from the first release** | **Drop** |
| D-25 | The reviewer's route, one authority model (external review, point 2) | **one authority model, two hosts** (5.2); two separate grant mechanisms | **One authority model** |

## 10. Not verified

- Whether a queued action can have a null `PolicySnapshot` (F9).
- Whether `ClaimAndRunAsync` or `RecheckRefusal` checks requester suspension (5.6).
- Whether the review page labels AI decisions, and shows model and prompt version (5.2, 5.13). The owner asked this earlier.
- Whether `--suspend` applies to `ai:` subjects (5.13).
- Whether the approval and the execution start share one transaction (5.8).
- Whether an interrupted retry is reachable and harmful (F11), and whether the endpoint's retry (F14) can be reproduced. Both are read from code, and neither has been run.
- How an external approver session would get an `ai` credential in practice: who issues it, and how it is rotated.
- Whether `Microsoft.Extensions.AI` is referenced by the Host today. V6 says it depends only on the abstractions; not checked.

## 11. Implementation log and history

| Phase | Date | What |
|---|---|---|
| R1 | 2026-10-16 (V6 status; commit not recorded here) | AI reviewer limits in `AiReviewRules`, enforced by the queue and used by the runner: `AiMaxRisk` (default Write, never Destructive), taint, no edits, not the second pair of eyes, `AiMaxRejections`, `AiBudget`, and the `ai_review` audit with model and prompt version. `AiReviewLimitsTests`; POLICY.md *AI reviewers*. |
| Undo window | 2026-10-18 | Execute with a delay, Stop, run-now and hold. `docs/design/undo-window.md`. |
| Gate plan | 2026-11-13 | First written as a separate proposal, without reading V6. Merged with V6 the same day, with V6 kept as a pointer. |
| Gate plan, revised | 2026-11-13 | Revised after an external review of the merged draft (section 13). No code changed. |

## 12. Self-review of this revision

- Every section of V6 is carried into this plan: the premise, status, cheat table, R1 to R5, the boundaries, the decision table, the order and the history.
- Each point of the external review was checked against the code before it was accepted. Section 13 records the disposition of each.
- The earlier contracts and findings are kept. The additions F12 to F16 and the changes to `ReviewAuthority` are based on reads of the cited lines.
- Where V6 and the earlier proposal disagreed (D-2, D-3, D-9, D-10, D-11), the plan records both positions as decisions.
- The external review's three blocking questions are answered in section 0. The answers are recommendations until the owner confirms them.
- Not done: no tests were run, no code was changed, and no second reader has checked this revision. The findings marked "read from code; not reproduced" need a reproduction before any fix relies on them.

## 13. External review of the merged draft: disposition

An external review (ChatGPT, pasted 2026-11-13) reviewed the draft, rated it 7 out of 10, and said it needs revision before implementation. Each point was checked against the code before this revision.

| Review point | Disposition | Where handled |
|---|---|---|
| 1. AI refusals blocked by `ReviewAuthority` and by `AiReviewRules`; the P2 test cannot pass against the A2A reply pin | **Accepted.** Confirmed in code (A1, A2; B2, B3, B4). | F12, F16; 5.4, 5.5; P2 |
| 2. Delayed rejection could execute an action; `RunAt - CreatedAt` allowance | **Accepted.** Confirmed: `RunDueDelayedAsync` executes what is due, and computes `RunAt - CreatedAt`. | F5, F13; 5.3, 5.6; D-24 |
| 3. AI identity needs action-level assignment; one or several reviewers; a dedicated credential | **Accepted.** | 5.2; D-20 |
| 4. `Reviewer: Ai` and `Reviewer: Either` are indistinguishable; humans can decide `Ai` actions | **Accepted.** Confirmed in Table A. | F15; D-19 |
| 5. The Retry path does not verify `Retryable`; the endpoint accepts a retry; AI must not repeat an unknown outcome | **Accepted.** Confirmed in the queue and endpoint. Not reproduced. | F14, F11; 5.9; P0 |
| F9. Fail closed for an existing action, not only at creation | **Accepted** | 5.9; P0 |
| F8. Atomic accounting across actions and processes | **Accepted** | 5.8; D-11 |
| D-4. Define the blocked identity, expiry, reversal, confirmation | **Accepted** | D-22; P5 |
| D-5. A per-tool setting for queued approvals | **Accepted.** `DelayFor` is Execute-only (confirmed). | 5.6; D-23 |
| D-6, D-7. Which A2A actions receive AI refusals | **Accepted** | 5.5; D-6, D-7 |
| AI history on the review page | **Accepted.** Not verified in the page. | 5.2; section 10 |
| Two integration models: complement or replace? | **Accepted.** One authority model, two hosts. | 5.2; D-25 |
| Test matrix: more dimensions and channels | **Accepted** | Section 7 |
| A2A submit guard in P0, not P7 | **Accepted** | 5.5; P0 |
| Implementation order | **Accepted**, with R5 and R2 placed where they depend on it | Section 6 |
| Drop "No with undo" from the first release | **Accepted** | D-24; 5.3 |
| Index: the new plan is not listed in `docs/design/README.md` | **Accepted.** Added. | `docs/design/README.md` |
| The three blocking questions | **Accepted as the structure of section 0** | Section 0 |
| A dedicated AI credential, not an extension of MCP keys | **Accepted.** Already the plan (route B). | 5.2 |
| "Refusals can obstruct legitimate work; refusals need their own limits" | **Accepted** | 5.4; D-21 |

No point was rejected. Two points were already in the plan in substance (the dedicated credential, and the review page showing AI history); they are restated so the review's wording is covered.
