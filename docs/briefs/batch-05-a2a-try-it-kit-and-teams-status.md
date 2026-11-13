# Batch 05: an A2A kit the owner can run, and read-only status commands in Teams

From the planning session (git-fa). Batch 04 is accepted and pushed (c159639). Your stopped point is decided: option (c), keep 401;
`a2a.md` section 8 now says an anonymous caller can tell that A2A is on and why that is accepted. Same rules: no commit/push/git
state; no public renames; `contract-v1.txt` unchanged; verify each claim in the code first; follow the code where the brief is wrong
and say so; STOP a part and write options when it needs a design decision not made here. Baseline: 1393 passed, 0 failed, 32 skipped.

## Part A: a kit that runs (the owner has never seen A2A work; this is how they will)

A1. **Sample files** in `docs/guides/a2a-sample/`: an HTTP config and a policy that start a Host on loopback with A2A enabled, one
    partner ("Example partner"), one inbox skill, one inbox profile for an internal agent, `a2a.submit` and `a2a.reply` on Queue,
    `DataFlowMode` Enforce, `Work.Enabled`. API keys in a sample are hashes of documented throwaway values, clearly marked
    "sample only, replace"; follow how existing samples or DEPLOY.md handle keys, and never write a real secret. If the Host has a
    command that makes a key hash or a partner id (`--new-partner`), the guide uses it instead of fixed values where it can.
A2. **`scripts/a2a-smoke.ps1`** (PowerShell 7, `Invoke-RestMethod` only, no modules): parameters base URL, partner key, goal text.
    It reads the card, sends with `returnImmediately` true, prints the task id and state, then polls GetTask every few seconds
    until a terminal state or a timeout, printing each state change and the answer when COMPLETED. `-Cancel` cancels instead.
    It prints what a person must do next while it waits ("approve the request on the review page: <url>"). It never prints the key.
A3. **`docs/guides/a2a.md`**: at most 150 lines, plain steps: what A2A on this Host is in five lines; start the sample; run the
    smoke script; approve the request (page, and Teams if configured); how an internal agent takes the work over MCP (the exact
    built-in tools: claim, read, complete; name them from `Mcp/BuiltInTools.cs`); approve the reply; what the partner then sees.
    Then: onboarding a real partner (new partner id, key with `a2a:call` alone, profile listing both tools, a trust rule on the
    `partner` argument, a `DataFlow` destination for the reply), every startup check and what it means, the limits (no streaming,
    no push, no files, one skill per partner, 30 second blocking wait), and what is not built. Every setting and tool name in the
    guide must exist in the code; link, do not repeat, OPERATIONS.md.
A4. **Tests that keep the kit honest** (`A2aKitTests`): the sample config and policy load and the Host starts with them (through the
    same loader the server uses); the full flow passes against the sample (send, approve submit, an agent of the inbox profile
    claims and completes through the built-in tools' path, approve reply, COMPLETED with the summary); every JSON-RPC method name
    and header the smoke script sends matches what the test's raw requests send (keep the request bodies in one place the test can
    read, or have the test parse the script for the method names; say which). Do not start PowerShell from a test.
A5. Correct `docs/briefs/a2a-reviewer-view.md`: the card's "OPEN" line is decided (option c, see a2a.md section 8).

## Part B: read-only status in Teams (free text still never becomes an action)

Batch 02 added `waiting` and `help` for listed reviewers. Add two more typed commands, same rules (personal chat, listed reviewers
only, exact words, anything else gets the fixed reply; nothing they do changes state):

B1. **`work`**: counts of work items by status (open, claimed, dead, expired in the last 7 days), then the 10 oldest open or dead
    items: short id, `For`, status, age. No goal text, no summary, no inputs: a work item's text may carry labelled data, and a
    chat is an exit. If work items are not enabled, it says so.
B2. **`tasks`**: A2A tasks in flight, newest 10: short task id, the partner's display name, the state a partner would see, age, and
    what it waits for ("a reviewer: request", "an agent", "a reviewer: reply"). No goal, no answer text. The Teams bot is in
    `OpenTail.Host.Http` and must not reference `OpenTail.Host.A2A`: add the smallest seam for it, for example a default interface
    member on `IHttpDeploymentExtension` that returns chat commands (name, one-line help, a function returning plain lines), which
    the bot escapes and renders. `help` lists commands from the seam too. When A2A is off the command does not exist.
B3. All text the commands print goes through the bot's existing escape. Partner display names come from config, but escape them too.
B4. `TEAMS-SETUP.md`: the commands, who may use them, and that they never show request or answer text.
Tests: each command for a reviewer; a non-reviewer and a channel message get what they get today; `tasks` absent when A2A is off;
no goal, summary or answer text appears in either command's card (use a goal and summary with a marker string and assert its
absence); the architecture test still forbids Http -> A2A. Mutation: print the goal in `tasks` -> the marker test fails; restore.

## Report

Message `git-fa`, at most 45 lines: per item done / changed / stopped; the seam of B2; anything in the guide you could not back
with code; existing tests changed and why (none is expected); files changed; full-solution counts; the mutation result.
