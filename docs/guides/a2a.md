# A2A on this Orca: a guide

## What it is

A2A lets an agent that is not yours send a request to this Orca over the A2A JSON-RPC binding. The request is outside text. It is queued
for a person to approve before it becomes a work item. An internal agent takes the work, a person approves the answer, and only then does
the partner receive it. Nothing a partner sends runs without review in the sample, and a partner gets task states and the answer text only.
The design is in [../design/a2a.md](../design/a2a.md); the settings and startup checks are in [../../OpenTail.Orca/OPERATIONS.md](../../OpenTail.Orca/OPERATIONS.md).

## Start the sample

The sample is in [a2a-sample/](a2a-sample/): `http.json` (the HTTP config) and `policy.json` (the policy). It listens on
`http://127.0.0.1:5600`, with one partner ("Example partner"), one skill ("draft-reply"), and one inbox profile ("inbox").

Its two API keys are documented throwaway values, and the sample holds only their hashes:

- partner key: `sample-only-partner-key-replace-me` (profile `partner-desk`, scope `a2a:call`)
- inbox key: `sample-only-inbox-key-replace-me` (profile `inbox`, scope `mcp:tools`)

Replace both before anything real touches Orca. Do not write a real key into a sample.

Two environment variables, set in your shell and never in a file:

- `OPENTAIL_DB_SYNCHRONOUS=full`: A2A refuses to start on a database at the default durability.
- `OPENTAIL_ADMIN_TOKENS='you:a-token-you-choose|review'`: a reviewer token, which opens the review page at `/`.

From the repository root:

    dotnet run --project OpenTail.Orca.Server -- --policy docs/guides/a2a-sample/policy.json --http docs/guides/a2a-sample/http.json --db sample.db

The log should end with `Listening on http://127.0.0.1:5600/`. A line that names a startup check means the config or the policy is wrong;
the message says which.

## Run the smoke script

In a second shell, with PowerShell 7:

    ./scripts/a2a-smoke.ps1 -PartnerKey sample-only-partner-key-replace-me -Goal "Check the invoice, please."

The script reads the agent card, sends the goal with `returnImmediately` true, prints the task id, and then asks for the task's state every
few seconds. It prints each change of state, and what a person must do next. It never prints the key. `-Cancel` cancels the task instead;
`-TimeoutSeconds` stops it following the task, and the task keeps running.

## Approve the request

The first state is `TASK_STATE_SUBMITTED`, and the script says: approve the request on the review page. Open `http://127.0.0.1:5600/`, sign
in with the reviewer token, and approve the `a2a.submit` action. The state becomes `TASK_STATE_WORKING`. Teams shows the same request in a
chat, if the Teams bot is configured ([../../OpenTail.Orca/TEAMS-SETUP.md](../../OpenTail.Orca/TEAMS-SETUP.md)).

## The inbox agent takes the work

The approved request is a work item for the `inbox` profile. The inbox agent calls Orca's MCP endpoint at `/mcp` with the inbox key,
and uses three of the built-in work tools (from `OpenTail.Orca/Mcp/BuiltInTools.cs`):

- `next_work` claims the next item for the agent and reads it. It takes an optional `waitSeconds`. The result carries `workId`, `goal`
  and `status` (`claimed`).
- `get_work` reads one item the agent may see, by `workId`.
- `complete_work` finishes an item it holds, with a `summary`. The summary is the answer a person will approve.

The agent may also `fail_work` or `hand_back_work` an item it does not finish, and `update_thread_note` to leave a note for the next run.
The kit's test runs claim and complete through `/mcp` with the inbox key.

## Approve the reply

Completing the item queues an `a2a.reply` for a person, with the summary as its text. The review page shows that exact text: approve it only
if it is what the partner should read. `a2a.reply` never allows edits. After approval the partner's task becomes `TASK_STATE_COMPLETED`
with one artifact holding the reply text. If the reply is rejected, the task becomes `TASK_STATE_FAILED`, and nothing of the summary reaches
the partner.

## What the partner sees

Its task moves through `TASK_STATE_SUBMITTED`, `TASK_STATE_WORKING`, and then one of `TASK_STATE_COMPLETED` (with the answer),
`TASK_STATE_FAILED`, `TASK_STATE_REJECTED` or `TASK_STATE_CANCELED`. The messages are generic, and no reviewer's reason is given. A
blocking send (`returnImmediately` false or omitted) waits up to `BlockingWait` (30 seconds by default) for a terminal state; if the wait
passes first, the partner gets an error naming the task, and the task keeps running.

## Onboard a real partner

1. Make a partner id, which is kept for good: `dotnet run --project OpenTail.Orca.Server -- --new-partner "Acme"`. It prints a UUID and the
   config line to add to `"Partners"` in the `A2A` section.
2. Make the partner's key: `--new-api-key acme-agent`. It prints a key once and the hash to put in `ApiKeys`. Give the key to the partner
   and keep only the hash here.
3. In `ApiKeys`, give the key `"Scopes": [ "a2a:call" ]` and nothing else, `"Profile"` (a profile in the policy's `Agents` that lists both
   `a2a.submit` and `a2a.reply` by name), and `"Partner"` (the id from step 1).
4. In the policy, keep `a2a.submit` and `a2a.reply` on `Queue`, with `"AllowEdits": false` on `a2a.reply`. To let one partner's requests run
   without a review, add a rule on the `partner` argument to `a2a.submit`:

       "a2a.submit": { "Mode": "Queue", "Rules": [ { "Name": "trust-acme", "Mode": "Execute",
           "When": { "arg": "/partner", "equals": "<partner id>" }, "IgnoreArgs": [ "taskId", "skill", "goal", "inputs" ] } ] }

   The rule is a trust in that partner's requests only. A reply is always reviewed, so no rule may run `a2a.reply` without a person.
5. Keep `DataFlowMode` at `Enforce`, and add a `DataFlow` destination for the reply, so a label the reply carries can reach only the partner
   it names (the sample has one, for `finance`):

       "DataFlow": { "finance": { "Destinations": [ { "Tool": "a2a.reply", "When": { "arg": "/partner", "equals": "<partner id>" } } ],
           "Otherwise": "Deny" } }

6. Restart Orca and read the log for startup checks.

## Startup checks

Each check refuses to start Orca, with the reason, when A2A is enabled and the check fails. The full list and what each means is in
[OPERATIONS.md](../../OpenTail.Orca/OPERATIONS.md), under "A2A (preview)". In short: `DataFlowMode` is `Enforce`; the database runs at
`synchronous=full`; every `a2a:call` key names a profile and a partner and has no other scope; the partner ids are UUIDs, unique and
named; `Work.Enabled` is set; `a2a.submit` has an entry; `a2a.reply` is `Queue` or `Deny` (never `Execute`), reviewed by a person, with no
edits and no `Execute` or AI-reviewed rule; every skill's `Inbox` is an agent profile; each `a2a:call` profile lists both tools; `PublicBaseUrl`
is `https`, or `http` on loopback; `BlockingWait` is from 1 second to 5 minutes; `MaxBlockingWaitsPerPartner` is from 1 to 64.

## Limits

- No streaming, no resubscribe, no push notifications, and no extended card. Each is refused with `UnsupportedOperation`.
- No file parts: a message with a file is refused.
- Skills: with exactly one skill configured, a message that names none is served. With several, a message names its skill through the
  skill-selection extension: list `https://opentail.net/a2a/extensions/skill-selection/v1` in the message's extensions, and give the skill id
  as the string value under the same URI in its metadata. A message that names none, or a skill the caller may not use, is refused as
  "not available". A skill's optional `Profiles` lists the profiles that may use it (each must list `a2a.submit`); without it, any profile
  that lists `a2a.submit` may.
- Contexts: a message with a `contextId` of one of the partner's own contexts is a new task on that context, and the context's taint and
  labels apply to it. Another partner's context, or an unknown one, is refused as an unknown task. A first message gets a context id from the
  Host; a `contextId` the partner makes up on a first message is refused.
- A blocking send waits at most `BlockingWait` (30 seconds by default, 5 minutes at most), then answers an error.
- A partner may have at most `MaxBlockingWaitsPerPartner` (4 by default) blocking sends waiting at once.
- `ListTasks` is served for the partner's own tasks only, newest first by creation time, in pages of up to 100.
- The agent card names no tool, profile, partner or reviewer. Its authorisation is the same as `/a2a`'s.

## Not built

Tool skills, the extended card, signing of the card, push notifications, outbound calls from Orca to other agents, and file parts.
The design lists them, with the reasons, in [../design/a2a.md](../design/a2a.md).

## Where the details are

- Settings and startup checks: [OPERATIONS.md](../../OpenTail.Orca/OPERATIONS.md), "A2A (preview)".
- Teams commands and approvals: [TEAMS-SETUP.md](../../OpenTail.Orca/TEAMS-SETUP.md).
- The protocol decisions and the states: [../design/a2a.md](../design/a2a.md).
