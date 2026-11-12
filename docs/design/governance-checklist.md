# Checklist: governance naming, re-check, and A2A

**One place to see what is done and what is not.** Each item links to the document that describes it. `[x]` means on `origin/main`
(last push `f7a7583`, 2026-11-11). `[ ]` means open. Decisions that stay put are plain bullets, not work.

Documents behind this list:
[naming plan](governance-naming-plan.md) · [comparison with Microsoft's toolkit](agt-comparison.md) · [A2A plan](a2a.md)

## Next up

Order follows the external review of 2026-11-11, which the owner asked us to take on board:

1. **Close the re-check test gaps.** No decision needed: a reviewer's retry under a changed policy, a delayed action that runs after a
   policy change, and the edit digest surviving the SQLite store. Section 1.
2. **Whole-action binding, with RFC 8785 test vectors.** Keep the raw-text `ArgumentsHash` and add a binding digest beside it. Section 1.
3. **Public approval and resolution contract.** Export mapping, versioning, and conformance tests. Section 1.
4. **A2A phase 0**, including the partner identity foundation. Waits for your go-ahead. Section 4.
5. **A2A phase 1**, only after the corrections in `a2a.md` are built. Binding (step 2) must land first, because an external inbox is
   where binding the whole action matters most.

## 1. Approval naming and the re-check (naming plan)

Done:

- [x] Comparison with the toolkit recorded, with locations and file hashes (`agt-comparison.md`, `89caf47`). Maturity corrected to the
  review's facts: the core package is stable on NuGet; the companion packages are labelled Public Preview.
- [x] Step 1: approval audit events renamed to `approval_requested`, `approval_allowed`, `approval_denied`, `approval_expired`,
  `approval_cancelled` (`d6b10f3`).
- [x] Phase 2: an approved action is re-checked before it is claimed. Checks: expiry, the policy hash it was decided under, the argument
  digest, and the digest of any reviewer edit (`92c0048`).
- [x] Phase 2: reason codes `approval_expired`, `policy_version_mismatch`, `action_digest_mismatch`, stored as the action's error code
  (`92c0048`).
- [x] Phase 2: `approval_consumed` in the claim's transaction; `execution_denied` for a refused approval (`92c0048`).
- [x] Phase 2 tests: 6 in `RecheckAtClaimTests`. Each check was switched off in turn, and only its own test failed (`92c0048`).

Re-check gaps (step 1 of "Next up"):

- [ ] A reviewer's retry under a changed policy has no direct test. The retry path goes through the same check.
- [ ] A delayed action that runs after a policy change has no direct test. It goes through the same check.
- [ ] The edit digest (`ActionReview.EditedArgumentsHash`) has only been tested with the in-memory store. Actions are stored as JSON
  (`SqliteActionStore`, the `$json` column), so it should survive, but that is not tested.
- [ ] Owner decision: records made before `PolicyHash` existed, or an edit with no recorded digest, are refused at claim (fail closed).
  Confirm that is acceptable for the data that exists.

Decided: `execution_allowed` is not added. `approval_consumed` plus `execution_started` cover it. The external review recommended this, and
it matches the code. The owner can reverse it.

Whole-action binding (step 2 of "Next up"):

- [ ] Decide, recorded as the review's recommendation: keep the raw-text `ArgumentsHash` and add a binding digest beside it, so stored digests
  stay valid. `ArgumentsHash` is not canonical JSON; it is the first 32 hex digits of SHA-256 over the stored text (`ArgumentDigest.cs`).
- [ ] Binding over the whole action: agent, subject, operation, tool, tool schema version, resource, arguments, policy version. One digest.
  Today the digest covers the arguments only, and the policy hash is compared separately.
- [ ] Canonical JSON per RFC 8785 for the binding digest, checked against the reference test data (`cyberphone/json-canonicalization`,
  `testdata`; not opened yet). Do not call the digest RFC 8785 compliant until it passes those vectors. The toolkit's "RFC 8785-style"
  wording is not enough.
- [ ] Check the .NET writer's number formatting against RFC 8785. The toolkit's digest writes some numbers differently (exponent forms).
- [ ] Compute `tool_schema_version` from each tool's parameter schema. The Host does not store one today.
- [ ] Store the binding digest per action. Decide the schema question first: actions are JSON, so a new field may need no bump. The current
  schema is 12 (`SqliteActionStore.cs`).

Public contract (step 3 of "Next up"):

- [ ] Phase 3: a resolution record per action: outcome, digest, policy version, reason, time.
- [ ] Phase 3: export an approval in the field names of `ApprovalRequest` and `ApprovalResolution` (CLI and audit export).
- [ ] Versioning for the export: a version field, and a rule for what changes need a new version.
- [ ] Conformance tests for the export mapping, run against the toolkit's expectations. Read `ApprovalProtocolTests.cs` first. Only at the
  migration gate (section 5) for the full harness; the export tests can come earlier.
- [ ] Run the toolkit's own tests, which have not been run. The local copy is at `C:\Git\split\examples\agent-governance-toolkit\`.
- [ ] Before relying on the toolkit copy, re-check its file hashes (`agt-comparison.md`). The copy has no git history of its own.

## 2. Cross-repo contracts (the workbench)

The workbench is in `C:\Git\split\opentail-workbench` (branch `progressive-disclosure`). It reads these strings in `src/OpenTail.Workbench`:
`ShadowRunner.cs`, `GradingQueue.cs`, `Formats/Candidates.cs` and `Reports/TextReport.cs`.

- [ ] Decide: the review-outcome export values (`approved`, `rejected`, `changes_requested`). Keep them, or version them.
- [ ] Decide: the action status strings the workbench reads (`denied`, `failed`, `error`, and others).
- [ ] If either changes: a coordinated, versioned change in `opentail-host` and `opentail-workbench`.

Decided: notification names stay (operator configuration). Telemetry reason labels stay. `denied`, `changes_requested`, `delayed`,
`policy_decision`, `execution_*` and `approval_recorded` stay.

## 3. Owner decisions and housekeeping

Decisions:

- [ ] Freeze trigger for public names: the first deployment that keeps audit data, or Host 1.0. The default is recorded; confirm it.
- [ ] Write the freeze point into the Host's README, as the naming plan asks. Not done.
- [ ] Check the published packages. `host-v0.0.1` (25 packages, published 2026-10-20) carries the old audit names. Who consumes them?
  Any later release changes the names for those consumers.
- [ ] Check GHCR pull counts for `ghcr.io/opentail-net/opentail-host:0.0.1`. Is the image public, and was it pulled?
- [ ] Check whether anyone kept an old audit database, or consumes the webhook event names on `audit_entry` routes. Tell them the names changed.

Clean-up:

- [ ] Discard the development databases written before the event rename. Their audit chains fail verification.
- [ ] Update documents that still quote old event names (for example `audit-and-evidence.md`, which lists `approved`).
- [x] All eleven skills branches are now ancestors of `main`. `skills-plan` was merged in `6f6ec88`; the rest were already there.
- [x] Architecture rule allows `OpenTail.AgentSkills` for modules, as a leaf library (`90b34ea`).
- [ ] Remove the eight extra worktrees before deleting their branches: `C:\Git\split\opentail-host-attr`, `-golden`, `-hash`, `-main`,
  `-s1`, `-s9`, `-skills` and `-store`.
- [ ] Delete the merged local branches: the eleven skills branches (`standard-skill-files`, `feat/agentskills-library`,
  `feat/agentskills-names`, `feat/agentskills-preserve`, `feat/hash-v2`, `skills-plan`, `test/skill-identity-golden`,
  `feat/attribution-measure`, `fix/s1-discovery-labels`, `fix/s9-labels-overwrite`, `feat/store-integrity-rest`), plus
  `naming-public-names`, `recheck-at-claim` and `integrate-skills`. Their copies on `origin` are a separate decision.
- [ ] The OpenTail tool recreates `.opentail-shadow.git/` in the repo. It is excluded locally. Decide: turn shadow off in the tool's config,
  or accept the folder.
- [ ] The C# rename tool fails in this session with a missing-assembly error. A `restart_server` would rebuild it. Owner decision.

Tests not run here:

- [ ] `OpenTail.Host.Tests.SqlServers`: skips unless `OTAIL_SQL_*` variables are set. Its one reference to an approval name is a test
  title, not a name it asserts on.
- [ ] `OpenTail.Host.Tests.Snowflake` and `OpenTail.Host.Tests.Worker`: not run, and they name none of the changed events in source.

## 4. A2A (A2A plan)

Decided by the owner, 2026-11-11: wanted now. The card requires authentication (see the owner decision below: the review reverses this
default). Every reply is reviewed. Durability is `synchronous=full` when A2A is on.

Still the plan's defaults, not yet confirmed by the owner:

- [ ] One key per partner (decision 3). The review replaces this with a `partnerId` principal; see below.
- [ ] JSON-RPC first; HTTP+JSON when a partner asks (decision 4).
- [ ] A generic error for a skill the caller may not use (decision 7).
- [ ] Card and skill changes are configuration only, not `skills.publish` (decision 8).
- [ ] Reviewer reasons stay internal (decision 9).
- [ ] File parts are refused in v1 (decision 11).

**Not started. Phase 0 and phase 1 wait for your go-ahead.**

Corrections from the external review, all checked against the spec (`a2a.md`, "Corrections from external review"). Adopted, to build:

- [ ] Task ids: a separate persistent `a2a_tasks` id, linked to the `a2a.submit` action, the work item and the `a2a.reply` action. The server
  generates the id, and a client may not supply one (spec §3.4.2). First-message handling is crash-safe and idempotent (correction 1).
- [ ] `returnImmediately` is honoured. The default is blocking; `true` returns once the task is created (spec §3.2.2). Tests for `true`,
  `false` and omitted (correction 2).
- [ ] ListTasks: cursor pagination with `pageToken` and `nextPageToken`, newest first (spec §3.1.4) (correction 5).
- [ ] `A2A-Version`: an empty or missing value is rejected as unsupported. The spec makes an empty value 0.3 (spec §3.6.2) (correction 6).
- [ ] Partner identity: an immutable `partnerId` is the principal, with credentials mapped to it. This replaces `key:<name>` as the subject
  (correction 7). It belongs in phase 0, the "partner identity foundation".
- [ ] Skill selection: a skill-selection extension, declared in the card's capabilities and sent in the message's extensions, replaces the
  `metadata.skill` proposal (correction 4). The URI needs the owner's confirmation.

Owner decisions these raise:

- [ ] **Blocking requests that wait on a person.** A review can take days. The spec's blocking default waits only for a terminal or
  interrupted state, and has no non-terminal state a blocked request can return with. Options: (a) return the SUBMITTED task at once for
  these requests, and document it as a deviation from the default; (b) hold the request up to a limit, then return the current state, which
  breaks the spec; (c) something else. The review and this plan do not choose.
- [ ] **The card default.** The review proposes a public static card plus an authenticated per-caller `/extendedAgentCard` (correction 3).
  That reverses the default recorded from your "no idea" answer. Confirm the change.
- [ ] **The extension URI.** The review proposes `https://opentail.net/a2a/extensions/skill-selection/v1`. Confirm the name and that we own
  the domain.
- [ ] **Partner ids and credentials.** Format for `partnerId`, and where credentials are managed (config file for now).

Open questions (fix in `a2a.md` before phase 1, or decide):

- [ ] Skill visibility. Which profiles may use an inbox skill: any profile that lists `a2a.submit` (the current wording), or a per-skill
  list. Not written down.
- [ ] Work items. Read them through the work store (`IWorkStore.Get`), not `WorkService.Read`. `Read` taints the reader's session and
  renews the lease, which a partner's read must not do.
- [ ] Caller identity. `A2ARequestContext` carries no caller. The caller comes from `IHttpContextAccessor`, which needs registering.
- [ ] Schema. One bump for both the governance binding and A2A, or two? Decide once, with the binding question in section 1.

Phase 0, prerequisites (no A2A code yet):

- [ ] The role and scope `a2a:call` (`Roles` in `OpenTail.Host.Admin/AdminServer.cs`), accepted by the API key scope validation
  (`HostHttpOptions.cs`, which refuses only review, admin and group scopes today).
- [ ] Startup check: `DataFlowMode` must be `Enforce` when A2A is configured (`PolicyConfig.DataFlowMode`, which defaults to `ReportOnly`).
- [ ] Startup check: `OPENTAIL_DB_SYNCHRONOUS=full` when A2A is configured (`SqliteActionStore.Synchronous` is public; it defaults to `normal`).
- [ ] Startup check: a key with `a2a:call` must name a profile.
- [ ] Test: a conditional rule on a top-level argument (`partner`) works in `Policy/Rules/Conditions.cs`.
- [ ] Partner identity foundation (correction 7).

Phase 1, inbound inbox (after the whole-action binding in section 1):

- [ ] Confirm the decision to implement the SDK's `IA2ARequestHandler`, not `A2AServer`. (Recorded in `a2a.md`.)
- [ ] Add `A2A` and `A2A.AspNetCore`, both `1.0.0-preview4`, to `Directory.Packages.props` and the HTTP project. (Restore was tested in a
  scratch project; nothing is added yet.)
- [ ] Tables `a2a_contexts`, `a2a_tasks`, `a2a_messages`. The schema question is open (section 1).
- [ ] A `Completed` event on the work service, added without changing behaviour.
- [ ] Tools `a2a.submit` (reads outside content, queued) and `a2a.reply` (an exit, queued, edits not allowed), each with a preview.
- [ ] Map work items and reply actions to A2A task states.
- [ ] JSON-RPC endpoint, the authentication matrix, the public card and the authenticated extended card (correction 3).
- [ ] Tests: task-id creation after a crash, `messageId` reuse, `returnImmediately` (`true`, `false`, omitted), ListTasks pagination and
  ordering, `A2A-Version` empty and missing, taint on submit, a labelled reply refused, foreign and unknown ids, rate limit, out-of-profile
  trip, and a context that survives a session expiry.

Verify before building (from `a2a.md`'s verification list, plus what the review checked):

- [ ] SDK: `MapA2A` returns a builder that accepts `RequireAuthorization`, and an ASP.NET policy can guard the endpoint.
- [ ] Specification: sections 3, 4.3, 5.4 and the full text of 13.1 were not read. Check whether `skills` may be empty.
- [ ] Specification: `application/a2a+json` accepted on requests (the SDK's guide asks for strict JSON).
- [ ] Specification: a missing `A2A-Version` header is not specified. Our rule rejects it; confirm that is acceptable for partners.
- [ ] The review page escapes partner text when it shows a queued request.
- [ ] Rule syntax for conditions on a top-level argument.

Phase 2, curated skills:

- [ ] Tool skills with data parts; the extended card in full; card signing; HTTP+JSON; 0.3 compatibility if a partner needs it.

Phase 3, push and input:

- [ ] Push, status only, to registered hosts.
- [ ] `work.ask_partner`, for the INPUT_REQUIRED state.

Phase 4, outbound:

- [ ] Remote agents as modules; card pinning; callbacks, once external waits exist.

## 5. Gate for migrating to the Microsoft package

All of these must hold before any package is taken:

- [ ] The approval protocol is stable: its ADR is accepted, its .NET implementation has a conformance suite, and the toolkit's README no
  longer calls its releases preview. (The core package 5.0.0 is already stable on NuGet; the gate is about the protocol and the README.)
- [ ] Our digest passes the reference test data.
- [ ] The conformance harness (phase 4) passes.
- [ ] The ADR's open items are specified: the store interface, the webhook response fields, and how an approver's identity is asserted.
- [ ] YamlDotNet: the package needs 18.1.0 or later; the Host pins 17.1.0 (`Directory.Packages.props` line 26). Moving the pin changes every
  project that uses YamlDotNet, including the Skills library.
- [ ] A real requirement exists, not a preference: an external interoperability need, not just the overlap.

Revisit triggers from the comparison, which are not work yet:

- [ ] We consume third-party MCP servers, so we need the toolkit's tool-description scanning (`McpSecurityScanner`, not read).
- [ ] We need OPA or Cedar policy backends (the toolkit has them; we do not).

## Decided, not doing for now

- Adopting the Microsoft NuGet package. Deferred because of the overlap with the Host, the integration work, the open conformance question,
  and no real external interoperability requirement yet. The maturity wording in `agt-comparison.md` is corrected.
- Renaming `ActionStatus`, `ToolMode`, or the operator config values `Execute`, `Queue`, `Deny`.
- Changing notification names or telemetry labels.
- Not adding `execution_allowed` (see section 1).
