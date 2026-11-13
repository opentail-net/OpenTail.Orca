# Batch 06: bring the records in line with the code (documents only, plus one test)

From the planning session (git-fa). Batch 05 is accepted and pushed (7b6003c); 1402 passed, 0 failed, 32 skipped on the planner's
run. This is the last batch of this run. No production code changes. Same rules: no commit/push/git state. Every statement you
write about the code must come from reading the code or a test today; mark nothing as built that has no test. Use the documents'
own plain style: short sentences, what is built and what is not, no praise.

## What to write

R1. **`OpenTail.Orca/STATUS.md`.** It still says the A2A design "is proposed and not built" (line 210) and has no A2A section. Add
    one, in the file's existing form: what is built (phase 0; the slice: endpoint, partner identity, task table, submit, work item,
    reviewed reply, GetTask, CancelTask, blocking sends, ListTasks, the card, the kit), what is not (streaming, push, files, the
    skill-selection extension, several skills per partner, contexts and multi-turn, tool skills, the extended card, card signing,
    HTTP+JSON, outbound, JWT partners), and what waits for the owner (R4). Update the Teams lines for batches 02, 04 and 05 (risk
    order fix, Request changes, typed commands waiting/help/work/tasks, cut previews). Fix line 210 and any other line the batches
    made untrue; list the lines you changed in the report.
R2. **`docs/design/governance-checklist.md`.** Read every item in sections B and C against the code and tests. Tick what is done,
    annotate what is partly done with the test or file that shows it, and leave the rest. Record batches 02 and 03a, which the
    checklist does not mention yet. Remove wording such as "in the working tree, not committed".
R3. **`docs/design/a2a.md`.** "What is built" must match R1. In the build order, mark each phase 1 item built or not. In
    "Verification before building", mark each item closed (with where) or still open. Two phase 1 tests in the build order cannot
    be written as stated; say so there with the reason, after checking it is true: "out-of-profile trips" (startup refuses a
    partner key whose profile lacks either tool, so the path is unreachable through A2A; check whether any other A2A path can make
    an out-of-profile call) and "a context surviving a session expiry with its labels" (contexts are not built; one task has one
    session, and the reply step starts a new session when the old one is gone).
R4. **`docs/briefs/README.md`** (new, at most 60 lines): one line per brief and report in this folder (what it asked, the commit
    that closed it), then "Decisions the planner made for the owner" (each with where it is recorded and how to change it), then
    "Waiting for the owner". Take the decisions from the briefs and from the checklist's "Decided" entries. Known ones, verify each:
    the A2A-Version rule; `a2a.reply` never Execute; completion carries the completer's labels; blocking wait 30 seconds ending in
    an error; the cap of 4; ListTasks ordered by creation; 401 for an anonymous caller; a cut Teams preview cannot be approved from
    chat. Waiting for the owner: whether `OpenTail.Orca.A2A` ships as a package; whether the default `ChatMaxRisk` should let Teams
    approve `a2a.reply`; whether 400 characters is the right card preview limit; a live check in a real Teams tenant (mentions).
R5. **One test** (the only code in this batch): the session-expiry case of R3. A task whose submit session no longer exists when
    the item completes: the reply is still queued, the item's labels are on the new session, and a labelled reply to a partner
    with no allowed destination is still refused. If it fails, do not fix production code: report it as a defect with the test.

## Report

Message `git-fa`, at most 30 lines: files and line ranges changed; anything the documents claimed that the code does not do (and
which way you corrected it); the R5 result; full-solution counts.
