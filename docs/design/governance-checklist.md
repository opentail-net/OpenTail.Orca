# Checklist: governance naming and the re-check (the A2A items moved to a2a.md)

**One place to see what is done and what is not.** Each item links to the document that describes it. `[x]` means done and committed on `main`
(`origin/main` is at `7b6003c`, batch 05, 2026-11-13). `[ ]` means open. Decisions that stay put are plain bullets, not work.

Documents behind this list:
[naming plan](governance-naming-plan.md) · [comparison with Microsoft's toolkit](agt-comparison.md) · [A2A plan](a2a.md)

**Scope.** Governance naming, the re-check at the claim, housekeeping, and the gate for migrating to the Microsoft package. **The A2A items moved to [`a2a.md`](a2a.md) on 2026-11-13**; sections B and C below are pointers. Section A is what a first external partner needed before A2A phase 0. Section D is housekeeping, which does not block a partner.

## Next up

Order agreed with the owner on 2026-11-12. Items 1 to 5 are done and committed (see Batches below); they stay listed so the order is visible.

1. ~~**Enforce the binding digest at the claim** (A.2).~~ Committed in `15b9e8b`.
2. ~~**Fold the corrections into the body of `a2a.md`** (B.0).~~ Committed in `15b9e8b`.
3. ~~**The resolution record, without the export** (A.3).~~ Committed in `15b9e8b`. The export waits for a consumer.
4. ~~**A2A phase 0** (B), including the partner identity foundation.~~ Committed in `f24f8c5`.
5. ~~**A2A thin slice** (C.0): one inbox skill end to end against the SDK's own client.~~ Committed in `fae2725` to `7b6003c`. What is still open is in [`a2a.md`](a2a.md).

## Batches (record)

The batch table (01 to 06, A2A and Teams) moved to [`a2a-history.md`](a2a-history.md) on 2026-11-13. The briefs and reports are listed in `docs/briefs/README.md`.

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

A.1 Re-check test gaps (done 2026-11-12, committed in `15b9e8b`):

- [x] A reviewer's retry under a changed policy is refused (`RecheckAtClaimTests`).
- [x] A delayed action that comes due after a policy change is refused.
- [x] The edit digest (`ActionReview.EditedArgumentsHash`) survives the SQLite store.

A.2 Whole-action binding (the naming plan's phase 1; done 2026-11-12, committed in `15b9e8b`):

- [x] The raw-text `ArgumentsHash` is unchanged (verified: it is the digest of the raw text, not canonical JSON). The binding digest is a
  new field beside it, `ActionRecord.BindingDigest`. Stored digests stay valid.
- [x] Binding over the whole action (`ActionBinding`): `schema_version`, `operation` (`tool_call`), `agent_id` (the requester's id),
  `subject_id` (null until delegation exists), `target` (`tool_name`, `tool_schema_version`, `resource`: null), `parameters`,
  `policy_version`. One digest, `sha256:` over the canonical form.
- [x] Canonical JSON per RFC 8785 (`Jcs` in `OpenTail.Orca.Abstractions`). It passes all six reference vectors, vendored unchanged in
  `OpenTail.Orca.Abstractions.Tests/Rfc8785/` (Apache-2.0, source commit `32a4819`). The reference repository's 100-million-number file
  is generated, not shipped, and was not run.
- [x] Number formatting follows ECMAScript (`1e+30`, `1e-7`, `1e+21`, `0.000001`), tested at the edges. It does not use the .NET
  writer's format.
- [x] `tool_schema_version` is `sha256:` over the canonical form of the tool's parameter schema, computed when the tool is registered
  (`ToolCatalog.SchemaVersionOf`).
- [x] Stored per action, in the action's JSON. No schema bump was needed; the schema is still 12.
- [x] Enforce the binding at the claim (agreed 2026-11-12; committed in `15b9e8b`). Recompute the binding from the stored record
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
  Abstractions 70 of 70; Orca 1204 of 1204 (`dotnet test OpenTail.Orca.Tests/OpenTail.Orca.Tests.csproj`).

A.3 Resolution record (scope reduced 2026-11-12; committed in `15b9e8b`):

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

## B and C. A2A phases 0 and 1: moved

The A2A items that were here (phase 0, the thin slice, the blockers, the verification points and the points to decide while
building) moved to [`a2a.md`](a2a.md) on 2026-11-13, under "Item-level checklist, phases 0 and 1". That file is now the one
place for A2A work, done and open.

## C2. Later (not blocking a first partner)

A2A phases 2 to 4 are in the plan in [`a2a.md`](a2a.md).

- [ ] Conformance harness against the toolkit's full expectations. Only at the migration gate (section E).
- [ ] Run the toolkit's own tests. The local copy is at `C:\Git\split\examples\agent-governance-toolkit\`. Re-check its file hashes before
  relying on it (`agt-comparison.md`); the copy has no git history of its own.
- [ ] Workbench contracts: the review-outcome values (`approved`, `rejected`, `changes_requested`) and the action status strings the
  workbench reads (`denied`, `failed`, `error`). Keep, or version them in a coordinated change in `opentail-orca` and
  `opentail-workbench`. The workbench reads them in `ShadowRunner.cs`, `GradingQueue.cs`, `Formats/Candidates.cs` and `Reports/TextReport.cs`.
- [ ] Revisit triggers from the comparison: third-party MCP servers (`McpSecurityScanner`, not read); OPA or Cedar backends.

## D. Housekeeping (does not block a partner)

- [ ] Freeze trigger for public names: the first deployment that keeps audit data, or Orca 1.0. The default is recorded; confirm it.
- [ ] Write the freeze point into Orca's README, as the naming plan asks.
- [ ] Check the published packages. `host-v0.0.1` (25 packages, 2026-10-20) carries the old audit names. Who consumes them?
- [ ] Check GHCR pull counts for `ghcr.io/opentail-net/opentail-host:0.0.1`. Is the image public, and was it pulled?
- [ ] Check whether anyone kept an old audit database, or consumes the webhook event names on `audit_entry` routes. Tell them.
- [ ] Discard the development databases written before the event rename. Their audit chains fail verification.
- [ ] Update documents that still quote old event names (for example `audit-and-evidence.md`, which lists `approved`).
- [ ] Remove the eight extra worktrees before deleting their branches: `C:\Git\split\opentail-orca-attr`, `-golden`, `-hash`, `-main`,
  `-s1`, `-s9`, `-skills` and `-store`.
- [ ] Delete the merged local branches: the eleven skills branches (`standard-skill-files`, `feat/agentskills-library`,
  `feat/agentskills-names`, `feat/agentskills-preserve`, `feat/hash-v2`, `skills-plan`, `test/skill-identity-golden`,
  `feat/attribution-measure`, `fix/s1-discovery-labels`, `fix/s9-labels-overwrite`, `feat/store-integrity-rest`), plus
  `naming-public-names`, `recheck-at-claim` and `integrate-skills`. Their copies on `origin` are a separate decision.
- [ ] The OpenTail tool recreates `.opentail-shadow.git/` in the repo. It is excluded locally. Turn shadow off in the tool's config, or accept
  the folder.
- [ ] The C# rename tool fails in this session with a missing-assembly error. A `restart_server` would rebuild it.
- [ ] Tests not run here: `OpenTail.Orca.Tests.SqlServers` (skips without `OTAIL_SQL_*`); `OpenTail.Orca.Tests.Snowflake` and
  `OpenTail.Orca.Tests.Worker`. They name none of the changed events in source.
- [x] All eleven skills branches are ancestors of `main`. `skills-plan` was merged in `6f6ec88`; the rest were already there.
- [x] Architecture rule allows `OpenTail.AgentSkills` for modules, as a leaf library (`90b34ea`).

## E. Gate for migrating to the Microsoft package

All of these must hold before any package is taken:

- [ ] The approval protocol is stable: its ADR is accepted, its .NET implementation has a conformance suite, and the toolkit's README no
  longer calls its releases preview.
- [ ] Our digest passes the reference test data.
- [ ] The conformance harness passes.
- [ ] The ADR's open items are specified: the store interface, the webhook response fields, and how an approver's identity is asserted.
- [ ] YamlDotNet: the package needs 18.1.0 or later; Orca pins 17.1.0 (`Directory.Packages.props` line 26). Moving the pin changes every
  project that uses YamlDotNet, including the Skills library.
- [ ] A real requirement exists: an external interoperability need, not just the overlap.

## Decided

The A2A decisions that were listed here moved to [`a2a.md`](a2a.md), under "Decisions".

- Not adopting the Microsoft NuGet package now (overlap, integration cost, conformance question, no external interoperability requirement yet).
- Not renaming `ActionStatus`, `ToolMode`, or the operator config values `Execute`, `Queue`, `Deny`.
- Not changing notification names or telemetry labels.
- Not adding `execution_allowed`.
- The binding digest is enforced at the claim; a tool schema change refuses older approvals (owner, 2026-11-12).
- The Microsoft-shaped export is deferred until it has a consumer; the resolution record is built (owner, 2026-11-12).
- The naming freeze is not in effect; the owner has not set one (2026-11-12).
