# Running the Host for a team

Step-by-step procedures for operators, reviewers, security and auditors (what happens, and what you do) are in [SOP.md](SOP.md).

## Shape of a deployment

The quickest deployment needs no program of your own: `OpenTail.Host.Server` (program `opentail-host`, and the container image) has
every module compiled in and enables what `modules.json` names. [DEPLOY.md](DEPLOY.md) covers running it at a public HTTPS address.

A deployment of your own is a small console program that names its modules and calls a shared `Main`. See `OpenTail.Host.Sample.TeamHttp`:

```csharp
return await HttpDeploymentRunner.RunAsync(args, "my-host", builder =>
{
    builder.AddModule(new FilesModule(new FilesModuleOptions { Root = @"D:\shared" }));
    builder.AddModule(OpenApiModule.Create(File.ReadAllText("crm.json"), new OpenApiModuleOptions { Namespace = "crm", AuthTokenEnvVar = "CRM_TOKEN" }));
});
```

```
my-host --policy policy.json --db D:\host\queue.db --http http.json --notifications notifications.json
```

| Flag | |
|---|---|
| `--policy <file>` | Required. See [POLICY.md](POLICY.md). |
| `--db <file>` | The SQLite file: queue, audit, sessions, notices, outbox. Default `./opentail-host.db`. |
| `--http <file>` | Serve Streamable HTTP with sign-in (below). Without it the process speaks MCP on stdin/stdout, as before. |
| `--notifications <file>` | Tell reviewers about pending actions (below). Needs `--http`. |
| `--modules <file>` | Modules enabled by configuration, with their hosts and credentials. See [MODULES.md](MODULES.md). |
| `--list-modules`, `--lint` | What is loaded (version, contract, tools, risks, what the policy does) and warnings. No credentials needed. |
| `--adopt-policy <module>` | Write a module's suggested policy to `policy.d/<module>.json` (`--allow-execute`, `--replace`). |
| `--explain`, `--replay-policy`, `--test-policy <cases.json>` | Look at policy without serving. |
| `--export-review-outcomes <out.jsonl> [--since 30d]` | Rejected and sent-back actions, with the reviewers' reasons, the skill versions served in the same session and the outside content the agent had read, redacted (email addresses, phone numbers, accounts, long numbers become placeholders). Each line is a scenario candidate for the skills workbench. Redaction is best effort: check every line before it leaves the deployment. Needs `--policy` and `--db`. |
| `--skill-outcomes [--since 30d] [--min-cases 20] [--worse-by 0.15]` | Per skill version: how people decided the actions that followed it in the same session, with a 95% interval, and a rollback suggestion when the newest version is turned down noticeably more often than the one before (at least `--min-cases` each, worse by `--worse-by`, beyond the interval). It only suggests: rolling back is a `skills.publish` of the earlier version, through approval. Needs `--policy` and `--db`. |
| `--export-shadow-cases <out.jsonl> [--since 7d]` | Sessions in which a skill was served and people decided actions: the message ids read, the skills served, the decisions. For shadow runs of a candidate skill. **Not redacted**: keep it inside the deployment and delete it with the proposal. |
| `--shadow` (when serving) | Shadow mode: Read tools run against the real systems with this instance's own credentials (use read-only ones and a database of its own); every tool above Read is recorded and never run. Refused together with notifications or Teams, so a shadow instance never reaches reviewers. `--export-review-outcomes` also takes `--approved-sample 0.1`: that share of approved actions, redacted, for grading real work. |
| `--usage [--since 7d]` | Daily use per identity and tool: outcomes, records, distinct entities, recipients and bytes. Needs `--db`. |
| `--verify-audit [--checkpoint <seq:hash> ...]` | Walk the audit trail's hash chain and name the first entry that was changed, removed or reordered; with checkpoints, also catch entries removed from the end. Also checks the decision records kept for purged actions against the digests the trail holds for them. Exit 1 when anything is wrong. Needs `--db`. |
| `--audit-checkpoint` | Print the chain's newest entry and hash (`<seq>:<hash>`). Keep it where the database's owner cannot rewrite it (a ticket, a write-once bucket); a chain is only as trustworthy as its last checkpoint someone else holds. |
| `--policy-versions` | Every policy this database has run under, by hash, with when it was first and last loaded. A Host records the policy it starts with and audits `policy_loaded` (hash, Host version, instance); every action carries the hash it was decided under. Needs `--db`. |
| `--policy-diff <hash> <hash>` | What changed between two stored policy versions, line by line (`-` only in the first, `+` only in the second). A unique prefix of a hash is enough. Needs `--db`. |
| `--export-audit <folder> [--since 90d \| --from <time>] [--until <time>]` | A folder for an auditor: the audit trail (each entry with the exact text its hash covers), the decision records of the period (complete purge batches, and actions not yet purged), every policy version in force, the chain's head, a manifest of file hashes and `verify.py`, which checks all of it with Python's standard library only. No argument values or results. See [AUDIT.md](AUDIT.md). Needs `--db`. |
| `--access-report [--at <time>]` | Access as of a time, rebuilt from the audit trail and the stored policy versions: every API key with its profile and what the profile allowed, the policy in force, temporary grants (those still stored) and identities above Normal. For access reviews. Needs `--db`. |
| `--work [--state <state>] [--thread <id>]` | Work items, oldest first: status, kind, who it is for and from, runs, thread. States: open, claimed, done, cancelled, expired, dead. Needs `--db`. |
| `--work-item <id>` | One work item: goal, inputs, note, outcomes, runs, delegation chain, labels and the thread note. Needs `--db`. |
| `--redrive <id> --by <name> [--for profile:<name>] [--exclude-failed] [--drop-note]` | Put a dead or expired item back in the inbox with the inspection's findings attached; optionally for another profile, without the identities that failed it, or without the old thread note. Audited as `redrive.approved`. Needs `--db`. |
| `--requeue <id> --by <name>`, `--cancel-work <id> --by <name>` | Put an ended item (dead, expired, done, cancelled) back in the inbox with its runs reset; cancel an open or claimed one. Audited. Needs `--db`. |
| `--suspend <subject> --by <name> --reason <text>` | Suspend an identity by hand (an incident): every call refused until a person clears it, whatever the configured modes. Audited as `level_suspended`. Needs `--db`. |
| `--cancel-pending <subject> --by <name> --reason <text>` | Cancel all of an identity's pending actions, announced ones included. Audited. Needs `--db`. |
| `--circuits` | Routes paused (or that would be, report only), with fingerprint and the calls they hold, from the audit trail. Circuits are per process and a restart clears them. Needs `--db`. |
| `--record-restore --by <name> --note <text>` | Record a restore from backup in the audit trail, with the head restored to; later checkpoints will show as missing. Needs `--db`. |
| `--by <name>` (when serving) | Who started the Host, named in the `policy_loaded` audit entry. Default: the operating-system account. |
| `--levels` | Identities that are Elevated or Suspended, or would be (report only), and why. Needs `--db`. |
| `--clear-level <subject> --by <name> [--key-rotated]` | Return an identity to Normal; audited. A suspended API key must be rotated first, and the Host checks its own `key_rotated` entry since the suspension. Needs `--db`. |
| `--review-report [--since 7d]` | How people reviewed, per reviewer and tool: approval rate, median time to decide, approvals within 5 s of arriving, approvals in bursts of 10+ a minute, approved actions that then failed; and tools approved almost always (candidates for less friction) or often rejected. For the operator and each reviewer, never shown to other reviewers or used to block anyone. Needs `--db`. |
| `--grants`, `--revoke-grant <id> --by <name>` | Temporary access grants in force (identity, tool, until, calls left, reason); end one early, audited. Needs `--db`. |
| `--new-api-key <name>` | Print a new API key once, with the hash for the config. |

stdout belongs to the protocol in stdio mode; all messages go to stderr. At startup each module is listed there (`module github
(github-issues, OpenTail.Modules.GitHubIssues 0.1.0, contract 1): 7 tool(s) ...`), followed by any warnings. The approval page has a
read-only **Modules** view with the same information, and every action records the module version that handled it.

## HTTP and sign-in

`http.json`:

```json
{
  "Urls": [ "https://0.0.0.0:8443" ],
  "PublicBaseUrl": "https://approvals.example",
  "Jwt": { "Authority": "https://login.example/realms/team", "Audience": "https://approvals.example/mcp" },
  "ApiKeys": [ { "Name": "alice-agent", "Hash": "sha256:...", "Profile": "reply-assistant" } ],
  "AllowedOrigins": [],
  "RateLimitPerMinute": 120
}
```

* **Agents** sign in with a **JWT** from your identity provider (the Host is an OAuth 2.1 resource server: it publishes protected-resource
  metadata and a `WWW-Authenticate` challenge, so MCP clients that support authorization find your provider on their own) or with an
  **API key** (`my-host --new-api-key alice-agent`; give the key to the agent, put the hash in the file). Give each key a `Profile`
  naming an entry of the policy's `Agents`: the key then sees and may call only those tools ([agent profiles](POLICY.md#agent-profiles)).
  Scope `mcp:tools` (or your `RequiredScope`) is needed to call tools.
* **The approval page** shows, for each waiting action: why it waits (the rule, or the outside content the caller read first, with a
  link to that read), what kind of effect it has, what the caller did just before, and how the same tool's last actions were decided.
  Filters: only what I can decide, only high impact, expiring within 2 hours. Keys: `j`/`k` move, `a` approve (asks first), `x` reject
  (a reason is required to reject or request changes).
* **Reviewers sign in to the page with the company's identity provider** when the HTTP config's `Jwt` section has `Authority` and
  `PageClientId`. For Entra ID: register the Host as an API (Application ID URI such as `api://opentail-host`, a delegated scope
  `opentail.review`, app roles for groups), register the page as a **single-page application** with redirect URI `<PublicBaseUrl>/`
  and permission to that scope, then set `"Authority": "https://login.microsoftonline.com/<tenant>/v2.0"`, `"Audience":
  "api://opentail-host"` and `"PageClientId": "<page app id>"` (`PageScope` overrides the default `openid <Audience>/opentail.review`).
  The page uses the authorization code flow with PKCE (no client secret), checks the returned `state`, keeps the token in the tab's
  session storage only, and asks again when it expires. Its CSP allows connections only to itself and the identity provider.
* **Reviewers** can also sign in to the approval page and API (`/` and `/api`) with a reviewer **token** from the environment, or a JWT
  carrying the `opentail.review` scope (and `opentail.admin` for operators; groups as `group:name` roles):

  ```
  OPENTAIL_ADMIN_TOKENS="rita:<16+ chars>|review,admin,group:finance;jo:<16+ chars>|review"
  ```

  A token without a `|` part is both reviewer and admin (single-operator compatibility).
* **Administration on the same page** (admin role only; a reviewer sees the approvals and Modules and gets 403 from everything else).
  The **Admin** button opens:

  | Tab | Shows |
  |---|---|
  | Jobs | Everything waiting or running, whatever holds it: work items agents owe, continuations waiting to resume, and jobs in the work queues passed as `AdminOptions.WorkQueues`. Each row says what it is, who it is for and what created it (its source), with **Cancel** (it never runs again; a running one's worker loses it), **Run now** (a delayed queue job) and **Stop the source** (when the Host owns what keeps creating it: the parent work item or a continuation). Dead jobs below, with **Redrive** and **Delete**, and **Bring back dead jobs**: after an outage, every dead job that matches a resource (`db:*`), a reason, a source and a time, previewed first; queue jobs come back through their resource's limit and circuit, so they trickle. Per queue, a banner when it needs attention (a job waiting too long, dead letters piling up, a stuck job, an open circuit), and a **Resources** table: each concurrency key, how many run against its limit, how many wait, and its circuit. Running jobs show how long they have run, and **stuck** past their maximum run time. Each registered queue is also a `/healthz` check, `queue:{name}`, degraded (never failing) while it needs attention. Every change is audited with the admin's name. Goals are shown; queue bodies and work inputs are not. The one place on the admin page that changes something directly: stopping work is always the safe direction. |
  | Agents | Each agent profile, every tool it names with its risk and mode (and the mode after outside content), what it may ask for and whom it may hand work to. Tools the profile names but the policy denies are listed as `Deny`. |
  | People and keys | `People`, and the API keys as the last start recorded them: name, hash prefix, profile. Never the key. |
  | Policy versions | Every policy this database ran under; *Changes since* diffs one against the running policy, section by section. |
  | Health | The `/healthz` checks, the last maintenance sweep, startup warnings, shadow mode. |
  | Usage | The `usage_daily` rollups of the last 7 days (counts only). |
  | Telemetry | Metrics and recent traces recorded in this process (below). |
  | Audit | The newest 200 audit entries, each linked to its action. |
  | Proposals | Policy changes proposed, approved by a second admin and deployed by you ([SOP C1](SOP.md#c1-changing-the-policy)). |

  The same views are JSON under `/api/admin/...` (`overview`, `policy-versions`, `policy-versions/diff?from=`, `usage?days=`,
  `telemetry/metrics`, `telemetry/traces`, `audit?limit=`, `policy/current`, `proposals`, `jobs`, and `POST jobs/act` with `kind`,
  `id`, `action` and, for a queue job, `queue`; `POST jobs/redrive-matching` with `queue` (or `work`), `resource`, `reasonContains`,
  `source`, `deadSince` and `dryRun`); `/api/me` says whether the caller is an admin.
  Everything works in any browser; nothing here needs Teams.
* **TLS:** give Kestrel a certificate (`Kestrel__Certificates__Default__Path` / `__Password`), or terminate TLS in a reverse proxy and set
  `"TrustProxy": true` (only when nothing but the proxy can reach the port).
* The process **refuses to start** open beyond loopback, in plain http beyond loopback without `TrustProxy`, or without `PublicBaseUrl` when
  not on loopback.
* `/healthz` answers without a credential, with no detail.

Connect Claude Code, for example, with a project `.mcp.json`:

```json
{ "mcpServers": { "team": { "type": "http", "url": "https://approvals.example/mcp", "headers": { "Authorization": "Bearer <api key>" } } } }
```

## Notifications

`notifications.json` (see the sample): channels (`webhook` signed with HMAC-SHA256, `slack`, `teams`), routes (which events, which tools,
minimum risk), reminders. Secrets are environment-variable *names*. Messages carry no argument values unless a route sets
`IncludePreviewSummary` (a one-line preview, escaped so it cannot inject links or markup).

**How a decision was made:** every action records its decision trace: each check it passed (profile, identity level, policy,
rules, trust, taint, data flow, delay, queue), what each concluded, its evidence (rule names, labels, grant ids; never argument
values) and whether it changed the outcome. The review page shows it under *How this was decided*, with the hash of the policy the
action was decided under.

**Reads and exports:** opening an action on the review page (its arguments may hold personal data) is audited as `action_viewed`
with the reader; every export (`--export-review-outcomes`, `--export-shadow-cases`, `--export-audit`) is audited as `export` with
the operating-system account that ran it. An AI reviewer's review carries the SHA-256 of the action exactly as the Host handed it
over (`AiInputHash`); with `"AiReviewInput": "Full"` in the policy the input itself is kept with the action and purged with it.

**Access changes:** API keys live in the HTTP config, so each start compares them with the last ones seen and audits `key_added`,
`key_removed`, `key_rotated` and `key_profile_changed` (by name and a prefix of the hash, never the key). A new policy version is
compared with the one the database ran under before, and changes are audited as `profile_added`, `profile_changed`,
`profile_removed` and `people_changed`.

**Decision records:** when `RetentionDays` deletes finished actions (they hold arguments and results), a decision record of each
is kept in their place: tool, module version, requester, profile, session, the decision trace, the policy hash, every review (who,
groups, decision, reason, AI model and instruction version), the outcome and failure kind, the arguments' hash and result counts.
Never arguments or results. Each purge's `retention_purge` audit entry names the batch and the digest of its records, so they cannot
be altered without `--verify-audit` noticing. `AuditRetentionDays` (default 0: keep forever) deletes old decision records; it must be
zero or at least `RetentionDays`.

**Audit checkpoints:** a route with `"On": ["audit_checkpoint"]` receives the audit chain's newest entry and hash (`<seq>:<hash>`)
every hour and once at shutdown, on a webhook, Slack or Teams channel (not the Teams bot). That copy lives outside the database,
so `--verify-audit --checkpoint <seq:hash>` can later prove nothing was edited or removed up to it.

**Audit stream:** a route with `"On": ["audit_entry"]` sends every audit entry, as written, with its sequence number and chain
hashes, to a webhook channel (a log store such as Splunk, Microsoft Sentinel or Elastic behind an HTTP collector), signed like
every webhook. The notifier picks up new entries on each scheduler pass (every 30 seconds) and remembers how far it got across
restarts; a log store that is down catches up through the outbox's retries. Only webhook channels take it: every entry as a chat
message would bury the channel. With it, a copy of the trail exists outside the Host within a minute of being written.

**Teams, simplest** (no app to install, no public address needed): in the Teams channel choose **Workflows**, then the template *Post to a channel
when a webhook request is received*, and copy the URL it gives. Put it in an environment variable, and add:

```json
{ "PublicBaseUrl": "https://approvals.corp.example",
  "Channels": [ { "Name": "team", "Type": "teams", "UrlEnv": "TEAMS_WORKFLOW_URL" } ],
  "Routes": [ { "On": [ "queued", "reminder" ], "To": [ "team" ], "IncludePreviewSummary": true } ] }
```

**The Teams bot** (Approve, Approve with changes and Reject inside Teams; needs an app registration and a public HTTPS address). The
step-by-step guide for IT, with the app package script (`scripts/make-teams-app.ps1`), is [TEAMS-SETUP.md](TEAMS-SETUP.md). In short:

1. Create an **Azure Bot** (single tenant) with its Entra app registration and a client secret; set its messaging endpoint to
   `<public address>/api/teams/messages`. Package a Teams app for the bot (manifest with the bot id, personal and team scopes) and let IT
   publish it; reviewers install it (that is when the bot learns their chat).
2. In the HTTP config file add:

   ```json
   "Teams": { "AppId": "<bot app id>", "AppPasswordEnv": "TEAMS_BOT_SECRET", "TenantId": "<tenant id>",
              "ChatMaxRisk": "ExternalEffect", "DigestThreshold": 3,
              "Reviewers": { "<entra object id>": { "Name": "alice", "Groups": [ "finance" ] } } }
   ```

   and in the policy link each person's Teams identity to their AI agent:
   `"People": { "alice": [ "teams:<entra object id>", "key:alice-agent" ] }`.
3. In `notifications.json` add a channel `{ "Name": "bot", "Type": "teams-bot", "Target": "requester" }` (the chat of the person whose AI
   asked; or `"conversation:channel:<conversation id>"` for a channel the app is in) and route `queued` and `decided` events to it.

Each card shows what will happen, the risk, whether it waits after outside content, the draft's text fields (editable for **Approve with
changes**; the edited version is checked by the tool's preview before it runs), a reason box, and Approve, Approve with changes, Reject
and Open. More than `DigestThreshold` waiting actions in one chat become one digest card. A click counts only when the Bot Framework's
token is valid for this bot and service, the tenant is yours, the user is listed under `Reviewers`, and the card was signed for that
action and that chat; then the same rules as on the page apply (groups, required approvals, `SelfApproval`, expiry), and tools above
`ChatMaxRisk` show only Open. Cards update in place after a decision, wherever it was made. The bot's card signatures derive from its
secret: rotating the secret invalidates the buttons on older cards (Open still works).

Each waiting action arrives as a card: what will happen, its risk, whether it was held after outside content, and an **Open** button to
the review page at `PublicBaseUrl/#<action id>`. Nothing can be approved from the card: approving happens on the page, after sign-in, so
Teams never calls the Host and the page can stay inside the company network (`PublicBaseUrl` only has to work for the reviewers). The
workflow URL is a secret (anyone with it can post to the channel): keep it in the environment, never in the file.

Delivery is from a durable outbox in the same database, retried 30 s, 60 s, ... up to 1 h, dead after 6 attempts (`notification_dead` in
the audit trail; the queue itself is never affected). At least once: receivers should de-duplicate on `X-OpenTail-Event-Id` and reject old
`t=` timestamps in `X-OpenTail-Signature`. A pending action whose notification was lost (for example in a crash) is announced by the next
pass; actions pending longer than `ReconcileWindow` (6 h) when notifications are first switched on are not.

## Surviving a power cut

The database runs in WAL mode. By default (`OPENTAIL_DB_SYNCHRONOUS=normal`) a commit survives the Host process crashing or being
killed, but a power cut or an operating-system crash can roll back the most recent commits: the file stays consistent, and the last
few decisions (an approval, an audit entry, a queued action) may be lost. Set `OPENTAIL_DB_SYNCHRONOUS=full` and every commit that
returned survives a power cut too, at the cost of one disk flush per write; `extra` also syncs the directory. The setting applies
to the action store and the notification outbox (the Teams store already uses SQLite's default, which is full). Every Host process
on one database should use the same value. An unknown value stops startup.

A disk that acknowledges writes it has only cached (some consumer drives, some virtual disks) defeats any setting: for high stakes,
put `/data` on storage that honours flushes.

## What to back up and how to upgrade

* **Back up the SQLite file** (use `sqlite3 queue.db ".backup copy.db"` or copy it while the Host is stopped; WAL files travel with it).
* **Upgrades:** start the new version; if the database schema is older it is migrated, and a copy is taken first
  (`<db>.bak-v<old>-<timestamp>`). The migration is one-way: an older Host refuses a newer database, so to roll back, restore the copy.
* **Policy changes** take effect on restart; an approved proposal from the page is a policy file like any other ([SOP C1](SOP.md#c1-changing-the-policy)). Run `--replay-policy` against the real database first, and `--test-policy` if you keep cases.
* **Module upgrades:** a module built against a contract this Host does not support is refused at startup with both versions named.
  Within contract 1 any module version works with any Host version.
* **Removing a module:** set `"Enabled": false` in `modules.json` first (its pending actions expire, its promotions are cleared, its
  policy entries are tolerated), then remove the package. See [MODULES.md](MODULES.md#removing-a-module).
* **Rotating a module credential:** change the environment variable or file; it is re-read on the next use. OAuth tokens refresh
  themselves and are dropped on a 401.

## Watching it

`GET /healthz` (anonymous, any Host header) answers `ok`, `degraded` or `unhealthy` with each check's state and nothing else.
**Unhealthy, 503:** the maintenance sweep (recovery, expiry, delayed actions, retries, work, retention) has not finished for 3
minutes; an orchestrator should restart the Host. **Degraded, 200:** a check needs a person, such as `notifications` when a
notification created in the last day could not be delivered; restarting would not fix it.

**On the page, without a collector:** the review page's *Telemetry* tab shows every metric below since the process started (counters
per tag set; histograms with count, average, maximum and an approximate p95) and the last 500 spans, grouped by trace and drawn as a
waterfall: `action.submit`, `action.review`, `action.execute`, `maintenance.sweep`, `notification.deliver`, each linked to its action
(`opentail.action_id`). Memory is bounded (at most 2,000 tag combinations; older spans drop) and nothing leaves the process. It records
only while the page is served; `"RecordTelemetry": false` in `http.json` turns it off. It is not a history: export for that.

OpenTelemetry (`System.Diagnostics`; subscribe to the `OpenTail.Host` source and meter):

| Signal | Use |
|---|---|
| `opentail.host.actions` (tool, module, status, mode, tainted) | volume and outcomes |
| `opentail.host.pending_actions` | the queue; a number that only grows means nobody is looking |
| `opentail.host.time_to_decision` | how long people take |
| `opentail.host.execution_duration` | slow tools (default lease is 5 minutes; a longer-running tool would be treated as interrupted after a restart) |
| `opentail.host.backoff` (tool, outcome: delayed or refused) | capacity limits being hit; tune `Capacity` from it |
| `opentail.host.tasks` (tool, reason: queued, delayed, running) | calls answered with an MCP task instead of the pending JSON |
| `opentail.host.work` (event, kind) | work items and continuations: created, claimed, done, failed, dead, expired, parked, woken |
| `opentail.host.circuits` (tool, event) | routes paused, reopened, or that would have been paused (report only) |
| `opentail.host.result.records`, `opentail.host.result.bytes` (tool, module, profile) | how much successful actions return; never tagged by identity |
| audit trail (`audit` table) | who decided what and when, with no argument values |
| usage rollups (`usage_daily` table, `--usage`) | per identity, tool and day: calls, outcomes, records, distinct entities, recipients, bytes. Kept 13 months, past `RetentionDays`, so adaptive limits will have a baseline. No argument values or results |

Useful audit events: `policy_decision`, `queued`, `approved`, `approval_recorded`, `rejected`, `changes_requested`, `edit_refused`,
`review_refused`, `execution_succeeded`, `execution_failed`, `interrupted`, `expired`, `notification_dead`, `retention_purge`,
`trust_promoted`, `trust_demoted`, `event_sink_error`; and since the audit, work and failures work: `policy_loaded`,
`profile_added`/`profile_changed`/`profile_removed`/`people_changed`, `key_added`/`key_removed`/`key_rotated`/`key_profile_changed`,
`action_viewed`, `export`, `decision_records_purge`, `retry_automatic`, `circuit.opened`/`circuit.would_open`/`circuit.reopened`/
`circuit.closed`, `work.created`/`work.claimed`/`work.done`/`work.failed`/`work.lease_lost`/`work.dead`/`work.expired`,
`continuation.parked`/`continuation.woken`, `inspection.opened`/`inspection.to_operators`, `redrive.approved`,
`credential.suspicious`, `work.runner_started`; and since the administration views: `policy_proposed`, `policy_proposal_approved`/`policy_proposal_rejected`/`policy_proposal_withdrawn`/`policy_proposal_applied`; and from the Jobs view: `continuation.cancelled`, `job.cancel`, `job.run_now`,
`job.redrive`, `job.delete`, `job.stop_source`, `job.redrive_matching` (queue jobs; work items keep their `work.*` and `redrive.approved` events).

What to do about each, step by step, is in [SOP.md](SOP.md).

## When something looks wrong

| Symptom | Look at |
|---|---|
| "My agent's tool is not listed" | The policy has no entry for it (deny by default; Read tools of configured modules too, unless `AllowDefaultRead`). `--list-modules`. |
| "Cannot start: Module 'x' needs type 'y'" | The package providing `y` is not referenced by the deployment (or was trimmed away). The message lists the types present. |
| "Cannot start: ... environment variable X is not set" | A mapped credential source is missing. Describe commands (`--list-modules`) work without it. |
| "may not reach https://..." in a tool error | The module tried a host that is not in its `Network.Hosts` (or plain http to a host listed without a scheme). |
| "Everything is held for approval" | The caller is tainted (it read untrusted content within `TaintWindow`). That is the design; approve, or wait out the window. |
| "A rule I wrote never auto-approves" | `--explain` with the real arguments: look for "does not account for argument(s) ..." or an unknown test. |
| "A rule denies everything" | A misspelt path in a `Deny` rule is unknown on every call, which denies. Fix the path. |
| "401 / 403 from /mcp" | 401: no or wrong credential. 403: valid credential without the tool scope (a reviewer token cannot call tools). |
| "403 from /api" | The credential is not a reviewer (an API key never is), or not in the required group, or the action forbids approving your own request. |
| "Reviewers got no message" | `opentail.host.pending_actions`, the audit trail for `notification_dead`, and the outbox table (`status`, `last_error`). |
| "The queue is full for an agent" | `MaxPendingPerRequester`: decisions are waiting. |
| "After a crash an action says interrupted" | The tool is not idempotent by key, so the Host did not re-run it. A reviewer decides (Retry). |

## Known limits

One node; SQLite; no clustering. The approval page has been exercised with a script and automated header checks but not walked through by
a second person in a real browser. MCP tasks are implemented for queued, delayed and long-running calls (clients that opt in), tested
with the SDK's own client only; their push notifications and Slack approve-buttons are not implemented. Modules run inside the Host process unless
`modules.json` isolates them (a worker process per module; containment needs the operator's account and network setup). The
GitHub Issues module has been tested against a stateful fake of the GitHub API over real HTTP, not yet against github.com. See [SECURITY.md](SECURITY.md).
