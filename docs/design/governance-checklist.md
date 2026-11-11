# Checklist: governance naming, re-check, and A2A

**One place to see what is done and what is not.** Each item links to the document that describes it. `[x]` means on `origin/main`
(last push `635d35e`, 2026-11-11). `[ ]` means open. Decisions that stay put are listed separately, not as work.

Documents behind this list:
[naming plan](governance-naming-plan.md) · [comparison with Microsoft's toolkit](agt-comparison.md) · [A2A plan](a2a.md)

## Next up

1. **Close the small test gaps** (no decision needed): the reviewer's retry under a changed policy, a delayed action that runs after a
   policy change, and the edit digest surviving the SQLite store. Section 1.
2. **Decide the binding design, then build phase 1 of the naming plan.** Two questions are open: keep the raw-text `ArgumentsHash` beside a
   new binding digest (recommended, so stored digests stay valid), and whether the schema needs a bump at all. Section 1.
3. **A2A phase 0 and phase 1 wait for your go-ahead.** Before phase 1, settle the task-id rule (section 4). Phase 0 can start on your word.
4. **Owner decisions** in section 3: the freeze trigger, the GHCR pull check, and the development databases.

## 1. Approval naming and the re-check (naming plan)

Done:

- [x] Comparison with the toolkit recorded, with locations and file hashes (`agt-comparison.md`, `89caf47`).
- [x] Step 1: approval audit events renamed to `approval_requested`, `approval_allowed`, `approval_denied`, `approval_expired`,
  `approval_cancelled` (`d6b10f3`).
- [x] Phase 2: an approved action is re-checked before it is claimed. Checks: expiry, the policy hash it was decided under, the argument
  digest, and the digest of any reviewer edit (`92c0048`).
- [x] Phase 2: reason codes `approval_expired`, `policy_version_mismatch`, `action_digest_mismatch`, stored as the action's error code
  (`92c0048`).
- [x] Phase 2: `approval_consumed` in the claim's transaction; `execution_denied` for a refused approval (`92c0048`).
- [x] Phase 2 tests: 6 in `RecheckAtClaimTests`. Each check was switched off in turn, and only its own test failed (`92c0048`).

Open, phase 2:

- [ ] Gap: a reviewer's retry under a changed policy has no direct test. The retry path goes through the same check.
- [ ] Gap: a delayed action that runs after a policy change has no direct test. It goes through the same check.
- [ ] Gap: the edit digest recorded with a review (`ActionReview.EditedArgumentsHash`) has only been tested with the in-memory store.
  Actions are stored as JSON (`SqliteActionStore`, the `$json` column), so it should survive, but that is not tested.
- [ ] Decide: records made before `PolicyHash` existed, or an edit with no recorded digest, are refused at claim (fail closed). Confirm that
  is acceptable for the data that exists.
- [ ] Deviation: the plan named `execution_allowed`. It was not added, because `approval_consumed` plus `execution_started` cover it.
  Decide: keep it that way, or add it.

Open, phase 1 (action binding and digest):

- [ ] Decide: keep the raw-text `ArgumentsHash` (stored digests stay valid) and add a binding digest beside it. Recommended. The current
  `ArgumentsHash` is not canonical JSON; it is the first 32 hex digits of SHA-256 over the stored text (`ArgumentDigest.cs`).
- [ ] Binding over the whole action: agent, subject, operation, tool, tool schema version, resource, arguments, policy version. One digest.
  Today the digest covers the arguments only, and the policy hash is compared separately.
- [ ] Canonical JSON per RFC 8785 for the binding digest, checked against the reference test data (`cyberphone/json-canonicalization`,
  `testdata`; not opened yet).
- [ ] Check the .NET writer's number formatting against RFC 8785. The toolkit's digest writes some numbers differently (exponent forms).
- [ ] Compute `tool_schema_version` from each tool's parameter schema. The Host does not store one today.
- [ ] Store the binding digest per action. Decide the schema question first: actions are JSON, so a new field may need no bump. The current
  schema is 12 (`SqliteActionStore.cs`).

Open, phases 3 and 4:

- [ ] Phase 3: a resolution record per action: outcome, digest, policy version, reason, time.
- [ ] Phase 3: export an approval in the field names of `ApprovalRequest` and `ApprovalResolution` (CLI and audit export).
- [ ] Phase 4: conformance harness against the toolkit's test expectations. Read `ApprovalProtocolTests.cs` first. Only at the migration
  gate (section 5).
- [ ] Run the toolkit's own tests, which have not been run. The local copy is at `C:\Git\split\examples\agent-governance-toolkit\`.
- [ ] Before relying on the toolkit copy, re-check its file hashes (`agt-comparison.md`). The copy has no git history of its own.

## 2. Cross-repo contracts (the workbench)

The workbench is in `C:\Git\split\opentail-workbench` (branch `progressive-disclosure`). It reads these strings in `src/OpenTail.Workbench`:
`ShadowRunner.cs`, `GradingQueue.cs`, `Formats/Candidates.cs` and `Reports/TextReport.cs`.

- [ ] Decide: the review-outcome export values (`approved`, `rejected`, `changes_requested`). Keep them, or version them.
- [ ] Decide: the action status strings the workbench reads (`denied`, `failed`, `error`, and others).
- [ ] If either changes: a coordinated, versioned change in `opentail-host` and `opentail-workbench`.
- Decided: notification names stay. They are operator configuration.
- Decided: telemetry reason labels stay.
- Decided: `denied`, `changes_requested`, `delayed`, `policy_decision`, `execution_*` and `approval_recorded` stay.

## 3. Owner decisions and housekeeping

Decisions:

- [ ] Freeze trigger for public names: the first deployment that keeps audit data, or Host 1.0. The default is recorded; confirm it.
- [ ] Write the freeze point into the Host's README, as the naming plan asks. Not done.
- [ ] Check GHCR pull counts for `ghcr.io/opentail-net/opentail-host:0.0.1`, published 2026-10-20. Is the image public, and was it pulled?
- [ ] If anyone uses webhook routes on `audit_entry`: tell them the event names changed.

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

Decided by the owner, 2026-11-11: wanted now. The card requires authentication. Every reply is reviewed. Durability is
`synchronous=full` when A2A is on.

Still the plan's defaults, not yet confirmed (the owner did not answer these):

- [ ] One key per partner (decision 3).
- [ ] JSON-RPC first; HTTP+JSON when a partner asks (decision 4).
- [ ] A2A-Version 1.0 only, until a partner needs 0.3 (decision 5).
- [ ] A generic error for a skill the caller may not use (decision 7).
- [ ] Card and skill changes are configuration only, not `skills.publish` (decision 8).
- [ ] Reviewer reasons stay internal (decision 9).
- [ ] File parts are refused in v1 (decision 11).

**Not started. Phase 0 and phase 1 wait for your go-ahead.**

Open questions found while re-reading the plan (fix in `a2a.md` before phase 1):

- [ ] Task id. `a2a.md` says the task id is the work item id. A work item does not exist until the request is approved, but the partner
  needs a task id on the first response. Decide: a separate task id, mapped to the action and, after approval, to the work item.
  Recommended.
- [ ] Card. A static card would list every inbox skill to every partner. Phase 1 needs a per-caller card at the well-known path, and
  `a2a.md` does not say so yet.
- [ ] Skill selection. The SDK's message has no skill field. `a2a.md` proposes `metadata.skill`, which needs checking against the
  specification.
- [ ] Skill visibility. Which profiles may use an inbox skill: any profile that lists `a2a.submit` (the current wording), or a
  per-skill list. Not written down.
- [ ] Work items. Read them through the work store (`IWorkStore.Get`), not `WorkService.Read`. `Read` taints the reader's session and
  renews the lease, which a partner's read must not do.
- [ ] Caller identity. `A2ARequestContext` carries no caller. The caller comes from `IHttpContextAccessor`, which needs registering.
- [ ] Schema. One bump for both the governance binding and A2A, or two? Decide once, with the governance question above.

Phase 0, prerequisites (no A2A code yet):

- [ ] The role and scope `a2a:call` (`Roles` in `OpenTail.Host.Admin/AdminServer.cs`), accepted by the API key scope validation
  (`HostHttpOptions.cs`, which refuses only review, admin and group scopes today).
- [ ] Startup check: `DataFlowMode` must be `Enforce` when A2A is configured (`PolicyConfig.DataFlowMode`, which defaults to `ReportOnly`).
- [ ] Startup check: `OPENTAIL_DB_SYNCHRONOUS=full` when A2A is configured (`SqliteActionStore.Synchronous` is public; it defaults to `normal`).
- [ ] Startup check: a key with `a2a:call` must name a profile.
- [ ] Test: a conditional rule on a top-level argument (`partner`) works in `Policy/Rules/Conditions.cs`.

Phase 1, inbound inbox:

- [ ] Confirm the decision to implement the SDK's `IA2ARequestHandler`, not `A2AServer`. (Recorded in `a2a.md`.)
- [ ] Add `A2A` and `A2A.AspNetCore`, both `1.0.0-preview4`, to `Directory.Packages.props` and the HTTP project. (Restore was tested in a
  scratch project; nothing is added yet.)
- [ ] Tables `a2a_contexts`, `a2a_tasks`, `a2a_messages`. The schema question is open (section 1).
- [ ] A `Completed` event on the work service, added without changing behaviour.
- [ ] Tools `a2a.submit` (reads outside content, queued) and `a2a.reply` (an exit, queued, edits not allowed), each with a preview.
- [ ] Map work items and reply actions to A2A task states.
- [ ] JSON-RPC endpoint, the authentication matrix, and the authenticated card per caller.
- [ ] Tests: `messageId` reuse, taint on submit, a labelled reply refused, foreign and unknown ids, rate limit, out-of-profile trip, and a
  context that survives a session expiry.

Verify before building (from `a2a.md`'s verification list):

- [ ] SDK: `MapA2A` returns a builder that accepts `RequireAuthorization`, and an ASP.NET policy can guard the endpoint.
- [ ] Specification: sections 3, 4.3, 5.4 and the full text of 13.1 were not read. Check whether `skills` may be empty.
- [ ] Specification: `application/a2a+json` accepted on requests (the SDK's guide asks for strict JSON).
- [ ] The review page escapes partner text when it shows a queued request.
- [ ] Rule syntax for conditions on a top-level argument.

Phase 2, curated skills:

- [ ] Tool skills with data parts; the extended card; card signing; HTTP+JSON; 0.3 compatibility if a partner needs it.

Phase 3, push and input:

- [ ] Push, status only, to registered hosts.
- [ ] `work.ask_partner`, for the INPUT_REQUIRED state.

Phase 4, outbound:

- [ ] Remote agents as modules; card pinning; callbacks, once external waits exist.

## 5. Gate for migrating to the Microsoft package

All of these must hold before any package is taken:

- [ ] A stable .NET release of the approval protocol, without a preview label.
- [ ] Our digest passes the reference test data.
- [ ] The conformance harness (phase 4) passes.
- [ ] The ADR's open items are specified: the store interface, the webhook response fields, and how an approver's identity is asserted.
- [ ] A real requirement exists, not a preference.

Revisit triggers from the comparison, which are not work yet:

- [ ] We consume third-party MCP servers, so we need the toolkit's tool-description scanning (`McpSecurityScanner`, not read).
- [ ] We need OPA or Cedar policy backends (the toolkit has them; we do not).

## Decided, not doing for now

- Adopting the Microsoft NuGet package. Deferred; see `agt-comparison.md`.
- Renaming `ActionStatus`, `ToolMode`, or the operator config values `Execute`, `Queue`, `Deny`.
- Changing notification names or telemetry labels.
