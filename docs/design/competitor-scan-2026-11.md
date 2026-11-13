# Competitor scan, November 2026: findings and expansion plan

Status: **record and proposal.** Nothing here is built, and no existing design document has been amended. Written 2026-11-13.

**Provenance.** The scan was made outside this repository, as a comparison of four products, and the owner supplied it. The competitor claims are reported as stated in the scan. **They are not verified** (section 2). The scan's claims about this repository were checked against the files named in section 3.

## 1. Summary

- Four products were compared: Permit.io, Arcade, Composio and HumanLayer. The scan's conclusion is that none is a direct competitor to Orca's approval gateway, and that none is a reason to change the architecture.
- Six expansions are proposed (section 6): delegated authority (E1), connected accounts (E2), human input beyond approval (E3), integration discovery (E4), event idempotency (E5), and retrieval kept apart from action (E6).
- Most of the scan's "existing design" claims hold. Two need correcting. Orca sign-in items are in Connectivity's plan, section 3, not in Orca document. No design document covers a structured question from an agent to a person.
- The check found three things the scan missed, which matter more than its list. (a) The undo window is already built (section 4). (b) An AI-reviewer plan already exists, `docs/MCP-HOST-V6-PLAN.md`, and it overlaps the reviewer-gate plan written earlier (section 5). (c) The owner's stated preference conflicts with V6's default.

## 2. What the scan reports (unverified)

| Product | Reported offering | Distinction the scan draws | Verify before relying |
|---|---|---|---|
| Permit.io | Human and agent identity, delegated trust, RBAC, ABAC and ReBAC, consent, audit, per-tool approvals, time-limited access | An authorisation platform with its own policy control plane | Whether human-in-the-loop approval is an Enterprise-only feature |
| Arcade | Multi-user MCP gateway, large tool registry, OAuth, per-user authorisation, credential management | A managed execution runtime, not only a proxy | Per-user OAuth and action-level authorisation as documented |
| Composio | Large toolkit catalogue, connected accounts, OAuth, dynamic discovery, triggers, human approvals | A hosted integration ecosystem. Its approval feature is labelled experimental | The experimental label; reliance on MCP elicitation |
| HumanLayer | Coding-agent tasks, sessions, remote execution, checkpoints, review | An agent-operation environment, not a governance gateway | Whether the earlier SDK is superseded and removed |

## 3. The scan's claims against this repository

"Matches" means the repository says what the scan says.

- **Human and agent permissions intersect (scan H1).** Matches. `docs/design/roles-and-permissions.md`, lines 130-135 ("Later: delegation"), says the effective tools are the profile intersected with what the person may do. Not built.
- **Delegated consent, per-user token storage and refresh (scan H2).** Matches, with a correction. These items are in Connectivity's plan, section 3 (`docs/plan.md`, lines 30-39), and all are still unchecked. The scan called it Orca sign-in plan.
- **Structured human input (scan H3).** Partly. `docs/design/work-and-continuations.md` covers parking (continuations), resume items, notes and work-item fields. A structured question from an agent and a structured answer from a person appear in no design document. A search for "question" found nothing. This is new work.
- **Integration discovery (scan H4).** Matches. `--list-modules` exists, and I ran it. `docs/MODULES.md` says a tool with no policy entry is not offered. Per-agent profiles exist.
- **Triggers (scan C3).** Matches. Connectivity plan, section 21 (lines 240-257), lists schedule, webhook, Orca event and connectivity-watch triggers. Proposed on 2026-10-20; nothing built.
- **Connectivity kept apart from live actions (scan C2 and the roadmap).** Consistent with the plan's own separation. Not checked line by line.

## 4. What the scan missed: the undo window is built

`docs/design/undo-window.md` (status: built on 2026-10-18) implements the pattern the owner asked about:

- a `DelayFor` on Execute rules: the action is announced, runs after the delay unless stopped, and is audited either way;
- Stop, run-now and hold (hold turns the action into an ordinary queued action);
- a default delay of 10 minutes, with anyone in the reviewer group able to stop it; taint still forces Queue.

The design rests on the same reasoning the owner gave: queued approvals cost people, and approval fatigue is the weak point of approval systems.

This matters for the reviewer-gate plan. Its section 5.3 proposes a new path for "yes, with undo". Execute-with-delay already exists. What is still new is delayed approval of a queued action, and that needs its own decision.

## 5. Overlap with the reviewer-gate plan: reconcile before going further

`docs/design/reviewer-gate.md` (on `main` since commit `f534c8b`) covers the same system as `docs/MCP-HOST-V6-PLAN.md`. I wrote the gate plan without reading V6 first. Our rules say to read the existing contracts before writing a plan, and V6 is one of them. That was my error. It needs correcting before either plan is used for a decision.

V6 already has R1, marked done: the AI ceiling (`AiMaxRisk`), taint escalation, no edited approvals, four eyes, quorum, a budget and an AI limit on refusals. Still open in V6: a model-backed reviewer (R2), shadow mode and measurement (R3), and a red-team suite (R5).

Where the two plans differ:

| Point | V6 | `reviewer-gate.md` | Owner's stated view |
|---|---|---|---|
| Tainted external action, AI "no" | A person decides; an AI cannot decide at all (cheat row 6) | Option B: AI may reject, not approve | Not stated |
| Refusal above the risk ceiling | An AI may not reject either | Proposes refusals exempt (D-3) | Not stated |
| An approver session is treated as a human (its HTTP identity is `admin:` or `key:`) | Not mentioned. V6 says "anything signed in as `ai:`" without saying how one signs in | Identified as a blocker (F1) | Wants an AI approver that is not a human |
| Shadow mode before authority | Yes, the default (R3; sampling 10%) | Not in the plan | Rejected: "I am not training AI" |
| Same model family as the requester | Warn (cheat row 7) | Not in the plan | Not stated |
| Reviewer packet built by Orca; reviewer-directed text detected | Yes (cheat row 1) | Not in the plan | Not stated |
| Undo window | Not in V6 | Proposes a new delayed-approval path | Built as Execute-with-delay |

**Resolved 2026-11-13.** The owner chose one plan. `docs/design/reviewer-gate.md` now merges the gate proposal with V6, and `docs/MCP-HOST-V6-PLAN.md` is a pointer to it. The disagreements in the table above are recorded there as decisions D-2, D-3, D-9, D-10 and D-11, not settled silently.

## 6. Expansions (proposed; none started)

Each expansion names the document it amends, its dependencies and its acceptance criteria. Priorities follow the scan's order. The owner confirms them.

**E1. Delegated authority (Orca). Scan priority 1.**
- Amends: `docs/design/roles-and-permissions.md`, "Later: delegation".
- Depends on: Connectivity plan section 3 (per-user sign-in) and section 12 (profiles).
- The effective authority of an action is the intersection of: the person's authenticated identity; the person's real permissions in the connected system; the agent's profile; the operator's policy; and resource and argument conditions. No layer can widen another.
- Grants have an expiry and can be revoked. Each grant records who authorised which agent, for which tools and accounts, and for how long. Granting, changing and withdrawing a grant are audited.
- Authority is rechecked when a parked task resumes.
- Acceptance: a test matrix (person, agent profile, policy, argument condition) with the expected result for each cell. A revoked grant stops the next call. A resumed task is refused once its grant has lapsed.
- Connectivity section 12 already reserves this as a later item. Both plans must use the same intersection rule.

**E2. Connected accounts (Connectivity, with Orca sign-in). Scan priority 1, together with E1.**
- Amends: Connectivity `docs/plan.md`, section 3.
- Adds to the existing unchecked items:
  - several accounts per provider (for example, personal and business mail);
  - choosing the account for an agent or task;
  - knowing each tool's required scopes before consent;
  - connecting when a tool first needs it, not at setup;
  - refresh, expiry, revocation and failed sign-in as explicit states;
  - credentials never in model context or tool results;
  - never falling back to a shared account when the person's own authorisation fails.
- Architecture: Connectivity receives an authorised connection from Orca. It does not become a credential-management product.
- Acceptance: each state above has a test. A failed per-user sign-in returns an error, never a result from a shared account.

**E3. Human input beyond approve and reject (Orca). Scan priority 2.**
- Amends: `docs/design/work-and-continuations.md`.
- Adds: a structured question from agent to person, with the allowed answer types; a structured answer; parking the work; resuming with the answer and the original task context; keeping taint, labels, identity and audit through the round trip; Teams first, Slack later.
- Orca stays the ledger and the governance layer, not the agent runtime.
- Acceptance: a question is parked, answered in Teams and resumed. The resumed work carries the original taint and labels.

**E4. Integration discovery (Orca). Scan priority 3.**
- Amends: `docs/MODULES.md` and the `--list-modules` output.
- Adds: a consistent description for each module and integration; authentication and scope requirements; tool capability and risk; a health and authentication check for each module.
- Not a hosted catalogue. The operator decides what is installed and exposed.
- Acceptance: `--list-modules` shows the authentication status and required scopes of each module. A failing check is reported without exposing credentials.

**E5. Event idempotency for connectors and triggers (Connectivity and Orca). Scan priority 4.**
- Amends: Connectivity section 21 (triggers) and the connector contracts.
- Rule: an event may be delivered more than once, but the resulting work or external action must happen once. The triggers design already plans idempotency keys and a durable firing log (T2).
- Polling, provider change feeds and inbound webhooks become explicit event-source contracts.
- Acceptance: a duplicate delivery creates no second work item and no second external action.

**E6. Retrieval kept apart from action (Connectivity). A boundary rule, not a priority.**
- Amends: Connectivity `docs/roadmap.md`.
- A connector that retrieves information is not authorised to act on its source. Retrieval and action are separate capabilities, with separate credentials and separate scopes.

## 7. Things we will not copy

The scan's list, with its reasons:

- Permit's external policy platform: keep Orca's local policy engine.
- Arcade's managed execution runtime: keep execution under the operator's control.
- Composio's catalogue: build an integration when a real requirement needs it.
- Composio's experimental approval: Orca's durable queue stays authoritative. Elicitation could become an extra interface later.
- HumanLayer's coding-agent orchestration: Orca is not a coding harness.
- Batch approval: each action keeps its own authority and review.
- Automatic access from a model's stated intent: intent can inform a decision, but it never grants authority.

## 8. Open items

- Verify the product claims in section 2 before any of them is cited in a design.
- Resolve section 5: reconcile the gate plan with V6.
- Owner to confirm the priorities in section 6, and whether to amend the existing documents in one batch or one at a time.
- The Connectivity amendments (E2, E5, E6) belong in the Connectivity repository and are not made here.
