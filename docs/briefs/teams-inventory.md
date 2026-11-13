# Teams inventory

Read only, from the code at 15b9e8b. No code was changed for this file. Line numbers are as read on 2026-11-12. Paths are relative to the repo root; `Teams/` is `OpenTail.Orca.Http/Teams/`.

## 1. What a person can do from Teams

- **Tier 1, webhook** (`"Type": "teams"`, `OpenTail.Orca.Notifications/Channels.cs` :196): a card with **Open**, a link to the review page, when a review link is set (:281). Nothing can be decided.
- **Tier 2, bot** (`"Type": "teams-bot"`, `Teams/TeamsBot.cs`; `BotConnector.cs` for Bot Framework calls; `TeamsStore.cs` for chats and card ids):

| Button | Verb | Line | Limits |
|---|---|---|---|
| Approve | approve | :131 | risk <= `ChatMaxRisk` (:122-124); clicker listed in `Reviewers` (:111) |
| Reject | reject | :132 | reason required (:133) |
| Approve with changes | edit | :136 | existing top-level string fields only (:141); 3 longest, 1-20,000 chars (:269-274) |
| Run now | approve | :301 | delayed actions only |
| Stop | stop | :114 | delayed actions only; any reviewer, any risk, no reason (:113) |
| Open | open; link | :109, :317 | the link needs `PublicBaseUrl` |
| Digest | back, open | :101, :325 | up to 20 items; shown when waiting > `DigestThreshold` (:196) |

Non-reviewers see the card and a note (:111-112). Cards refresh after decisions, including page decisions (:119, :166, :187). Only `queued` and `reminder` post a card (:186-188). Target `requester` goes to the requester's `teams:` chat (:178-181); `conversation:<key>` posts to one chat (:181).

## 2. What the review page can do that Teams cannot

Admin surface, `OpenTail.Orca.Admin/`:
- Decide `request_changes` and `retry` (AdminServer.cs:259-260). Teams has approve, reject and approve with changes only.
- Decide any risk: the review endpoint has no risk limit (AdminServer.cs:241-306). Teams stops at `ChatMaxRisk` (TeamsBot.cs:123, :316).
- Edit any argument: `editedArguments` takes any JSON object (AdminServer.cs:270-278). Teams edits three string fields.
- Browse actions: `GET /api/actions`, `/api/actions/{id}` (AdminServer.cs:213, :229). Teams shows only waiting items posted to that chat (TeamsBot.cs:325-329).
- `GET /api/trust/{tool}`, `POST .../promote`, `GET /api/explain/{tool}`, `GET /api/modules` (AdminServer.cs:320-339).
- `/api/me`, `/overview`, `/policy-versions` (+diff), `/policy/current`, `/proposals` (create, decide), `/telemetry/*`, `/audit`, `/usage` (AdminViews.cs:22-161).
- `/jobs`, `/jobs/act`, `/jobs/redrive-matching` (AdminJobs.cs:33, :122, :153).

## 3. Typed messages to the bot

- Personal chat: any message gets one fixed reply (TeamsBot.cs:88-90). No commands, no free text.
- Channel messages and other activity types are ignored (:91-92). The chat is remembered first (:82).

## 4. Tests

- **Fake Bot Framework connector**, `OpenTail.Orca.Tests/TeamsBotTests.cs`: `FakeConnector` (:28), faked token endpoint (:69), HS256 test keys for validation (:72). Covers delivery, approve (:149), approve with changes (:176), forged, foreign, replayed and unlisted clicks (:189), non-reviewer (:217), risk limit at Write (:228), digest (:239), page decision refreshes the card (:262), stop (:276), open and back (:251-254).
- **Fake HTTP handler**, webhook only: `TeamsNotificationTests.cs` (`ScriptedHandler`, :21; the card has only Open, :35-53).
- **Config refusal:** `NotificationTests.cs:510-513` (audit checkpoints cannot go to the bot).
- **No test:** `conversation:<key>` delivery (TeamsBot.cs:178-202); Run now (:301); edit validation through a Preview, and its refusal (:144-148); the missing-secret refusal (:33-34); production token validation with Bot Framework metadata and RS256 (BotConnector.cs:30-31, :46); `TeamsStore` on its own; `scripts/make-teams-app.ps1` (no test, no CI reference).

## 5. Configuration the Teams pieces read

- HTTP config `Teams` (`TeamsBotOptions.cs`, read through `HttpDeploymentConfig`): `AppId` (required, :14); `AppPasswordEnv` (:17, names the secret's environment variable; TeamsBot.cs:33, BotConnector.cs:83); `TenantId` (required, :20); `ChatMaxRisk` (:23, default `ExternalEffect`); `DigestThreshold` (:26, default 3, at least 1 at TeamsBot.cs:32); `Reviewers` (:31: object id to `Name` and `Groups`, :50-54); `MessagingPath` (:34); `OpenIdMetadataUrl` (:36); `TokenEndpoint` (:37); `ConnectorScope` (:38). Not settable from JSON: `ConfigureValidation`, `Handler`, `Env` (:41-45).
- HTTP config outside `Teams`: `PublicBaseUrl` (HttpDeploymentRunner.cs:56; used at TeamsBot.cs:317).
- Environment: the `AppPasswordEnv` variable; a webhook URL through `UrlEnv` (NotificationConfig.cs:73).
- `notifications.json`: a `teams-bot` channel with `Target` (NotificationConfig.cs:64-68); a `teams` channel with `Url` or `UrlEnv` (:73, :87-88); routes with `On` and `To` (:153-170).
- Policy `People`, entries `teams:<object id>` (PolicyConfig.cs:150; ReviewAuthority.cs:24).
- Not read by the Host: the parameters of `scripts/make-teams-app.ps1`.

## 6. Unfinished or inconsistent

1. TEAMS-SETUP.md:80-81 says that with `ChatMaxRisk` `ExternalEffect`, Destructive actions "show only Open". ToolRisk puts Destructive (2) below ExternalEffect (3), so the default allows Destructive (TeamsBot.cs:122-124, :300).
2. TEAMS-SETUP.md:92-93 routes `queued`, `reminder`, `decided`. `decided` means rejected, changes_requested, succeeded, failed, expired (NotificationConfig.cs:219; Notifier.cs:193). `approved` and `cancelled` never reach the bot, so a stop on the page does not refresh the card.
3. `TeamsBot.cs:21` (`Decided`) includes approved and cancelled; `NotificationConfig.cs:219` (`IsDecided`) does not. `denied` is in neither (NotificationConfig.cs:189).
4. People are matched case-sensitively (PolicyConfig.cs:150; ReviewAuthority.cs:24). Chats are stored under the lowercased id (TeamsBot.cs:78; TeamsBotOptions.cs:47), but the lookup uses the id as written (TeamsBot.cs:180). TEAMS-SETUP.md:86 does not mention case. The result is "No Teams conversation" (TeamsBot.cs:193), then `notification_dead` (Notifier.cs:227-236).
5. TEAMS-SETUP.md:99 says the Host lists the Teams configuration at start. No Teams output is printed; startup prints module lines and warnings only (HttpDeploymentRunner.cs).
6. TEAMS-SETUP.md:113 says Open still works after rotation. Only the OpenUrl link does (TeamsBot.cs:317; needs `PublicBaseUrl`). The signed in-chat `open` (:104-106, :109) and the digest's review buttons (:344) fail.
7. `back` is answered before the signature check (TeamsBot.cs:101, before :104). It lists only cards already in that chat (:325-329). Not documented.
8. `RefreshAsync` swallows all but three exception types (TeamsBot.cs:225) and is started unawaited (:119, :166).
9. Edits are checked only when the tool has a Preview (:144-148). An edited field that is not in the draft is dropped with no message (:141).
10. When the tool has no registered risk, `canDecide` is true (:300).
11. TEAMS-SETUP.md:132 says "real token signing". Tests use HS256 test keys (TeamsBotTests.cs:72); the production path is untested (BotConnector.cs:30-31, :46).
12. TeamsBot.cs:34: the throw and `_o = options;` share one line.
