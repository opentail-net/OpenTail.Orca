# Batches 08 and 09: the three settings, the missing tests, then the model reviewer

From the planning session (`git-ee`). The owner's instruction (2026-11-14): the coder carries more of the work, including the
tests, the records and its own review. The planner reads the production diff and decides.

**The specification is the plan, not this file.** `docs/design/reviewer-gate.md`: section 1a (the rule for settings), section 2
(what is true now), section 3 (each open box, with its sub-boxes), section 5.1 (the model reviewer). Where the plan is silent,
choose the stricter behaviour, write the choice in your report, and go on. STOP only if the plan contradicts the code or itself.

The rules of `batch-07-gate-gaps.md` apply, with these changes:

- **You own the tests.** Write them, and change existing tests where the new behaviour requires it. List every existing test you
  changed and why. Never weaken a test to make it pass.
- **You own the records.** In the same working tree: tick the boxes in `reviewer-gate.md` section 3, change section 2 so it
  describes the code as it now is, add one log row per batch to `reviewer-gate-history.md` section 11, and update `POLICY.md`,
  `OPERATIONS.md` and `OpenTail.Orca/STATUS.md` where they describe what you changed. Plain style, as those files are written.
- **Review your own diff before you report**: read it once against the plan as a reviewer would, and fix what you find.
- Start batch 08 as soon as batch 07 is reported. Do not wait for the planner between batches unless you STOPPED. Report each
  batch separately. Still no commit and no push.

## Batch 08: settings and tests

1. **D-32, `UnknownOutcomeRetry`** (`Never`, `Once`, `WithReason`; default `Once`; `Never` for a Destructive tool unless its policy
   says otherwise). `failures-and-inspection.md` must say what the code does.
2. **D-36, `DefaultAiReviewers`.** After batch 07, a tool with `AiMayRefuse` counts as AI-decidable here too.
3. **D-37, `AiOwnRequest`** (`ForbidApproval` default, `ForbidAll`, `Allow`).
4. For all three: section 1a holds. A loosening value is in the policy hash, is printed as a startup warning on both runners, and
   is shown by `--explain`. A policy that sets none of them keeps its hash, except where a new default changes behaviour: say in
   the report which existing policies behave differently, and put that in `POLICY.md`.
5. **G5.** Trace whether an action can be queued with no policy snapshot. If it cannot, write down why in the plan (section 6) and
   close the box. If it can, refuse it at creation, with a test.
6. **G8, the tests that need no new production code:** channel parity (one action through MCP, A2A, Teams, HTTP and the runner
   gets one answer); a restart during a scheduled window; pins for the decision and delay tables (plan 2.4 and 2.5); a red-team
   case for a side door (a tool with no policy entry). Pairwise coverage is not in this batch. A test that fails shows a defect:
   leave it in place, skipped with the reason, and report it; do not fix production code for it in this batch.

## Batch 09: the model reviewer against a fake client (R2)

Everything in the R2 box of section 3 except the run against a real model, built as section 5.1 describes. Fixed points:

- `ModelReviewer : IReviewer` over `IChatClient`; no provider package is added.
- The packet is built by Orca from the stored action only. Nothing the requester wrote outside the arguments reaches it.
- The pre-scan is deterministic and has its own tests, one per kind of text it looks for.
- Output is strict: anything that is not the JSON shape, a timeout, or an empty `checks` list is an abstention or an escalation as
  5.1 says. A model's `reject` goes to the queue as a Reject; with `AiMayRefuse` off the queue refuses it and the action stays for
  a person. Do not work around that.
- The system prompt is a versioned resource in the package, and its version is what `AiPromptVersion` reports.
- Configuration: the `AiReviewer` section in `http.json`; the key is named by environment variable and never read from the file.
  Off unless configured. Startup refuses a configured reviewer whose name is not a valid reviewer name.
- Tests with a fake client: approve, reject, escalate, malformed output, timeout, an injection hit in each scanned field, and the
  red-team corpus run through `ModelReviewer` with a fake that always answers "approve": every structural limit must still hold.
- Dual review is not in this batch.

## Report (each batch)

Message `git-ee`, at most 40 lines: files changed; each numbered item done, stopped, or chosen-where-silent (with the choice);
existing tests changed and why; defects found by new tests; what your own review found and fixed; the test counts; the three
places in the diff you are least sure of.
