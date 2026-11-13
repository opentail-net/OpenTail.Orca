# OpenTail Orca v5: the people in the loop, a pilot, and what businesses ask for (plan)

Status: proposal, 2026-10-16. Follows v4 (integration profiles, [`MCP-HOST-V4-PLAN.md`](done/MCP-HOST-V4-PLAN.md)), which needs nothing from
outside. This plan is split from it because most of it depends on the owner: accounts, a Microsoft 365 tenant, a reviewer, and
conversations with businesses. The owner has said those will be slow, so each phase below says what can be built without them and what
waits, and nothing that waits blocks v4.

## 1. Why this matters

Orca has two users: the AI that calls tools, and the person who answers for what it does. The safety model (policy, taint, review)
only holds if reviewing is quick and clear for a business user who is not a developer. If it is a chore, people write `Execute` rules to
make it go away. That makes the reviewer experience the biggest return after integration breadth (v4), and the pilot the only way to
know whether any of it works for real.

## 2. Phases

Legend: **build now** = can be done against fakes without the owner; **waits** = needs something only the owner can provide.

### V1. Business discovery (owner; no code)

Talk to two or three businesses: which systems they would connect first, what they would let an AI do there, who would approve, and
whether that person lives in Teams, Slack or email. Write the answers into this plan. **Waits** (owner). This can reorder V2 to V5; until
it happens the defaults below stand.

### V2. Review page for business users (build now; 2 days)

What a reviewer needs on one screen, from the data Orca already has:

* **Why it is waiting:** the rule that queued it, or "the caller read outside content" with which tool and when (taint source).
* **What will happen:** the preview summary in plain words, the diff, and for writes to outside systems who will see it.
* **Context:** the caller, its recent actions in the session (what it read before asking), and similar past decisions.
* **Decide fast:** approve, reject with a reason, request changes; keyboard shortcuts; a filtered queue ("mine", "high impact", "expiring").
* Accessibility and mobile width; every value written with `textContent` as now.

Exit: an automated walk-through of each decision path; a reviewer can decide on a typical action without opening anything else.
Live check with a real person **waits** (owner, at minimum).

### V3. Approvals in Teams (then Slack) (build now against fakes; live check waits; 3 to 4 days)

Built in two tiers (decided with the owner, 2026-10-16), so a pilot works on day one and the bot comes when IT agrees:

* **Tier 1 (no bot, no public endpoint):**
  * a `teams` notification channel posting an Adaptive Card through a Teams Workflows webhook: what will happen, why it waits, the
    risk, and an **Open** button to the review page (`Action.OpenUrl`). Nothing is approved from the card, so Microsoft never calls
    Orca, and Orca can stay inside the company network;
  * the review page opens the action named in the link (`/#<id>`). A link only ever opens the page and carries only the action id;
    approving always happens after sign-in;
  * then **sign-in with the company's identity provider for the page** (OpenID Connect with PKCE, Entra ID first) instead of a typed
    reviewer token, mapping the signed-in user to a reviewer in `People`.
* **Tier 2 (the bot):** the rest of this section: cards with Approve, Approve with changes and Reject, the digest, verified callbacks.
  Needs an app registration, admin consent and a public HTTPS endpoint.

Decided with the owner (2026-10-16): **Teams is the main channel**, because businesses have it security-approved, installed and in daily
use; and **edit, then send** is required, because the main use is an AI preparing someone's work (an email reply) for that person to approve.

* **A Teams bot**, not the Teams Approvals app (which would tie this to Power Automate and cannot edit before sending or explain why an
  action waits). Two places: a **personal chat with the bot**, where an AI's prepared actions arrive for the person who owns that AI
  session (routing by requester, the default for "my AI prepares my work"); and a **channel or thread** for a central AI or a reviewer
  group.
* **One Adaptive Card per action**: what it will do (preview sentence and the first lines of the draft), why it waits and the risk, and
  **Approve**, **Approve with changes** (a text box prefilled with the editable field, such as the email body; Orca already approves
  with edited arguments, and the preview is re-run on the edited version), **Reject** (asks for a reason) and **Open** (the review page).
  After a decision the card updates in place ("Sent: approved by Rita, 10:42"), so the chat is also the person's log.
* **Digest** (decided with the owner): when more than 3 actions wait for the same person or channel, they get one digest card instead
  of one card each, updated in place as actions arrive and are decided. Each line shows what it will do, the risk and whether it waits
  after outside content, and expands into the full card (Approve, Approve with changes, Reject). There is no "approve all": it would turn
  review into a rubber stamp. If ever wanted, it is an opt-in policy setting for Read and Write tools only. The threshold is configurable.
* **Callbacks** are verified (Bot Framework token: issuer, audience, signing keys; plus a short-lived signed action id so a card cannot be
  replayed for another action), the Teams user is mapped to a reviewer in `People` (by Entra object id or UPN), and the same rules as the
  page apply: groups, required approvals, `SelfApproval` (allowed by default, so a person approves their own AI's draft), expiry.
* **What chat may approve** is a policy setting per tool (for example ExternalEffect yes, Destructive only on the page).
* A decision from chat is audited with the reviewer's identity and "teams"; a forged, replayed, stale or mismatched callback is refused.
* **Deployment note:** Teams must reach Orca over public HTTPS (the bot's messaging endpoint), so a Teams-enabled Orca runs behind a
  public address or a relay; say so in OPERATIONS.md.
* **Slack** second, the same model: Block Kit buttons, request signatures (signing secret, timestamp window), the same user mapping.

Exit: tests with recorded callback shapes and forged ones; the approval page and chat agree on the state. **Waits** for a live run:
a Teams test tenant with an app registration, and a Slack workspace with an app (owner).

### V4. Pilot (waits; about a week of elapsed time once accounts exist)

One real mailbox plus one real ticket or issue system (GitHub Issues on a throwaway repository is the cheapest start; ServiceNow if a
developer instance is available), the owner as reviewer, for a week. Also runs the outstanding live tests: GitHub with an injected issue
from a second account, ServiceNow, one social platform, Microsoft 365 or Google.

Exit: a written list of what broke and what was fixed, and a short account of the workflow that can be shown to a business.
**Waits** (owner: accounts, tokens, a repository or instance, time to review).

### V5. What businesses ask for next (only after V1 or V4 says so)

Candidates, none started without a customer asking: audit export (CSV or SIEM webhook), Postgres store for more than one node, a hosted
deployment, per-team policies, a reviewer delegation and holiday model, MCP tasks. Each gets its own short plan when asked for.

AI reviewers that approve the routine, and the defences that keep an eager requester from talking them into "yes", are planned in
[`MCP-HOST-V6-PLAN.md`](MCP-HOST-V6-PLAN.md). They come after V4, because measuring them needs real human decisions.

## 3. What the owner needs to provide, in the order it unblocks work

| Item | Unblocks | Effort for the owner |
|---|---|---|
| A throwaway GitHub repository, a second account, a fine-grained token | V4 start, the v3 live GitHub test | 30 minutes |
| A mailbox that can be used for testing (an app password, or a Microsoft 365 / Google test account) | V4 | 30 minutes to a day, depending on the provider |
| A Microsoft 365 developer tenant and an app registration for Teams | V3 live check | an afternoon |
| A Slack workspace and app | V3 live check for Slack | an hour |
| Conversations with two or three businesses | V1, and the order of everything after | elapsed time |

## 4. Meanwhile

Until these arrive, the order is: v4 (profiles) in full, then V2 (review page), then V3 built and tested against fakes. Nothing in v4 or
V2 needs the owner. Each phase is committed and pushed when its exit passes.

## 5. Implementation log

| Phase | Commit | What |
|---|---|---|
| V2 | 377ffa9 | Action detail gains `why` (rule, policy, or taint with the read that caused it, found among the session's earlier actions; no schema change), `effect` (plain words by risk), `recent` (the caller's last ten actions, the taint source marked) and `similar` (the tool's last five decisions with reviewer and reason); list rows gain `risk`. The page shows these first, filters by "mine", "high impact" and "expiring", marks high-impact and post-taint rows, supports `j`/`k`/`a`/`x`, requires a reason to reject or request changes, and scrolls to the detail on phones. Still text-only rendering under the nonce CSP. Not done: a walk-through by a real reviewer (waits on the owner). |
| V3 Tier 1a | 3266100 | `teams` notification channel: an Adaptive Card through a Teams Workflows webhook (what will happen, risk, held-after-outside-content, Requested by) with a single `Action.OpenUrl` to `PublicBaseUrl/#<id>`; model-written text is stripped of Markdown and links are broken with a zero-width space. The review page opens the action named in the link, only after sign-in, and accepts only an action id from it. Next: sign-in with the company's identity provider (1b). |
| V3 Tier 1b | 560619c | The review page signs reviewers in with the company's identity provider: `/sign-in.json` (public, no secret) gives authority, client id and scope, derived from the HTTP `Jwt` options plus `PageClientId`; the page runs the authorization code flow with PKCE (S256), checks `state`, restores the action link after the redirect, keeps the token in session storage and asks again on 401; the CSP adds only the provider's origin to `connect-src`. Tested without a live provider (endpoint, CSP, page flow); the live check with an Entra tenant waits on the owner. Next: Tier 2 (the bot). |
| V3 Tier 2 | 69b1031 | The Teams bot, without the Bot Framework SDK: `/api/teams/messages` accepts activities only with a valid Bot Framework token (issuer, audience = the bot, signing keys from the Bot Framework metadata, `serviceurl` claim = the activity's), from the configured tenant; conversation references are stored in Orca's SQLite file on install or message. A `teams-bot` notification channel posts a card to the chat of the person whose AI asked (via `People`, `teams:<object id>`) or to a conversation; more than `DigestThreshold` waiting actions become one digest (open one, back to the list). Clicks (Universal Actions `adaptiveCard/action`) carry only an action id and an HMAC binding it to that conversation; the user must be in `Reviewers`; risk must be within `ChatMaxRisk`; Approve with changes edits the draft's top-level text fields, validated by the tool's preview; then `ReviewAsync` applies every rule. Cards update in place (invoke response for the clicker, PUT for other copies and on decisions made elsewhere). Also fixed: `PageClientId` / `PageScope` could not be set from the HTTP config file. Tested against a fake connector with signed tokens; a live run needs an Azure Bot, a Teams app and a tenant (owner). |
| V3 package and guide | 16bb496 | `scripts/make-teams-app.ps1` writes the Teams app package (manifest 1.17 with the bot in personal and team scope, no Graph permissions, valid domain = Orca's public host; icons generated by the script, so no binaries in the repository) and keeps the app id stable across versions. `OpenTail.Orca/TEAMS-SETUP.md`: the IT guide (levels 1 and 2, data flow, bot registration, package upload, Orca config, checks, secret rotation, troubleshooting, what is and is not tested). The bot now refuses to start without its client secret. |
| Deploy | (this commit) | `OpenTail.Orca.Server` (`opentail-orca`): every module and profile compiled in, enabled by `modules.json`; in the deployment check. A container image (`OpenTail.Orca.Server/Dockerfile`: non-root, `/config` read-only, `/data` volume, an entrypoint that starts from the files it finds or passes commands through), Docker Compose with Caddy for HTTPS, starting config files, and `OpenTail.Orca/DEPLOY.md` (tunnel for trials, one server, own platform; why not a network file share). Orca changes: `/healthz` answers any Orca header (probes); loopback with a non-loopback `PublicBaseUrl` (a tunnel) now needs sign-in; the "available but not enabled" module warnings are one line. CI builds the image and smoke-tests it (Docker is not on the development machine, so CI is the first real run). |
