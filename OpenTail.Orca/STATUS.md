# OpenTail Orca: status by version

As of 2026-11-13. This is the one place to see what Orca has built, from which plan, and what is still open. The plans are the
design record; each plan's implementation log is the detailed record, and where a plan's status line disagrees with its log, the log
wins. What is listed as built comes from those logs. The test counts in them are snapshots and were not re-run for this document.

**Reading the version names.** `MCP-HOST-PLAN.md` is v1, `MCP-HOST-V2-PLAN.md` is v2, and so on. Inside the v2 plan the phases are also
called V1 to V7 (its sections 5 to 11). When this document says "v2 phase V3", it means that phase. The files `MCP-HOST-V5-PLAN.md` and
`MCP-HOST-V6-PLAN.md` are separate plans, not phases of v2.

## At a glance

| Plan | Subject | State | Read |
|---|---|---|---|
| v1 | Orca core, queue, SQLite, first modules, sample deployments | Built (phases 0 to 8 and a review pass) | [archived](../docs/done/MCP-HOST-PLAN.md) |
| v2 | Team use: HTTP and sign-in, policy rules, notifications, resumable work, approval controls, docs | Built (its phases V1 to V7); acceptance 2026-10-16 | [archived](../docs/done/MCP-HOST-V2-PLAN.md) |
| v3 | Modules as bricks: contract, credentials, configuration, provider modules, process isolation | Built, except W6 (publishing), the live tests and the outside-author conformance kit | [archived](../docs/done/MCP-HOST-V3-PLAN.md) |
| v4 | Integrations as data: integration profiles | Built; ServiceNow kept as code by decision | [archived](../docs/done/MCP-HOST-V4-PLAN.md) |
| v5 | The people in the loop: Teams, company sign-in, pilot | Partly built; the pilot waits for the owner | [active](../docs/MCP-HOST-V5-PLAN.md) |
| v6 | AI reviewers and the defences against an eager requester (merged into the gate plan) | R1 built; R2 and later open | [active](../docs/design/reviewer-gate.md) |
| v7 | Administration and telemetry on the review page | Built (A1 to A4, 2026-10-19) | [archived](../docs/done/MCP-HOST-V7-PLAN.md) |

Designs for work that is planned but not built, and for work after these plans, are indexed in [`docs/design/README.md`](../docs/design/README.md).

## v1: Orca core

Built (log of 2026-10-15):

- The core model in `OpenTail.Orca.Abstractions`, and Orca in `OpenTail.Orca`: the MCP stdio server, the action queue with approval
  states, the SQLite store with leases and recovery, rate limits, the trust ramp, `--explain`, redaction, and OpenTelemetry under the
  `OpenTail.Orca` source.
- Modules: Files, Email, Csv, Sql (read-only queries, with previewed writes on request), Calendar, Web (guarded fetch), and OpenApi, a
  self-contained module on `Microsoft.OpenApi.Readers`. The older `OpenTail.OpenApi` was left untouched.
- `DeploymentRunner` (policy file, SQLite, loopback admin page, stdio), two sample deployments (`OpenTail.Orca.Sample.EmailOnly`,
  `OpenTail.Orca.Sample.FilesOpenApi`) and `scripts/check-host-deployments.ps1`.
- The old coding server `opentail/OpenTail.Mcp` was retired on 2026-10-15 with the owner's say-so. Its successor is
  `C:\Git\OpenTail.MCP-src`.

Open at the end of v1, with later changes noted:

- A real-client check over stdio against a sample deployment. A real-client check over HTTP was done under v2.
- Reading an incoming `traceparent` from the MCP `_meta` field. It needs a change at handler level.
- Exposing queued actions as MCP tasks. Still not built (see v2).
- Lease extension: a tool that runs longer than its lease (default 5 minutes) is treated as interrupted.
- Trust promotions were kept in memory only, on purpose, in v1. V2 V1 persisted them (schema 2).
- `SessionRegistry` never evicted sessions in v1, so memory grew with each `start_session` call. V2 V1 moved sessions into SQLite; the
  log does not say whether that changed this.
- A non-author walkthrough of the approval page.
- The sample deployments are 34 to 57 MB, because native runtimes for every platform are included. A runtime-specific publish was not done.

## v2: team use

The v2 plan's phases V1 to V7 are all logged as done on 2026-10-16:

- **V1, foundations.** Namespaced subjects, owner-bound sessions with a taint window, scoped reads and writes, atomic limits, an event sink,
  and a schema migration that takes a backup first.
- **V2, HTTP and sign-in.** `OpenTail.Orca.Http`: Streamable HTTP, JWT sign-in, hashed API keys (keys can never review), reviewer tokens,
  and startup refusals for unsafe settings.
- **V3, conditional policy.** Per-tool rules over a small predicate language with three-valued logic, budgets, `--explain` and
  `--replay-policy`.
- **V4, notifications.** `OpenTail.Orca.Notifications`: a durable outbox, webhook (HMAC-signed) and Slack channels, reminders, and
  reconciliation of lost events.
- **V5, resumable work.** `notices`, `get_updates`, `wait_for_action`, and retention.
- **V6, approval controls.** Reviewer groups, no AI decisions on human-only actions, self-approval refused, N-of-M approvals decided
  inside the store transaction, and edits that void earlier approvals.
- **V7, documentation and packaging.** `POLICY.md`, `OPERATIONS.md`, `SECURITY.md`, `PACKAGING.md`, and the deployment check.

Acceptance (logged 2026-10-16): a real Claude Code client over signed-in HTTP queued a write; a reviewer asked for changes; a new
conversation read the reason, resubmitted, and was approved; an API key was refused on the review API; a signed webhook was verified by
an independent receiver.

Still open, from the plan's own list:

- No second person has walked through the approval page in a real browser.
- Single node on SQLite. No load or soak test. No independent security audit.
- MCP tasks and Slack approve buttons are not implemented.
- At the time of the log, packages were not published. The repository now has a `host-v0.0.1` tag. What was published under it is not
  recorded in the plans.
- Sign-in was tested with a local signing key, not a live identity provider. Notifications were tested against a scripted handler and a
  local receiver, not a live Slack workspace.
- `OpenTail.Core` does not pack (missing icon). This predates Orca work.

## v3: modules as bricks

Built (log of 2026-10-16):

- The contract: `ModuleManifest`, a contract version read from `[assembly: HostContractVersion]`, a contract freeze test against
  `contract-v1.txt`, and `OpenTail.Orca/MODULES.md`.
- Credentials and outbound HTTP: named credentials from environment, file, or OAuth2 client credentials; redaction of every secret handed
  out; a guarded HTTP client that intersects operator hosts with the manifest, refuses non-public addresses unless allowed, and re-checks
  redirects.
- Configuration: `modules.json`, factory discovery through `deps.json`, `--list-modules`, `--lint`, `--test-policy`, `--adopt-policy`, and
  `Enabled: false` to retire a module.
- Modules: `GitHubIssues` (W5), `ServiceNow` (W5b), `Social` (W8, Reddit only since v4 P3), `Google` (Gmail and Google Calendar), the
  provider packages `Email.Imap`, `Calendar.Ics` and `Microsoft365`, and factories for `csv`, `web`, `sql`, `files` and `openapi`.
- Process isolation (W7): `"Isolation": { "Mode": "Process" }`. The worker is the deployment's own program, HTTP is brokered, the
  environment is empty, and there are timeouts, size limits and restarts.
- A read-only Modules view on the review page.

Not done:

- **W6, publishing.** The feed choice is open. No module package is recorded as published under this plan.
- **Live tests.** GitHub (a second account writes an injected issue into a throwaway repository), a ServiceNow instance, Reddit, YouTube,
  X, Microsoft 365. Gmail and Google Calendar have been described but never called.
- **A conformance kit for outside authors** (`Host.Testing`). It was planned for W7; `MODULES.md` has the rules and a complete example.
- **OS-level containment on Windows** for process-isolated modules.
- **Taint on every `Write` tool.** Not done. Bricks mark outward effects `ExternalEffect`, and `--lint` warns about the dangerous
  combination. Decide later if needed.

## v4: integrations as data

Built (log of 2026-10-16 and after):

- **P1, profiles.** A profile model and loader with curated tools, stated risk per tool, argument and body rules, previews, error
  mapping, and `Config.Profile`. Operator guide: `OpenTail.Orca/PROFILES.md`.
- **P2, responses.** Field selection, text caps, pseudonymisation, and the untrusted wrapper.
- **P3, proof.** YouTube and X re-expressed as profiles in `opentail/profiles/`. The Social module is Reddit only now; old `YouTube` and
  `X` sections are refused with a pointer to the profiles. The Social code went from 583 to 377 lines, about a third less than the half
  estimated.
- **P4, decision.** ServiceNow stays a hand-written module. Its tests need two dependent requests per call, which one-request-per-tool
  profiles cannot express without becoming a workflow language.
- **P4b, packaging.** `package:Assembly/name` profiles, the `OpenTail.Profiles.YouTube` and `OpenTail.Profiles.X` packages, and a
  conformance check that runs at startup and in `--lint`.
- **Licence.** Orca, its modules and profiles are under proprietary commercial licence (`LICENSE.md`). The shared `OpenTail.Connectivity.*`
  packages published before 2026-11-13 stay under MIT for those who received them. The Connectivity code now lives in the Dolphin
  repository, under the same commercial licence as Orca.

Open:

- **Owner decision (plan section 6):** profiles in this repository or in a separate one later. The default applies if unanswered: here,
  until there are more than a handful. No answer is logged.
- Live verification of any provider. The v5 pilot is where that starts.
- Google publishes Discovery documents, not OpenAPI. The YouTube spec is a hand-written subset of it.
- A query builder for filter languages (Jira JQL, OData, SOQL) is deferred until a real API needs it.

## v5: the people in the loop (active)

Plan: [`MCP-HOST-V5-PLAN.md`](../docs/MCP-HOST-V5-PLAN.md). Proposal of 2026-10-16, partly built.

Built (its implementation log):

- The review page shows why an action is held, its effect in plain words, the caller's recent actions and similar past decisions, and
  has filters, keyboard shortcuts, and a required reason to reject or request changes.
- Teams, tier 1a: an Adaptive Card with a link to the review page. Teams, tier 1b: company sign-in for the page (OpenID Connect with PKCE).
- Teams, tier 2: the bot without the Bot Framework SDK, with token validation, conversation references, cards that update in place, and
  digests. Tested against a fake connector.
- `scripts/make-teams-app.ps1` builds the Teams app package. The IT guide is `OpenTail.Orca/TEAMS-SETUP.md`.
- Teams, batch 02 (`fef5b4f`): the chat risk order is fixed (Read, Write, ExternalEffect, Destructive); Request changes is on the
  card; typed `waiting` and `help` for listed reviewers.
- Teams, batch 04 (`c159639`): a card shows a preview of up to 400 characters. A longer one is cut and says so, and it offers no
  approve, run now or change, on the card or on the click. The digest and the Teams notification card mark a cut line.
- Teams, batch 05 (`7b6003c`): typed `work` (work-item counts, and the ten oldest open or dead items, with no text) and `tasks` (the
  A2A tasks in flight, when A2A is on), read-only, for listed reviewers.
- `OpenTail.Orca.Server` compiles in every module and profile and is enabled by `modules.json`. There is a container image, a Compose file
  with Caddy, and `OpenTail.Orca/DEPLOY.md`. CI builds and smoke-tests the image; the log notes that Docker is not on the development
  machine, so CI is the first real run.

Waiting for the owner:

- **The pilot (this plan's V4):** one real mailbox, one real ticket system, the owner as reviewer, for a week. It needs a throwaway GitHub
  repository with a second account and token, a test mailbox, a Microsoft 365 developer tenant with a Teams app registration, a Slack
  workspace, and conversations with businesses.
- Live checks of company sign-in with Entra, the Teams bot in a real tenant, and Slack.
- A walkthrough of the review page by a real reviewer.

Not built: Slack approve buttons (the Slack notification channel from v4 exists). Candidates for after the pilot, none started: audit
export, a Postgres store, a hosted deployment, per-team policies, reviewer delegation and holiday cover, MCP tasks.

## v6: AI reviewers (active)

Plan: [`design/reviewer-gate.md`](../docs/design/reviewer-gate.md), which merges V6 (now a pointer). R1 done 2026-10-16; the rest proposed.

Built (R1):

- The AI reviewer rules are enforced by the queue in `AiReviewRules`, so they hold for any reviewer. An AI may decide only up to
  `AiMaxRisk` (default `Write`, never `Destructive`); a tainted `ExternalEffect` action always goes to a person; an AI approval cannot
  carry edits; with `SelfApproval: Forbid` an AI may reject but not approve; after `AiMaxRejections` (default 2) only people decide; and
  `AiBudget` caps AI approvals per requester or globally.
- Each AI decision writes an `ai_review` audit entry with the reviewer, model and prompt version.
- `IReviewer` and `ReviewerRunner` exist. The plan records that no deployment runs the runner yet. Tests: `AiReviewLimitsTests`.

Open:

- **R2, a model-backed reviewer** (`ModelReviewer` over `IChatClient`). It can be built against a fake model first. The plan has Orca
  build the packet, and detect and escalate reviewer-directed text in the content.
- **R3 and R4** start only after the v5 pilot produces human decisions to compare against.
- **R5, the test corpus.** Needs no model and no owner input. The plan's order section puts it next.
- **Owner decisions.** The plan's table has recommendations, not logged decisions: the first provider (OpenAI-compatible first), the
  default AI ceiling (`Write`), whether AI suggestions show during shadow measurement (no), sampling in decide mode (10%), a warning when
  the reviewer is the same model family as the requester, and reasons given in full with attempts counted.

## v7: administration and telemetry (archived)

Built (log of 2026-10-19):

- **A1, read-only views.** The overview (profiles resolved per tool, people, keys, health, warnings), policy versions with a diff, and
  usage. The Admin button shows only to admins.
- **A2, telemetry.** `HostTelemetryRecorder` keeps metric totals and the last 500 spans in memory, with bounded memory. The page has
  Telemetry (metrics and a trace waterfall) and Audit tabs. New spans cover review, the maintenance sweep and notification delivery.
- **A3, policy proposals** (schema 12). A candidate policy is validated, replayed against stored actions, and then approved by a different
  admin, rejected with a reason, or withdrawn. An approved proposal is marked applied on the first sweep after the policy loads.
- **A4, documentation.** `OPERATIONS.md` (admin views, telemetry, audit events), `SOP.md` (a policy change needs two admins), and a pointer
  in `POLICY.md`.

Not done:

- A walkthrough in a real browser. The tests cover the API, headers and structure.
- Hot reload. A policy change takes effect on restart; hot reload of an approved policy is listed as later work.
- Editing keys or secrets on the page, by design.
- The recorder is not a metrics backend. Use OpenTelemetry export for history.
- Later items: a Teams tab with frame-ancestors and SSO for the page, and proposals for module configuration and notification routes.

## A2A: the agent-to-agent front door (design: `docs/design/a2a.md`)

Plan and design: [`docs/design/a2a.md`](../docs/design/a2a.md). The checklist of what is done is [`docs/design/governance-checklist.md`](../docs/design/governance-checklist.md). A2A is off unless `A2A.Enabled` is true.

Built (its commits are in the checklist's batch record):

- Phase 0 (`f24f8c5`): the `a2a:call` role and scope, the startup checks, the partner ids, and a conditional rule on `partner`, each tested.
- The thin slice (`fae2725` to `7b6003c`): JSON-RPC `SendMessage` (with a bounded blocking wait and a per-partner cap), `GetTask`,
  `CancelTask` and `ListTasks`; `a2a.submit` (queued, and an approved request becomes a work item for an inbox profile); `a2a.reply`
  (queued and reviewed, an exit, no edits); the authenticated agent card; the task id table; the rules for partner text on the review
  page and in Teams; and the kit (`docs/guides/a2a-sample/`, `scripts/a2a-smoke.ps1`, `docs/guides/a2a.md`).
- Tests: `A2aSliceTests`, `A2aPhase0Tests`, `A2aKitTests` and `ReviewViewTests`, and the Teams tests in `TeamsBotTests`.

Not built: the skill-selection extension; more than one skill per Orca; contexts and multi-turn; tool skills; the extended card; card
signing; HTTP+JSON; push and `INPUT_REQUIRED`; outbound calls; partners authenticated by JWT; streaming and resubscribe; file parts.

Waiting for the owner (the decisions the planner made, and where each is recorded, are in [`docs/briefs/README.md`](../docs/briefs/README.md)):

Decided by the owner on 2026-11-13:

- A2A ships inside `OpenTail.Orca.Server` (one server for A2A and MCP), not as its own package.
- Teams may approve a partner reply by default. The switch `ApprovePartnerRepliesInChat` (default `true`) turns that off; it is built and tested (2026-11-13).
- The 400-character card preview limit is kept.

Waiting for the owner:

- A live check in a real Teams tenant, including whether a bare `@name` in a card becomes a mention.
- The missing-header rule of `A2A-Version`, and which profiles may use an inbox skill.

## Built after these plans

Work after the plans is not recorded here. See [`docs/design/README.md`](../docs/design/README.md) for designs and their state. That
index covers the governance naming and re-check at claim (on `main` as `d6b10f3` and `92c0048`), the Snowflake read-only Sql target
([done: S1 and S2](../docs/done/snowflake-s1-s2.md)), the skills plan, and the A2A design, which is partly built (see the A2A section below).

The solution also contains `OpenTail.Profiles.Jira` and `OpenTail.Profiles.Confluence`. No plan log covers them.

## Module support status

Two levels. The level says what the owner keeps working, not how good the code is.

- **Maintained:** demonstrated live against the real system, and kept working when the vendor changes.
- **Available:** built and tested against fakes or a recorded connection. Not run against the real system by us. Fixed when a
  deployment needs it.

As of 2026-11-12, every module and profile is **Available**. None is Maintained yet, because none has been run against a real system
(see "Needs a live system" below). A module moves to Maintained when its live check has passed and someone uses it.

| Level | Modules and profiles |
|---|---|
| Maintained | none yet |
| Available | Modules: Calendar, Calendar.Ics, Csv, Email, Email.Imap, Files, GitHubIssues, Google, Microsoft365, OpenApi, ServiceNow, Skills, Social, Sql, Sql.Databricks, Sql.Oracle, Storage, Web. Profiles: Confluence, Jira, X, YouTube. |

Dependency versions are kept current by Dependabot (`.github/dependabot.yml`): it opens grouped pull requests each week, and CI runs the
tests on each. That keeps Available modules building against current packages. It does not show that a vendor's API still behaves the same.

## Open across the plans

**Needs the owner:** the profile hosting decision (v4), the packaging feed (v3 W6), the pilot's accounts and time (v5), the AI reviewer
provider and defaults (v6), and the tenant, bot and Slack app for the live Teams and Slack checks (v5).

**Needs a live system; so far only fakes:** GitHub, ServiceNow, Reddit, YouTube, X, Microsoft 365, Gmail and Google Calendar (never
called), Slack notifications, company sign-in, the Teams bot, and the container image (CI's first real run).

**Engineering not started:** v6 R2 and R5, MCP tasks, Slack approve buttons, `traceparent` from `_meta`, lease extension, the outside-author
conformance kit, Windows containment for isolated modules, hot reload of policy, proposals for module configuration and notification
routes, and session eviction.

**Limits that stay:** single node on SQLite, no independent security audit, no load or soak test. `OpenTail.Orca/SECURITY.md` lists the
threat model and what it does not cover.

**Not run in the pass that wrote this document:** `OpenTail.Orca.Tests.SqlServers`, `OpenTail.Orca.Tests.Snowflake` and
`OpenTail.Orca.Tests.Worker`.

## Where to read next

- Operator documents, all in `OpenTail.Orca/`: `README.md` (start here), `POLICY.md`, `OPERATIONS.md`, `SECURITY.md`, `SOP.md`,
  `PACKAGING.md`, `MODULES.md`, `PROFILES.md`, `DEPLOY.md`, `TEAMS-SETUP.md`, `AUDIT.md`.
- Plans, archived and active: [`docs/done/README.md`](../docs/done/README.md) lists the archived ones, with a note on the commit hashes in
  their logs.
- Designs: [`docs/design/README.md`](../docs/design/README.md).
- Briefs, reports and the planner's decisions: [`docs/briefs/README.md`](../docs/briefs/README.md).
