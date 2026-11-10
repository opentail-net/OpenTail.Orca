# Design: activation evaluation in the Workbench

**Status:** designed 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** the Workbench (`opentail-workbench`): how a skill load is recognised, and how activation expectations are scored and compared. It is a separate system from the Host's skills module.
**Summary:** [skills-plan.md](skills-plan.md), step 2a. This document is the detail.
**Code basis:** `opentail-workbench` branch `progressive-disclosure`, commit `b762d50`. Clean.

## Purpose

The Workbench should measure whether the agent chooses the right skill, not only whether it eventually performs the right action. A near-miss scenario passes today when the agent acts on nothing, even if it loaded an irrelevant skill first. Activation expectations close that gap.

## Findings checked

| # | Fact | Evidence |
|---|---|---|
| W1 | Catalog activations are recorded on the transcript | `AgentRunner.cs`: `ActivateSkill` adds `skill.Label` to `activated` on success; `AgentTranscript.Activated` is set from it |
| W2 | Tools loads are not in `Activated` | `Activated` comes only from the `activate_skill` function. A `skills.get` call appears only in `Calls`, with its result JSON |
| W3 | A successful `activate_skill` returns `served`, `id`, `version` and `instructions`; an unknown id returns `{ "error": ... }` with no `status` key | `AgentRunner.cs`, `ActivateSkill` |
| W4 | A successful `skills.get` returns `served` (`id@version`) and `instructions`; a refused one throws, and the recorder marks the call `error` | `SkillsModule.cs:112`; `AgentRunner.cs`, `RecordingFunction`, catch branch |
| W5 | The checker matches each `must` and `must_not` entry against every call, by tool name and arguments; activation is not a separate kind | `Checks/Checker.cs:10-24`; `Formats/Scenario.cs` (`Expectations`) |
| W6 | `Comparison.Shifts` lists every call that is not a built-in as a call shift, so `activate_skill` appears there | `Runs/Comparison.cs`, `Shifts` |
| W7 | Native delivery puts every skill's full instructions in the prompt; None gives no skill | `AgentRunner.cs`, `SystemPrompt` |

## Contracts this must satisfy

- `AgentRunner` (`Agent/AgentRunner.cs`): `SkillDelivery { Native, Tools, Catalog, None }`; `AgentTranscript(Calls, FinalText, InputTokens, OutputTokens, Duration, Error)` with `Turns` and `Activated`; `ToolCall(Seq, Tool, Args, ResultJson, Status)`. Callers: `ScenarioRunner.RunOnceAsync`, the shadow runner, and tests.
- `Checker.Check(expect, calls, artifact)` returns `CheckResult(Kind, Index, Expectation, Passed, EvidenceSeq)`. `Kind` is `must` or `must_not` today. Callers: `ScenarioRunner.RunOnceAsync`, `RunResult.MustPassed` and `MustNotPassed`.
- `Expectations` (`Formats/Scenario.cs`): `Must`, `MustNot`, `Should`, `Grade`. A new `Activates` and `MustNotActivate` list is added at the end, with empty defaults so existing scenarios parse unchanged.
- `Comparison.Compare` and `ScenarioComparison`: per-scenario rates. Callers: `Cli/Program.cs` (`compare`), the HTML and text reports.
- `RunStore`: stores calls and turns per run. Activation is derived from stored calls, so no schema change is needed for the first step.

## The rule: what counts as an activation

| Delivery | How a load is recognised | Activation record |
|---|---|---|
| Catalog | a call to `activate_skill` whose result has `served` and no `error` | skill id, version (from `served`), call sequence |
| Tools | a call to `skills.get` whose result has `served` and no error | skill id, version (from `served`), call sequence |
| Native | not applicable: every skill is already in the instructions | none; result is N/A |
| None | not applicable: no skill is supplied | none; result is N/A |

A call that is refused or fails is an attempt, not an activation. It is kept in the record as an attempt. The check reads the result, not the status alone, because a failed `activate_skill` has no `status` key (W3).

## Branch-by-branch: loads

| Case | Catalog | Tools | Counts as |
|---|---|---|---|
| successful load of the expected skill | activation | activation | activation of that id |
| successful load of a different skill | activation of that other id | activation of that other id | an activation of the other id (matters for `mustNotActivate`) |
| refused: unknown id | `{ "error" }`, no `served`: attempt | the call throws: `error` status: attempt | attempt; no activation |
| refused: skill not in this scenario's catalog or store | as unknown id | as unknown id | attempt |
| exception during the load | attempt | attempt | attempt |
| the same skill loaded twice | two activation records | two activation records | one activation for the expectation; both records kept (O-W2) |
| the same skill loaded at two versions | two records, one per version | two records, one per version | one activation of the id; the versions are recorded |
| load in a run that has no skills (None) | no `activate_skill` function exists | no `skills.get` exists | N/A |
| load under Native | no function; the skill is already present | no function | N/A |

## Expectations (scenario contract)

```yaml
expect:
  activates:
    - invoice-check
  mustNotActivate:
    - email-triage
```

- `activates: [id]`: at least one successful load of that id in the run. Matching is by id; the version is recorded, not matched (O-W1).
- `mustNotActivate: [id]`: no successful load of that id in the run.
- Under Native and None the two lists are not applicable: the check reports N/A, which is neither a pass nor a failure, and never fails the run.
- The checks are reported as their own kinds (`activates`, `mustNotActivate`). They do not change `MustPassed`, `MustNotPassed` or `ChecksPassed` (O-W3).

## Reporting

- Per scenario and delivery: the activation rate (runs with the expected activation, and runs with a forbidden activation) with its 95% interval, reported beside the pass rates.
- Comparison: the activation rates of the two batches, with the same warning rules as pass rates.
- The call-shift list keeps listing `activate_skill` and `skills.get` as before; the activation rate is the new, separate figure.

## Decisions for the owner

- **O-W1 Matching by id or by id and version.** Recommended: by id in v1, with the version recorded. An `id@version` form can follow when a scenario needs it.
- **O-W2 Repeated loads.** Recommended: one activation for the expectation; every load recorded.
- **O-W3 Counting toward a pass.** Recommended: reported beside the pass rate, so existing pass rates do not move. Counting toward the pass is a later decision, once the rates are known.
- **O-W4 Near-miss scenarios.** Recommended: add `mustNotActivate` for the skill each near miss is about, reported beside the pass rate, as in step 2a.

## Implementation order

1. Derive activation records from stored calls (catalog and tools), with no change to any existing result. Tests first.
2. Parse `activates` and `mustNotActivate` in `Scenario.cs`, with the empty-default rule. Add the checks to `Checker.cs`, reported under their own kinds.
3. Add the activation rates to `Comparison` and the reports, separately from pass rates.
4. Add `mustNotActivate` to the three near-miss scenarios (O-W4), and document the format in `docs/formats.md`.

## Tests

- `A_successful_catalog_load_is_an_activation_with_its_version`
- `A_successful_tools_load_is_an_activation_with_its_version`: the same result shape through `skills.get`
- `A_refused_catalog_load_is_an_attempt_and_not_an_activation`
- `A_refused_tools_load_is_an_attempt_and_not_an_activation`
- `A_repeated_load_is_one_activation_for_the_expectation_and_both_are_recorded`
- `A_load_at_two_versions_is_one_activation_of_the_id_with_both_versions_recorded`
- `Mustnotactivate_fails_on_a_successful_load_of_the_forbidden_skill`
- `Mustnotactivate_passes_when_only_attempts_were_refused`
- `Activates_under_native_or_none_is_not_applicable_and_never_fails`
- `Activation_checks_do_not_change_the_pass_flags`
- `Existing_scenarios_without_activation_expectations_parse_and_run_unchanged`

## Acceptance

- Each row of both branch tables has a test.
- The activation rate is reported separately from the pass rates, and the pass rates of existing batches are unchanged by this work.
- A near-miss scenario whose agent loads the irrelevant skill and then takes no action fails its `mustNotActivate` check and passes its `must_not` check. That is the case this design exists to report.

## Not built

Nothing in this document is built. Step 1 needs no owner decision and can start once the order of work reaches it.
