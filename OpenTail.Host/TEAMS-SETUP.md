# OpenTail approvals in Microsoft Teams: setup for IT

This guide is for the Microsoft 365 / Entra administrator. It sets up the **OpenTail approvals** Teams app, through which people approve,
change or reject what an AI prepared for them (an email reply, a ticket comment) before it is sent.

There are two levels. Start with level 1; it needs no app installation and no inbound access.

| | Level 1: notifications | Level 2: the approvals bot |
|---|---|---|
| In Teams | A card per waiting action with an **Open** button | Cards with **Approve**, **Approve with changes**, **Reject** and **Open** |
| Decisions | On the OpenTail review page (company sign-in) | In Teams, or on the review page |
| Needs | A Teams Workflows webhook URL | An Azure Bot + app registration, the Teams app below, the Host on public HTTPS |
| Inbound traffic to the Host | None from Microsoft (only reviewers' browsers) | Microsoft's Bot Framework posts to one endpoint |

Level 1 is in [OPERATIONS.md](OPERATIONS.md#notifications) ("Teams, simplest"). The rest of this guide is level 2.

## 1. What it does and what data goes where

* When an AI asks for an action that needs a person, the Host posts a card **to the chat of the person whose AI asked** (or to a chosen
  channel). The card shows what will happen, the risk, why it waits, and **the draft's text** (so it can be changed before sending).
  Card content therefore lives in your Microsoft 365 tenant, in that chat, like any other Teams message.
* When someone presses a button, Teams sends the click to the Host. The Host accepts it only if all of these hold: the request carries a
  valid Bot Framework token issued for **your** bot and the service it names; it comes from **your** tenant; the person is listed as a
  reviewer; the card was signed by the Host for that action and that chat; the action's risk is within what Teams may decide; and the
  usual approval rules pass (groups, number of approvals, approving one's own AI's request, expiry).
* The bot asks for **no Microsoft Graph permissions**. It cannot read mail, files, chats or calendars. It can only post and update its own
  cards, in chats and teams where the app is installed.
* Every decision is recorded in the Host's audit trail with the reviewer's identity and "teams".

## 2. Prerequisites

* The OpenTail Host running with its HTTP mode (`--http`), reachable from the internet over **HTTPS** at a stable address, for example
  `https://approvals.contoso.com`. Only the path `/api/teams/messages` must be reachable by Microsoft; the review page and API can stay
  behind your network or proxy rules for staff. [DEPLOY.md](DEPLOY.md) shows three ways to get there: a tunnel for a trial, one server
  with Docker and Caddy, or your own platform.
* Permission to create an Azure Bot resource and an app registration, and Teams admin rights to upload a custom app.
* The Entra object ids of the people who will review from Teams.

## 3. Register the bot

1. In the Azure portal create an **Azure Bot** resource. Type of app: **Single Tenant**. Let it create a new app registration, or use one
   you created in Entra (Microsoft App ID = the app registration's client id).
2. In the app registration, create a **client secret**. Store it where the Host reads environment variables, for example as
   `TEAMS_BOT_SECRET`. Note its expiry; see *Rotating the secret*.
3. In the Azure Bot: **Configuration** > Messaging endpoint = `https://approvals.contoso.com/api/teams/messages`.
4. In the Azure Bot: **Channels** > add **Microsoft Teams**.

## 4. Build and publish the Teams app

On any machine with PowerShell 7:

```
pwsh scripts/make-teams-app.ps1 -BotAppId <Microsoft App ID> -PublicBaseUrl https://approvals.contoso.com
```

It writes `opentail-teams-app.zip` (manifest and icons) and prints the Teams app id. **Keep that id** and pass it as `-AppId` when you
build a later version, so Teams treats it as an update. The package holds no secret.

In the **Teams admin center** > Teams apps > Manage apps > **Upload new app**, upload the zip. Then, in an app setup or permission policy,
allow it for the reviewers (and optionally pin it). The manifest requests only the bot, in personal and team scope.

## 5. Configure the Host

In the HTTP config file:

```json
"Teams": {
  "AppId": "<Microsoft App ID>",
  "AppPasswordEnv": "TEAMS_BOT_SECRET",
  "TenantId": "<your tenant id>",
  "ChatMaxRisk": "ExternalEffect",
  "DigestThreshold": 3,
  "Reviewers": {
    "<entra object id of Alice>": { "Name": "alice", "Groups": [ "finance" ] }
  }
}
```

* `Reviewers`: only these people can decide from Teams. Anyone else sees the card but cannot act on it.
* `ChatMaxRisk`: the highest risk Teams may decide. `ExternalEffect` lets people send prepared emails from Teams; `Destructive` actions
  (deleting, closing) then show only **Open**. Use `Write` to allow nothing that reaches other people from Teams.

In the policy file, link each reviewer's Teams identity to their AI agent, so the agent's actions arrive in their chat:

```json
"People": { "alice": [ "teams:<entra object id of Alice>", "key:alice-agent" ] }
```

In `notifications.json`:

```json
"Channels": [ { "Name": "bot", "Type": "teams-bot", "Target": "requester" } ],
"Routes": [ { "On": [ "queued", "reminder", "decided" ], "To": [ "bot" ] } ]
```

Use `"Target": "conversation:channel:<conversation id>"` instead to post to one channel where the app is installed (for a central AI
whose actions a team reviews).

Restart the Host. On start it lists the configuration; a missing secret or a malformed section stops it with the reason.

## 6. Check it works

1. A reviewer opens the app in Teams (or it is pinned) and sends it any message. The bot answers; from now on it can write to them.
   (Installing the app is enough; the message just confirms it.)
2. Let the AI prepare a test action that needs approval, or submit one with the Host's tools.
3. The card arrives in the reviewer's chat. Press **Approve**: the card changes to "Approve by …", and the action runs.
4. Try **Approve with changes** with an edited text, and **Reject** without a reason (it asks for one).
5. On the review page (`https://approvals.contoso.com/`), the same action shows the decision and the audit trail shows "teams".

## 7. Operations

* **Rotating the secret:** create a new client secret, update `TEAMS_BOT_SECRET`, restart, then delete the old secret. Buttons on cards
  posted before the rotation stop working (their signatures came from the old secret); **Open** still works.
* **Removing someone:** delete their entry from `Reviewers` and restart. Their cards remain, but their clicks are refused.
* **Uninstalling:** remove the app in the Teams admin center and the `Teams` section from the config. Nothing else depends on it.
* **Logs:** a refused click (wrong token, tenant, reviewer or card) answers 401/403 or shows a message on the card; the Host never
  executes anything for it.

## 8. Troubleshooting

| Symptom | Cause |
|---|---|
| No card arrives | The person has not installed or opened the app (the bot cannot start a chat with someone it has never met); or `People` does not link their `teams:` identity to the AI agent that asked. The notification is retried, then recorded as `notification_dead`. |
| "This card is not valid here." | The card was clicked in another chat than the one it was sent to, or by another person in a personal chat. |
| "You are not set up to decide from Teams." | The person is not under `Reviewers`. |
| "… actions are decided on the review page." | The tool's risk is above `ChatMaxRisk`. |
| The bot does not answer at all | The messaging endpoint is not reachable over HTTPS, or `AppId` / `TenantId` do not match the Azure Bot. |
| Buttons stopped working after a change | The bot secret was rotated; use **Open**, or wait for new cards. |

## 9. What has been tested

The bot is tested end to end against a fake Bot Framework service with real token signing: card delivery, approve, approve with changes,
reject, digest, refusals of forged, replayed, foreign and unlisted clicks, the risk limit, and cards updating after a decision on the page.
It has **not yet been run in a live Teams tenant**; expect the first live setup to surface details (card rendering on desktop and mobile,
token specifics). Report them, and they will be fixed.
