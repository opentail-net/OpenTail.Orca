# Batch 07: reviewer gate, the small gaps (G1, G3, G6, the taint message, one A2A test)

From the planning session (`git-ee`). The plan is `docs/design/reviewer-gate.md` (current design, section 2; open boxes, section 3).
The baseline is `main` at the commit that added this file; the last recorded run of `OpenTail.Orca.Tests` was 1523 passed, 0 failed.

## Rules for every batch

- No commit, no push, no branch, no stash: leave the changes in the working tree. The planner reviews, commits and pushes.
- Do not edit an existing test, except where a part below names it. If an existing test fails for another reason, STOP and report.
- Do not change behaviour that this brief does not name. If the brief and the code disagree, STOP and report; do not pick one.
- Read the code before you change it. A statement in your report about the code comes from reading it today.
- Do not open anything under `C:\Git\split\examples`.
- Documents: `OpenTail.Orca/POLICY.md` only, and only the rows named below. The planner updates the plans and STATUS.
- Run `dotnet test OpenTail.Orca.Tests` at the end and report the counts.

## Part A (G1): a refusal on a Human-only tool can be assigned

Today `PolicyConfig` validation refuses `AiReviewers` unless the tool is `Mode: Queue` with `Reviewer` `Ai` or `Either`. A Human-only
tool with `AiMayRefuse: true` therefore cannot name its reviewers, and every configured AI reviewer may refuse (and list, and read)
its actions.

A1. Validation: accept `AiReviewers` on a tool that is `Mode: Queue` and is either `Reviewer` `Ai` or `Either`, **or** has
    `AiMayRefuse: true`. Refuse it otherwise, with a message that names both conditions.
A2. `AiReviewRules.UnassignedAiTools`: also report a Queue tool that has `AiMayRefuse: true` and no `AiReviewers`, whatever its
    `Reviewer`. The message says an AI reviewer may refuse it. One warning per tool, not two.
A3. Do not change `Assigned` or `Offered`: read them and confirm in the report that they already do the right thing for such a tool.
A4. Tests, in `P1ReviewerTypesTests` or a new `G1AssignedRefusalTests`:
    - a Human-only Queue tool with `AiMayRefuse` and `AiReviewers: ["a"]` passes validation; without `AiMayRefuse` it is refused;
    - reviewer `ai:a` may refuse its action; reviewer `ai:b` is refused by the queue with `review_refused` ("not assigned");
    - neither may approve it;
    - through the runner, `b` is not offered the action (skipped), `a` is;
    - the warning lists such a tool when it has no list, and not when it has one.
A5. `POLICY.md`: the `AiReviewers` row says where it is accepted.

## Part B (G3): rules on `a2a.submit`

`A2AFrontDoor.Check` refuses an `a2a.submit` whose `Reviewer` is not Human. It does not look at the tool's rules, and a rule can name
its own reviewer. The reply's rules are checked a few lines below.

B1. For each rule of `a2a.submit`: a rule whose `Reviewer` is set and is not Human is a startup error, worded like the reply's.
    A rule in mode Execute stays allowed (it is the documented way to trust one partner); do not change the existing warning.
B2. Tests beside the existing A2A startup tests: a submit rule naming `Ai` is refused at startup; a submit rule naming `Human`, and
    an Execute rule with no reviewer, still start.

## Part C (G6): the no-list warning on the standalone runner

`UnassignedAiTools` is printed only by `HttpDeploymentRunner`. Find where the standalone `DeploymentRunner` prints its startup
warnings and add it there, in the same form. If the standalone runner has no place where startup warnings are printed, STOP on this
part only and report what you found; do the other parts.

C1. One test that the standalone path yields the warning, in whatever form that runner's warnings are testable today. If they are
    not testable without a new seam, say so and add none.

## Part D: the taint message

`AiReviewRules.Check` answers a tainted ExternalEffect approval with "...so an AI reviewer may not decide this." Since P2 an AI may
refuse such an action; it may not approve it. Change the last words to "may not approve this."

D1. You may change the assertions of existing tests that pin the old words, and nothing else in them. List each one in the report.

## Part E: an AI and an A2A reply (a test only)

E1. In the A2A tests: with `a2a.reply` as `Reviewer: Human`, `AiMayRefuse: true`, startup accepts the policy; an AI reviewer's
    Reject of a queued reply is accepted; an AI reviewer's Approve of a queued reply is refused with `review_refused`.
    If any of the three fails, do not fix production code: report it as a defect, with the test left in place and marked skipped
    with the reason.

## Report

Message `git-ee`, at most 30 lines: files and line ranges changed; for each part, done or stopped and why; the existing tests whose
assertions you changed (part D); anything the brief says about the code that is not so; the test counts.
