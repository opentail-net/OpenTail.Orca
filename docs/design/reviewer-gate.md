# Reviewer gate: one gate for every tool

Status: **proposal for owner decision. Nothing in this document is built.** Written 2026-11-13 from the code as read that day. Line
numbers are from those reads. Section 11 says what was checked and what was not.

## 1. What is asked

- One reviewer gate for every tool and every channel (MCP, A2A, and any later channel). The owner agreed to this in principle on 2026-11-13.
- AI reviewers may decide actions, within limits. The owner chooses, per action, whether to approve directly or to oversee the AI.
- Five verdicts for an AI reviewer: yes now, yes with undo, no now, no with undo, and no and block the sender.
- The owner's time is the scarce resource. Approval by a person should be the exception, not each step.

Out of scope: the Teams bot's own behaviour, the pair of sessions, and the A2A kit. They appear only where they touch the gate.

## 2. Contracts this must satisfy

| # | Member | Where | Promise | Who relies on it |
|---|---|---|---|---|
| 1 | `ReviewAuthority.Check(r, decision, subject, groups, people)` | `OpenTail.Host/Policy/ReviewAuthority.cs:52-80` | Says who may make this decision. Refuses an AI on a human-only action (57-59). Refuses an AI on a group action and anyone outside the group (61-65). Refuses self-approval under `Forbid` (67-74). Refuses an unknown identity on a several-approver action (76-77). | `ActionQueue.ReviewAsync`, before any state change |
| 2 | `AiReviewRules.Check(r, decision, risk, editsArguments, history)` | `OpenTail.Host/Policy/AiReviewRules.cs:22-48` | The AI limits. Pure. Order: destructive, risk ceiling, taint, refusals, then edits, four-eyes and budget for approvals. | `ActionQueue.AiVerdictAsync`; `ReviewerRunner` (to decide what to offer) |
| 3 | `AiReviewRules.IsAiRefusal`, `IsAiApproval` | `AiReviewRules.cs:60-64` | Which past reviews count as AI refusals and AI approvals. `Retry` counts as an approval. | `AiHistoryAsync` |
| 4 | `ActionQueue.ReviewAsync(actionId, review)` | `OpenTail.Host/Runtime/ActionQueue.cs`, from about line 360 | The only path that changes an action's decision. Stamps edit digests, runs `ReviewAuthority` and then the AI check, then switches on the decision. | `AdminServer` `/actions/{id}/review` (`OpenTail.Host.Admin/AdminServer.cs:241`); `TeamsBot.cs:281`; `ReviewerRunner.cs:60` |
| 5 | `ActionQueue.AiVerdictAsync`, `AiHistoryAsync` | `ActionQueue.cs:529-560` | Adds store history to `AiReviewRules.Check`: refusals up the `ParentActionId` chain, and approvals inside the `AiBudget` window. | `ReviewAsync`; `ReviewerRunner` |
| 6 | `ActionQueue.StopAsync`, `RunDueDelayedAsync`, `ExpireDueAsync` | `ActionQueue.cs` around 596-660 | A delayed action is `Pending` with `RunAt`. It runs when due, as a system approval (`delay_elapsed`), unless stopped. The requester or the reviewer group may stop it. A run more than one delay late is held for a person (`delay_held`). | Scheduler; `AdminServer` `/actions/{id}/stop` (`AdminServer.cs:307`) |
| 7 | `IReviewer` | `OpenTail.Host.Abstractions/Actions/Interfaces.cs:124-128` | `Name`, and `ReviewAsync(action)` returning a review or null (abstain). | `ReviewerRunner` only. The only implementation in the projects read is `DelegateReviewer` (`ReviewerRunner.cs:13`). |
| 8 | `ReviewerRunner.RunOnceAsync` | `OpenTail.Host/Runtime/ReviewerRunner.cs:28-62` | Offers only actions whose snapshot says `Ai` or `Either` (line 35) and that `AiVerdictAsync` allows (36). Stamps the identity as `ai:<name>` (54), and the input hash. Audits `ai_review`. | Runs in-process only |
| 9 | `Subjects.Ai`, `Subjects.IsAi` | `OpenTail.Host.Abstractions/Identity/CallerIdentity.cs:28-38` | `ai:<name>` is the AI subject. `IsAi` is the prefix test the rules use. | Every AI rule |
| 10 | HTTP reviewer identity | `AdminServer.cs:141-160`, roles at `AdminServer.cs:51-60` | A reviewer token yields `admin:<name>` (155-157). Roles are `opentail.review`, `opentail.admin` and `group:*`. No HTTP credential yields `ai:`. | The review endpoint |
| 11 | `ToolPolicy` defaults | `OpenTail.Host.Abstractions/Policy/ToolPolicy.cs` | `Reviewer` Human (46). `RequiredApprovals` 1 (55). `SelfApproval` Allow (57). `DefaultDelay` 10 minutes (71). `AiMaxRisk` null, which means Write (90, and `AiReviewRules.cs:16`). `AiMaxRejections` 2 (93). `AiBudget` null (96). | Everything above |
| 12 | `PolicyConfig` validation | `OpenTail.Host/Policy/PolicyConfig.cs:215-258` | Reviewer, group, approvals and expiry only on Queue rules (223-224). A delay only on Execute (217, 246-247). `AiMaxRisk` not Destructive (254-255). `AiBudget` needs positive values (257-258). | Startup |
| 13 | `PolicyResolver` delays | `OpenTail.Host/Policy/PolicyResolver.cs:196-206` | A rule's delay or a trust ramp's delay is applied to Execute decisions. | `ActionQueue` |
| 14 | A2A startup checks | `OpenTail.Host.A2A/A2AFrontDoor.cs:56-84`, warning at 133-134 | The reply must be Human, Queue or Deny, with no edits, no Execute rule and no non-Human rule (68-79). An Execute submit gets a warning. There is no reviewer check on the submit. | Startup |
| 15 | A2A tool risks | `OpenTail.Host.A2A/A2ASubmitModule.cs:28, 35` | `a2a.submit` is Write. `a2a.reply` is ExternalEffect. | Both gates |
| 16 | Teams chat decisions | `OpenTail.Host.Http/Teams/TeamsBot.cs:223, 366-374` | A chat decision is allowed up to `ChatMaxRisk` (default ExternalEffect). Its subject is `teams:<id>`, a human. | The human path; must stay |
| 17 | One queue for every channel | `OpenTail.Host/Mcp/QueuedToolFunction.cs`; `OpenTail.Host.A2A/A2ARequestHandler.cs`, `A2ASubmitModule.cs`, `A2AWork.cs` | MCP and A2A calls both reach the queue. | Channel parity (section 5.1) |

## 3. Existing behaviour, branch by branch

### Table A: `ReviewAuthority.Check`

| Row | Condition | Result | Audit |
|---|---|---|---|
| A1 | AI subject, `Reviewer = Human` | Refused: needs a human | `review_refused` |
| A2 | `ReviewerGroup` set, AI subject | Refused: an AI cannot stand in | `review_refused` |
| A3 | `ReviewerGroup` set, subject not in the group | Refused | `review_refused` |
| A4 | Approve or Retry, `SelfApproval = Forbid`, subject unknown | Refused | `review_refused` |
| A5 | Approve or Retry, `Forbid`, subject is the requester's person (`People`) | Refused: cannot approve own | `review_refused` |
| A6 | `RequiredApprovals > 1`, subject unknown | Refused | `review_refused` |
| A7 | Otherwise | Allowed | none here |

### Table B: `AiReviewRules.Check` (order matters; the first refusal wins)

| Row | Condition | Result, for any decision unless noted |
|---|---|---|
| B1 | Effective risk Destructive, or the tool is no longer loaded | Refused |
| B2 | Risk above the ceiling (`AiMaxRisk`, default Write) | Refused |
| B3 | Action tainted and risk ExternalEffect | Refused, **including reject** (F2) |
| B4 | Refusals in the chain at or above `AiMaxRejections` (default 2; 0 turns it off) | Refused, **including reject** (F3) |
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
| C6 | Retry | Failed becomes Approved and runs again at once. |
| C7 | Reject | Rejected, resolved as Denied. Trust is demoted if it had failed. An AI session's requester is recorded with `levels`. |
| C8 | RequestChanges | ChangesRequested |

### Table D: delayed execution

| Row | Situation | Result |
|---|---|---|
| D1 | Execute rule with a delay | Pending, `RunAt` set, announced |
| D2 | Stopped before `RunAt` by the requester or the reviewer group | Cancelled. Recorded as a Reject by the stopper. |
| D3 | `RunAt` passed, within one delay | System approval (`delay_elapsed`), then run |
| D4 | `RunAt` passed by more than one delay | `delay_held`: `RunAt` cleared, a person decides |
| D5 | `ExpireAfter` passed | Expired |

## 4. Findings

Each was verified in the code cited. The plan has to fix them before the matching feature is safe.

**F1. An approver session is treated as a human.** Only `ReviewerRunner` mints `ai:` subjects (`ReviewerRunner.cs:54`). An HTTP reviewer token yields `admin:<name>` (`AdminServer.cs:155-157`). So an approver session that uses a token is audited as a person, and none of rows B1 to B7 apply to it. This blocks the approver-assistant as the code stands.

**F2. The taint rule also stops AI refusals.** Row B3 runs before the decision is looked at, so an AI cannot reject a tainted external action such as a partner reply. That removes "no, immediately" for replies.

**F3. The AI limits also apply to refusals.** Rows B2 and B4 apply to any decision. An AI cannot refuse a `Destructive` tool, which is harmless, and after two refusals in a chain it cannot refuse again.

**F4. "Yes, with undo" has no path.** Delays exist only for Execute rules (`PolicyConfig.cs:217, 247`). An approved queued action runs at once (C5). The delayed-run shape (D1 to D5) exists and could be reused.

**F5. "No, with undo" has no state.** A rejection is final at once (C7).

**F6. "No and block the sender" has no AI path.** Suspension is a human command (`--suspend`, in `OPERATIONS.md`) and an automatic level (`--levels`). A block by an AI would be new authority.

**F7. The A2A submit pin is not in code.** The design says `a2a.submit` is `Reviewer: Human` (`docs/design/a2a.md:62`). `A2AFrontDoor.cs` checks only the reply (70-79). The submit gets only a warning for Execute (133-134).

**F8. The AI budget is not atomic.** `AiHistoryAsync` counts approvals before the decision is written. The code's own comment says two AI reviewers could both take the last approval, and that one runner per Host is the supported shape. Nothing enforces that shape.

**F9. A missing policy snapshot falls back to defaults.** `AiReviewRules` treats a null policy as the defaults: ceiling Write, two refusals, no four-eyes. `ReviewAuthority` guards on `policy is not null`. Whether a queued action can have a null `PolicySnapshot` is **not verified**. If it can, an AI could approve Write actions by default.

**F10. A retry counts as an AI approval** (`AiReviewRules.cs:38, 64`). An AI can re-run a failed action under the same limits. Whether that is intended is open (D-8).

## 5. Target design

### 5.1 One gate, keyed on risk and taint, not on channel

Every tool call goes through `ActionQueue`. A channel adds no verdict rules of its own. Channel-specific pins, such as the A2A reply pin, are removed once 5.2 and 5.4 are in place (5.5).

### 5.2 An AI identity that HTTP can use (fixes F1)

Add an `ai` role to HTTP credentials (API keys or reviewer tokens), which yields `Subjects.Ai(name)`. Rules:
- an AI credential cannot carry `opentail.admin` or group roles;
- an AI credential cannot call MCP tools (`mcp:tools` is never granted to it);
- its decisions are audited with the `ai:` subject and the existing `ai_review` event.

This must land before any external AI decision counts.

### 5.3 The five verdicts, mapped to the queue's primitives

| Verdict | Mechanism | Status |
|---|---|---|
| Yes, now | Approve (C5) under B1 to B8 | Exists |
| No, now | Reject (C7), under B1 to B4 and the fixed B3 (5.4) | Exists |
| Yes, with undo | Approve recorded; run deferred by D1 to D3; Stop allowed by D2 | **New**: approve-with-delay for Queue tools |
| No, with undo | Reject recorded; made effective at `RunAt` by D1 and D2 | **New**: delayed rejection |
| No, and block | Reject, plus a block proposal. The block takes effect only after a person confirms (first version). | **New** |

### 5.4 Taint and refusals (decision D-2)

- **A.** Keep it: an AI never decides a tainted external action (today).
- **B.** An AI may reject or request changes on any action, including tainted external ones. An AI may approve only untainted actions. **Recommended.**
- **C.** An AI may approve tainted external actions with an added check. Not recommended now.

Reason for B: a reviewer that has been fooled into "no" stops one action. A reviewer that has been fooled into "yes" sends one. B keeps the injection risk in one direction. Its cost is that a fooled AI can block legitimate replies. B4 bounds that.

Refusals should also be exempt from the risk ceiling (D-3), since a refusal has no side effect.

### 5.5 A2A pins

Remove the `Reviewer: Human` pins only after 5.2 and 5.4 are in place and the owner has signed off. Until then, keep the reply pin, and add the submit pin that the design already states (F7), so the code matches the design.

### 5.6 Undo windows

Default 10 minutes (`ToolPolicy.DefaultDelay`). A shorter window needs an explicit per-tool setting. An undo only helps if a person can see the decision in time, so a 30-second window is mostly a delay.

### 5.7 Default closed

New tools default to `Reviewer: Human`, as today. Every new channel must go through `ActionQueue`. A test per channel checks that an AI cannot decide a Human-only action through it.

### 5.8 Atomic AI budget (fixes F8)

Count approvals in the same store transaction as the decision. The alternative, a startup check for one runner per Host, is weaker and is not preferred.

### 5.9 Missing snapshot (fixes F9)

If a snapshot can be null, the queue refuses to queue such an action. This must be verified first (section 10).

## 6. Phases

Each phase is a separate commit. Each runs the full test suite. Each diff touching identity or the queue gets an owner review.

- **P0. Characterise, no behaviour change.** Tests that pin tables A to D as they are. Verify F9. Decide on F7 with the owner.
- **P1. AI identity (5.2, F1).** Tests: an AI credential gets an `ai:` subject; it is refused for admin and MCP; a reviewer token still gets `admin:`.
- **P2. Refusals (5.4, F2, F3).** Tests: an AI can reject a tainted reply, but cannot approve it; refusals are exempt from the risk ceiling if D-3 is yes.
- **P3. Yes, with undo (5.3, F4).** Tests: approve-with-delay leaves the action Pending with `RunAt`; Stop before `RunAt` cancels it; it runs at `RunAt`; a late run is held; an approval counts once; a restart during the window keeps the action.
- **P4. No, with undo (F5).** Only after P3.
- **P5. AI block (F6).** The proposal is recorded; a person confirms; the block expires; everything is audited. Only after P1 to P3.
- **P6. Atomic budget (F8).**
- **P7. Remove the A2A Human pins (5.5).** Update `a2a.md`, `POLICY.md`, `OPERATIONS.md`, the checklist and `STATUS.md`.

## 7. Test matrix

For tables A and B, generate every combination of:

- tool risk (Read, Write, ExternalEffect, Destructive, unloaded);
- taint (yes or no);
- decision (approve, reject, request changes, retry);
- subject (human, AI, unknown);
- history (refusals at or below the maximum; budget used or not).

Each combination has one expected verdict, written down before the code changes. Add three further tests: channel parity (the same action through MCP and A2A gets the same verdict); fail closed (an unknown tool is refused for an AI); and a double approval counts once.

## 8. Built and not built

**Built:** `ReviewAuthority` (A1 to A7); `AiReviewRules` (B1 to B8); the queue's decision paths (C1 to C8); the delayed run and stop (D1 to D5); the `ReviewerRunner` framework, which runs an in-process `IReviewer`; `ai:` subjects, minted in-process only.

**Not built:** any external AI identity (F1); approve-with-delay (F4); delayed rejection (F5); an AI block (F6); an atomic AI budget (F8); the A2A submit pin (F7); the taint change (5.4); per-tool undo defaults (5.6). No model-backed reviewer exists in the projects read. `POLICY.md` lists one as future work.

## 9. Decisions for the owner

- **D-1.** One gate for every channel. Agreed in principle on 2026-11-13. Record it formally.
- **D-2.** Taint and AI refusals: A, B or C. Recommended: **B**.
- **D-3.** Refusals exempt from the risk ceiling. Recommended: yes.
- **D-4.** AI block: propose only, with a person confirming (recommended), or an automatic expiring block. If the latter, what expiry default?
- **D-5.** Undo window default: 10 minutes (recommended). Shorter only for low-risk tools that are visible in chat.
- **D-6.** Submits to an AI for the pair's partners: yes, after P1 and P2 (recommended).
- **D-7.** Replies to an AI: an AI "no" is allowed after P2 under B. An AI "yes" stays human unless D-2 is changed to C.
- **D-8.** Retry (F10): keep it as an AI approval, or count it separately.

## 10. Not verified

- Whether a queued action can have a null `PolicySnapshot` (F9). This decides whether 5.9 is urgent.
- Whether the review page labels AI decisions distinctly. The owner asked this earlier, and it is still open.
- How an approver session would get an `ai` credential in practice: who issues it, and how it is rotated.
- Whether an approve-with-delay keeps the audit events unchanged.
- Whether the `ai` role interacts with the existing `People` map and self-approval checks (A5, B6).

## 11. Self-review

This plan was checked against the code and documents listed in section 2, by reading each cited line.

- Every default in section 2 comes from `ToolPolicy.cs`, `AiReviewRules.cs` or `PolicyConfig.cs`.
- Every finding in section 4 cites the line it depends on.
- Every row in tables A to D comes from a line read in `ReviewAuthority.cs`, `AiReviewRules.cs` or `ActionQueue.cs`.

Not done: no tests were run for this document, and no second reader has reviewed it. Section 10 lists what remains unverified. Nothing in section 8 is claimed as built beyond what the code shows.
