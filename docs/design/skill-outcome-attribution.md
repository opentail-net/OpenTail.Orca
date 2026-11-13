# Design: skill outcome attribution

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** Orca's measurement of how people decided the actions that followed a served skill version: `OpenTail.Orca/Runtime/SkillOutcomes.cs`, and its admin command in `OpenTail.Orca.Admin/PolicyCli.cs` (`--skill-outcomes`).
**Not in this plan:** the store, the hashes, the library. Also not here: `ReviewOutcomeExport` (`ReviewOutcomeExport.cs:120`) and `ShadowCases` (`:184`), which read the same `skill_served` records and list every skill served earlier in the session as context for each decision. They do not count or suggest, so the misattribution does not drive a rollback there; whether they should use the same rule is a later question.
**Code basis:** `opentail-orca` on `standard-skill-files` at 4b8a8b7 (committed). The line numbers below are that commit's. Checked by grep on 2026-11-10: only `SkillsModule.cs:46` declares the `skill_served` audit event in Orca repositories under `C:\Git\split` (the workbench's `NativePackage.cs` names it in text only).

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
| A6 | Record the skill context at submission | **Needs a protocol change** | Orca sees tool calls only. Nothing in a call says which skill shaped it. See option B. |
| A7 | Not every counted decision is a person's (found in this review) | **Confirmed by reading** | `SkillOutcomes.cs:51, 54` count any `Approve`, `Reject` or `RequestChanges` review. Orca writes some itself: `ActionQueue.cs:597` adds a `system` approval when an undo window elapses; `PolicyCli.cs:183` adds a `Reject` when an operator cancels pending actions (status `Cancelled`); AI reviewers write reviews with an AI subject (`AiReviewRules.cs:61, 64`). A stop during the undo window (`ActionQueue.StopAsync`, `ActionQueue.cs:619`) adds a `Reject` under the stopper's name, who may be the requester, with no subject. All are counted as people's review decisions today. See O-A3. |

## Contracts this must satisfy

- `SkillOutcomes.CollectAsync(host, since, ct)` returns `SkillVersionOutcome(Skill, Version, Decided, Approved, Rejected, ChangesRequested, LastServed)`, a positional record, with `TurnedDown` and `Interval` (95% Wilson, `(0, 1)` when `Decided` is 0). Results are ordered by skill, then by version as a number. Caller: `PolicyCli.cs:476`. Tests: `OpenTail.Orca.Tests/SkillOutcomesTests.cs`, which also constructs the record positionally (`:70-71`), so new members go at the end with defaults.
- `SkillOutcomes.Suggest(outcomes, minCases = 20, worseBy = 0.15)` returns `RollbackSuggestion(Skill, Current, Previous, Why)`. Per skill it takes the two outcomes with the latest `LastServed`. Caller: `PolicyCli.cs:486`.
- `LastServed` today is the latest serve of that version that preceded a counted decision (`SkillOutcomes.cs:65`), not the latest serve. It decides which two versions `Suggest` compares.
- Data read: serve tools are `host.Catalog.All` where `Descriptor.AuditResult.Event == "skill_served"`, read from the field named in `AuditResult.Field` (`served` for `skills.get`, `SkillsModule.cs:46`). Serves are queried with `Status = Succeeded` and `UpdatedSince = since` (`:45`); that window is what O-A2 replaces. Actions come from `host.Store.QueryAsync` with `SessionId` and no window (`:50`), and each record gives `CreatedAt`, `ToolName`, `ResultJson` and `Reviews`.
- The store query (`ActionQuery`, `OpenTail.Orca.Abstractions/Actions/Interfaces.cs`) has two time filters, `UpdatedSince` (`:15`) and `CreatedBefore`; neither filters on a review's time. So the window of O-A2 cannot be a store filter today. Proposed: query with `UpdatedSince` at the window's start and select by `ActionReview.At` in memory. That is complete only if adding a review always moves the action's update time; to verify in the store's code before step 3. If it does not, the window needs a new `ActionQuery` member, which is a contract change in `OpenTail.Orca.Abstractions` and must be added to this plan first.
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
| a delayed action stopped during its undo window (`ActionQueue.cs:619`: a `Reject` by the requester or a reviewer, no subject, status `Cancelled`) | counted as rejected | not in the rate; reported as excluded, with its own count (O-A3). It carries no subject today, so it needs one before it can be classed |
| one of several required approvals recorded, the action still pending (`RequiredApprovals`, `ActionQueue.cs:457-473`) | counted as approved, by the rule at `:54` (the last decision review is an `Approve`; status is not read). Read from the rule, not reproduced; to confirm with a test | not counted until the approval is complete (Effective decision). This needs the action's status or `RequiredApprovals`, which `CollectAsync` does not read today; to settle in step 3 |
| decision by an AI reviewer (AI subject) | counted like a person's | counted separately (O-A3) |
| a person's decision followed by a `system` review or an operator cancellation (for example sent back by a person, then cancelled) | the last review counts, so the cancellation counts as rejected | the last review by a person counts; the excluded review is skipped, not the whole action (O-A3) |
| a person's decision and an AI reviewer's decision on one action | the last of them counts | the person's last decision counts in the rate; the AI decision is counted in the separate AI figure (O-A3) |
| the serve itself | skipped | unchanged |
| action created before a serve and decided after it | not counted | unchanged: the serve came after the action |
| session id null | excluded | unattributed (counted in the report) |
| `since` window | applied to serves only, by their update time (`UpdatedSince`); later actions are read without it | the window selects decisions by their review time (`ActionReview.At`, the time of the decision itself). Serves are read without a time bound, for the sessions of the decisions selected, so a serve before the window still explains a decision inside it. An action created before the window and decided inside it is counted (O-A2) |
| `LastServed` | latest serve that preceded a counted decision | latest successful serve of that version in the window, whether or not it was followed by a clean decision, so `Suggest` compares the versions actually served last. The serves read for attribution (previous row) are only those of sessions with a selected decision, so this needs its own query of serves in the window; to settle in step 3 |

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

**Option B (later; needs an owner decision): recorded context.** The client sends the skill context with each action it submits (for example a `skillsInUse` argument, or request metadata), and Orca records it on the action at submission. Attribution is then exact for that action. It needs a new field on the stored action (a SQLite schema change, with a migration), a contract change in `OpenTail.Orca.Abstractions`, and client adoption. It measures only the clients that send the context.

**Option C (rejected): keep the session-level figure and label it.** A figure labelled "session-level" still drives the suggestion. The suggestion is what needs the attribution to be right.

**Effective decision (one per action).** An action can hold several review records: a request for changes, a resubmission, then an approval; or two approvals where production requires two. Each action counts once. Its effective decision is the review that established its final outcome: for an approved action, the approval that completed the approval (the last one a required approval needed); for a rejected action, the rejection; for an action sent back and not approved, one sent-back outcome, at the time of its last request. The review's `At` timestamp is the decision time that the window uses (O-A2). Earlier approvals and requests for changes on the same action are not counted as separate outcomes.

**Reviewer classes.** A review is classed by its `Subject`, which the server sets from the authenticated caller (the `ActionReview.Subject` documentation, `OpenTail.Orca.Abstractions/Actions/ActionRecord.cs`). The display name in `Reviewer` is never used to class a review. AI reviews carry an AI subject (`Subjects.Ai`, set in `OpenTail.Orca/Runtime/ReviewerRunner.cs:54`). Reviews by people carry a subject set from the authenticated reviewer (`OpenTail.Orca.Admin/AdminServer.cs:289`, `OpenTail.Orca.Http/Teams/TeamsBot.cs:155`; grep of every `new ActionReview(`, 2026-11-10). Two reviews carry no subject today: the elapsed-undo-window approval (`ActionQueue.cs:597`, reviewer `system`) and the stop during an undo window (`:619`, written under the stopper's name, who may be the requester). `ActionExecutor.cs:219` and `:258` write `system` reviews too, but they are `Retry`, which is never a decision. A review with no subject cannot be classed, so this plan requires a subject on both `ActionQueue` reviews (`system` for the first; the stopper's authenticated subject, or a distinct marker, for the second), as Orca change that must land before option A (to verify its effect on the existing approval and audit paths); the change is not made here. Until then a review with no subject is excluded and counted as unclassed, never treated as a person's. Operator cancellations carry the subject `operator:<name>` (`PolicyCli.cs:183`). The human subject form is to verify in `OpenTail.Orca.Abstractions/Identity/CallerIdentity.cs`.

**Presentation (both options).** `SkillVersionOutcome` gains `Ambiguous` (decisions in which this version was one of several candidates), at the end of the record with a default so existing callers compile. Unattributed decisions belong to no version, so they are a report-level total, not a per-version count; `CollectAsync` gains a companion (for example a result that carries the list and the totals, or a second method), shape fixed in step 3. If O-A3 is confirmed, the record also gains a count of AI reviewer decisions (at the end, with a default), and the report-level totals include the excluded `system` reviews and operator cancellations, and the unreadable serves. The admin command prints the per-version clean counts with their interval, the ambiguous count, and the totals.

**Suggestions.** `Suggest` uses clean counts only. Both versions must have at least `minCases` clean decisions. The suggestion text states the ambiguous and unattributed counts beside the clean ones.

## Decisions for the owner

- **O-A1 Option B.** Whether to ask clients for the skill context, and when. Recommended: decide after option A has run and shown how many decisions it leaves ambiguous. If most are ambiguous, option B is needed for the measurement to be useful at all.
- **O-A2 Window.** A report about recent judgements should select decisions by when they were made, not when the action was submitted: an action submitted last month and decided today is a decision made today. Recommended: the window selects decisions by `ActionReview.At` (the review's own timestamp), serves are read without a time bound for the sessions involved, and the flag is renamed to `--decided-since`, so its meaning shows in the command line. `--since` is refused with a message naming the new flag, rather than silently changing meaning. Confirm.
- **O-A3 Whose decisions count.** Today an operator's cancellation counts as a rejection and an elapsed undo window as an approval (A7). Recommended: count only reviews by people; leave out `system` reviews and operator cancellations (both reported as excluded, with counts); count AI reviewer decisions separately, not in the rate a rollback suggestion uses. When an excluded review follows a person's decision on the same action, recommended: the person's last decision counts and the excluded review is skipped (the alternative, leaving the whole action out, loses a real judgement). Classing uses the review's `Subject`, as set out under Effective decision and Reviewer classes; system reviews need a subject before they can be excluded by class (Orca change under Reviewer classes, which must land before option A). A stop during the undo window (`ActionQueue.cs:619`) is a separate case, because the stopper may be the requester withdrawing their own action. Recommended: leave it out of the rate and report it with its own count; the alternative is to count a stop by someone other than the requester as a person's rejection, which needs the stopper's subject on the review.

- **O-A4 Late stops.** Report stops at or after the scheduled time separately (recommended: no behaviour change), or make `StopAsync` refuse them (a behaviour change; the owner decides).

## Decided (2026-11-10)

1. **Undo-window stops and expiries leave the human rate and are counted separately.** A stop is recognised by its audit entry `stopped` (`ActionQueue.cs`, `StopAsync`), not by its review: the review is a `Reject` by the person who stopped the action, with reason text, so a filter on human reviews would count it as a rejection. An automatic expiry is recognised by its audit entry `delay_elapsed` (`ActionQueue.cs`, about line 597); its review is a `system` approval. An operator's cancellation is the audit entry `cancelled` (`PolicyCli.cs`), and is also separate. The measurement reads the audit entries for these events, and the reviews for the decisions. The main human rate is approve, reject and request changes. Stops are counted against delayed actions, with the denominator defined in the report. Automatic approvals and AI decisions are counted separately. **Undo-window stop rate:** the numerator is unique actions stopped by a human while their undo window was active; the denominator is unique actions that entered a stop-eligible undo window, whether later stopped, released automatically, or otherwise completed. Only completed cohorts count: windows still active are excluded. The existing `delayed` audit entry, written when a delayed action is created (`ActionQueue.cs:259`), marks entry into the window, and its detail says when the action runs unless stopped. But that time exists only in the text detail, and `RunAt`, which marks the window, is cleared when a delayed action is held past its time (`ActionQueue.cs:591`). So the denominator cannot yet be built reliably from the fields. So the scheduled time needs a structured field on the `delayed` entry, or a specified and tested detail format: a small Orca change that must land before any rate is published. The read-only report itself changes nothing; until then it states the limitation.

**Late stops.** `StopAsync` checks that the action is Pending with a `RunAt`, but not that the scheduled time is still in the future (`ActionQueue.cs:614`). A stop after the scheduled time, before the maintenance sweep runs, is currently recorded as a stop. The rate counts a stop only when it is strictly before the scheduled time. Later stops are reported separately as late stops. A test covers the boundary. Whether `StopAsync` should refuse them is O-A4.

**Stop-rate period.** The stop rate is not measured over `--decided-since`. Its cohorts are delayed actions whose scheduled time falls in the period given by `--scheduled-since 30d`, and whose undo window has completed (scheduled time before the report's end). Decision metrics use the final review timestamp; stop-rate cohorts use the scheduled time. The report labels both periods, and never shows them in one figure. A stop is not a judgement that the action was wrong, so the rate is kept apart from human rejections and ordinary cancellations.
2. **Step 0 is a read-only measurement report**, named `--skill-outcomes-measure [--decided-since 30d] [--scheduled-since 30d]`, with a default of 30 days for each. `--decided-since` selects decisions by their final review timestamp. `--scheduled-since` selects the undo-window stop-rate cohorts by scheduled time (see the stop-rate period). It is not `--skill-outcomes`, which is the existing rollback command. It changes nothing and never produces a rollback suggestion. Each action with an effective final decision is classed as clean (exactly one distinct skill and version successfully served before the action was created), ambiguous (more than one candidate), or unattributed (no earlier successful serve, or no session). AI, system and undo-window-stop counts are reported separately. The period is the timestamp of the final effective review; the test of which versions were served before the action uses the action's creation time. The report shows clean decisions per skill version against the 20 per version threshold, and the calendar time that threshold would take at the observed rate. It also shows the ambiguous, unattributed, AI, system and undo-window-stop counts, and the rules it applied. It must not present session-level association as proof of causation. For each skill version it shows: clean decisions observed; days measured (the length of the requested window, default 30); clean decisions per 30 days (clean ÷ days × 30); and months to reach 20 clean decisions (20 ÷ the per-30-day rate, or no projection when the rate is zero). It states the observation period, and that the projection assumes the rate stays broadly stable. 20 is a minimum evidence threshold, not proof of causation or statistical reliability.
3. **Partial approvals count once, at the final approval** that satisfies the required number. The effective decision time is that approval's time. A partial approval followed by a rejection or a request for changes counts only the final effective decision. Test: `Two_required_approvals_produce_one_outcome_at_the_final_approval`, and a case for partial approval then rejection. The path that records a partial approval goes through the store's transition, which sets `UpdatedAt` (`ActionQueue.cs:457-464`; `SqliteActionStore.cs:543`; `InMemoryActionStore.cs:132`). That is read, not tested. Required test, against the real SQLite store and queue: create an action before the measurement window; advance the test clock into the window; record the final approval or rejection; query with `UpdatedSince` at the window start; confirm the old action is returned and its effective final review timestamp is inside the window. Also the two-approval case: the first approval must not be an outcome, and the second, completing approval must. If the test fails, add a review-time query. Do not rely on `UpdatedSince` as a substitute for review time.
4. **Activation has its own design**, in [workbench-activation-evaluation.md](workbench-activation-evaluation.md). It is not part of this plan.

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
- `A_stop_during_the_undo_window_is_excluded_and_counted_separately` (O-A3; the stop row)
- `A_first_of_two_required_approvals_is_not_counted_while_the_action_is_pending` (the partial-approval row; the same test first records today's behaviour)
- `A_review_with_no_subject_is_excluded_as_unclassed` (reviewer classes)
- `One_effective_decision_per_action_even_with_several_approvals` (the effective-decision rule: two approvals count once, at the time of the one that completed the approval).
- `A_review_is_classed_by_subject_and_not_by_display_name` (reviewer classes: the same display name with a different subject is classed by the subject).
- `A_decided_action_in_a_session_with_no_serve_is_unattributed_and_reported`
- `Since_is_refused_only_with_skill_outcomes_and_still_works_for_the_other_commands` (O-A2)
- `Last_served_is_the_latest_serve_even_when_no_clean_decision_followed_it`
- `Late_stop_boundary_is_classed_by_scheduled_time` (parameterised: a stop strictly before the scheduled time is an undo-window stop; a stop exactly at it is a late stop; a stop after it is a late stop)
- `An_action_created_before_the_window_and_finally_reviewed_inside_it_is_returned_by_UpdatedSince` (real SQLite store and queue: the action is created before the window; the test clock is advanced into it; the final approval is recorded; `UpdatedSince` at the window start returns the old action; its effective review timestamp is inside the window). This is the test the review-timestamp decision requires.
- `Two_required_approvals_produce_one_outcome_at_the_final_approval` (real store: the first approval leaves the action Pending and is not an outcome; the second completes it, and it executes once)
- `A_failed_serve_or_an_unreadable_served_field_is_ignored`

**Existing tests, mapped** (`SkillOutcomesTests.cs`, read 2026-11-10; two tests):

| Test | What it covers | Rows | Under option A |
|---|---|---|---|
| `A_version_whose_actions_are_turned_down_more_often_gets_a_rollback_suggestion` (`:31`) | 40 sessions, each serving one version then one `mail.send` a person decides; v1 2 of 20 rejected, v2 10 of 20; `Suggest` fires, not with `minCases: 50` or `worseBy: 0.5`; the label is not moved | "every serve before the action is the same version"; `Suggest` "otherwise", "fewer than `minCases`", "worse by less than `worseBy`" | unchanged: every session is clean, so the counts and the suggestion stay the same |
| `Similar_rates_are_not_flagged` (`:65`) | `Suggest` on constructed outcomes, 4 of 40 against 6 of 40 | `Suggest` "worse by less than `worseBy`" | unchanged; constructs `SkillVersionOutcome` positionally, so new members need defaults |

Rows with no test today: everything else in both tables.

## Work order

0. Before any of the steps below: a report-only diagnostic, which is the measurement in `skills-plan.md`'s order of work (step 4). On a real store it counts, per skill version, the clean, ambiguous and unattributed decisions, and the usable decisions per version; it counts human, AI and system decisions separately. It applies the rules this plan proposes (the review-time window of O-A2, classification by subject, and the effective-decision rule), and its output states those rules. It never generates a rollback suggestion, never changes a label, and does not change `CollectAsync`. Whether it is a read-only script or a dry-run report is decided before it runs, with the contracts it reads written down. Its result decides whether option A is built at all.
1. Map the existing `SkillOutcomesTests` cases to the branch table (done in this plan, above; re-check before coding).
2. Write the failing tests above.
3. Implement option A, the counts and the suggestion rule, only if step 0 shows enough usable decisions.
4. Update the admin command's output, and `docs/guides` where rollback suggestions are described (to find).
5. Use step 0's result, not a second measurement, to decide O-A1.

## Acceptance

- The review's example session counts neither version for `calendar.create` or `mail.send`.
- No version receives a decision from a session in which another version or skill was served before it.
- Every row of both branch tables (attribution and `Suggest`) has a test or a stated reason it needs none.
- The suggestion never fires from ambiguous decisions.

## Not built

**Built: step 0, the read-only measurement** (`--skill-outcomes-measure [--decided-since 30d] [--scheduled-since 30d]`), merged to Orca `main` at `1088ea1` (branch `feat/attribution-measure`, commit `57935b9`; Orca suite passes on the merged state, 1132 tests, none skipped). It is `OpenTail.Orca/Runtime/SkillMeasurement.cs` and the command in `PolicyCli`. It applies the rules of this plan: decisions timed by their effective review; clean, ambiguous and unattributed attribution; AI, operator, undo-window, system and unclassed counts kept apart; the stop rate on its own period, by scheduled time. It changes nothing and suggests nothing; `SkillOutcomes.CollectAsync` and `Suggest` are unchanged.

What the built tests establish:
- The store's `UpdatedSince` returns an action created before the window and reviewed inside it (`An_action_created_before_the_window_and_finally_reviewed_inside_it_is_returned_by_UpdatedSince`). Each decision moves `UpdatedAt`, so the store's filter is a superset, and the review time decides the window. No review-time query is needed.
- The partial-approval rows hold: a first approval is not a decision while the action is pending; two approvals produce one outcome, at the completing one; a partial approval then a rejection counts only the rejection.
- The late-stop boundary holds: a stop before the scheduled time is an undo-window stop; at it, or after it, late (`Late_stop_boundary_is_classed_by_scheduled_time`, parameterised). A mutation that counted the boundary as an undo-window stop fails that test.
- The scheduled time is read from the queue's own `delayed` entry in UTC, to the second.

**Not built, and why:**
- **Option A (clean attribution in the admin command, and the suggestion on clean counts):** waits for O-A2 and O-A3, which are still recommendations, not decisions. The measurement exists so the owner can decide them on evidence (work order step 5).
- **The subject on the two `ActionQueue` reviews** (`system` for the elapsed window; the stopper's subject for a stop): not made. The measurement counts the subject-less reviews as unclassed and identifies the elapsed windows and stops from the audit trail instead. This is correct for the measurement; option A needs the change.
- **`LastServed` over all serves in the window** (option A, the `Last_served_…` row): not built.
- **Option B** (the client sends its skill context): waits for O-A1 and is not proposed for now.
- **The stop-rate denominator** is built from the `delayed` detail, which is to the second. A stop in the same second as the scheduled time counts as late; this is stated in the report's limits. A structured field on the `delayed` entry would remove that, and is not made.
- **AI reviewer decisions** have no integration test (they need the AI verdict path). Their classing is unit-tested only through `ClassOf`.
- **Operator cancellation after a person's decision** (`A_persons_decision_before_an_operator_cancellation_still_counts`): not tested. The store does not accept a review on a decided action, so the case needs a store-level setup that was not built.
- **Documentation** (work order step 4, `docs/guides`): not updated in this pass.

**Provisional decisions taken during autonomous work** (for the owner to reverse):
- The measurement counts a review as a person's when its subject is `local`, `admin:`, `jwt:` or `teams:`. A `key:` subject is counted as "other" and excluded, because an API key is not established as a person.
- An approval that did not complete the required approvals is "awaiting", not a decision, whatever the review time.
- `--since` is refused by the measurement command, naming `--decided-since`. The existing `--skill-outcomes` still takes `--since`, pending O-A2.
- A stop during the undo window (its audit entry `stopped`) excludes the action whatever other reviews it has. An elapsed undo window and an operator cancellation do not override a person's decision on the same action: the person's last decision counts (O-A3, as recommended).
