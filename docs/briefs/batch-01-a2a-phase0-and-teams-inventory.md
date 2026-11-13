# Batch 01: A2A phase 0, and a Teams inventory

From the planning session (git-fa) to the builder session. Repo: `C:\Git\split\opentail-orca` (C#/.NET 10, xUnit v3, warnings are errors).
Read `CLAUDE.md` and `docs/design/governance-checklist.md` (section B) and `docs/design/a2a.md` (sections 1, 2, 10 and "Build order") first.

## Rules

- Do not commit, push, stash or change git state. Leave everything in the working tree. The owner commits.
- Do not rename public names or audit event names. Do not change `contract-v1.txt`; if `ContractFreezeTests` fails, undo the change that
  caused it.
- Claims about code only after reading it. If this brief contradicts the code, follow the code and say so in the report.
- If a part needs a design decision this brief does not make, stop that part and write the options down. Do not guess.
- Baseline: `dotnet test OpenTail.Orca.Abstractions.Tests/OpenTail.Orca.Abstractions.Tests.csproj` 70 of 70;
  `dotnet test OpenTail.Orca.Tests/OpenTail.Orca.Tests.csproj` 1204 of 1204. Both must pass at the end, from a real full run.

## Part A: A2A phase 0 (build). No A2A endpoint, no SDK package, no new tools yet.

1. **Role and scope `a2a:call`.** Add it beside the existing roles (`Roles` in `OpenTail.Orca.Admin/AdminServer.cs`) and make API key
   scope validation accept it (`HostHttpOptions.cs`; read how `mcp:tools`, review, admin and group scopes are validated today). A key
   with only `a2a:call` must not be able to call `/mcp` tools or the review API. Tests for both.
2. **A2A configuration section.** An `A2A` object in the HTTP config with `Enabled` (default false). Nothing else yet. When it is absent
   or disabled, behaviour is unchanged and none of the checks below run.
3. **Startup checks when A2A is enabled** (each is a startup error with a clear message, following how existing startup refusals are
   written and tested):
   - `DataFlowMode` must be `Enforce`.
   - The database must run with `synchronous=full` (`SqliteDurability` / `OPENTAIL_DB_SYNCHRONOUS`). Read how the setting is resolved and
     check the resolved value, not the environment variable text.
   - Every API key that carries `a2a:call` must name a profile.
   - Every API key that carries `a2a:call` must carry no other scope (so a partner key cannot also call `/mcp`). This is from a2a.md
     section 2.
4. **Partner identity foundation.** An optional `Partner` reference on an API key entry, and a `Partners` list in the A2A config:
   each partner has `Id` (a UUID the operator generates once with a new CLI command, e.g. `--new-partner <displayName>`, which prints the
   config snippet; follow `--new-api-key`), and `DisplayName`. Startup checks when A2A is enabled: every `a2a:call` key references a
   partner that exists; ids are valid UUIDs and unique. Several keys may reference one partner (rotation). Do not build task tables or a
   partner subject yet; only the configuration, the validation and the CLI command.
5. **Rule on a top-level argument.** A test in the style of the existing rule tests showing that a conditional rule can match a
   top-level string argument named `partner` (`Policy/Rules/Conditions.cs`): one value executes, another queues. No production change
   expected; if one is needed, stop and report.
6. Docs: a short "A2A (not yet served)" subsection in `OpenTail.Orca/OPERATIONS.md` describing the config, the scope and the startup
   checks, saying plainly that no A2A endpoint exists yet. Tick the items in checklist section B, with "in the working tree, not
   committed" and the test counts.

## Part B: Teams inventory (read only; write one file, change no code)

Write `docs/briefs/teams-inventory.md`, at most 80 lines, from reading the code (`OpenTail.Orca.Http`, `OpenTail.Orca.Notifications`,
`OpenTail.Orca/TEAMS-SETUP.md`, the Teams tests, `scripts/make-teams-app.ps1`):

- What a person can do from Teams today, per tier (card with link; bot with buttons), with the file that implements each.
- What a person can NOT do from Teams that the review page can (list from the admin API endpoints).
- What the bot accepts as typed messages, if anything (commands, free text).
- Which parts have tests against a fake connector, and which have none.
- Every configuration key the Teams pieces read.
- Anything that looks unfinished or inconsistent, with file and line.

Facts only, with file paths. No recommendations.

## Report

Message the planning session (`git-fa`) when done, at most 40 lines: per item done / partly / not done; files changed; final test counts
and the commands used; anything stopped for a decision; any existing test changed and why.
