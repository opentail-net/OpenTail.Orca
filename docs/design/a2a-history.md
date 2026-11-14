# A2A as a second front door: history and record

> **This file is the record, not the current design.** The design and the checkbox plan are in [`a2a.md`](a2a.md). These sections were
> moved out of it unchanged on 2026-11-13: the corrections from the external review, the build order with its reasoning, the decisions as
> first recorded, the verification notes and what was built by commit. Add a dated line under "Record" when work is merged.

## Record

- 2026-11-13: the plan was converted to checkbox form; these sections moved here.

## Corrections from external review (2026-11-11)

An external review was checked against the A2A v1.0 specification (`https://a2a-protocol.org/latest/specification/`) and the SDK's
documentation. Each correction is now reflected in the body above. This is the change log.

1. **Task ids.** Reflected in section 4 (step 5) and section 10 (`a2a_tasks`). The task id is server-generated and is not the work item
   id. First-message handling is crash-safe and idempotent.
2. **`returnImmediately`.** Reflected in "Who decides" and in section 4. `true` returns once the task is created. `false` and an omitted
   value block until a terminal or interrupted state. **Open:** how many blocking waits one partner may hold open at once. That is an
   operational limit, and it must not change what a blocked request returns.
3. **The Agent Card.** Reflected in section 8. The card requires authentication. There is no public card.
4. **Skill selection.** Reflected in section 4 and the contracts. The extension URI is `https://opentail.net/a2a/extensions/skill-selection/v1`.
5. **ListTasks.** Reflected in section 4. Cursor pagination, newest first.
6. **A2A-Version. Still open, for the owner to confirm.** Reflected in sections 1 and 11: 1.0 is the only version served. The two sources
   disagree on an empty value:
   - **The spec** says an empty value is interpreted as 0.3 (§3.6.2). The server then answers `VersionNotSupportedError`, because it
     supports only 1.0.
   - **The SDK's migration guide** (`docs/migration-guide-v1.md` in a2a-dotnet) says empty or missing is accepted and defaults to the
     current version, 1.0. It accepts `0.3` and `1.0` and answers any other value with `VersionNotSupported` (-32009).
   - **A missing header** is not stated in the spec at all. The SDK's rule is the only written answer.
   - **Recommended, for the owner to confirm:** follow the spec for empty (reject as 0.3), and follow the SDK for missing (treat as 1.0).
     Reject `0.3` as this Orca does not serve it. Interoperability tests run against the SDK-mapped endpoint for empty, missing, `0.3` and
     `1.0`, and record the SDK's actual result for each.
7. **Partner identity.** Reflected in section 2 and the summary. The `partnerId` is a server-generated, immutable UUID, stored separately
   from the API key name.
8. **Order.** Reflected in the build order. Whole-action binding lands before the inbox is exposed to external partners.


## Build order

**Revised 2026-11-12 (owner agreed).** Phase 0 first, then a thin slice of phase 1: one inbox skill end to end, driven by the SDK's own
client, with `returnImmediately=true`, the task id table, `a2a.submit` and `a2a.reply`. The slice also records the SDK's real answer for
each `A2A-Version` case. A per-partner trust rule and a cap on blocking waits are part of the first cut. The rest of phase 1 (pagination,
the card, the skill-selection extension) follows the slice. The checklist ([governance-checklist.md](governance-checklist.md)) has the items.

- **Phase 0, prerequisites (no A2A yet):** the `a2a:call` role and scope; a startup check that `DataFlowMode` is `Enforce` when A2A is on;
  the durability setting documented and warned; a conditional rule on the `partner` argument, proven in a test; the whole-action
  binding in place, before the inbox is exposed to any external partner (correction 8).
  **Status (batch 06): built and committed in `f24f8c5`**, with the tests in `A2aPhase0Tests`.
- **Phase 1, inbound inbox:** `SendMessage`, `GetTask`, `CancelTask` over JSON-RPC; the authenticated card; `a2a.submit`
  and `a2a.reply` with previews; the context and message tables; the work-item mapping; the projection of work items. Tests: the
  authorisation matrix; `messageId` reuse; taint; the exit check for labelled replies; foreign and unknown ids; rate limits; out-of-profile
  trips; a context surviving a session expiry with its labels.
  - **Built** (checked against the code and tests, batch 06): `SendMessage` (`returnImmediately` true; false and omitted wait, with a
    cap), `GetTask`, `CancelTask`, `ListTasks`, over JSON-RPC (`A2ARequestHandler`); the authenticated card (`A2ACard`, batch 04);
    `a2a.submit` and `a2a.reply` with previews (`A2ASubmitModule`); the work-item mapping (`A2AWork`: the thread `a2a:<contextId>`
    and `work_item_id`, batch 03b-2).
  - **Partly built.** The context and message tables: there is one table, `a2a_tasks` (schema 13), with `context_id` and
    `message_id` columns; there are no separate context or message tables. The projection of work items is the state mapping
    `A2ARequestHandler.StateOf`, which reads the item's status and returns no text; it is not a separate type.
  - **Tests built:** the authorisation matrix (`A2aSliceTests`, `A2aPhase0Tests`); `messageId` reuse
    (`The_same_messageId_with_different_content_is_refused`); taint (`returnImmediately_creates_a_SUBMITTED_task...`); the exit check for
    labelled replies (`A_label_the_completer_held_is_checked_at_the_exit...`); foreign and unknown ids
    (`Another_partners_key_gets_TaskNotFound...`); rate limits (`The_body_limit_and_the_rate_limit_apply...`).
  - **Two tests cannot be written as stated, and why.**
    - *Out-of-profile trips.* A partner names no tool. The two tool calls it causes are `a2a.submit` (the handler refuses a caller
      whose profile does not list it, before it submits) and `a2a.reply` (submitted with the profile of the submit). Startup refuses an
      `a2a:call` key whose profile lacks either tool (`A2AFrontDoor.Check`). The queue's own trip, a `not_in_profile` refusal that
      suspends the requester (`ActionQueue.cs`, around line 92 to 102), is reached from A2A only if a profile loses a tool while Orca
      runs. This build has no hot reload of the policy, so that does not happen. Checked: no partner-driven path makes a call outside its
      profile.
    - *A context surviving a session expiry with its labels.* Contexts are not built. One task has one submit session (created at the
      submit, `host.Sessions.Start`), and there is no context table. The nearest test is
      `A_submit_session_that_expires_before_the_item_completes...` (batch 06, R5): the submit's session is removed, the reply is queued
      on a new session that holds the item's labels, and a labelled reply to a partner with no allowed destination is still refused.
  - **Not built:** contexts and multi-turn; more than one skill; tool skills (phase 2).
- **Phase 2, curated skills:** tool skills with data parts; the extended card; card signing; HTTP+JSON; 0.3 compatibility if needed.
- **Phase 3, push and `INPUT_REQUIRED`:** status-only push to registered hosts; `work.ask_partner` for `INPUT_REQUIRED`.
- **Phase 4, outbound:** remote agents as modules; card pinning; callbacks, once external waits exist.

- **Phases 2 to 4:** not built.

Each phase gets its own check against the code before it is built, as the work design did.


## Decisions

Decided by the owner on 2026-11-11 ("no idea" on card access was taken as the default):

- **Timing (10):** the owner is asking for A2A now, which is the "asked for" the work design waits on. This is a plan only: nothing
  is built, and Phase 0 and Phase 1 wait for the owner's review of this document.
- **Card (1):** requires authentication (default).
- **Replies (2):** every reply is reviewed in v1.
- **Durability (6):** `OPENTAIL_DB_SYNCHRONOUS=full` is required when A2A is on, for now. Revisit when Orca runs on a server
  database.

Still the proposed defaults, unchanged:

3. **One partner, one `partnerId`, one or more credentials** (replaces the one-key rule; see correction 7). Partner-specific trust through rules on the `partner` argument, not separate tools.
4. **JSON-RPC first**, HTTP+JSON when a partner asks.
5. **A2A-Version 1.0 only**, until a partner needs 0.3.
7. **A generic error for a skill the caller may not use.**
8. **Card and skill changes are configuration only**, not through `skills.publish`.
9. **Reviewer reasons stay internal.**
11. **File parts** are refused in v1; attachments later, with their own size and type limits.


## Verification before building

Status after batch 06. "Closed" names where the point was checked.

- **SDK, on the pinned version.** Closed: `MapA2A` takes `RequireAuthorization` and gives 401 without a credential
  (`a2a-sdk-facts.md` section 2, RUN); the default card path and its cache header (section 7, RUN); the version outcomes (section 5, RUN).
  Not used: `IAgentHandler` and `ITaskStore`; the handler is ours (`IA2ARequestHandler`, decision D4). Open: whether a push payload can be
  limited to status (phase 3, not built); whether the card can be canonicalised for signing (phase 2, not built).
- **Specification, still unread.** Open: §3 (abstract definitions), §4.3 (push objects), §5.4 (error table), §13.1 in full, and how
  negotiation works for versions and extensions. Partly: whether `skills` may be empty. The SDK serves an empty list (RUN, section 7);
  the specification text is not read.
- **Orca, in code.** Closed: the rule syntax for a condition on a top-level argument (phase 0 test, `Policy/Rules/Conditions.cs`); the
  review page's rendering of partner text (`ReviewViewTests.B1`, RUN: `textContent` throughout, no cut). Open: the throughput cost of
  `synchronous=full` (not measured).
- **Vendor claims not used.** Closed: neither the Gravitee pages nor agentgateway is relied on for a design decision.


## What is built

Built, by commit. The brief that asked for each batch is listed in `docs/briefs/README.md`.

- **Phase 0** (`f24f8c5`): the `a2a:call` role and scope; the startup checks; the partner ids; the conditional rule on `partner`, proven
  by a test.
- **The thin slice** (`fae2725`, `23020b1`, `00b52ce`, `c159639`, `7b6003c`). `OpenTail.Orca.A2A` is referenced only by a server or a
  test; the HTTP host takes it through an extension seam and never names it. It serves `/a2a` (JSON-RPC: our `IA2ARequestHandler` over
  the SDK's `MapA2A`) behind the `a2a:call` role, and enforces `A2A-Version` before the SDK (D6).
  - `SendMessage` with `returnImmediately` true. With false or omitted, a blocking wait (`BlockingWait`, default 30 seconds) that answers
    the task when it is terminal, or an error naming the task when the wait passes. A per-partner cap (`MaxBlockingWaitsPerPartner`,
    default 4).
  - The task id table (`a2a_tasks`, schema 13): Orca's id, the partner, the content hash, the submit, work-item and reply links,
    and no text.
  - `a2a.submit` as the partner, with the task id as the idempotency key. On approval it creates one work item for the skill's inbox
    profile, on the task's thread, tainted, carrying what the submit's session holds. The work item's id never goes to the partner.
  - A finished item is reconciled into one queued `a2a.reply` for its summary (or "Done."), in the submit's session and profile, with
    the item's taint and labels put on that session first. `a2a.reply` is an exit: Queue or Deny, reviewed by a person, no edits.
  - `GetTask`; `CancelTask` (a pending submit, or an open work item only); `ListTasks` (the caller's own tasks, keyset paging, newest
    first by creation).
  - The authenticated agent card (`/.well-known/agent-card.json`), with `private` caching and a `card_served` audit.
  - Startup checks, and the review-view rules: a cut Teams preview cannot be approved, on the card or on the click.
  - The completer's labels: `WorkService.CompleteAsync` carries the session's taint and labels onto the item (E1). `WorkService`
    raises `Completed` (E2) and cancels only an open item when asked (E7).
  - The kit: a sample config and policy (`docs/guides/a2a-sample/`), `scripts/a2a-smoke.ps1`, and `docs/guides/a2a.md`.
  - Teams: `tasks` (A2A, through the extension seam) and `work` (generic), read-only, for listed reviewers.
- **Reused without change:** the decision path, profiles, taint, labels, the audit trail, identity levels, the rate limits, the body
  limit and the HTTP sign-in.

**Not built:** the skill-selection extension; more than one skill (a request is served only when exactly one skill is configured);
contexts and multi-turn (a task has its own context, and a continuation is refused); tool skills; the extended card (`GetExtendedAgentCard`
answers UnsupportedOperation); card signing; HTTP+JSON (only JSON-RPC); push; outbound calls; partners authenticated by JWT (a partner
comes from an API key only); streaming and resubscribe; file parts.

The thin slice's version gate and the SDK facts are in `docs/briefs/a2a-sdk-facts.md`.

