# Checklist: governance naming, re-check, and A2A

**One place to see what is done and what is not.** Each item links to the document that describes it. `[x]` means on `origin/main`
(last push `c283158`, 2026-11-12). `[ ]` means open. Decisions that stay put are plain bullets, not work.

Documents behind this list:
[naming plan](governance-naming-plan.md) · [comparison with Microsoft's toolkit](agt-comparison.md) · [A2A plan](a2a.md)

**Scope.** A first external partner needs the blockers in section A (before the A2A phase 0 work) and section B (phase 0 and 1). Section
C is later work. Section D is housekeeping, which does not block a partner. Keeping them apart is the point of this list.

## Next up

1. **Re-check test gaps** (A.1). No decision needed.
2. **Whole-action binding, with RFC 8785 vectors** (A.2).
3. **Export and resolution contract, with versioning** (A.3).
4. **A2A phase 0**, including the partner identity foundation (B).
5. **A2A phase 1 blockers** (C.1), once the corrections in `a2a.md` are built.

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

A.1 Re-check test gaps:

- [ ] A reviewer's retry under a changed policy has no direct test. The retry path goes through the same check.
- [ ] A delayed action that runs after a policy change has no direct test. It goes through the same check.
- [ ] The edit digest (`ActionReview.EditedArgumentsHash`) survives the SQLite store. Tested only with the in-memory store so far.

A.2 Whole-action binding (the naming plan's phase 1):

- [ ] Keep the raw-text `ArgumentsHash` unchanged, and add a binding digest beside it. Stored digests stay valid.
- [ ] Binding over the whole action: agent, subject, operation, tool, tool schema version, resource, arguments, policy version. One digest.
- [ ] Canonical JSON per RFC 8785 for the binding digest, checked against the reference test data (`cyberphone/json-canonicalization`,
  `testdata`; not opened yet). The digest is not called RFC 8785 compliant until it passes those vectors. The toolkit's "RFC 8785-style"
  wording is not enough.
- [ ] Check the .NET writer's number formatting against RFC 8785. The toolkit's digest writes some numbers differently (exponent forms).
- [ ] Compute `tool_schema_version` from each tool's parameter schema. The Host does not store one today.
- [ ] Store the binding digest per action. Schema question first: actions are JSON (`SqliteActionStore`, `$json`), so a new field may need
  no bump. The current schema is 12.

A.3 Export and resolution contract:

- [ ] A resolution record per action: outcome, digest, policy version, reason, time.
- [ ] Export an approval in the field names of `ApprovalRequest` and `ApprovalResolution`, through the CLI and the audit export.
- [ ] Versioning for the export: a version field, and a rule for which changes need a new version.
- [ ] Conformance tests for the export mapping. Read `ApprovalProtocolTests.cs` in the toolkit copy first. The full harness is later (C.3).

## B. A2A phase 0 (prerequisites, no A2A code yet)

Waits for your go-ahead.

- [ ] The role and scope `a2a:call` (`Roles` in `OpenTail.Host.Admin/AdminServer.cs`), accepted by the API key scope validation
  (`HostHttpOptions.cs`, which refuses only review, admin and group scopes today).
- [ ] Startup check: `DataFlowMode` must be `Enforce` when A2A is configured (it defaults to `ReportOnly`).
- [ ] Startup check: `OPENTAIL_DB_SYNCHRONOUS=full` when A2A is configured (`SqliteActionStore.Synchronous`; it defaults to `normal`).
- [ ] Startup check: a key with `a2a:call` must name a profile.
- [ ] Test: a conditional rule on a top-level argument (`partner`) works in `Policy/Rules/Conditions.cs`.
- [ ] Partner identity foundation: a server-generated `partnerId` (UUID), stored separately from the key name, with credentials linked to it
  through an optional partner reference on each API key entry (`a2a.md`, correction 7).

## C. A2A phase 1 (inbound inbox): blockers before the first partner

C.1 Blockers:

- [ ] Whole-action binding (A.2) is in place before any external partner is connected. The review's rule.
- [ ] Task ids: a separate persistent `a2a_tasks` id, linked to the `a2a.submit` action, the work item and the `a2a.reply` action. The
  server generates it (spec §3.4.2). First-message handling is crash-safe and idempotent: a retry with the same `messageId` finds the
  existing task (correction 1).
- [ ] `returnImmediately`: honoured exactly as the spec says. `true` returns once the task is created. `false` and omitted block until a
  terminal or interrupted state, and a blocked request is never answered with SUBMITTED because a timeout expired. A disconnect discards
  nothing (correction 2). Tests for `true`, `false` and omitted.
- [ ] `A2A-Version`: an empty or absent header is read as 0.3, and the server answers `VersionNotSupportedError` (correction 6). Tests for
  empty, absent, `0.3` and `1.0`.
- [ ] ListTasks: cursor pagination with `pageToken` and `nextPageToken`, newest first (spec §3.1.4) (correction 5).
- [ ] Authenticated card (decided, correction 3): `/.well-known/agent-card.json` requires authentication. `/extendedAgentCard` is generated
  from the caller's profile. Internal tools, reviewer groups and policy stay out of both.
- [ ] Skill selection extension at `https://opentail.net/a2a/extensions/skill-selection/v1`, declared in the card and sent in the message
  (correction 4). The extension's specification is published at that URI before any partner is onboarded.
- [ ] Tools `a2a.submit` (reads outside content, queued) and `a2a.reply` (an exit, queued, edits not allowed), each with a preview.
- [ ] Map work items and reply actions to A2A task states.
- [ ] JSON-RPC endpoint, and the authentication matrix.
- [ ] Tests: task-id creation after a crash, `messageId` reuse, `returnImmediately`, ListTasks pagination and ordering, `A2A-Version`, taint
  on submit, a labelled reply refused, foreign and unknown ids, rate limit, out-of-profile trip, and a context that survives a session
  expiry.

C.2 Verify before building:

- [ ] SDK: `MapA2A` returns a builder that accepts `RequireAuthorization`, and an ASP.NET policy can guard the endpoint.
- [ ] Specification: `skills` may be empty? Check. Sections 3, 4.3, 5.4 and the full text of 13.1 were not read.
- [ ] Specification and SDK: how an absent `A2A-Version` header is handled. The spec covers only the empty case.
- [ ] Specification: `application/a2a+json` accepted on requests.
- [ ] The review page escapes partner text when it shows a queued request.
- [ ] Rule syntax for conditions on a top-level argument.
- [ ] `IA2ARequestHandler` (not `A2AServer`), with `A2A` and `A2A.AspNetCore` 1.0.0-preview4 added to `Directory.Packages.props`. Restore was
  tested in a scratch project; nothing is added yet.

C.3 Still to decide while building (not blockers if the first partner accepts the defaults):

- [ ] Skill visibility: which profiles may use an inbox skill. Any profile that lists `a2a.submit`, or a per-skill list.
- [ ] Work items are read through the work store (`IWorkStore.Get`), not `WorkService.Read`. `Read` taints the reader's session and renews
  the lease.
- [ ] Caller identity comes from `IHttpContextAccessor`, which needs registering.
- [ ] Schema: one bump for both the binding and A2A, or two.
- [ ] Owner decision: how many blocking waits one partner may hold open at once. An operational limit. It must not change what a blocked
  request returns.
- [ ] Plan defaults that the owner has not yet confirmed: JSON-RPC first (decision 4); a generic error for a skill the caller may not use
  (7); card and skill changes are configuration only (8); reviewer reasons stay internal (9); file parts refused in v1 (11).

## C. Later (not blocking a first partner)

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

- Timing: wanted now (owner, 2026-11-11).
- The card requires authentication; partners receive the card location and credentials out of band (owner default; second review, 2026-11-12).
- `returnImmediately=true` is the recommended integration mode; blocking requests follow the spec exactly (second review, 2026-11-12).
- The skill-selection extension URI is `https://opentail.net/a2a/extensions/skill-selection/v1` (second review, 2026-11-12).
- Partner ids are server-generated UUIDs, separate from key names; credentials use the Host's existing configuration (second review, 2026-11-12).
- Empty and absent `A2A-Version` are read as 0.3 and answered with `VersionNotSupportedError` (second review, 2026-11-12).
- Replies are reviewed; durability is `synchronous=full` when A2A is on (owner, 2026-11-11).
- Not adopting the Microsoft NuGet package now (overlap, integration cost, conformance question, no external interoperability requirement yet).
- Not renaming `ActionStatus`, `ToolMode`, or the operator config values `Execute`, `Queue`, `Deny`.
- Not changing notification names or telemetry labels.
- Not adding `execution_allowed`.
