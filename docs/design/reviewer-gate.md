# Reviewer gate: the plan for AI reviewers on Orca

Status: **the one plan for Orca's reviewer gate, version 3 (2026-11-13).** It merges the gate proposal with `docs/MCP-HOST-V6-PLAN.md`, which is now a pointer to this file. Version 3 answers a second external review of version 2 (section 13). Phase R1 of V6 is done. Everything else is proposed and nothing else is built (section 8). Owner decisions are in section 9. Section 0 lists what must be settled before any implementation starts.

**Repository and paths.** This plan lives in `OpenTail.Orca`, the renamed Host (`opentail-host`, still public and unchanged). Code paths are written as they are in `OpenTail.Orca`, for example `OpenTail.Orca/Runtime/ReviewerRunner.cs`. Line numbers were read on 2026-11-13 in the old repo at `4937917`; the rename changed names, not structure, so the lines match.

## 0. What must be settled before implementation

The first review asked three questions. Version 3 answers them, and the second review added blocking requirements. Each answer is this plan's recommendation until the owner confirms it in section 9.

1. **What can an AI reviewer refuse, including Human-only actions?** Any action it is assigned to, when the policy allows refusals (`AiMayRefuse`, D-21). Refusal crosses the Human-only boundary and the reviewer-group boundary (D-26). Approval never does. Approval is always subject to every existing check: Human-only, reviewer group, taint, risk ceiling, budget and four eyes.
2. **What distinguishes the five verdicts?** For the first release: yes now (approve), no now (refuse), and yes with undo (approve, then a scheduled run). "No with undo" is deferred, and so is "no and block the sender", which needs a human to confirm it. Each scheduled outcome is typed, so a rejection can never run as an approval.
3. **How is an AI reviewer restricted to exactly its assigned actions?** Each AI reviewer is a named identity with an explicit assignment. Assignment is enforced at three boundaries: list and read, the queue decision itself, and the in-process runner. Filtering a list is not enough (5.2).

**Blocking requirements the second review added:**
- the in-process runner must offer refusal-capable reviewers the Human-only actions they are assigned (`OpenTail.Orca/Runtime/ReviewerRunner.cs:35` excludes them today);
- assignment is enforced in the queue, not only in the list endpoint;
- refusal budgets use the same atomic accounting as approval budgets;
- an approved, delayed action can never bypass its delay, including through the final human approval;
- the policy recheck runs inside the claim transaction, not before it (F18);
- an `unknown_outcome` is never retried, by anyone, through any route.

## 1. What is asked, and the premise

**Premise (from V6, section 1).** No reviewer model can be made impossible to fool. The design therefore does not depend on the reviewer being incorruptible. It depends on four things:

1. the reviewer sees the action, never the requester's case for it;
2. an AI approval is only ever enough where being fooled is cheap;
3. retrying, slicing and side doors do not pay;
4. how often the reviewer is wrong is measured against people, continuously, and it loses its authority when it slips.

**An AI approval is delegated authority, not human oversight.** Records, screens and documents must say so. An approval by an AI is recorded and shown as an AI decision, with the reviewer's identity, model and prompt version (D-29). It is never shown as a person's decision.

**What the owner asks (2026-11-13).**
- One reviewer gate for every tool and every channel (MCP, A2A, and any later channel). Agreed in principle.
- AI reviewers may decide actions, within limits. The owner chooses, per action, whether to approve directly or to oversee the AI.
- Five verdicts for an AI reviewer: yes now, yes with undo, no now, no with undo, and no and block the sender. Section 0 says which ship first.
- The owner's time is the scarce resource. Approval by a person should be the exception, not each step.

**The tension to settle.** V6 puts measurement before authority: shadow mode, then sampling, then demotion. The owner's stated view is "AI saves time", and "I am not training AI". Decision D-9 settles this.

**Scope.** The Orca reviewer gate. The Teams bot's own behaviour, the pair of sessions and the A2A kit are out of scope, except where they touch the gate.

## 2. Contracts this must satisfy

| # | Member | Where | Promise | Who relies on it |
|---|---|---|---|---|
| 1 | `ReviewAuthority.Check(r, decision, subject, groups, people)` | `OpenTail.Orca/Policy/ReviewAuthority.cs:52-80` | Says who may make this decision. Refuses an AI on a Human-only action (57-59), **whatever the decision**. Refuses an AI on a group action (61-63), **whatever the decision**. Refuses a non-member of the group (64). Refuses self-approval under `Forbid` (67-74). Refuses an unknown identity on a several-approver action (76-77). | `ActionQueue.ReviewAsync`, before any state change |
| 2 | `AiReviewRules.Check(r, decision, risk, editsArguments, history)` | `OpenTail.Orca/Policy/AiReviewRules.cs:22-48` | The AI limits. Pure. An early Destructive check (27-28) applies to **every** decision. Then the ceiling (29-30) and the taint rule (31-32), which apply to refusals as well. Then refusals (34-36). Then edits, four eyes and budget, for approvals only. | `ActionQueue.AiVerdictAsync`; `ReviewerRunner` |
| 3 | `AiReviewRules.IsAiRefusal`, `IsAiApproval` | `AiReviewRules.cs:60-64` | Which past reviews count as AI refusals and AI approvals. `Retry` counts as an approval. | `AiHistoryAsync` |
| 4 | `ActionQueue.ReviewAsync(actionId, review)` | `OpenTail.Orca/Runtime/ActionQueue.cs`, from about line 360 | The only path that changes an action's decision. Stamps edit digests, runs `ReviewAuthority`, then the AI check, then switches on the decision. The Approve case calls `ExecuteAsync` at once when quorum is reached (F4). The Retry branch (about 497-501) checks neither `Retryable` nor the failure kind (F14). The Reject branch calls `levels.RejectedAsync` and the trust demotion for AI-session requesters (F19). | `AdminServer` `/actions/{id}/review` (`OpenTail.Orca/Admin/AdminServer.cs:241`, which accepts `retry`, 260); `TeamsBot.cs:281`; `ReviewerRunner.cs:60` |
| 5 | `ActionQueue.AiVerdictAsync`, `AiHistoryAsync` | `ActionQueue.cs:529-560` | Adds store history to `AiReviewRules.Check`: refusals up the `ParentActionId` chain, and approvals inside the `AiBudget` window. The count is taken before the decision is written (F8). | `ReviewAsync`; `ReviewerRunner` |
| 6 | `ActionQueue.StopAsync`, `RunDueDelayedAsync`, `ExpireDueAsync` | `ActionQueue.cs` around 596-660 | A delayed action is `Pending` with `RunAt`. `RunDueDelayedAsync` computes the late allowance as `RunAt - CreatedAt` (about 608), moves the action to Approved and calls `ClaimAndRunAsync` (about 624). It does not check suspension. Stop is open to the requester or the reviewer group. | Scheduler; `AdminServer` `/actions/{id}/stop` (`AdminServer.cs:307`) |
| 7 | `IReviewer` | `OpenTail.Orca.Abstractions/Actions/Interfaces.cs:124-128` | `Name`, and `ReviewAsync(action)` returning a review or null (abstain). | `ReviewerRunner` only. The only implementation in the projects read is `DelegateReviewer` (`ReviewerRunner.cs:13`). |
| 8 | `ReviewerRunner.RunOnceAsync` | `OpenTail.Orca/Runtime/ReviewerRunner.cs:28-62` | Line 35 offers only actions whose snapshot says `Ai` or `Either`, so every Human-only action is excluded before any reviewer sees it (F17). Line 36 asks `AiVerdictAsync` with `Reject`. Stamps the identity as `ai:<name>` (54), and the input hash. Audits `ai_review`. | Runs in-process only |
| 9 | `Subjects.Ai`, `Subjects.IsAi` | `OpenTail.Orca.Abstractions/Identity/CallerIdentity.cs:28-38` | `ai:<name>` is the AI subject. `IsAi` is the prefix test the rules use. | Every AI rule |
| 10 | HTTP reviewer identity | `AdminServer.cs:141-160`, roles at `AdminServer.cs:51-60` | A reviewer token yields `admin:<name>` (155-157). Roles are `opentail.review`, `opentail.admin` and `group:*`. No HTTP credential yields `ai:`. | The review endpoint |
| 11 | `ToolPolicy` defaults | `OpenTail.Orca.Abstractions/Policy/ToolPolicy.cs` | `Reviewer` Human (46). `RequiredApprovals` 1 (55). `SelfApproval` Allow (57). `DefaultDelay` 10 minutes (71). `AiMaxRisk` null, which means Write (90, and `AiReviewRules.cs:16`). `AiMaxRejections` 2 (93). `AiBudget` null (96). | Everything above |
| 12 | `PolicyConfig` validation | `OpenTail.Orca/Policy/PolicyConfig.cs:215-258` | Reviewer, group, approvals and expiry only on Queue rules (223-224). A delay only on Execute (217, 246-247). `AiMaxRisk` not Destructive (254-255). `AiBudget` needs positive values (257-258). | Startup |
| 13 | `PolicyResolver` delays | `OpenTail.Orca/Policy/PolicyResolver.cs:196-206` | A rule's delay or a trust ramp's delay is applied to Execute decisions. | `ActionQueue` |
| 14 | A2A startup checks | `OpenTail.Orca.A2A/A2AFrontDoor.cs:56-84`, warning at 133-134 | The reply must be Human, Queue or Deny, with no edits, no Execute rule and no non-Human rule (68-79). An Execute submit gets a warning. **There is no reviewer check on the submit.** | Startup |
| 15 | A2A tool risks | `OpenTail.Orca.A2A/A2ASubmitModule.cs:28, 35` | `a2a.submit` is Write. `a2a.reply` is ExternalEffect. | Both gates |
| 16 | Teams chat decisions | `OpenTail.Orca.Http/Teams/TeamsBot.cs:223, 366-379` | A chat decision is allowed up to `ChatMaxRisk` (default ExternalEffect). With `ApprovePartnerRepliesInChat` off, an Approve on a partner reply is refused (`ChatMayApprove`, 379). Its subject is `teams:<id>`, a human. | The human path; must stay |
| 17 | One queue for every channel | `OpenTail.Orca/Mcp/QueuedToolFunction.cs`; `OpenTail.Orca.A2A/A2ARequestHandler.cs`, `A2ASubmitModule.cs`, `A2AWork.cs` | MCP and A2A calls both reach the queue. | Channel parity (5.1) |
| 18 | `ActionExecutor.ClaimAndRunAsync` and `RecheckRefusal` | `OpenTail.Orca/Runtime/ActionExecutor.cs:394` and `:400-443` | Every run, delayed or approved, goes through `ClaimAndRunAsync`. It calls `RecheckRefusal` (the only call site, 400) **before** the claim transaction (`ChangeAdmittedAsync`, Approved to Executing). The recheck compares the policy hash and the binding digest. Whether it checks suspension is **not verified**. | Delayed runs, approved runs |
| 19 | **Planned:** `ModelReviewer : IReviewer` over `Microsoft.Extensions.AI`'s `IChatClient` | V6 R2; not built | An in-process reviewer. The Host depends only on the abstractions (V6 section 4, R2). | Route A (5.2) |
| 20 | **Planned:** `AiReviewer` section in `http.json` | V6 R2; not built | Provider, endpoint, model, the name of the environment variable holding the key, poll interval and timeout. The key never appears in the file. | Deployment |
| 21 | **Planned:** assignments, `AiMayRefuse`, `AiRefusalBudget`, `ApproveDelay`, typed scheduled outcome | this plan, 5.2 to 5.6; not built | See those sections. Names are proposals. | Route B and the verdicts |

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
| A8 (P1, D-19) | `Reviewer = Ai`, subject is a person or unknown: Approve or Retry | Refused. Reject and Request changes are allowed. | `review_refused` |
| A9 (P1, D-37) | AI subject that is the requester's own identity or its own person (`People`): Approve or Retry | Refused. Refusals are not affected. | `review_refused` |

**Reviewer modes, as the code enforces them.** Human-policy with a human: allowed. Human-policy with an AI: refused (A1). `Ai`-policy with an AI: allowed within the limits. `Ai`-policy with a person: approve and retry refused, reject and request changes allowed (A8, D-19, P1). `Either`: an AI or a person may approve (A8). Before P1, `Ai` and `Either` behaved identically and a person could approve an `Ai` action (F15); that P0 pin is replaced, and the change is in the §11 log.

### Table B: `AiReviewRules.Check` (order matters; the first refusal wins)

| Row | Condition | Result |
|---|---|---|
| B1 | Effective risk Destructive, or the tool is no longer loaded | Refused, **any decision** (the early check applies to refusals too; see F20) |
| B2 | Risk above the ceiling (`AiMaxRisk`, default Write) | Refused, **any decision**, including reject (F3) |
| B3 | Action tainted and risk ExternalEffect | Refused, **any decision**, including reject (F2) |
| B4 | Refusals in the chain at or above `AiMaxRejections` (default 2; 0 turns it off) | Refused, **any decision**, including reject |
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
| C5 | Approve, enough approvals | Approved, then `ExecuteAsync` **at once**. No delay on this path (F4, F21). |
| C6 | Retry | Failed becomes Approved and runs again at once. **No check of `Retryable` or the failure kind** (F14). |
| C7 | Reject | Rejected, resolved as Denied. Trust is demoted if it had failed. An AI session's requester is recorded with `levels` (F19). |
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

**F4. "Yes, with undo" has no path for queued approvals.** The undo window applies to Execute rules only (`PolicyConfig.cs:217, 247`; `docs/design/undo-window.md`). The Approve case runs the action at once when quorum is reached (C5, F21). 5.3, 5.6.

**F5. "No, with undo" has no safe path.** A rejection is final at once (C7). A delayed rejection cannot reuse the delayed-run path, because `RunDueDelayedAsync` executes whatever is due: it approves and runs the action (verified). It is deferred (section 6, P4).

**F6. "No and block the sender" has no AI path.** Suspension is a human command (`--suspend`, in `OPERATIONS.md`) and an automatic level (`--levels`). An AI block would be new authority. 5.3, D-4, D-22.

**F7. The A2A submit pin is not in code.** The design says `a2a.submit` is `Reviewer: Human` (`docs/design/a2a.md:62`). `A2AFrontDoor.cs` checks only the reply (70-79). The submit gets only a warning for Execute (133-134). Fix in P0, with a regression test (5.5).

**F8. The AI budget is not atomic.** `AiHistoryAsync` counts approvals before the decision is written. V6 R1 records the same fact and accepts one runner per Host as the supported shape. Nothing enforces that shape. Multiple processes would race on the same budget. The same applies to refusal budgets (F23). 5.8, D-11.

**F9. A missing policy snapshot falls back to defaults.** `AiReviewRules` treats a null policy as the defaults: ceiling Write, two refusals, no four eyes. `ReviewAuthority` guards on `policy is not null`. Whether a queued action can have a null `PolicySnapshot` is **not verified**. It matters at creation and also for an existing action an AI reviews. 5.9.

**F10. A retry counts as an AI approval** (`AiReviewRules.cs:38, 64`). An AI can re-run a failed action under the same limits. V6 does not mention it. D-8.

**F11. A retry of an interrupted action appears unguarded.** The recovery path marks an interrupted run as Failed and retryable, and retries it automatically only if the tool is idempotent by key (`ActionExecutor.cs:197-236`). The Retry branch (`ActionQueue.cs:497-501`) does not check that. **Read from code; not reproduced.** 5.9.

**F12. ReviewAuthority blocks AI refusals before AiReviewRules is consulted.** Rows A1 and A2 refuse an AI on Human-only and group actions, whatever the decision. So any change to `AiReviewRules` alone cannot let an AI refuse a Human-only action. 5.4 changes both.

**F13. The late allowance uses `CreatedAt`, not the start of the delay.** `RunDueDelayedAsync` uses `RunAt - CreatedAt` (about 608). For a queued action approved after hours, the allowance is hours. The delay start and duration must be stored separately. 5.6.

**F14. The Retry path does not check retryability, and the HTTP endpoint accepts a retry.** The HTTP review endpoint maps `retry` to `ReviewDecision.Retry` (`AdminServer.cs:260`). The queue's Retry branch moves a Failed action to Approved and runs it, with no check of `Retryable` or the failure kind. The review page may hide the button, but the endpoint does not enforce that. **Read from code; not reproduced.** The failures design says only a reviewer retries, and an unknown outcome is never retried (`docs/design/failures-and-inspection.md`, lines 20-21, 49). The queue must enforce that for everyone (5.9).

**F15. `Ai` and `Either` behave identically, and humans can decide `Ai` actions.** See section 3, Table A. The policy names suggest a difference the code does not enforce. D-28.

**F16. The reply pin and the AI refusal conflict today.** The A2A startup check requires `Reviewer: Human` on the reply (`A2AFrontDoor.cs:70-71`). So an AI cannot refuse a partner reply at all. The fix separates approval authority from refusal authority, and the startup check must say so (5.4, 5.5).

**F17. The in-process runner never offers a Human-only action to an AI.** `ReviewerRunner.cs:35` admits only `Ai` and `Either` policies. A refusal-capable reviewer therefore never sees a Human-only action, and changing `ReviewAuthority` or `AiReviewRules` alone does not make refusals work. P2 must change the runner's eligibility as well, offering the action under refusal authority only.

**F18. The policy recheck runs before the claim transaction, not inside it.** `ClaimAndRunAsync` calls `RecheckRefusal` (about line 400), then `ChangeAdmittedAsync` moves Approved to Executing with the `approval_consumed` audit (about 405-414). A policy change between the two can be missed. **Verified in the code.** The recheck must move inside the claim transaction, or the claim must compare the policy hash it checked (5.9).

**F19. A refusal can penalise the requester.** The Reject branch calls `levels.RejectedAsync` for AI-session requesters, and `trust.DemoteIfFailedAsync` for any reject of a failed action (`ActionQueue.cs`, Reject case). So "a refusal has no external effect" is accurate for the action but not for the requester's identity level. D-27 decides whether an AI refusal penalises.

**F20. The Destructive check blocks refusals.** Row B1 (`AiReviewRules.cs:27-28`) applies to every decision. A refusal of a Destructive action has no effect, so the ban on AI decisions about Destructive actions should apply to approvals only (5.4).

**F21. The quorum-reached approval runs the action at once.** The Approve case returns `ExecuteAsync` as soon as approvals reach `RequiredApprovals`. Any delay requirement for queued approvals must be placed here, and no reviewer, including the final human approver, may bypass it through the ordinary approval path (5.3, 6 P3).

**F22. Teams documentation contradicted the implementation.** `OPERATIONS.md` said nothing could be approved from a card. Cards approve within `ChatMaxRisk`, and only partner replies move to the page when the switch is off. **Corrected in version 3.**

**F23. The refusal budget has the same race as the approval budget.** `AiRefusalBudget` (proposed) counted before the refusal is written would let two reviewers, or two processes, each see the same remaining allowance and both refuse. It uses the same mechanism as F8 (5.8).

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
- A policy change between the recheck and the claim (F18).
- An AI refusal used to obstruct legitimate work. Refusals need their own limit (5.4).
- A refusal that penalises the requester's identity level without a human decision (F19).

## 5. Target design

### 5.1 One authority model, keyed on risk, taint and assignment, not on channel

Every decision goes through `ActionQueue`. A channel adds no verdict rules of its own. The same action must receive the same governance decision whether it arrives through MCP, A2A, Teams, HTTP or the in-process runner (section 7, channel parity).

### 5.2 AI reviewers: identity, assignment and two hosts

**One authority model, two hosts.** This plan has one authority model with two places to run the reviewer logic:
- **Route A, in-process (V6 R2): the default.** `ModelReviewer : IReviewer` runs inside Orca. The runner stamps the identity as `ai:<name>`. No HTTP credential exists, so F1 does not arise. The provider key is an environment variable named in `http.json`.
- **Route B, external session.** The same named AI reviewer, authenticated over HTTP with a dedicated `ai` credential. The reviewer logic may run in an external session. The authority is the same: the same assignment, limits and audit. Route B is not built. It needs F1 fixed and the mechanism below.

Two integration models that grant authority separately are not acceptable. There is one assignment table and one set of limits.

**Named AI reviewers and assignment (D-20).** Each AI reviewer is a named identity, `ai:<name>`, with an explicit assignment: the policy names and tool names it may review. Assignment is enforced at **three** boundaries, and each has its own test:
1. **List and read.** The list endpoint and the read endpoint return only assigned actions. An unassigned action is reported as not found.
2. **The queue decision.** `ActionQueue.ReviewAsync` refuses a decision on an unassigned action, whatever the caller, including a direct call that skips the list. The refusal is audited as `review_refused`.
3. **The in-process runner.** `ReviewerRunner` never sends unassigned action content to a model. The packet for an unassigned action is never built.

Filtering the list is not enough. An AI reviewer must not be able to submit a decision for an unassigned action by any route.

**The external credential, outside the agent's reach (P1).** An agent must not be able to call its own approval route. The mechanism:
- the `ai` credential is issued to a named reviewer identity, never to an agent profile, and an agent profile may not list the reviewer role;
- its secret is shown once and stored only as a hash in the HTTP config, rotated by an operator;
- the agent's credentials are refused at the reviewer endpoints (403), even when the agent's profile would otherwise allow the tool;
- a security test submits a decision from an agent credential and from an `ai` credential on an action the agent requested, and expects refusal in both cases.

Several named reviewers may exist (D-20). A compromised reviewer then affects only its own assignments. *Built (P1 slice 2):* the assignment is the `AiReviewers` list on each tool in the policy. Being per tool, it is part of the policy hash, and it is refused at validation unless the tool is queued and open to an AI. With no list, any AI reviewer may decide the tool (D-36). Boundary 1 and the credential (D-30) are built in P1 slice 4. An AI reviewer's secret is held only as a hash: `AiReviewers` in the HTTP config (hosted) or `AdminOptions.AiReviewers` (standalone). `--new-ai-reviewer <name>` issues it, shown once. The identity is `ai:<name>` with the review role only, so it cannot call a tool, and it cannot reach admin or group routes. An endpoint filter refuses an AI identity every `/api` route except list, read and review, and a route is closed to AI until someone marks it. An action not offered to the reviewer is reported as not found.

It may not carry `opentail.admin`, a group role, or `mcp:tools`.

**Under both routes**, an AI reviewer cannot change policy, budgets, credentials, suspensions or levels. Those stay human acts, as in Signet's split of approver powers.

**AI history on the review page (D-29).** Every AI decision shows the reviewer identity, model, prompt version and the checks it named, labelled as an AI decision. *Built in P1 slice 5:* the review detail carries `byAi`, `aiModel`, `aiPromptVersion` and `aiChecks` for each review, and the page prints "AI decision by …" with what the reviewer reported. The queue audits every AI decision as `ai_review` under its `ai:` subject, whichever channel brought it, and records an approval's actor as `ai:<name>`, not the bare name. The reported model and checks are the reviewer's claims and are shown as such.

### 5.3 The five verdicts, mapped to primitives and phased

| Verdict | Mechanism | First release? | Status |
|---|---|---|---|
| Yes, now | Approve (C5), under B1 to B8 | **Yes** | Exists |
| No, now | Refuse (5.4), under the refusal rules | **Yes**, once `AiMayRefuse` exists (P2) | New authority (F12, F17) |
| Yes, with undo | Approve recorded; a typed scheduled run with its own allowance (5.6) | **Yes**, after the scheduled execution path exists (P3) | New for queued approvals (F4, F21) |
| No, with undo | A typed scheduled rejection | **No.** Deferred (P4), conditional on the human-override semantics. | New (F5). The review recommends dropping it. |
| No, and block | A block proposal; a person confirms before it takes effect (D-4, D-22) | **No**, later (P5) | New (F6) |

**Typed scheduled outcomes.** A scheduled action carries an explicit `ScheduledOutcome`: `Approve` or `Reject`. The scheduler must execute only an `Approve` outcome. A `Reject` outcome never goes through `ClaimAndRunAsync`. Today the path executes whatever is due (F5), so the typing is a precondition for P4, and P3 must not start without it.

### 5.4 Positive and negative authority, separated

- **Approval authority** (positive): an AI may approve only what is untainted, within its risk ceiling, within its budget, with no edits, not under `SelfApproval: Forbid`, and never a Human-only or reviewer-group action. This is today's rule set.
- **Refusal authority** (negative): an AI may reject or request changes on any action it is assigned, when the policy says `AiMayRefuse: true` (D-21). It may refuse Human-only actions (D-26), tainted external actions (D-2 option B), and actions in a reviewer group (D-26). It ignores the risk ceiling (D-3). A refusal has no external effect on the action.
- **Destructive scope.** The ban on AI decisions about Destructive actions applies to **approvals only** (F20). The early Destructive check in `AiReviewRules.Check` must be scoped to approval decisions, or it would block the refusals this plan allows.
- **Refusal limits.** A refusal can obstruct legitimate work. Refusals are limited by `AiRefusalBudget` per requester and tool, and by `AiMaxRejections` along the chain. Both are counted atomically (5.8). When either is used up, a person decides.
- **No penalties from an AI refusal by default (D-27).** An AI refusal does not change the requester's identity level or trust. The existing human reject behaviour is unchanged. This keeps "a refusal has no external effect" true for the requester as well as the action (F19).
- **Implementation.** `ReviewAuthority` rows A1 and A2 become decision-aware: an AI may refuse, and may not approve. `AiReviewRules` changes in the same step (F12). `ReviewerRunner` line 35 changes to offer refusal-capable reviewers their assigned Human-only actions, under refusal authority only (F17).

**The A2A reply.** The startup check (`A2AFrontDoor.cs:70-71`) currently requires `Reviewer: Human` on the reply. Under this plan it becomes: the reply's approval is human, and an AI may refuse it when `AiMayRefuse` is set. The startup check changes in P2, so the P2 test (an AI refusing a tainted reply) can pass (F16).

### 5.5 A2A

- **P0: add the missing submit pin** (F7). `a2a.submit` must be `Reviewer: Human` by default, enforced at startup like the reply, with a regression test. It is added now because it is a gap in the existing design. It is not a relaxation.
- **P2: the reply's startup check** changes to "approval is human; AI may refuse" (5.4), with a startup test that an AI approval on a reply is refused and an AI refusal is allowed.
- **P7: relaxing the human-only approval** for submits or replies, only where D-6 and D-7 explicitly authorise it.

Which A2A actions can receive AI refusals, and which always need a human approval, is decided in D-6 and D-7.

### 5.6 Undo windows: the scheduled execution path

The undo window is built as an Execute rule with a `DelayFor` (`docs/design/undo-window.md`, 2026-10-18). The owner decided on 2026-10-18: a default of 10 minutes, and anyone in the reviewer group may stop an action. This plan does not change those decisions.

A "yes, with undo" on a queued approval is new. These requirements are mandatory:

- **The final approval schedules, it does not execute.** When an approval reaches `RequiredApprovals` and `ApproveDelay` applies, the action enters a scheduled state (Pending with a typed `Approve` schedule), and `ExecuteAsync` is not called. This replaces the immediate call at `ActionQueue.ReviewAsync` (F21).
- **No bypass.** No reviewer, including the final human approver, can skip the delay through the ordinary approval path. A human may stop the action, or run it now, through an explicit override that is audited as a separate action. It is never a silent bypass.
- **Typed execution.** The scheduler executes only an `Approve` outcome (5.3).
- **Recheck at run time.** Before execution the scheduler rechecks expiry, suspension, the policy version and the action binding, inside the claim transaction (F18, 5.9).
- **Persisted timing.** The schedule records `DelayStartedAt` and `DelayDuration`, separately from `CreatedAt` (F13). The schedule survives a restart.
- **Overdue schedules go to a person.** A schedule that is overdue beyond its allowance is held for a human, never executed automatically (D4).
- **Per-tool setting, `ApproveDelay`** (proposed name). `DelayFor` is accepted on Execute rules only (`PolicyConfig.cs:217, 247`). The setting is allowed on Queue rules and validated the same way.

Timeout approval: an undo window that runs by itself is a timeout approval. ADR-0030 calls that unsuitable for a governance boundary. For reversible actions the risk is small. For partner replies it is not, and D-12 decides it.

### 5.7 Default closed; channel parity

- New tools default to `Reviewer: Human`, as today. `AiMaxRisk` defaults to Write.
- Every new channel goes through `ActionQueue`. A test per channel checks that an AI cannot approve a Human-only action through it.
- The same action gets the same decision through MCP, A2A, Teams, HTTP and the runner (section 7).

### 5.8 Shared budgets: approvals and refusals (F8, F23)

A per-action transaction, or a count taken before the decision, is not enough. The budgets are shared across actions and across Orca processes.

- Each budget is one counter updated by a conditional statement in the store: increment only if the count is below the limit, in one step, **in the same transaction that records the decision**. Approval budgets and refusal budgets use the same mechanism.
- Two reviewers, or two processes, cannot both take the last unit. The test runs two decisions concurrently against one remaining unit and expects exactly one to succeed.
- D-11 chooses between this and a startup check that refuses more than one runner per Host. The startup check is weaker and is not preferred.

### 5.9 Fail-closed checks before execution (F9, F11, F14, F18)

- **Retry, for everyone (F14, F11).** An `unknown_outcome` is never retried by anyone, human, AI, through the endpoint or through the queue. It can be retried only when reconciliation proves the original effect did not happen, or the tool is idempotent by key. The Retry branch enforces `Retryable` and the failure kind itself, so the rule holds whatever the page shows. The kinds and rules are in `docs/design/failures-and-inspection.md` (lines 44-50), and the queue must match the recovery path in `ActionExecutor`.
- **The recheck is inside the claim (F18).** `RecheckRefusal` moves into the transaction that moves Approved to Executing, or the claim compares the policy hash it checked. A policy change between the check and the claim must refuse the run, with an audit entry. This needs a test that changes the policy between the two steps.
- **Missing snapshot (F9).** A queued action with no policy snapshot is refused at creation. An existing action with no snapshot is refused when an AI tries to review it. Verify first (section 10).

### 5.10 History-conditioned rules

Sequence rules, in the style of cordon: a rule can require approval for one tool after another in the same session, for example a send after a read of sensitive data. Rules add severity only. Today a rule sees only the arguments of the call in question. This is a candidate, not a commitment.

### 5.11 MCP channel defences

Two defences from the toolkit's MCP security spec, for the MCP channel only:
- **Response scanning** on tool results before they reach the agent (block, sanitise or log). A detected injection feeds the taint flag.
- **Tool fingerprints.** A hash of each tool's description and schema, checked against the approved one, to catch a tool that changes silently after approval. Drift is reported to a person.

### 5.12 The reviewer packet and its defences (V6 R2)

The packet is built by Orca, never by the requester:
- the tool's descriptor and risk;
- the arguments, as fenced JSON data;
- the preview of the effect;
- the rule or policy that queued it;
- taint and its source;
- the earlier attempts in the lineage, with their reasons;
- similar recent actions, which also links a fresh submission of a rejected intent (V6 V2 query);
- this requester's recent history with this tool.

The requester's conversation is never included, and there is no channel through which the requester can address the reviewer. The packet is built only for an action the reviewer is assigned (5.2, boundary 3).

**Pre-scan:** a deterministic check for reviewer-directed text: imperative approval language, claims of prior approval, role markers, invisible or bidirectional Unicode, and encoded blobs in plain-text fields. A hit goes into the packet and makes the reviewer escalate. It never decides by itself.

**Output:** strict JSON, `{ decision: approve | reject | escalate, checks: [...], reason }`. Anything else, a timeout or a refusal means abstain. The reviewer must name the checks it made. A decision with no checks counts as escalate.

**Stance:** a versioned system prompt shipped in the package. When unsure, escalate. The reviewer is told that the content may try to persuade it.

**Dual review (optional):** two reviewers, preferably from different vendors, must both approve. Any disagreement means a person decides.

### 5.13 Overseer, audit vocabulary and verdict review (the owner's role)

- **Approver:** `Reviewer: Either` lets a person decide any item the AI leaves. Its meaning is decided in D-28.
- **Overseer:** the owner sees each AI decision on the review page, labelled as an AI decision with reviewer, model and prompt version (5.2). The owner can stop a scheduled action, or hold it so it becomes an ordinary queued action. `--review-report` shows approval rate and time to decide per reviewer (`OPERATIONS.md`).
- **Audit vocabulary (D-29).** An AI approval is recorded with the `ai:` actor and an AI-specific event, never with a person's name. Documents describe AI approval as delegated authority. The documents do not describe it as human oversight.
- **Measurement:** `docs/design/more-extensions.md` ("Watching the reviewers") already designs per-reviewer measurement. R3 and R4 use it, not a second design.
- **Kill switch:** `--suspend <subject>` refuses every call from an identity until a person clears it. Whether it applies to `ai:` subjects is **not verified**.

A sent reply cannot be undone by a later review. For irreversible actions, the owner's control is the rule set, the hold and the kill switch.

### 5.14 What this does not solve (V6 section 5, kept)

- **Within its ceiling, a reviewer can still be fooled.** The ceiling, sampling and demotion limit the cost. They do not make it impossible.
- **Data leaves to the model's provider.** The packet contains the action's content. Where that is not allowed, a local or private-endpoint model is the answer, and quality varies.
- **Cost and latency.** Each review is a model call, two with dual review. Shadow mode doubles the work for a while.
- **Collusion through a shared vendor.** Different identities do not make different minds. Dual review across vendors is the mitigation, at a cost.

## 6. Phases

Order: the review's order, with V6's R5 and R2 placed where they depend on it. Each phase has acceptance tests. Nothing is started until section 0 is settled.

**Done**
- **R1.** The AI path's limits (V6, done 2026-10-16). Verified present in code.

**P0. Immediate: characterise and close the existing gaps**
- *Characterise* tables A to D with tests that pin current behaviour.
- *A2A submit pin* (F7). Acceptance: startup refuses a submit whose reviewer is not Human, unless the owner has changed D-6.
- *Retry for everyone* (F14, F11). Acceptance: a manual retry, an AI retry and a direct HTTP retry of an `unknown_outcome` are all refused; a retry of an idempotent-by-key tool is allowed; a retry of a non-retryable failure is refused.
- *Missing snapshot* (F9). Acceptance: an action without a snapshot cannot be queued, and an AI review of such an action is refused.
- *Recheck inside the claim* (F18). Acceptance: a policy change between the recheck and the claim refuses the run, with an audit entry.
- *R5, the red-team corpus* (V6 said it is next). It needs no model.

**P1. Reviewer types, credentials, assignment (blocking)**
*Status, 2026-11-13: slice 1 done (D-19, D-28, A8; branch `p1-reviewer-types`). Slice 2 done (D-20 and D-36, boundaries 2 and 3; branch `p1-named-reviewers`). Slice 3 done: D-37, no approval of a request from the reviewer's own identity or person (A9; branch `p1-own-request`). Slice 4 done: boundary 1 (list, read, decide) and the credential (D-30; branch `p1-ai-credential`). Slice 5 done: the AI label on the review page, and the `ai:` actor in the audit (D-29; branch `p1-ai-label`). Two acceptance tests are still open: an agent key refused at the review API, and an AI key refused at MCP. Both hold by construction (the roles), and neither has a test of its own yet. See §11.*
- Decide D-19 (what `Ai` and `Either` mean), D-20 (named reviewers with assignments), D-26 (groups and refusals), D-27 (refusal penalties), D-30 (credential mechanism).
- The `ai` credential role, the assignment table, and enforcement at the three boundaries (5.2). Acceptance: an unassigned action is refused at list, read, decision and in the runner; an agent credential is refused at the reviewer endpoints; an `ai` credential cannot call MCP tools; an `ai` credential cannot approve an action it requested itself.
- AI history on the review page, labelled as AI (5.2, D-29). Acceptance: the review page shows reviewer, model and prompt version on an AI decision (if not already true, this is a feature to build).

**P2. Decision-aware refusals (blocking)**
- `AiMayRefuse` and `AiRefusalBudget` (D-21). `ReviewAuthority` and `AiReviewRules` change together (5.4, F12). `ReviewerRunner` line 35 changes (F17).
- Destructive ban scoped to approvals (F20). Acceptance: an AI refuses a Destructive action; an AI cannot approve it.
- The refusal budget is atomic (5.8, F23). Acceptance: two concurrent refusals against one remaining unit yield exactly one refusal.
- No penalties from AI refusals by default (D-27, F19). Acceptance: an AI refusal leaves the requester's level and trust unchanged.
- The A2A reply's startup check changes to "approval is human; AI may refuse" (5.5, F16). Acceptance: the startup refuses an AI approval on a reply and accepts an AI refusal.
- Request-changes follows the same rules as reject.

**P3. Yes, with undo for queued approvals (high; only after P1 and P2 pass)**
- The scheduled path (5.6): the final approval schedules; no bypass; typed `Approve` outcome; recheck inside the claim; persisted timing; overdue to a person; `ApproveDelay` (D-23).
- Acceptance: a final human approval schedules and does not execute; the delay cannot be skipped through the approval path; the scheduler refuses a `Reject` outcome; a restart during the window keeps the schedule and runs it when due; a late run is held; a suspended requester is held; an expired action does not run; a policy change refuses the run.

**P4. Delayed rejection (conditional; not in the first release)**
- Only after the human-override semantics are written down. The review recommends dropping it (D-24).

**P5. AI block proposal (later)**
- The identity definition (D-22), a human confirmation, expiry, reversal, and audit (D-4). Acceptance: no block takes effect without a person's confirmation.

**P6. Shared budgets (high)**
- The conditional shared counter (5.8). D-11. Acceptance: the concurrency tests in P2 and P0 pass across two processes, not only in one.

**R2. A model-backed reviewer (after P1)**
- `ModelReviewer` over a fake `IChatClient`: the packet, pre-scan, strict output, stance, optional dual review, and the `AiReviewer` config (V6 R2). Route A (5.2). Acceptance: the plumbing tested with a fake client (approve, reject, escalate, malformed output, timeout, injection hit), and one manual run against a real model. Then one real-model run once the owner picks a provider (D-13).

**Measurement (after R2; decide per D-9)**
- **R3, shadow mode**, if D-9 chooses it (V6 R3).
- **R4, sampling, demotion, drift**, if D-9 chooses them (V6 R4), using `more-extensions.md`.

**P7. Relax the human-only A2A pins (after the security tests)**
- Only where D-6 and D-7 authorise it, and only after P1 to P3 pass.

**Overseer (any time after P1)**
- Verify the kill switch covers `ai:` subjects, and the review page labels AI decisions (section 10).

## 7. Tests

The matrix as written in version 2 was too large to be useful. The tests are now decision tables, one per area, plus pairwise coverage for ordinary combinations, plus targeted tests for the dangerous interactions. Each table row has an expected result, written down before the code changes.

**Decision tables (one per area):**
- *ReviewAuthority* (tables A): reviewer mode, reviewer group (absent, member, non-member), approval count, SelfApproval, decision, subject kind.
- *AiReviewRules* (table B): risk (Read, Write, ExternalEffect, Destructive, unloaded), taint, decision, edits, history, budget.
- *Scheduling* (table D and 5.6): final approval, `ApproveDelay` on or off, stop, run now, expiry, suspension, policy change, restart, overdue.
- *Retry* (5.9): failure kind (none, transient, environment, contract, unknown outcome, bug), idempotent by key or not, reconciled or not, actor (human, AI, HTTP).
- *Identity and assignment* (5.2): reviewer identity (none, one AI, several AIs), assignment (assigned, unassigned), route (list, read, decision, runner), credential (agent, `ai`, admin).

**Pairwise coverage** for the ordinary combinations within each table. This is a tool-generated covering array, not the full product.

**Targeted interaction tests (the dangerous ones):**
- a Human-only refusal by an assigned AI (allowed), and the same AI's approval of it (refused);
- a reviewer-group action refused by an AI, and approved by an AI (refused);
- a tainted external action refused by an AI, and approved by an AI (refused);
- two AI reviewers each assigned to different actions, and each trying an action assigned to the other (both refused);
- two concurrent budget consumptions against one remaining unit, in one process and across two processes;
- suspension during a scheduled window;
- expiry during a scheduled window;
- a policy change between the recheck and the claim;
- a restart during a scheduled window;
- an `unknown_outcome` retried by an AI, by a human and through HTTP (all refused).

**Channel parity.** The same action, reviewed or submitted through MCP, A2A, Teams, HTTP and the in-process runner, gets the same governance decision. This is a separate test set, not a sample of the tables.

**Red team.** The corpus (R5) runs in CI with the fake model.

## 8. Built and not built

**Built:** `ReviewAuthority` (A1 to A7); `AiReviewRules` (B1 to B8), which is V6's R1; the queue's decision paths (C1 to C8); delayed execution, stop and hold (D1 to D5, and the undo window); `ClaimAndRunAsync` with `RecheckRefusal`, which runs before the claim (F18); the `ReviewerRunner` framework for an in-process `IReviewer`; `ai:` subjects minted in-process only; the `ai_review` audit.

**Not built:** the retry guard for everyone (F14, F11); the missing-snapshot guard (F9); the A2A submit pin (F7); the recheck inside the claim (F18); named AI reviewers with assignment and its three boundaries (5.2); the `ai` credential role (F1, Route B); `AiMayRefuse`, the refusal budget and the runner change (5.4, F12, F16, F17, F23); the Destructive scoping (F20); the typed scheduled outcome and the scheduled execution path (5.3, 5.6, F4, F21); `ApproveDelay`; delayed rejection (F5); an AI block (F6); a shared budget (F8); the AI history on the review page (5.2); a model-backed reviewer (R2); the red-team corpus (R5); shadow mode and sampling (R3, R4); the MCP channel defences (5.11); history-conditioned rules (5.10). No model-backed `IReviewer` exists in the projects read.

## 9. Decisions for the owner

Recommended defaults are in bold. Items marked "decided" were settled earlier. Section 0 lists the blocking ones.

| # | Decision | Options | Recommended |
|---|---|---|---|
| D-1 | One authority model for every channel | yes, no | **Yes.** Agreed in principle on 2026-11-13; record it. |
| D-2 | An AI refusal on a tainted external action | A: a person decides, as V6 says; **B: AI may refuse, never approve**; C: AI may approve with an added check | **B** |
| D-3 | AI refusals and the risk ceiling | V6: refusals are capped by the ceiling; **proposed: refusals ignore the ceiling** | **Ignore the ceiling** |
| D-4 | An AI blocking a sender | **propose only, a person confirms**; an automatic block that expires (then state the expiry) | **Propose only** |
| D-5 | Undo window default | **10 minutes, stop by the reviewer group** | **Decided 2026-10-18.** No change. |
| D-6 | AI decisions on the pair's submits | yes, no | **Yes, after P1 to P3 and R2 and the red-team corpus** |
| D-7 | AI decisions on partner replies | an AI refusal under D-2 = B; an AI approval stays with a person unless D-2 = C | **As stated** |
| D-8 | A retry counts as an AI approval (F10) | **keep counting it**; count separately | **Keep** |
| D-9 | Measurement before AI authority | (a) V6 as written: shadow (R3) then sampling (R4); **(b) decide directly for named low-risk tools, with sampling (R4) and demotion from day one**; (c) decide directly, no sampling | **(b).** The owner's view is against training-style shadowing. Without any measurement, a wrong AI approval has no measured basis. |
| D-10 | How an AI reviewer runs (route) | **(a) in-process model reviewer (Route A) as the default**; (b) external session (Route B) | **(a)**, with Route B allowed only under the one authority model in 5.2 and the credential mechanism in 5.2 |
| D-11 | Shared budget atomicity (F8, F23) | **(a) one shared conditional counter, counted in the decision's transaction**; (b) one runner per Host, enforced at startup | **(a)** |
| D-12 | "Yes, with undo" on irreversible actions such as partner replies (a timeout approval, per ADR-0030) | reversible actions only; irreversible too, with the risk accepted in writing; hold for a person's confirmation | **Reversible only**, unless the owner accepts the risk in writing |
| D-13 | First model provider (V6) | **OpenAI-compatible endpoint first, Anthropic next**; both | **OpenAI-compatible first** |
| D-14 | Default AI ceiling (V6) | Read; **Write**; ExternalEffect | **Write**, with ExternalEffect per tool after evidence. The code default is Write. |
| D-15 | AI suggestion visible to the human during measurement (V6) | yes; **no, visible after** | **No.** Applies only if D-9 = (a). |
| D-16 | Sampling rate in Decide mode (V6) | 5%; **10%, at least one a day per tool**; 20% | **10%.** Applies if D-9 = (a) or (b). |
| D-17 | Same model family as the requester (V6) | refuse; **warn**; ignore | **Warn** |
| D-18 | Reasons to the requester (V6) | full; **full, attempts counted**; generic | **Full, attempts counted** |
| D-19 | Which of `Ai` and `Either` allows a person to approve (F15) | (a) **`Ai`: AI approves; a person may refuse but not approve. `Either`: an AI or a person may approve**; (b) keep today's behaviour, where the two are identical | **(a). Implemented in P1 slice 1 (A8).** It gives the two names different meanings, without changing the enum. Consequence: an `Ai` action the AI may not approve (budget spent, risk above the ceiling, tainted external, edited arguments, four eyes) waits for a refusal or expiry. An operator who needs a person to approve uses `Either`. |
| D-20 | One AI reviewer or several (5.2) | one global reviewer; **several named reviewers, each with an assignment** | **Several named reviewers.** Built in P1 slice 2 as `AiReviewers` on each tool (D-36 for the default). The list and read boundary is not yet built. |
| D-21 | AI refusals (5.4) | **`AiMayRefuse` per tool, default false; a refusal budget per requester and tool**; refusals unlimited | **Per-tool opt-in with a budget** |
| D-22 | An AI-proposed block (D-4): who is blocked | an A2A partner; an agent identity; **both, with the identity recorded**; expiry default | **Identity recorded; expiry 24 hours, renewed by a person** |
| D-23 | Name and default of the queued-approval undo setting (`ApproveDelay`) | **`ApproveDelay`, default off, per tool**; a different name | **As stated, default off** |
| D-24 | "No with undo" in the first release | keep as P4; **drop from the first release** | **Drop** |
| D-25 | The reviewer's route, one authority model | **one authority model, two hosts** (5.2); two separate grant mechanisms | **One authority model** |
| D-26 | Refusal across the Human-only and reviewer-group boundaries (second review, point 1) | **refusals may cross both boundaries; approvals never do**; refusals only inside the boundaries | **Refusals may cross both** |
| D-27 | Penalties from an AI refusal (F19) | **none by default**; the existing level and trust effects; an operator setting | **None by default** |
| D-28 | What distinguishes `Ai` from `Either` (F15) | (a) as in D-19; (b) drop the redundant value | **As in D-19. Implemented with it (A8).** Option (b) is not taken; if the owner prefers fewer values, (b) is a later contract change |
| D-29 | How AI decisions are recorded and labelled (second review, point on honesty) | **the `ai:` actor and an AI event; labelled AI on the review page; documents say "delegated authority", never "human oversight"** | **As stated. Built in P1 slice 5** (the `ai:` actor, the `ai_review` event and the page label; see 5.2). |
| D-30 | The external reviewer credential (5.2, P1) | **issued to a named reviewer identity, stored as a hash, shown once, rotated by an operator; agent profiles may not hold the reviewer role** | **As stated. Built in P1 slice 4**: `AiReviewers` holds the hash in the HTTP config; `--new-ai-reviewer` shows the secret once; rotation is a new hash; an API key already cannot carry the reviewer role (startup check). |
| D-31 | The refusal budget's unit and scope | **per requester and tool**; per tool only | **Per requester and tool** |
| D-32 | Retry of an unknown outcome (P0, F14, F11, decided by the implementer while the owner is away) | An AI never retries a tool that is not idempotent by key. A person may retry an unknown outcome only when the review records the check that the effect did not happen (the review reason). Non-retryable failures are refused for everyone. | **As implemented in P0** (`OpenTail.Orca/Runtime/RetryRules.cs`); the existing durability test already expects this path |
| D-33 | The policy recheck and the claim (F18) | Not reachable in this build: the policy is fixed for the life of a process (`PolicyResolver.PolicyHash` is computed once; there is no reload). The recheck stays before the claim. It moves inside the claim only if hot reload is added. | **Recorded, no change** |
| D-34 | AI review of an action with no policy snapshot (F9) | An AI decides nothing about such an action (`AiVerdictAsync` refuses). Humans are unchanged. | **As implemented in P0** |
| D-35 | The wording of the AI limit messages B3, B4 and B7 once `Ai` approval is a person's no-go (P1) | keep "a person decides" (wrong for an `Ai` policy); name what the AI may not do and leave the "who" to the policy | **Reworded** in `AiReviewRules.cs` (P1 slice 1). The tests keep the substrings that still hold. |
| D-36 | Who may decide an AI tool that has no assignment list (the D-20 migration) | (a) any AI reviewer, as before; (b) nobody until assigned | **(a), as built.** An `AiReviewers` list binds once set. A startup warning for an AI tool with no list is not built. |
| D-37 | An AI reviewer approving a request from its own identity or its own person (four eyes for AI) | (a) refuse approval only, refusals unaffected; (b) refuse every decision; (c) allow | **(a), as built (A9).** A refusal runs nothing, so it is not restricted. |

**Status of the other defaults (2026-11-13, autonomous loop).** D-19, D-28 and D-35 are implemented in P1 slice 1. D-20 and D-36 are implemented in P1 slice 2, boundaries 2 and 3. D-37 is implemented in P1 slice 3. D-30 and boundary 1 are implemented in P1 slice 4. D-29 (AI label on the review page) is implemented in P1 slice 5. D-26 and D-27 are accepted and built in P2 with `AiMayRefuse`. D-21 to D-25 and D-31 belong to later phases and are unchanged.

## 10. Not verified

- Whether a queued action can have a null `PolicySnapshot` (F9).
- Whether `ClaimAndRunAsync`'s store supports a policy-hash compare inside the claim transaction (5.9). The store API was not read in full.
- Whether `RunDueDelayedAsync` or `ClaimAndRunAsync` checks requester suspension (5.6).
- Whether the review page labels AI decisions, and shows model and prompt version (5.2, 5.13). The owner asked this earlier. **Answered in P1 slice 5:** it did not; it now does (`AdminPage.cs`, `AdminServer.cs` detail). Not checked in a real browser.
- Whether `--suspend` applies to `ai:` subjects (5.13).
- Whether an interrupted retry is reachable and harmful (F11), and whether the endpoint's retry (F14) can be reproduced. Both are read from code, and neither has been run.
- How an external approver session would get an `ai` credential in practice (D-30). The command exists (`--new-ai-reviewer`, P1 slice 4) and the endpoints refuse everything else. No real external session has been run against it.
- Whether `Microsoft.Extensions.AI` is referenced by Orca today. V6 says it depends only on the abstractions; not checked.

## 11. Implementation log and history

| Phase | Date | What |
|---|---|---|
| R1 | 2026-10-16 (V6 status; commit not recorded here) | AI reviewer limits in `AiReviewRules`, enforced by the queue and used by the runner: `AiMaxRisk` (default Write, never Destructive), taint, no edits, not the second pair of eyes, `AiMaxRejections`, `AiBudget`, and the `ai_review` audit with model and prompt version. `AiReviewLimitsTests`; POLICY.md *AI reviewers*. |
| Undo window | 2026-10-18 | Execute with a delay, Stop, run-now and hold. `docs/design/undo-window.md`. |
| Gate plan | 2026-11-13 | First written as a separate proposal, without reading V6. Merged with V6 the same day, with V6 kept as a pointer. |
| Gate plan, version 2 | 2026-11-13 | Revised after the first external review. No code changed. |
| P0 characterisation (branch `p0-characterise`) | 2026-11-13 | `CharacterisationTests`: 16 tests pin table A (ReviewAuthority, rows A1 to A7 and the Ai/Either behaviour, F15) and table B (AiReviewRules, rows B1 to B8) as the code is today. Tables C and D are covered by the existing suites (`ApprovalControlTests`, `AiReviewLimitsTests`, `DurabilityTests`) and have no new pins; adding them is a later task. Full Orca test project: 1358 passed, 0 failed. |
| P0 (branch `p0-gate-hardening`) | 2026-11-13 | Retry rule for everyone (D-32); the A2A submit pin (F7); AI review refused without a snapshot (D-34); the recheck decision (D-33). Tests: `P0GateTests` (10 cases). Full Orca test project: 1342 passed, 0 failed. |
| P1 slice 1 (branch `p1-reviewer-types`) | 2026-11-13 | D-19 and D-28: `ReviewAuthority` row A8. A person may not approve or retry an `Ai` action (the queue audits `review_refused`); a person may still refuse it. `Either` is unchanged. B3, B4 and B7 no longer say "a person decides" (D-35). `CharacterisationTests`: the P0 pin that `Ai` and `Either` behaved alike is replaced by A8 tests; A3 uses `Either`, since its subject is the group check. `P1ReviewerTypesTests`: four queue tests. POLICY.md *Reviewer* and *AI reviewers* updated. **Not done in P1:** named reviewers and assignment, with the three boundaries (D-20); the external credential (D-30); the AI label on the review page (D-29); the "AI cannot approve its own request" test. Full Orca test project: 1364 passed, 0 failed. |
| P1 slice 2 (branch `p1-named-reviewers`) | 2026-11-13 | D-20 and D-36. `ToolPolicy.AiReviewers`: the named AI reviewers assigned to a tool. It is serialised only when set, so policies without it keep their hash. Enforced at the queue decision (`ReviewAsync`, audited `review_refused`, before the AI limits) and in `ReviewerRunner` (an unassigned action is skipped before the reviewer sees it). Validation: names must be valid, the list non-empty, and the tool queued and open to an AI. POLICY.md *Reviewer*, *AiReviewers*, *AI reviewers*. `P1ReviewerTypesTests`: eleven new tests (queue refusal, assigned decision, runner skip and offer, the default without a list, canonical text, validation). **Not done:** boundary 1 (list and read; no HTTP path yields `ai:` yet), the credential (D-30), the AI label on the review page (D-29), and the own-request test. Full Orca test project: 1375 passed, 0 failed. |
| P1 slice 3 (branch `p1-own-request`) | 2026-11-13 | D-37: `ReviewAuthority` row A9. An AI reviewer may not approve or retry a request from its own identity, or from its own person in `People`; refusals are not affected. Test: `P1ReviewerTypesTests.An_ai_reviewer_may_not_approve_a_request_from_its_own_identity_or_person_but_may_refuse_it`. Full Orca test project: 1376 passed, 0 failed. |
| P1 slice 4 (branch `p1-ai-credential`) | 2026-11-13 | Boundary 1 and D-30. `AiReviewRules.Offered` (the policy lets an AI decide, and the action is assigned to it), used by the runner, the list, the read and the decision. The standalone server and the hosted server accept an AI reviewer's secret, held as a hash (`AdminOptions.AiReviewers`; `HostHttpOptions.AiReviewers` in the HTTP config). The identity is `ai:<name>` with the review role only. `AdminEndpoints`: an endpoint filter refuses an AI identity every route but three (`AllowAiReviewer`); an unoffered action is not found. `--new-ai-reviewer <name>` issues a secret once. `AiReviewerCredentialTests`: eight tests (list and read, decision, not offered, refused routes, wrong secret and hash-as-secret, admin unchanged, startup validation, hosted role). Full Orca test project: 1384 passed, 0 failed. **Not done:** the list applies its row limit before the AI filter, so an AI may see fewer rows than the limit; the AI label on the review page (D-29). |
| P1 slice 5 (branch `p1-ai-label`) | 2026-11-13 | D-29. `ActionQueue.ReviewAsync` is now a wrapper: it audits every AI decision as `ai_review` under its `ai:` subject, whichever channel brought it (the runner no longer writes its own copy). An approval, retry or rejection by an AI records `ai:<name>` as its actor detail, not the bare name. The review detail carries `byAi`, `aiModel`, `aiPromptVersion` and `aiChecks`; the review endpoint takes them from an AI reviewer's body (its claims, clipped). The page prints "AI decision by …" with the model, the prompt version and the checks. `P1ReviewerTypesTests`: queue audit and one runner line. `AiReviewerCredentialTests`: HTTP fields and the page label. Full Orca test project: 1387 passed, 0 failed. Not checked in a browser. |
| Gate plan, version 3 | 2026-11-13 | Revised after the second external review; moved into `OpenTail.Orca`. Teams sentence in `OPERATIONS.md` corrected. No code changed. |

## 12. Self-review of this revision

- Every point of the second review was checked before it was accepted. Section 13 records each disposition.
- The code claims that were new in this revision were read: the runner's eligibility filter (`ReviewerRunner.cs:35`), the claim order in `ClaimAndRunAsync` (the recheck before `ChangeAdmittedAsync`), the Teams sentence in `OPERATIONS.md`, and the Destructive check in `AiReviewRules.cs:27-28`.
- The two claims in the review that I did not repeat are not in this plan: that changing `AiReviewRules` alone cannot make refusals work (true, and now covered in F12 and F17), and that a shared counter is required (accepted, 5.8).
- Not done: no tests were run, no code was changed, and no second reader has checked this revision. The findings marked "read from code; not reproduced" need a reproduction before any fix relies on them.

## 13. External reviews: disposition

**First review (merged draft, 2026-11-13).** Seven points on the refusal design, the delayed rejection, the identity, the Ai/Either modes, the retry path and the test matrix. All were accepted and handled in version 2 (F12 to F16; 5.2 to 5.6; 7). No point was rejected.

**Second review (version 2, 2026-11-13).** Points, and where handled:

| Review point | Disposition | Where handled |
|---|---|---|
| 1. The in-process runner never offers Human-only actions to an AI | **Accepted.** Confirmed at `ReviewerRunner.cs:35`. | F17; 5.4; P2 |
| 1 (cont.). Do reviewer groups restrict refusals too, or only approvals? | **Accepted as a decision.** Recommended: refusals may cross groups; approvals never. | 0; 5.4; D-26 |
| 2. Assignment must be enforced at the queue boundary, and in the runner; the external credential must be out of the agent's reach | **Accepted.** Three boundaries, each with a test. | 5.2; P1 |
| 3. The refusal budget needs atomic accounting | **Accepted.** | F23; 5.8; P2, P6 |
| 4. The queued approval delay needs an explicit execution path, including the final approval, typed outcomes, recheck, persistence, overdue handling and the policy race | **Accepted.** Confirmed at the quorum-reached `ExecuteAsync` call (F21). The policy race is F18. | F18, F21; 5.6; P3 |
| 5. Retry must be blocked for unsafe outcomes for everyone, including HTTP | **Accepted.** | 5.9; P0 |
| 6. Refusal rules must state their full scope: the Destructive ban applies to approvals only; refusals may change requester penalties | **Accepted.** Confirmed at `AiReviewRules.cs:27-28` (F20) and the Reject branch (F19). | F19, F20; 5.4; D-27 |
| 7. Ai and Either are indistinguishable | **Accepted.** Confirmed. | F15; D-19; D-28 |
| 8. The test matrix is too large | **Accepted.** Replaced with decision tables, pairwise coverage and targeted tests. | 7 |
| The Teams update | **Accepted.** The `OPERATIONS.md` sentence was wrong; corrected. | F22; `OpenTail.Orca/OPERATIONS.md` |
| Honesty: AI approval is not human oversight | **Accepted.** | 1; 5.13; D-29 |
| Second review's own point: the review refers to the old repo | **Noted.** The plan now lives in `OpenTail.Orca`, and paths are updated. The code is unchanged in structure. | Header |
