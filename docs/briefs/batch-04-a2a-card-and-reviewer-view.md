# Batch 04: the A2A card, and what a reviewer sees of a partner request (page and Teams)

From the planning session (git-fa). Batch 03b-3 is accepted and pushed (00b52ce); the planner removed the superseded blocking test,
cut the ListTasks half from the streaming test, and made the timeout test's retry use an omitted `returnImmediately`. Read
`docs/design/a2a.md` section 8 and "Threats and controls", `docs/briefs/a2a-sdk-facts.md` section 7, and `docs/briefs/teams-inventory.md`.
The design below is fixed by the planner; where it differs from a2a.md this brief wins and you list the difference. Same rules: no
commit/push/git state; no public renames; `contract-v1.txt` unchanged; verify each claim in the code before changing anything;
follow the code where the brief is wrong and say so; STOP a part and write options when it needs a design decision not made here.
Baseline: 1372 passed, 0 failed, 32 skipped across the solution.

## Part A: the agent card (build)

A1. `GET /.well-known/agent-card.json`, mapped with the SDK's `MapWellKnownAgentCard` from `OpenTail.Host.A2A`, only when A2A is
    enabled (otherwise 404), behind the same requirement as `/a2a` (authenticated, role `a2a:call`). No credential: 401. A key
    without `a2a:call`: 403. An anonymous caller must not be able to tell that the Host speaks A2A.
A2. Content: name, description and version from a new optional config section `A2A.Card { Name, Description, Version }` (defaults:
    "OpenTail Host", a one-line generic description, "1.0"); one interface: JSON-RPC, `protocolVersion` "1.0", URL =
    `PublicBaseUrl` + `/a2a`; capabilities streaming, pushNotifications and extendedAgentCard all false; default input and output
    modes `text/plain` and `application/json`; **no skills** (an empty list); security schemes: only what the Host really accepts
    for an API key. Read `StaticTokenAuthentication.cs` and the JWT setup for the header names; do not copy them from a2a.md.
    Nothing else: no tool names, module names, profile names, partner names, policy or reviewer groups.
A3. Startup, when A2A is enabled: `PublicBaseUrl` must be set and be https, or http on loopback (the card needs a real address).
    If a rule about `PublicBaseUrl` already exists, reuse it and say what it is.
A4. Caching: the card is the same for every caller, but it is not public. Check the SDK's `Cache-Control`; if it says `public`,
    make it `private` (say how). Keep the ETag if it works behind authorization.
A5. Audit `a2a.card_served` with the partner subject, at most once per partner per 10 minutes (in memory).
A6. `GetExtendedAgentCard` stays UnsupportedOperation.
Tests: the authorization matrix (none, wrong scope, `mcp:tools`, right scope, A2A disabled); the card parses with the SDK's own
card type and has the fields of A2; the serialized card contains none of: any tool name of the test policy, any profile name, any
partner id or display name; the interface URL follows `PublicBaseUrl`; the startup refusals of A3; the audit is rate-limited.

## Part B: what a reviewer sees (verify first; fix only what a failing test proves)

A partner's goal is outside text, and the reply text is what leaves. A reviewer must see both exactly, and never as markup.

B1. **Review page.** Submit an `a2a.submit` whose goal contains `<script>alert(1)</script>`, `<img src=x onerror=1>`, a markdown
    link, and a line break followed by text that imitates the page's own labels (for example "Approved by: admin"). Assert on the
    served HTML (and on the JSON the page's script renders from, if rendering is client-side: then read the script and say how the
    text reaches the DOM, `textContent` or `innerHTML`). Report what you find. If any of it can become markup, that is a defect:
    failing test first, then the smallest fix.
B2. **Teams card.** The same goal, and an `a2a.reply` with the same text: inspect the Adaptive Card JSON the bot would post. Is the
    text in a `TextBlock` where Teams markdown applies (links, bold, mentions)? Report it. If partner text can render as a clickable
    link or a mention, that is a defect: failing test first, then escape or use a non-markdown presentation, whichever the code
    already does elsewhere.
B3. **Truncation.** Find every place where a preview is shortened for the page or for a card, and the limit. Rule: a reviewer
    approves what was shown, so **an action whose preview was shortened on a Teams card cannot be approved, rejected-with-edit or
    changed from that card**; the card says the text is cut and offers Open (the page shows it whole). Apply the rule to every tool,
    not only A2A, and enforce it on the click as well as on the card (as batch 02 did for risk). If an existing test fails because
    of this rule, do not change that test: STOP B3 and report the tests by name. If the page itself shortens a preview with no way
    to see the whole text, report it and STOP that point with options.
B4. **Chat risk for the two tools.** Under the default chat risk setting, state (with a test each) what a reviewer can do from
    Teams for a pending `a2a.submit` (risk Write) and a pending `a2a.reply` (risk ExternalEffect), and that `waiting` lists both.
    Change nothing here; this is a record for the owner.

## Part C: tests only (no production change expected; if one fails, report it, do not fix it in this batch)

C1. A per-partner trust rule end to end: a rule on the `partner` argument makes partner A's `a2a.submit` Execute and leaves
    partner B's on Queue. A's send answers WORKING with a work item; B's answers SUBMITTED.
C2. A suspended partner identity: SendMessage is refused with a generic message and creates nothing new that runs; say what
    GetTask and ListTasks do for it today.
C3. The pending cap: a partner at the per-requester pending cap gets the next send refused; say with which error and whether a
    task row is left behind.

## Docs

`OPERATIONS.md` (the card, `A2A.Card`, the `PublicBaseUrl` rule); checklist C.1 and the threats row "A human approver manipulated
by partner text" in a2a.md (replace *verify* with what B1 and B2 found); a2a.md "What is built". Write Part B4 and Part C results
into `docs/briefs/a2a-reviewer-view.md` (at most 60 lines, each line marked RUN or READ).

## Report

Message `git-fa`, at most 45 lines: per item done / found / stopped; defects found in Part B with the test that proves each;
existing tests changed and why (none is expected); files changed; full-solution counts; anything in Part C that failed.
