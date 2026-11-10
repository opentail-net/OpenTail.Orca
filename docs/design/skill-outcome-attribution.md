# Design: skill outcome attribution

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** the Host's measurement of how people decided the actions that followed a served skill version: `OpenTail.Host/Runtime/SkillOutcomes.cs`, and its admin command in `OpenTail.Host.Admin/PolicyCli.cs` (`--skill-outcomes`).
**Not in this plan:** the store, the hashes, the library. Also not here: `ReviewOutcomeExport` (`ReviewOutcomeExport.cs:120`) and `ShadowCases` (`:184`), which read the same `skill_served` records and list every skill served earlier in the session as context for each decision. They do not count or suggest, so the misattribution does not drive a rollback there; whether they should use the same rule is a later question.
**Code basis:** `opentail-host` on `standard-skill-files` at 4b8a8b7 (committed). The line numbers below are that commit's. Checked by grep on 2026-11-10: only `SkillsModule.cs:46` declares the `skill_served` audit event in the Host repositories under `C:\Git\split` (the workbench's `NativePackage.cs` names it in text only).

## Purpose

The measurement should say how a skill version went, and nothing it cannot support. A decision must count only for the skill version that could have influenced it. Where that cannot be known, the decision is reported as ambiguous, never counted for every candidate.

## Findings checked

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| A1 | Every decided action is attributed to every skill version served earlier in the session | **Confirmed** | `SkillOutcomes.cs:55-56`: `serves.Where(s => s.CreatedAt <= a.CreatedAt).DistinctBy(s => s.Label)`. Each label receives the decision. |
| A2 | Two versions of one skill served in a session both count | **Confirmed** | Same line. `DistinctBy(Label)` keeps both versions. |
| A3 | Actions after two different skills count for both | **Confirmed** | Same line. |
| A4 | This can produce misleading rollback recommendations | **Partly** | The suggestion is printed only by the admin command, with the note "Nothing was changed" (`PolicyCli.cs:486-488`). Nothing applies it. A person could act on a wrong suggestion. `Suggest` requires 20 decided actions per version and a confidence check (`SkillOutcomes.cs:79-94`), which limits noise but not misattribution. |
| A5 | Expose sample sizes and confidence intervals | **Already done, mostly** | `PolicyCli.cs:478-483` prints decided, approved, rejected, sent back, turned down with its 95% interval, and last served. Missing: counts of decisions that were left out, and why. |
| A6 | Record the skill context at submission | **Needs a protocol change** | The Host sees tool calls only. Nothing in a call says which skill shaped it. See option B. |
| A7 | Not every counted decision is a person's (found in this review) | **Confirmed by reading** | `SkillOutcomes.cs:51, 54` count any `Approve`, `Reject` or `RequestChanges` review. The Host writes some itself: `ActionQueue.cs:597` adds a `system` approval when an undo window elapses; `PolicyCli.cs:183` adds a `Reject` when an operator cancels pending actions (status `Cancelled`); AI reviewers write reviews with an AI subject (`AiReviewRules.cs:61, 64`). All are counted as people's decisions today. See O-A3. |

## Contracts this must satisfy

- `SkillOutcomes.CollectAsync(host, since, ct)` returns `SkillVersionOutcome(Skill, Version, Decided, Approved, Rejected, ChangesRequested, LastServed)`, a positional record, with `TurnedDown` and `Interval` (95% Wilson, `(0, 1)` when `Decided` is 0). Results are ordered by skill, then by version as a number. Caller: `PolicyCli.cs:476`. Tests: `OpenTail.Host.Tests/SkillOutcomesTests.cs`, which also constructs the record positionally (`:70-71`), so new members go at the end with defaults.
- `SkillOutcomes.Suggest(outcomes, minCases = 20, worseBy = 0.15)` returns `RollbackSuggestion(Skill, Current, Previous, Why)`. Per skill it takes the two outcomes with the latest `LastServed`. Caller: `PolicyCli.cs:486`.
- `LastServed` today is the latest serve of that version that preceded a counted decision (`SkillOutcomes.cs:65`), not the latest serve. It decides which two versions `Suggest` compares.
- Data read: serve tools are `host.Catalog.All` where `Descriptor.AuditResult.Event == "skill_served"`, read from the field named in `AuditResult.Field` (`served` for `skills.get`, `SkillsModule.cs:46`). Serves are queried with `Status = Succeeded` and `UpdatedSince = since` (`:45`); that window is what O-A2 replaces. Actions come from `host.Store.QueryAsync` with `SessionId` and no window (`:50`), and each record gives `CreatedAt`, `ToolName`, `ResultJson` and `Reviews`.
- The store query (`ActionQuery`, `OpenTail.Host.Abstractions/Actions/Interfaces.cs`) filters by time only through `UpdatedSince` (`:15`); it has no filter on a review's time. So the window of O-A2 cannot be a store filter today. Proposed: query with `UpdatedSince` at the window's start and select by `ActionReview.At` in memory. That is complete only if adding a review always moves the action's update time; to verify in the store's code before step 3. If it does not, the window needs a new `ActionQuery` member, which is a contract change in `OpenTail.Host.Abstractions` and must be added to this plan first.
- The admin command reads `--since` once, for every subcommand (`PolicyCli.cs:149`); the usage text for `--skill-outcomes` is at `:20`. The other subcommands (`--export-review-outcomes`, `--export-shadow-cases`, `--review-report`, `--usage` and others) keep `--since`. The refusal in O-A2 applies only when `--since` is given with `--skill-outcomes`.
- A decision is the last of `Approve`, `Reject` and `RequestChanges` in an action's reviews (`SkillOutcomes.cs:54`); `Retry` is not a decision. The action's status is not read.
- Serve tools are excluded from the decided actions (`SkillOutcomes.cs:53`).

## Branch-by-branch: attribution

| Case | Today | Proposed (option A) |
|---|---|---|
| no serve before the action in its session | not counted | unattributed (counted in the report) |
| a session with no successful serve at all | never read (only sessions with a serve are queried, `:47-50`) | its decided actions are unattributed (counted in the report); this needs a query of every decided action in the window, not only per served session |
| every serve before the action is the same version | counted for that version | counted for that version (clean) |
| the same version served more than once | counted once per label | counted once (clean) |
| serve and action with equal `CreatedAt` | the serve counts as before the action (`<=`, `:56`) | unchanged |
| two versions of one skill served before the action | counted for both | ambiguous: in no version's rate |
| two different skills served before the action | counted for both | ambiguous |
| a serve that failed (status not `Succeeded`) | ignored (`:45`) | unchanged |
| a serve whose result has no string `served` field containing `@` | ignored (`:49`, `Field` returns null on bad JSON) | unchanged; counted as "serve unreadable" in the report |
| a decision changed later (for example sent back, then approved) | last decision counted | unchanged |
| action with no `Approve`, `Reject` or `RequestChanges` review (pending, expired, or only `Retry`) | not counted | unchanged |
| action with a decision review but a later status (sent back and resubmitted, so pending again) | counted, by its last decision; status is not read | unchanged (stated in the report's help text) |
| operator cancellation (`Cancelled`, with a `Reject` review, `PolicyCli.cs:183`) | counted as rejected | not a decision (O-A3) |
| undo window elapsed (`system` approval, `ActionQueue.cs:597`) | counted as approved | not a decision (O-A3) |
| decision by an AI reviewer (AI subject) | counted like a person's | counted separately (O-A3) |
| a person's decision followed by a `system` review or an operator cancellation (for example sent back by a person, then cancelled) | the last review counts, so the cancellation counts as rejected | the last review by a person counts; the excluded review is skipped, not the whole action (O-A3) |
| a person's decision and an AI reviewer's decision on one action | the last of them counts | the person's last decision counts in the rate; the AI decision is counted in the separate AI figure (O-A3) |
| the serve itself | skipped | unchanged |
| action created before a serve and decided after it | not counted | unchanged: the serve came after the action |
| session id null | excluded | unattributed (counted in the report) |
| `since` window | applied to serves only, by their update time (`UpdatedSince`); later actions are read without it | the window selects decisions by their review time (`ActionReview.At`, the time of the decision itself). Serves are read without a time bound, for the sessions of the decisions selected, so a serve before the window still explains a decision inside it. An action created before the window and decided inside it is counted (O-A2) |
| `LastServed` | latest serve that preceded a counted decision | latest successful serve of that version in the window, whether or not it was followed by a clean decision, so `Suggest` compares the versions actually served last |

**`Suggest` branches** (`SkillOutcomes.cs:79-94`)

| Case | Today | Proposed |
|---|---|---|
| fewer than two versions of the skill | no suggestion (`:85`) | unchanged |
| either of the two latest has fewer than `minCases` decisions | none (`:87`) | the same test on clean decisions |
| the newer is worse by less than `worseBy` | none (`:88`) | unchanged, on clean counts |
| the newer's interval low end is at or below the older's rate | none (`:88`) | unchanged, on clean counts |
| otherwise | one suggestion naming both versions and their counts | the text adds the ambiguous and unattributed counts |
| the two latest versions are not adjacent, or the newer is a rollback to an older number | compared by `LastServed`, not by number | unchanged |

## Design

**Option A (proposed now): clean attribution.** For each decided action in a session, let `S` be the serves before it in that session.

1. If `S` is empty, the action is unattributed.
2. If every serve in `S` has the same skill id and version, the action is attributed to that version. This is the only count a version receives.
3. Otherwise the action is ambiguous. It is reported, but counted for no version.

No protocol change. The sample for each version shrinks in sessions that use several skills or versions, and the report says so, so the loss is visible rather than hidden.

**Option B (later; needs an owner decision): recorded context.** The client sends the skill context with each action it submits (for example a `skillsInUse` argument, or request metadata), and the Host records it on the action at submission. Attribution is then exact for that action. It needs a new field on the stored action (a SQLite schema change, with a migration), a contract change in `OpenTail.Host.Abstractions`, and client adoption. It measures only the clients that send the context.

**Option C (rejected): keep the session-level figure and label it.** A figure labelled "session-level" still drives the suggestion. The suggestion is what needs the attribution to be right.

**Presentation (both options).** `SkillVersionOutcome` gains `Ambiguous` (decisions in which this version was one of several candidates), at the end of the record with a default so existing callers compile. Unattributed decisions belong to no version, so they are a report-level total, not a per-version count; `CollectAsync` gains a companion (for example a result that carries the list and the totals, or a second method), shape fixed in step 3. If O-A3 is confirmed, the record also gains a count of AI reviewer decisions (at the end, with a default), and the report-level totals include the excluded `system` reviews and operator cancellations, and the unreadable serves. The admin command prints the per-version clean counts with their interval, the ambiguous count, and the totals.

**Suggestions.** `Suggest` uses clean counts only. Both versions must have at least `minCases` clean decisions. The suggestion text states the ambiguous and unattributed counts beside the clean ones.

## Decisions for the owner

- **O-A1 Option B.** Whether to ask clients for the skill context, and when. Recommended: decide after option A has run and shown how many decisions it leaves ambiguous. If most are ambiguous, option B is needed for the measurement to be useful at all.
- **O-A2 Window.** A report about recent judgements should select decisions by when they were made, not when the action was submitted: an action submitted last month and decided today is a decision made today. Recommended: the window selects decisions by `ActionReview.At` (the review's own timestamp), serves are read without a time bound for the sessions involved, and the flag is renamed to `--decided-since`, so its meaning shows in the command line. `--since` is refused with a message naming the new flag, rather than silently changing meaning. Confirm.
- **O-A3 Whose decisions count.** Today an operator's cancellation counts as a rejection and an elapsed undo window as an approval (A7). Recommended: count only reviews by people; leave out `system` reviews and operator cancellations (both reported as excluded, with counts); count AI reviewer decisions separately, not in the rate a rollback suggestion uses. When an excluded review follows a person's decision on the same action, recommended: the person's last decision counts and the excluded review is skipped (the alternative, leaving the whole action out, loses a real judgement).

## Tests

- `A_session_with_one_version_attributes_its_actions_to_it` (the clean case)
- `A_serve_after_an_action_does_not_count_it` (the "decided after" row)
- `A_session_with_two_versions_of_one_skill_leaves_later_actions_ambiguous` (A2)
- `Earlier_actions_before_the_second_version_stay_clean_for_the_first` (the boundary between two versions)
- `A_session_with_two_skills_leaves_its_actions_ambiguous` (A3)
- `The_review_example_leaves_calendar_create_and_mail_send_ambiguous_and_counts_neither_version` (the example from the review: `invoice-check@1`, `customer-reply@2`, `calendar.create`, `mail.send`)
- `A_null_session_is_unattributed_and_reported`
- `Suggest_does_not_fire_on_ambiguous_decisions` (A4)
- `The_window_selects_decisions_by_review_time_and_keeps_the_earlier_serve` (O-A2): an action created before the window and decided inside it is counted, with the serve that came before the window; `--since` is refused with a message naming `--decided-since`.
- `An_operator_cancellation_and_an_elapsed_undo_window_are_not_counted_as_decisions` (O-A3)
- `An_ai_reviewer_decision_is_counted_separately_and_not_in_the_suggestion_rate` (O-A3)
- `A_persons_decision_before_an_operator_cancellation_still_counts` (O-A3)
- `A_decided_action_in_a_session_with_no_serve_is_unattributed_and_reported`
- `Since_is_refused_only_with_skill_outcomes_and_still_works_for_the_other_commands` (O-A2)
- `Last_served_is_the_latest_serve_even_when_no_clean_decision_followed_it`
- `A_failed_serve_or_an_unreadable_served_field_is_ignored`

**Existing tests, mapped** (`SkillOutcomesTests.cs`, read 2026-11-10; two tests):

| Test | What it covers | Rows | Under option A |
|---|---|---|---|
| `A_version_whose_actions_are_turned_down_more_often_gets_a_rollback_suggestion` (`:31`) | 40 sessions, each serving one version then one `mail.send` a person decides; v1 2 of 20 rejected, v2 10 of 20; `Suggest` fires, not with `minCases: 50` or `worseBy: 0.5`; the label is not moved | "every serve before the action is the same version"; `Suggest` "otherwise", "fewer than `minCases`", "worse by less than `worseBy`" | unchanged: every session is clean, so the counts and the suggestion stay the same |
| `Similar_rates_are_not_flagged` (`:65`) | `Suggest` on constructed outcomes, 4 of 40 against 6 of 40 | `Suggest` "worse by less than `worseBy`" | unchanged; constructs `SkillVersionOutcome` positionally, so new members need defaults |

Rows with no test today: everything else in both tables.

## Work order

1. Map the existing `SkillOutcomesTests` cases to the branch table (done in this plan, above; re-check before coding).
2. Write the failing tests above.
3. Implement option A, the counts and the suggestion rule.
4. Update the admin command's output, and `docs/guides` where rollback suggestions are described (to find).
5. Measure on a real store: how many decisions are clean, ambiguous and unattributed. This is the input to O-A1.

## Acceptance

- The review's example session counts neither version for `calendar.create` or `mail.send`.
- No version receives a decision from a session in which another version or skill was served before it.
- Every row of both branch tables (attribution and `Suggest`) has a test or a stated reason it needs none.
- The suggestion never fires from ambiguous decisions.

## Not built

Nothing in this plan is built: not option A, not option B. Implementing option A waits for two decisions, which the skills-plan gates table lists: O-A2 (the window: review time, and the flag name) and O-A3 (whose decisions count). Option B waits for O-A1, and the protocol change it needs is not proposed for now.
