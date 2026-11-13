# Checklist: governance naming, re-check, and A2A

**One place to see what is done and what is not.** Each item links to the document that describes it. `[x]` means on `origin/main`
(last push `0a09946`, 2026-11-12). `[ ]` means open. Decisions that stay put are plain bullets, not work.

Documents behind this list:
[naming plan](governance-naming-plan.md) · [comparison with Microsoft's toolkit](agt-comparison.md) · [A2A plan](a2a.md)

**Scope.** A first external partner needs the blockers in section A (before the A2A phase 0 work) and section B (phase 0 and 1). Section
C is later work. Section D is housekeeping, which does not block a partner. Keeping them apart is the point of this list.

## Next up

Order agreed with the owner on 2026-11-12, after a review of these plans. Items 1 to 3 are done in the working tree, not committed
(2026-11-12); they stay listed so the order is visible.

1. ~~**Enforce the binding digest at the claim** (A.2).~~ Done in the working tree, not committed.
2. ~~**Fold the corrections into the body of `a2a.md`** (B.0).~~ Done in the working tree, not committed.
3. ~~**The resolution record, without the export** (A.3).~~ Done in the working tree, not committed. The export waits for a consumer.
4. **A2A phase 0** (B), including the partner identity foundation.
5. **A2A thin slice** (C.0): one inbox skill end to end against the SDK's own client, before the rest of phase 1.

## A. Governance prerequisites (before A2A phase 0)

Done:

- [x] Comparison with the toolkit, with locations and file hashes (`agt-comparison.md`, `89caf47`). Maturity corrected: the core package is
  stable on NuGet; the companion packages are labelled Public Preview; YamlDotNet 18.1.0 is required.
- [x] Step 1: approval audit events renamed to `approval_requested`, `approval_allowed`, `approval_denied`, `approval_expired`,
  `approval_cancelled` (`d6b10f3`).
- [x] Phase 2: an approved action is re-checked before it is claimed: expiry, the policy hash, the argument digest, and the digest of any
  reviewer edit (`92c0048`).
- [x] Phase 2: reason codes `approval_expired`, `policy_version_mismatch`, `action_digest_mismatch`, stored as the action's error code.
- [x] Phase 2: `approval_consumed` in the claim's transaction; `execution_denied` for a refused approval.
- [x] Phase 2 tests: 6 in `RecheckAtClaimTests`. Each check was switched off in turn, and only its own test failed.
- [x] `execution_allowed` is not added. `approval_consumed` plus `execution_started` cover it (second review agrees).

A.1 Re-check test gaps (done 2026-11-12, in the working tree, not committed):

- [x] A reviewer's retry under a changed policy is refused (`RecheckAtClaimTests`).
- [x] A delayed action that comes due after a policy change is refused.
- [x] The edit digest (`ActionReview.EditedArgumentsHash`) survives the SQLite store.

A.2 Whole-action binding (the naming plan's phase 1; done 2026-11-12, in the working tree, not committed):

- [x] The raw-text `ArgumentsHash` is unchanged (verified: it is the digest of the raw text, not canonical JSON). The binding digest is a
  new field beside it, `ActionRecord.BindingDigest`. Stored digests stay valid.
- [x] Binding over the whole action (`ActionBinding`): `schema_version`, `operation` (`tool_call`), `agent_id` (the requester's id),
  `subject_id` (null until delegation exists), `target` (`tool_name`, `tool_schema_version`, `resource`: null), `parameters`,
  `policy_version`. One digest, `sha256:` over the canonical form.
- [x] Canonical JSON per RFC 8785 (`Jcs` in `OpenTail.Host.Abstractions`). It passes all six reference vectors, vendored unchanged in
  `OpenTail.Host.Abstractions.Tests/Rfc8785/` (Apache-2.0, source commit `32a4819`). The reference repository's 100-million-number file
  is generated, not shipped, and was not run.
- [x] Number formatting follows ECMAScript (`1e+30`, `1e-7`, `1e+21`, `0.000001`), tested at the edges. It does not use the .NET
  writer's format.
- [x] `tool_schema_version` is `sha256:` over the canonical form of the tool's parameter schema, computed when the tool is registered
  (`ToolCatalog.SchemaVersionOf`).
- [x] Stored per action, in the action's JSON. No schema bump was needed; the schema is still 12.
- [x] Enforce the binding at the claim (agreed 2026-11-12; in the working tree, not committed). Recompute the binding from the stored record
  and the loaded tool, and refuse a mismatch with `action_digest_mismatch`. A reviewer's edit records a binding of its own, as it records
  an argument digest (`ActionReview.EditedBindingDigest`). A record with no binding digest is refused, except where the tool was unknown
  or the arguments were not JSON (those cannot run). The binding is built in one helper (`ActionBindings`) for submission, the edit and the
  claim. The check is in `ActionExecutor.RecheckRefusal`, after the argument digest.
- [x] Accepted consequence documented: a module upgrade that changes a tool's parameter schema refuses approvals given before it
  (`OPERATIONS.md`, upgrades).
- [x] Mutation check: the binding comparison switched off with `&& false`. Four refusal tests failed (schema change, stored digest changed,
  stored digest cleared, requester changed); the edit test still passed, as it does not depend on the check. Restored, all pass.
- Tests: `JcsTests` (28), `ActionBindingTests` (8), `RecheckAtClaimTests` 16 (11 before, 5 for the claim check). `DurabilityTests`
  fixtures for interrupted records now carry a real binding (they are refused without one, as intended).
  Abstractions 70 of 70; Host 1204 of 1204 (`dotnet test OpenTail.Host.Tests/OpenTail.Host.Tests.csproj`).

A.3 Resolution record (scope reduced 2026-11-12; in the working tree, not committed):

- [x] A resolution record per action: outcome, binding digest, policy version, reason, time, and the resolver. This answers an auditor's
  question: what was approved, under which policy, and did it run.
  - Design: `ActionRecord.Resolution` (an `ActionResolution`), stored in the action's JSON. No schema bump (the schema is still 12).
    It is set in the same write as the status change it describes, so the status and its resolution cannot disagree. It holds no
    arguments. `DecisionRecord` copies it at purge, so it outlives the action. The export stays deferred.
  - Outcomes (`ResolutionOutcomes`): `allowed` (reviewer approval completed, or the undo window elapsed), `denied` (reviewer rejection),
    `expired` (sweep, late approval, or module switched off), `cancelled` (requester, reviewer who stopped a delayed action, or operator),
    `execution_denied` (approval refused at its claim). `changes_requested` and a denial at submission have no resolution: the outcome
    list has no value for them, and their status and decision trace already answer the question.
  - Tests: `ResolutionRecordTests` (9): one per outcome, the late approval, the delayed run, a stop, the refused claim over SQLite with
    a reopen, and a check that a resolution is not stored without its entry. Plus one assertion each in `BrickTests` (module removal) and
    `DecisionRecordTests` (purge keeps it).

Deferred until someone needs it (no consumer today):

- [ ] Export in the field names of `ApprovalRequest` and `ApprovalResolution`, with a version field and a versioning rule.
- [ ] Conformance tests for the export mapping. Read `ApprovalProtocolTests.cs` in the toolkit copy first.

## B. A2A phase 0 (prerequisites, no A2A code yet)

Go-ahead given 2026-11-12.

- [x] B.0: fold the corrections into the body of `a2a.md`, so the body and the corrections no longer disagree (task ids in section 4,
  the partner identity in section 2, the versions in sections 1 and 11, the authenticated card in section 8, the skill-selection
  extension and ListTasks in section 4, `a2a_tasks` in section 10, the build order). Committed in 15b9e8b (2026-11-12). The
  correction 6 A2A-Version question is still open for the owner.
- [x] The role and scope `a2a:call` (`Roles` in `OpenTail.Host.Admin/AdminServer.cs`), accepted by the API key scope validation
  (`HostHttpOptions.cs`, which refuses only review, admin and group scopes today). In the working tree, not committed. The validation
  needed no change: it already accepts `a2a:call`. Tests: `A2aPhase0Tests` (2): the key passes validation; a key with only `a2a:call`
  gets 403 on `/mcp` and on `/api/actions`, with controls for a tools key and a reviewer.
- [x] Startup check: `DataFlowMode` must be `Enforce` when A2A is configured (it defaults to `ReportOnly`). In the working tree, not
  committed. Test: `Enabled_a2a_requires_dataflow_enforce`.
- [x] Startup check: `OPENTAIL_DB_SYNCHRONOUS=full` when A2A is configured. It reads the store's resolved `Synchronous`, not the
  environment text; only `full` passes (`extra` is refused too, as written); a store that is not SQLite is refused. In the working tree,
  not committed. Tests: `Enabled_a2a_requires_full_durability_as_the_store_resolved_it` and
  `Enabled_a2a_starts_when_the_store_runs_with_full_durability_and_refuses_normal`.
- [x] Startup check: a key with `a2a:call` must name a profile. Also checked: it carries no other scope, and names a listed partner.
  In the working tree, not committed. Tests: `A_partner_key_must_name_a_profile`, `A_partner_key_carries_a2a_call_alone`,
  `A_partner_key_must_name_a_listed_partner`.
- [x] Test: a conditional rule on a top-level argument (`partner`) works in `Policy/Rules/Conditions.cs`. In the working tree, not
  committed. Test: `A_rule_on_the_partner_argument_executes_one_partner_and_queues_another`. No production change.
- [x] Partner identity foundation: a `partnerId` (UUID), stored separately from the key name, with credentials linked to it through an
  optional partner reference on each API key entry (`a2a.md`, correction 7). In the working tree, not committed. `--new-partner` makes
  the id, as the phase 0 brief asks. **Open for the owner:** `a2a.md` section 2 says the id is server-generated; the brief has the
  operator generate it. Built as the brief says. Tests: `New_partners_are_fresh_uuids_with_a_trimmed_name_and_pass_the_checks`,
  `Partner_ids_must_be_uuids_and_unique_and_partners_need_a_name`, `Several_keys_may_name_one_partner`.
- Test counts after phase 0 (a full run, Debug): Abstractions 70 of 70; Host 1221 of 1221 (1204 before, plus 17 in `A2aPhase0Tests`).
  `ContractFreezeTests` passes, and `contract-v1.txt` is unchanged.

## C. A2A phase 1 (inbound inbox): blockers before the first partner

C.0 Thin slice first (agreed 2026-11-12):

- [ ] One inbox skill end to end: `SendMessage` with `returnImmediately=true`, `GetTask`, the task id table, `a2a.submit` and `a2a.reply`,
  driven by the SDK's own client in a test. No pagination, no card, no extension yet. **Partly (03b-1):**
  `SendMessage` (true), `GetTask`, `CancelTask`, the task table and `a2a.submit`, all driven by the SDK's client over a socket. Not yet:
  `a2a.reply` and the work item (03b-2).
- [x] The same test records what the SDK-mapped endpoint does for an empty, a missing, a `0.3` and a `1.0` `A2A-Version` header. The
  owner's version decision is then made on observed behaviour. **Done (03b-1):** `The_version_matrix_gives_the_outcomes_of_D6...` records
  this Host's outcomes; the SDK's raw results are in `docs/briefs/a2a-sdk-facts.md` section 5 (RUN). Decision D6 is under Decided.
- [ ] A per-partner trust rule in the first cut: a conditional rule on `partner`, so a trusted partner's submit or reply need not queue
  twice for a person. Two approvals per request is the default for a new partner only. **Not built in 03b-1** (the rule syntax is proven by
  the phase 0 test; the operator's policy for each partner is still to be written).
- [ ] A cap on blocking waits per partner before any partner connects (it was listed as an open detail; it is a prerequisite).
  **Not built in 03b-1:** blocking sends are refused, so there is no wait to cap yet.

C.1 Blockers:

- [x] Whole-action binding (A.2) is in place before any external partner is connected. The review's rule. (In the working tree, not
  committed.)
- [ ] Task ids: a separate persistent `a2a_tasks` id, linked to the `a2a.submit` action, the work item and the `a2a.reply` action. The
  server generates it (spec §3.4.2). First-message handling is crash-safe and idempotent: a retry with the same `messageId` finds the
  existing task (correction 1). **Partly (03b-1):** the id is generated by the Host, the table is in schema 13, `messageId` reuse is
  refused for different content, and a crash between the row and the submit is healed by the retry (tested). The links to the work item
  and to the reply action are 03b-2.
- [ ] `returnImmediately`: honoured exactly as the spec says. `true` returns once the task is created. `false` and omitted block until a
  terminal or interrupted state, and a blocked request is never answered with SUBMITTED because a timeout expired. A disconnect discards
  nothing (correction 2). Tests for `true`, `false` and omitted. **Partly (03b-1):** `true` is served; `false` and omitted are refused with
  UnsupportedOperation, leave no row, and are tested. The blocking wait is 03b-2.
- [x] `A2A-Version` (correction 6): the spec and the SDK disagree on an empty header. Recommended: empty is rejected as 0.3, per the spec; a
  missing header follows the SDK, as 1.0. The owner confirms both. Interop tests against the SDK-mapped endpoint for empty, missing, `0.3`
  and `1.0`, recording the SDK's actual result for each. **Done (03b-1):** enforced by the Host before the SDK (decision D6, recorded under
  Decided); the version matrix test covers missing, empty, `0.3`, `1.0`, `2.0` and the repeated and comma-separated cases.
- [ ] ListTasks: cursor pagination with `pageToken` and `nextPageToken`, newest first (spec §3.1.4) (correction 5).
- [ ] Authenticated card (decided, correction 3): `/.well-known/agent-card.json` requires authentication. `/extendedAgentCard` is generated
  from the caller's profile. Internal tools, reviewer groups and policy stay out of both.
- [ ] Skill selection extension at `https://opentail.net/a2a/extensions/skill-selection/v1`, declared in the card and sent in the message
  (correction 4). The extension's specification is published at that URI before any partner is onboarded.
- [ ] Tools `a2a.submit` (reads outside content, queued) and `a2a.reply` (an exit, queued, edits not allowed), each with a preview.
  **Partly (03b-1):** `a2a.submit` is built, with a preview; `a2a.reply` is not.
- [ ] Map work items and reply actions to A2A task states.
- [x] JSON-RPC endpoint, and the authentication matrix. **Done for this slice (03b-1):** `/a2a` over `MapA2A` with a role requirement; 401
  with no credential, 403 for a key without `a2a:call` (and for an `mcp:tools` key); foreign and unknown ids alike; the body limit and the
  rate limit apply. The `ListTasks` scope and the card are not built yet.
- [ ] Tests: task-id creation after a crash, `messageId` reuse, `returnImmediately`, ListTasks pagination and ordering, `A2A-Version`, taint
  on submit, a labelled reply refused, foreign and unknown ids, rate limit, out-of-profile trip, and a context that survives a session
  expiry. **03b-1 covers** the crash, `messageId` reuse, `returnImmediately` (true; false and omitted refused), `A2A-Version`, taint on
  submit, foreign and unknown ids, and the rate limit. **Not yet:** ListTasks, a labelled reply, the out-of-profile trip, and contexts.

C.2 Verify before building:

- [x] SDK: `MapA2A` returns a builder that accepts `RequireAuthorization`, and an ASP.NET policy can guard the endpoint. **Closed:**
  `docs/briefs/a2a-sdk-facts.md` section 2 (RUN: 401 without a credential).
- [ ] Specification: `skills` may be empty? Check. Sections 3, 4.3, 5.4 and the full text of 13.1 were not read.
- [x] Check the SDK's actual behaviour on the endpoint for each `A2A-Version` case (see C.1). The SDK's migration guide says empty and
  missing are accepted and default to 1.0, which contradicts the spec for empty. The spec is silent on missing. **Closed on the SDK side:**
  the SDK accepts missing, empty and `0.3` and refuses `2.0` (RUN; `a2a-sdk-facts.md` section 5). The spec side is still open.
- [ ] Specification: `application/a2a+json` accepted on requests.
- [ ] The review page escapes partner text when it shows a queued request.
- [x] Rule syntax for conditions on a top-level argument. **Closed:** the phase 0 test `A_rule_on_the_partner_argument...` (RUN).
- [x] `IA2ARequestHandler` (not `A2AServer`), with `A2A` and `A2A.AspNetCore` 1.0.0-preview4 added to `Directory.Packages.props`. Restore was
  tested in a scratch project. **Done (03b-1, D1):** both packages are pinned, `Microsoft.Extensions.AI.Abstractions` is raised to 10.11.0,
  and the handler is ours (`A2ARequestHandler`, not `A2AServer`).

C.3 Still to decide while building (not blockers if the first partner accepts the defaults):

- [ ] Skill visibility: which profiles may use an inbox skill. Any profile that lists `a2a.submit`, or a per-skill list.
- [ ] Work items are read through the work store (`IWorkStore.Get`), not `WorkService.Read`. `Read` taints the reader's session and renews
  the lease.
- [x] Caller identity comes from `IHttpContextAccessor`, which needs registering. **Done (03b-1):** registered by the HTTP host; the caller is
  read from the partner and profile claims, never from the request object.
- [x] Schema: one bump for both the binding and A2A, or two. **One bump (03b-1):** schema 13 creates `a2a_tasks`; the binding has no table of its own.
- [ ] Owner decision: how many blocking waits one partner may hold open at once. An operational limit. It must not change what a blocked
  request returns.
- [ ] Plan defaults that the owner has not yet confirmed: JSON-RPC first (decision 4); a generic error for a skill the caller may not use
  (7); card and skill changes are configuration only (8); reviewer reasons stay internal (9); file parts refused in v1 (11).

## C2. Later (not blocking a first partner)

- [ ] Phase 2: curated skills in full; the extended card with every skill; card signing; HTTP+JSON; 0.3 compatibility if a partner needs it.
- [ ] Phase 3: push, status only, to registered hosts; `work.ask_partner` for the INPUT_REQUIRED state.
- [ ] Phase 4: outbound remote agents as modules; card pinning; callbacks, once external waits exist.
- [ ] Conformance harness against the toolkit's full expectations. Only at the migration gate (section E).
- [ ] Run the toolkit's own tests. The local copy is at `C:\Git\split\examples\agent-governance-toolkit\`. Re-check its file hashes before
  relying on it (`agt-comparison.md`); the copy has no git history of its own.
- [ ] Workbench contracts: the review-outcome values (`approved`, `rejected`, `changes_requested`) and the action status strings the
  workbench reads (`denied`, `failed`, `error`). Keep, or version them in a coordinated change in `opentail-host` and
  `opentail-workbench`. The workbench reads them in `ShadowRunner.cs`, `GradingQueue.cs`, `Formats/Candidates.cs` and `Reports/TextReport.cs`.
- [ ] Revisit triggers from the comparison: third-party MCP servers (`McpSecurityScanner`, not read); OPA or Cedar backends.

## D. Housekeeping (does not block a partner)

- [ ] Freeze trigger for public names: the first deployment that keeps audit data, or Host 1.0. The default is recorded; confirm it.
- [ ] Write the freeze point into the Host's README, as the naming plan asks.
- [ ] Check the published packages. `host-v0.0.1` (25 packages, 2026-10-20) carries the old audit names. Who consumes them?
- [ ] Check GHCR pull counts for `ghcr.io/opentail-net/opentail-host:0.0.1`. Is the image public, and was it pulled?
- [ ] Check whether anyone kept an old audit database, or consumes the webhook event names on `audit_entry` routes. Tell them.
- [ ] Discard the development databases written before the event rename. Their audit chains fail verification.
- [ ] Update documents that still quote old event names (for example `audit-and-evidence.md`, which lists `approved`).
- [ ] Remove the eight extra worktrees before deleting their branches: `C:\Git\split\opentail-host-attr`, `-golden`, `-hash`, `-main`,
  `-s1`, `-s9`, `-skills` and `-store`.
- [ ] Delete the merged local branches: the eleven skills branches (`standard-skill-files`, `feat/agentskills-library`,
  `feat/agentskills-names`, `feat/agentskills-preserve`, `feat/hash-v2`, `skills-plan`, `test/skill-identity-golden`,
  `feat/attribution-measure`, `fix/s1-discovery-labels`, `fix/s9-labels-overwrite`, `feat/store-integrity-rest`), plus
  `naming-public-names`, `recheck-at-claim` and `integrate-skills`. Their copies on `origin` are a separate decision.
- [ ] The OpenTail tool recreates `.opentail-shadow.git/` in the repo. It is excluded locally. Turn shadow off in the tool's config, or accept
  the folder.
- [ ] The C# rename tool fails in this session with a missing-assembly error. A `restart_server` would rebuild it.
- [ ] Tests not run here: `OpenTail.Host.Tests.SqlServers` (skips without `OTAIL_SQL_*`); `OpenTail.Host.Tests.Snowflake` and
  `OpenTail.Host.Tests.Worker`. They name none of the changed events in source.
- [x] All eleven skills branches are ancestors of `main`. `skills-plan` was merged in `6f6ec88`; the rest were already there.
- [x] Architecture rule allows `OpenTail.AgentSkills` for modules, as a leaf library (`90b34ea`).

## E. Gate for migrating to the Microsoft package

All of these must hold before any package is taken:

- [ ] The approval protocol is stable: its ADR is accepted, its .NET implementation has a conformance suite, and the toolkit's README no
  longer calls its releases preview.
- [ ] Our digest passes the reference test data.
- [ ] The conformance harness passes.
- [ ] The ADR's open items are specified: the store interface, the webhook response fields, and how an approver's identity is asserted.
- [ ] YamlDotNet: the package needs 18.1.0 or later; the Host pins 17.1.0 (`Directory.Packages.props` line 26). Moving the pin changes every
  project that uses YamlDotNet, including the Skills library.
- [ ] A real requirement exists: an external interoperability need, not just the overlap.

## Decided

- A2A-Version (decision D6, the planner's delegated decision, 2026-11-13): missing is 1.0; `1.0` is accepted; an empty value, `0.3`, and
  anything else are refused with the SDK's VersionNotSupported error (HTTP 200, -32009), after sign-in and before the SDK. A header sent more
  than once, or a comma-separated list, is accepted only when every value is `1.0`. Empty and `0.3` follow the spec; missing follows the SDK.
- Timing: wanted now (owner, 2026-11-11).
- The card requires authentication; partners receive the card location and credentials out of band (owner default; second review, 2026-11-12).
- `returnImmediately=true` is the recommended integration mode; blocking requests follow the spec exactly (second review, 2026-11-12).
- The skill-selection extension URI is `https://opentail.net/a2a/extensions/skill-selection/v1` (second review, 2026-11-12).
- Partner ids are server-generated UUIDs, separate from key names; credentials use the Host's existing configuration (second review, 2026-11-12).
- `1.0` is the only version served. Empty is rejected as 0.3, per the spec (recommended; owner to confirm). The missing-header rule is
  an open owner decision (recommended: 1.0, as the SDK does).
- Replies are reviewed; durability is `synchronous=full` when A2A is on (owner, 2026-11-11).
- Not adopting the Microsoft NuGet package now (overlap, integration cost, conformance question, no external interoperability requirement yet).
- Not renaming `ActionStatus`, `ToolMode`, or the operator config values `Execute`, `Queue`, `Deny`.
- Not changing notification names or telemetry labels.
- Not adding `execution_allowed`.
- The binding digest is enforced at the claim; a tool schema change refuses older approvals (owner, 2026-11-12).
- The Microsoft-shaped export is deferred until it has a consumer; the resolution record is built (owner, 2026-11-12).
- A2A is built as a thin slice first, with a per-partner trust rule and a cap on blocking waits in the first cut (owner, 2026-11-12).
- The naming freeze is not in effect; the owner has not set one (2026-11-12).
