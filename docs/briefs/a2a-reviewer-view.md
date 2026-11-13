# A2A: what a reviewer sees, and the card's open point (batch 04, parts A, B and C)

Every line is marked RUN (a test in the suite, named) or READ (from the code, file and line).

## B1 Review page

- READ: AdminPage.cs writes every dynamic value through `el()`, which sets `textContent`. The page has no `innerHTML` (the only mention is in its comment).
- READ: the only `substring` calls on the page cut a policy hash (AdminPage.cs:177, 373, 399), never a preview. The trace list is sliced at 60 items, not text.
- RUN: `ReviewViewTests.B1_the_review_page_shell...`: the served shell has no partner text and none of `.innerHTML`, `insertAdjacentHTML`, `outerHTML`, `document.write` or `eval(`.
- RUN: the same test: `GET /api/actions/{id}` gives `previewSummary` whole, with the markup as text inside a JSON string.
- RUN: found no defect. The partner's text cannot become markup on the page.

## B2 Teams card

- RUN: `ReviewViewTests.B2_the_Teams_card...`: a goal and a reply with markup, a markdown link, a tag-like `<at>`, a bare address and a line break reach the card with none of `<`, `>`, `[`, `](`, `**`, `://`, `www.evil` or a line break.
- RUN: the same test: the forged line "Approved by: admin" stays inline in the quoted partner text.
- READ: TeamsChannel.Escape (Channels.cs) replaces the markdown characters, brackets and angle brackets, breaks `://` and `www.` with U+200B, and turns line breaks into spaces.
- RUN: test note: the `://` check must be ordinal. xUnit's string overload compares culturally, which ignores U+200B, so the first version failed on a correct escape.
- READ, not run in a tenant: a TextBlock is rendered as Markdown by Teams; the escape is the only defence. Whether a bare `@name` becomes a mention in a TextBlock is to verify in a live tenant.
- RUN: found no defect in the escape.

## B3 Truncation

- RUN: `ReviewViewTests.B3_a_preview_cut...`: a cut Teams card has no approve, run now or edit control; it keeps reject, request changes and Open, and says the text is cut.
- RUN: `TeamsBotTests.A_preview_cut_on_the_card...`: an approve click on a cut preview is refused and the action stays Pending. Mutation: with the click check removed this test fails.
- RUN: `ReviewViewTests.B3_the_Teams_notification_card...`: the notification card marks a preview cut at 300 characters and offers only Open.
- RUN: the digest line at 140 characters says "(cut: open it to read it all)" (`B3_a_preview_cut...`); the digest offers only Review.
- READ: the review page does not cut a preview (see B1), so the page-side stop does not apply.
- READ: Open exists only when `PublicBaseUrl` is set (TeamsBot.cs, FullCard). Without it a cut card says to use the review page but has no link to it.
- READ: not previews, left as they are: reviewer reason 200, session labels 300, tool name 80, requester 80.
- RUN: the existing Teams, notification and review-context classes passed with the rule in place, so no existing test was changed.

## B4 What Teams can decide by default (a record for the owner)

- RUN: `ReviewViewTests.B4_by_default...`: with the default `ChatMaxRisk` (ExternalEffect) Teams shows Approve for `a2a.submit` (Write) and for `a2a.reply` (ExternalEffect).
- RUN: `ReviewViewTests.B4_with_ChatMaxRisk_Write...`: the reply has no Approve, and the card says "Decide this on the review page (Teams may decide up to Write)."
- RUN: the waiting digest's selection (`ReviewAuthority.Check`) allows both actions for the reviewer, and the digest lists both as "Review: ..." buttons.
- RUN: record: with the default, a reviewer in Teams can approve a partner's reply, which is an exit. Owner's decision whether that stays.

## Part C (tests only; no production change was needed)

- RUN: C1 `A2aSliceTests.C1_a_per_partner_rule...`: partner A's submit runs (WORKING, one work item); partner B's is queued (SUBMITTED).
- RUN: C2 `...C2_a_suspended_partner...`: SendMessage answers REJECTED with the generic text and no reason; the submit is Denied and no work item is created. No JSON-RPC error is returned. GetTask answers REJECTED and ListTasks lists the task.
- RUN: C3 `...C3_a_partner_at_the_pending_cap...`: the next send answers REJECTED; the submit is Denied with error code `queue_full`; the row is left (two rows); a retry with the same messageId answers the same task; another partner is unaffected. No JSON-RPC error is returned.

## Part A: the card (built) and its open point

- RUN: `The_card_takes_the_same_sign_in...`: no credential 401; a key without a2a:call 403; an mcp:tools key 403; a2a:call 200.
- RUN: `The_card_does_not_exist_when_A2A_is_disabled`: 404 with a2a:call.
- RUN: the card parses with the SDK's AgentCard and has the fields of A2 (`The_card_parses_with_the_SDK...`); it names no tool, profile, partner, policy or reviewer (`The_card_names_no_tool...`).
- RUN: `Startup_refuses_a_PublicBaseUrl...`: missing, and plain http off loopback, are refused; https and loopback http are accepted.
- RUN: `The_card_is_private_and_the_SDK_gives_it_an_ETag...`: `Cache-Control` is private; an ETag is present.
- RUN: `The_card_read_is_audited_once_per_partner...`: two reads by A and one by B give two audits.
- RUN: `GetExtendedAgentCard_stays_UnsupportedOperation`.
- RUN: `An_anonymous_caller_gets_401_...`: 401 with A2A on, 404 with it off. So an anonymous caller can tell that A2A is on.
- READ: DECIDED (option c, keep 401): an anonymous caller can tell that A2A is on, and `a2a.md` section 8 records that and why it is accepted.
  The options that were open (a: 404 to an anonymous caller; b: map the card with A2A off too) are not taken.
