# Design: Slack as a front door for work

**Status:** deferred, not planned for now (owner, 2026-10-18). Written so the thinking is kept for when a customer asks; see
[why not now](#why-we-are-not-doing-this-now). Builds on [work items and continuations](work-and-continuations.md). Slack facts
checked on 2026-10-18 from Slack's developer changelog and third-party pricing guides; re-check before building.

**Where it fits:** a way for people to hand work to agents from Slack. It is a front door to work items, like the review page and
the Teams bot, never a transport between agents. Not to be confused with the Slack *connector* in the work plan (reading Slack as
a data source), which has its own blockers.

## What it would be

A person in Slack hands a task to an agent and follows it in the same thread:

- **Input:** a "Send to agent" message shortcut and a `/opentail` slash command, each opening a form: which skill or profile, the
  goal, the inputs. The form creates a work item. The Host does not interpret free text (it never calls a model to do the work),
  so the goal is stored as written and the agent interprets it.
- **Thread:** the Slack thread is the work item's `thread`. The Host posts progress into it: claimed, waiting for approval, done
  with a short summary.
- **Approvals in the thread,** up to a risk ceiling, as the Teams bot does with `ChatMaxRisk`; anything higher links to the review
  page.
- **Follow-ups:** replies by the requester are added to the item as instructions; replies by anyone else as outside content.

Slack would never be how agents talk to each other: agents delegate through `delegate`, where identity, profiles and depth limits
apply. An agent that reads Slack messages as tasks would make Slack the easiest place to inject instructions.

## Rules it would need

1. **The real person's identity, not the bot's.** A work item from Slack carries the requester's own identity and authority.
   Otherwise anyone in the channel delegates with the bot's rights (a confused deputy). Until per-user sign-in exists (work plan
   section 3), only Slack users explicitly mapped to a Host identity by an operator could create work.
2. **Only the requester's words are instructions.** Other messages in the thread, pasted email, and anything in a Slack Connect
   channel (which includes people from other organisations) are outside content and taint the item.
3. **Posting to a channel is an exit.** Everyone in the channel sees a reply, not only the requester, and a Slack Connect channel
   reaches outside the company. Replies pass the [data-flow](data-flow.md) rules. The default is status and a short summary in the
   thread, details behind a link to the review page.
4. **Signed requests and quick acknowledgement.** Every request from Slack is signature-checked; Slack expects an answer within
   3 seconds, so work is created and acknowledged at once and everything else happens afterwards.

A cheaper stopgap exists: a signed "create work item" API that Slack Workflow Builder can call, with no Slack app. Identity there
is weak (the workflow's, not the person's), so it would be allowed only for low-risk profiles.

## Why we are not doing this now

1. **Work items do not exist yet.** Slack is only a front door to them ([section 17](work-and-continuations.md#order), step 2).
   Building it first would mean designing the inbox inside a chat bot.
2. **Our likely customers use Teams.** Companies with an ERP and Microsoft 365 mostly have Teams already, at no extra cost, inside
   Microsoft 365. Slack is more common in tech companies, startups and media, which are not who the Host is aimed at first. The
   Teams bot already covers the same flow (cards, approvals, `ChatMaxRisk`).
3. **Identity is not ready.** Rule 1 needs per-user sign-in (work plan section 3), which is still waiting. Without it, the only
   safe version is a hand-maintained mapping of Slack users, which does not scale past a pilot.
4. **A second bot is a second thing to maintain.** A Slack app to distribute, Enterprise Grid quirks, rate limits, the 3-second
   rule, and Slack's app review if we list it; all for a channel no customer has asked for yet.
5. **Slack restricts apps it has not reviewed.** Since May 2025, a commercially distributed app outside the Slack Marketplace may
   read channel and thread history only once a minute, 15 messages at a time (Marketplace apps and a customer's own internal apps
   keep the normal limits). The front door mostly reacts to events, so it would cope, but anything that reads threads would push
   us towards a Marketplace listing and its review. Slack's 2025 API terms are also understood to restrict bulk export and using
   Slack data to train models; check them before any Slack work.
6. **Cost for customers.** Slack is an extra subscription (Pro about $7.25 per user per month on annual billing, Business+ about
   $15 for SSO and compliance exports, prices from third-party guides); Teams comes with Microsoft 365 plans most of our likely
   customers already pay for.

## When to revisit

- A paying customer, or a serious prospect, runs on Slack and asks for it.
- Work items (section 17, step 2) are built, so there is something to be a front door to.
- Per-user sign-in exists, or the customer accepts an operator-maintained mapping of users for a pilot.

When those hold, build it on the Teams bot's pattern: form input, mapped identities, updates in the thread gated by data-flow
rules, approvals up to `ChatMaxRisk`, and everyone but the requester treated as outside content.
