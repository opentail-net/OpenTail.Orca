# Plan: align approval naming with Microsoft's vocabulary now, freeze it later

**Status:** step 1 done locally 2026-11-11 (see [What was done](#what-was-done)); the rest proposed. Builds on [the comparison with Microsoft's toolkit](agt-comparison.md), which decided
to defer adoption and conversion.

**The question:** can we keep our naming and record shapes close to Microsoft's approval protocol (ADR-0030 and the .NET package), so
that a later migration is a package swap plus a small mapping, without taking the package now?

## Short answer

1. **Align names now, while the Host is pre-release.** Renaming costs churn in code, tests and docs. It loses no data, because
   nothing outside the owner's development environment is known to hold audit data (see [constraints](#constraints)).
2. **Align the public surfaces, not the internals.** Public surfaces are audit event names, reason codes, export JSON and the
   canonical binding keys. Those are what a migration touches. Internal types (`ActionStatus`, `ToolMode`, `ActionRecord`) keep their
   names, with a map.
3. **Keep the operator's policy words** (`Execute`, `Queue`, `Deny`). They are the Host's public vocabulary, and changing them would
   rewrite every policy file for a benefit that only a migration would realise.
4. **Build the three missing mechanisms in our own code, in Microsoft's shape:** an action binding with a digest, a re-check at claim,
   and a stored resolution record. The Host needs these whatever we decide about Microsoft.
5. **Freeze the public names at a defined point** (see [the freeze rule](#the-freeze-rule)). After that, a rename needs a migration step.

## Constraints

- **The audit hash covers event names.** `OpenTail.Host/Storage/AuditChain.cs` builds the hash from `seq`, timestamp, event, action,
  tool, actor, detail and previous hash (lines 35-36, 58). Renaming an event changes the hash that a verifier computes for an old row,
  so any existing chain would fail verification. This only matters where a chain already exists.
- **Who holds chains today.** The owner's development databases (`host-dev`, per project memory). Discard them rather than migrate.
  Project memory also records that Host 0.0.1 packages and a container image (`ghcr.io/opentail-net/opentail-host`) were published on
  2026-10-20. Whether anyone pulled the image cannot be seen from here. Check the registry's pull counts before the freeze, if the
  image is public.
- **No customer deployment is recorded.** The design documents describe one ("when the first customer runs Entra ID") as future work.
  Confirm this before relying on it.
- **Size.** The names appear in about 90 `.cs` files, most of them tests. The raw grep count was 688 occurrences, but that includes
  unrelated uses of words such as "approved" and "denied", so treat it as an upper bound.
- **Canonical JSON.** The toolkit's digest writes numbers with .NET's formatting. RFC 8785 specifies ECMAScript number formatting, and
  the reference repository's sample shows `1e+30`. Any digest we write must pass the reference test data
  (`cyberphone/json-canonicalization`, `testdata` folder, not yet opened) before we claim conformance.

## The freeze rule

Public names freeze at whichever comes first:

- the first deployment that keeps audit data we must keep (a customer, or a production run), or
- the Host's 1.0 release.

Before the freeze: rename freely, update tests and docs, and discard development databases (record that in the changelog). After the
freeze: a public name changes only with a migration step, a reviewed tool that rewrites or re-hashes the chain, or by adding the new
name and keeping the old one.

## Naming rules

1. **Concept matches:** use Microsoft's word (the ADR's name, or the .NET type's name).
2. **No match:** keep our name, and add a row to the map below.
3. **Operator config keeps our words:** `Mode` values `Execute`, `Queue`, `Deny` do not change.
4. **Approval reason codes use theirs:** `approval_pending`, `approval_denied`, `approval_expired`, `approval_cancelled`,
   `approval_consumed`, `action_digest_mismatch`, `policy_version_mismatch`, and the chain codes. Our other reasons (`not_in_profile`,
   `suspended`, `decoy_sent`, `unknown_tool`, and the rest) keep their names.
5. **Every map row is either tested or marked "no equivalent".**

## The vocabulary map

### Public surfaces (renamed now)

| Surface | Ours today | Microsoft's (ADR-0030 / .NET) | Decision |
|---|---|---|---|
| Event: a call is queued | `queued` | `ApprovalRequested` | Rename to `approval_requested`. |
| Event: a reviewer approves | `approved` | `ApprovalResolved` (outcome `Allow`) | Rename to `approval_allowed` (split from the rejection, so the outcome is in the name). |
| Event: a reviewer rejects | `rejected` | `ApprovalResolved` (outcome `Deny`) | Rename to `approval_denied`. |
| Event: expiry | `expired` | `ApprovalExpired` | Rename to `approval_expired`. |
| Event: cancellation | `cancelled` | `ApprovalCancelled` | Rename to `approval_cancelled`. |
| Event: claimed for execution | `execution_started` (at claim) | `ApprovalConsumed` | Add `approval_consumed` at claim. Keep `execution_started` for the run. |
| Event: re-check passed or refused | none | `ExecutionAllowed`, `ExecutionDenied` | Add `execution_allowed` and `execution_denied`, each with a reason code. |
| Event: policy decision | `policy_decision` | `PolicyDecision` | Keep. |
| Reason codes (approval path) | none | the `ApprovalReasonCodes` strings | Adopt the strings (rule 4). |
| Canonical binding keys | none | `schema_version`, `operation`, `agent_id`, `subject_id`, `target` (`tool_name`, `tool_schema_version`, `resource`), `parameters` | New. Use these keys. |
| Export JSON | none | `ApprovalRequest`, `ApprovalResolution`, chain entries | New. Use these field names (below). |

### Internal types (kept, mapped)

| Ours | Microsoft's | Map |
|---|---|---|
| `ActionStatus.Pending` | `ApprovalStatus.Pending` | Same. |
| `ActionStatus.Approved` (before claim) | `ApprovalStatus.Allowed` | Map in export. |
| `ActionStatus.Executing`, `Succeeded`, `Failed` (after claim) | `ApprovalStatus.Consumed` | Map in export. Our `Failed` can be retried, so there is no exact match. |
| `ActionStatus.Denied`, `Rejected` | `ApprovalStatus.Denied` | Map in export. We keep both: `Rejected` is a reviewer's choice, `Denied` is policy. |
| `ActionStatus.Expired`, `Cancelled` | `ApprovalStatus.Expired`, `Cancelled` | Same. |
| `ActionStatus.ChangesRequested` | none | No equivalent. |
| `ToolMode.Queue` | `PolicyDecision.Action` = `require_approval` | Map in export. |
| `ToolMode.Execute` | `allow` | Map in export. |
| `ToolMode.Deny` | `deny` | Map in export. |
| `ActionRecord.ArgumentsHash` | part of `ActionBinding.Digest` | Keep. Add the digest beside it. |
| `ActionRecord.PolicyHash` | `ApprovalRequest.PolicyVersion` | Keep. Use it in the re-check. |
| `ActionRecord.ExpiresAt` | `ApprovalRequest.ExpiresAt` | Same. |
| `ActionRecord.Reviews` | `ApprovalChainEntry` | Map in export. |
| `ReviewerGroup`, `RequiredApprovals` | `ApprovalStage` (`AllowedRoles`, `AllowedIdentities`) | Map in export. Their stages are ordered; ours is one group and a count. |
| (none) | `ApprovalChainId`, `ApprovalChainVersion` | No equivalent. Export `policy_version` only until we need chains. |
| (none) | `SubjectId` | No equivalent until delegation exists. Export null. |
| (none) | `target.resource` | No equivalent. Export null. |

### Field names for the export

- `ApprovalRequest`: `approval_request_id` (our action id), `policy_decision_id`, `action_digest`, `agent_id`, `subject_id`,
  `operation`, `target_resource`, `policy_version`, `requested_at`, `expires_at`, `status`, `fail_closed_on_timeout`.
- `ApprovalResolution`: `approval_resolution_id`, `approval_request_id`, `outcome`, `action_digest`, `policy_version`,
  `resolved_at`, `reason_code`.

## The plan

### Phase 1: the binding and its digest

- Add an `ActionBinding` record in `OpenTail.Host.Abstractions`, with the canonical keys above.
- `tool_schema_version`: a hash of the tool's parameter schema. The Host does not store this today, so it has to be computed when the
  catalogue is built.
- Canonicalise with RFC 8785 rules. Check the canonicaliser against the reference test data before using it. Where the
  .NET writer differs (number formatting), fix the writer or document the difference; do not claim conformance otherwise.
- Store the digest on each action at creation, beside `ArgumentsHash`.
- Check whether `ArgumentsHash` is computed over canonical JSON. This is not yet verified.
- **Useful without Microsoft:** yes. It binds an approval to the exact action.

### Phase 2: the re-check at claim (closes the gap)

- At `ActionExecutor` claim (`OpenTail.Host/Runtime/ActionExecutor.cs`, around line 385), recompute the digest from the stored
  binding and compare it. Compare `PolicyHash` with the running policy. Check the expiry and the status.
- A refusal writes `execution_denied` with `action_digest_mismatch`, `policy_version_mismatch` or `approval_expired`, and the action
  is not run.
- A claim that passes writes `approval_consumed` and `execution_allowed`, in the same transaction as the claim.
- Bundle the renames in the public-surfaces table with this phase, because it adds the new events and reason codes anyway.
- Tests: restart the Host with a changed policy file and check that the old approval is refused; tamper with stored arguments and check
  the digest refusal; let an approval expire and check the refusal.
- **Useful without Microsoft:** yes. This is the most important item.

### Phase 3: the resolution record and export

- Store the resolution per action: outcome, digest, policy version, reason and time. A schema bump, following the pattern used for
  `work_items`.
- Export the approval as JSON in the field names above, through the CLI and the audit export.
- **Useful without Microsoft:** yes. Auditors ask "what was approved, under which policy, and was it run?"

### Phase 4: conformance harness (only at the migration gate)

- Turn the map into tests. Read `tests/AgentGovernance.Tests/ApprovalProtocolTests.cs` in the local copy first.
- Run the reference test data against our canonicaliser and digest.
- Build the harness so that the build fails if a public name drifts from the map.

### Migration gate (all required before any package)

- Microsoft's .NET approval protocol has a stable release, without a preview label.
- Our digest passes the reference test data.
- The conformance harness passes.
- The ADR's open items are specified: the store interface, the webhook response fields, and how an approver's identity is asserted.
- A real requirement exists, not just a preference.

## Risks

- **Similar names are not compatibility.** Mitigation: the conformance harness tests behaviour, not names.
- **The ADR is Python-first, and the .NET code may diverge.** Mitigation: pin to a .NET release, and re-hash the files as in
  [the comparison](agt-comparison.md).
- **Renames cost churn.** Mitigation: rename only the public surfaces, and do it in one change before the freeze.
- **The freeze may be missed.** Mitigation: write the freeze point in the Host's README, and check the registry's pull counts for
  0.0.1 before the freeze.
- **Preview churn.** Mitigation: no dependency until the gate.

## Decisions

Proposed defaults, for the owner to change:

1. **Rename the public surfaces now**, in one change (default: yes).
2. **Freeze trigger:** the first deployment that keeps audit data, or Host 1.0, whichever is first (default).
3. **Keep the policy words** `Execute`, `Queue`, `Deny` (default: yes).
4. **Audit re-check refusals** as `execution_denied`, with a reason code (default: yes).
5. **Build Phases 1 and 2 first,** before any other naming work (default: yes). They fix a real gap.

## What was done

Step 1, done 2026-11-11 (commits `d6b10f3` and `89caf47`, on main): the audit event names on the approval path.

- New `OpenTail.Host/Runtime/AuditEvents.cs` with `ApprovalRequested`, `ApprovalAllowed`, `ApprovalDenied`, `ApprovalExpired` and
  `ApprovalCancelled`.
- Renamed (old names as on `main`): `queued` to `approval_requested`; `approved` to `approval_allowed`; `rejected` to
  `approval_denied`; `expired` to `approval_expired`; `cancelled` to `approval_cancelled`. Writers: `ActionQueue`, `OpenTailHost`
  (module removed) and `PolicyCli` (operator cancel).
- Detail unchanged: both the approval and the rejection still record the reviewer's name, as on `main`. The outcome is in the event
  name, so queries can tell them apart.
- Split on purpose: a single `approval_resolved` name hid the outcome from anyone filtering by event name. The two names mirror
  the outcome vocabulary (`Allow`, `Deny`) of the Microsoft protocol.
- Tests updated where they assert these names: `AuditAtomicityTests`, `DurabilityTests`, `InboxTriageScenarioTests`,
  `ResumableTests`, `SopGapTests`, `TelemetryRecorderTests`.
- Result: `OpenTail.Host.Abstractions.Tests` 33 of 33 and `OpenTail.Host.Tests` 1179 of 1179, the same counts as before the change.

**Deliberately not changed:**

- `denied` stays. It is a policy refusal, not a reviewer decision, so the plan's mapping did not cover it.
- `changes_requested`, `delayed`, `policy_decision`, `execution_*` and `approval_recorded` stay (no equivalent or already fine).
- **Cross-repo contracts** stay: the review-outcome export values `approved`, `rejected` and `changes_requested` and the action status
  strings. `opentail-workbench` reads these (`ShadowRunner.cs`, `GradingQueue.cs`, `Candidates.cs`, `TextReport.cs`). Changing them
  needs a coordinated, versioned change in both repositories.
- **Notification names** stay. They come from `ActionEventKind` and are operator configuration (`"On": ["queued"]`).
- **Telemetry reason labels** stay (`queued`, `delayed`, `running`). They are metric labels.

**Consequences to know about:**

- Audit entries written before this change carry the old names. The chain hash covers the event name, so those chains now fail
  verification. The owner's development databases should be discarded, not migrated.
- Audit routes on webhooks (`audit_entry`) and the admin audit API show the new names.
- No reason codes exist yet on the approval path, so there was nothing to rename there. They arrive with the re-check (phase 2).

## Phase 2: the re-check at claim (done 2026-11-11, commit `92c0048`, on main at `635d35e`)

- Before anything is claimed, an approved action is checked for three things: expiry (`approval_expired`), the policy hash it was
  decided under (`policy_version_mismatch`), and the digest of the arguments that would run (`action_digest_mismatch`). A reviewer's
  edit is checked against a digest recorded with its review (`ActionReview.EditedArgumentsHash`).
- A refused approval moves the action to `Failed` with that reason, not retryable. It writes `execution_denied` with the reason as the
  detail, and runs nothing.
- A passing claim writes `approval_consumed` in the claim's own transaction. `execution_started` follows when the run starts.
- Fail-closed: a record with no policy hash, or an edit with no recorded digest, is refused.
- Tests: `RecheckAtClaimTests` (6). Each check was switched off in turn, and only its own test failed.
- Behaviour to know about: an approval given after a policy change is refused. The same check refuses a reviewer's retry, and a delayed
  action that runs after a policy change; those two paths are not tested directly. The test fixture for interrupted actions now
  stores a real digest and the real policy hash.

## Sources

- ADR-0030, action-bound approval protocol: <https://microsoft.github.io/agent-governance-toolkit/adr/0030-action-bound-approval-protocol/>
- .NET source, local copy: `C:\Git\split\examples\agent-governance-toolkit\agent-governance-dotnet\` (hashes in [the comparison](agt-comparison.md))
- RFC 8785 reference implementations and test data: <https://github.com/cyberphone/json-canonicalization>
