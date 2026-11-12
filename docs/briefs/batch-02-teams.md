# Batch 02: Teams as the front end (fixes first, then two additions)

From the planning session (git-fa). Batch 01 is accepted (Host 1221 of 1221 on the planner's own run). Same rules as batch 01: no
commit, push or git state change; no public renames; follow the code where this brief is wrong and say so; stop and write options
where a design decision is missing. Baseline now: Abstractions 70 of 70, Host 1221 of 1221. Item numbers below refer to section 6 of
`docs/briefs/teams-inventory.md`. Verify each finding in the code before changing anything; if one is not real, say so and skip it.

## Part A: fixes (each with a test that fails before the fix)

A1 (item 1, security). Risk comparison in the bot must rank Destructive highest. Decision: order is Read < Write < ExternalEffect <
   Destructive for `ChatMaxRisk` (see `AiReviewRules.Rank` for the same idea). With the default `ChatMaxRisk: ExternalEffect`, a
   Destructive action shows only Open and cannot be approved, edited or run-now from chat, as TEAMS-SETUP.md says. Destructive is
   decidable from chat only when the operator sets `ChatMaxRisk: Destructive`. Stop stays allowed at any risk (stopping is safe).
A2 (item 10). A tool with no registered risk (not loaded) is treated as Destructive: not decidable from chat. Fail closed.
A3 (items 2, 3). One definition of "decided" for card refresh, covering approved and cancelled too, so a decision or a stop made on the
   page refreshes the card in chat. Fix the code or the documented route, whichever is the smaller correct change; the guide's example
   route must result in refreshed cards for every way an action stops waiting. `denied` never had a card; leave it out.
A4 (item 4). Object ids are compared case-insensitively everywhere the bot uses them (store, lookup, `People` entries `teams:<id>`,
   `Reviewers`). A mixed-case id in policy must deliver.
A5 (item 7). `back` must pass the same signature check as every other verb.
A6 (item 9). An edit naming a field that is not in the draft is refused with a message, not silently dropped. An edit on a tool
   without a Preview: keep today's behaviour but say so in the card text ("not checked by the tool").
A7 (item 8). `RefreshAsync`: do not swallow unexpected exceptions silently; log them through the existing logging/audit pattern the
   bot uses for delivery failures. Keep it non-blocking for the click.
A8 (item 12). Formatting only.
A9 (docs, items 5, 6, 11). Make TEAMS-SETUP.md say what the code does: no "lists the Teams configuration at start" unless you add
   that line to startup output (adding one line listing AppId, tenant, ChatMaxRisk and reviewer count, no secrets, is fine and
   preferred); what works after secret rotation; "tested with test signing keys, not real Bot Framework tokens".
A10. Tests for the paths the inventory lists as untested and that need no real tenant: `conversation:<key>` delivery, Run now, edit
   validation through a Preview and its refusal, the missing-secret refusal, `TeamsStore` on its own.

## Part B: additions

B1. **Request changes from chat.** A button beside Reject, with a required reason, mapping to `ReviewDecision.RequestChanges` through
   the same `ReviewAsync` path and the same limits as Reject (reviewer listed; any risk, as Reject is today — check what Reject allows
   and match it). The card updates in place like the others.
B2. **Two typed commands in the personal chat**, for listed `Reviewers` only. Exact words, case-insensitive, nothing else is parsed:
   - `waiting` : the digest of actions waiting that this person may decide (reuse the digest card; up to 20, oldest first), whether or
     not a card was posted to this chat before. If none: one line saying so.
   - `help` : the fixed list of commands and what the buttons do.
   Any other text gets the existing fixed reply plus "type help". A non-reviewer gets the existing reply only. Free text never becomes
   an action, an argument or a reason. Every click from a `waiting` card goes through the same signed-click checks as today.
   If `waiting` needs a query the bot cannot make without a design choice (whose actions a reviewer "may decide" — use
   `ReviewAuthority` if it can answer that; otherwise stop B2 and write the options).

## Not in this batch

Retry from chat, browsing history, policy proposals, any new notification type, anything needing a real tenant.

## Report

Message `git-fa`, at most 40 lines: per item done / skipped (finding not real) / stopped; files changed; final counts from full runs
of both suites; for A1 and A2, name the tests that failed before the fix; existing tests changed and why.
