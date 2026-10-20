# OpenTail Host

An MCP server that exposes **only the modules you load** and decides, per tool, whether a call
**runs now**, **waits for a reviewer**, or is **denied**. It runs on your machine over stdio, or as a signed-in server for a small
team. The design and the reasons are in [`../MCP-HOST-PLAN.md`](../MCP-HOST-PLAN.md) (v1),
[`../MCP-HOST-V2-PLAN.md`](../MCP-HOST-V2-PLAN.md) (team features) and [`../MCP-HOST-V3-PLAN.md`](../MCP-HOST-V3-PLAN.md) (modules as
bricks), each with its design review and implementation log.

| Read | For |
|---|---|
| [MODULES.md](MODULES.md) | enabling modules from `modules.json`, credentials, a module's suggested policy, writing a brick |
| [DEPLOY.md](DEPLOY.md) | running it at a public HTTPS address: the container image, a tunnel for trials, Docker Compose with Caddy for a server |
| [TEAMS-SETUP.md](TEAMS-SETUP.md) | for IT: the approvals app in Microsoft Teams (bot registration, app package, Host config, checks) |
| [PROFILES.md](PROFILES.md) | integration profiles: turn an OpenAPI document into a curated, safe module with one JSON file, no code |
| [POLICY.md](POLICY.md) | writing the policy file and conditional rules |
| [OPERATIONS.md](OPERATIONS.md) | running it for a team: HTTP, sign-in, notifications, backups, monitoring, troubleshooting |
| [SECURITY.md](SECURITY.md) | what it protects against and, honestly, what it does not |
| [PACKAGING.md](PACKAGING.md) | the 21 NuGet packages, the frozen module contract, versioning and releases |

## The model in one minute

- A **module** (`OpenTail.Modules.*`) declares tools and their *risk* (`Read`, `Write`, `Destructive`, `ExternalEffect`).
  It never reads policy.
- The operator writes a **policy file**. For each tool: `Execute`, `Queue` or `Deny`. A tool with no entry is
  denied (and not even advertised to the model); only `Read` tools can default to `Execute`. Optional **rules** refine a tool by its
  arguments, the time and a budget ("mail to our own domain runs; anything else waits; never to a competitor"), and fail closed: input
  the Host cannot read can only make a call *more* careful.
- **Taint:** once a caller has read untrusted content (mail, web, files, API responses, database rows), every
  `Destructive`/`ExternalEffect` tool is forced to `Queue` for that caller, whatever the policy says, for a window of time and across
  sessions (a fresh session does not launder it, and neither does a restart). Calls with no session count as tainted.
- A queued call returns `pending_approval` at once. The AI sees `succeeded`, `failed`, `rejected` (with the reason) or
  `changes_requested` (then `resubmit_action`). If its turn ended before the decision, the news rides on its next call (`notices`),
  `get_updates` lists it, and `wait_for_action` waits a few seconds.
- Reviewers use the approval page (or an AI reviewer behind `ReviewerRunner`). They see the real arguments, a preview of the effect and
  whether the caller was tainted, and can edit before approving. Actions can require a **reviewer group**, **several approvers**, or
  **someone other than the requester**.
- Reviewers are **told** (signed webhook, Slack) with a link to the approval page, and reminded if it sits.
- State is one SQLite file. Approved-but-unstarted and interrupted actions are recovered on startup; a
  non-idempotent action that was interrupted is **never re-run silently**.
- Agents and reviewers are different kinds of credential: **an API key can never approve**, and a reviewer token can never call tools.
- Each agent's API key names an **agent profile**: the agent sees and may call only that profile's tools, and no prompt can change it.

## Working on the Host

`OpenTail.Host.slnx` holds the Host, the modules, the profiles, the samples, the server and the tests. `dotnet build OpenTail.Host.slnx`
and `dotnet test --solution OpenTail.Host.slnx` run in seconds. The Connectivity libraries come as packages from GitHub Packages, which
needs a token to read: set `GITHUB_PACKAGES_TOKEN` to a token with `read:packages` (see `nuget.config`).

## Build a deployment

Reference the module packages you want and enable them in `modules.json` ([MODULES.md](MODULES.md)); the program can be one line:

```csharp
return await HttpDeploymentRunner.RunAsync(args, "my-host");
```

```
my-host --policy policy.json --modules modules.json --list-modules     # what is loaded, and what the policy does with each tool
my-host --policy policy.json --modules modules.json --adopt-policy github
```

Or add modules in code, which also works for the ones that need objects (adapters, connections):

```csharp
return await HttpDeploymentRunner.RunAsync(args, "my-host", builder =>
{
    builder.AddModule(new FilesModule(new FilesModuleOptions { Root = @"C:\data\shared" }));
    builder.AddModule(OpenApiModule.Create(File.ReadAllText("crm.json"), new OpenApiModuleOptions
    {
        Namespace = "crm",
        AuthTokenEnvVar = "CRM_API_TOKEN",   // the NAME of the variable; the token never reaches the model
    }));
});
```

On your own machine (stdio, approval page on loopback):

```
my-host --policy policy.json --db queue.db --admin-port 5599
OPENTAIL_ADMIN_TOKENS="alice:<16+ char token>;bob:<16+ char token>"
```

For a team (signed-in HTTP, notifications):

```
my-host --policy policy.json --db queue.db --http http.json --notifications notifications.json
my-host --new-api-key alice-agent        # prints the key once, and the hash to put in http.json
```

Samples with policy files: `OpenTail.Host.Sample.EmailOnly`, `OpenTail.Host.Sample.FilesOpenApi`, `OpenTail.Host.Sample.SqlServer` (stdio) and
`OpenTail.Host.Sample.TeamHttp` (HTTP, rules, notifications). `scripts/check-host-deployments.ps1` publishes them and checks that nothing
from the heavy OpenTail stack ships, that all 15 packages pack with one version, and that projects outside the repo (one naming no module in code) can restore and run them from the
packed feed.

## Modules

| Package | Tools | Notes |
|---|---|---|
| `OpenTail.Modules.Social` | `social.search`, `get_post`, `get_thread`, `sentiment` | Reddit; read only; everything taints; authors pseudonymised; from `modules.json` |
| `OpenTail.Profiles.YouTube`, `OpenTail.Profiles.X` | `search`, `get_video`, `get_comments` / `search`, `get_post` | integration profiles as packages (no code) for the `openapi` module: read only; authors pseudonymised; see [PROFILES.md](PROFILES.md#shipped-profiles) |
| `OpenTail.Modules.ServiceNow` | `servicenow.search_records`, `get_record`, `list_journal`, `add_comment`, `create_record`, `update_record`, `resolve_incident` | allow-listed tables and fields; every write is ExternalEffect (resolve Destructive), so reading a ticket holds the next write for review; from `modules.json` |
| `OpenTail.Modules.Storage` | `storage.list`, `storage.read` | one S3 bucket, a prefix allow-list, capped reads with text extraction; read only; from `modules.json` |
| `OpenTail.Modules.GitHubIssues` | `github.search_issues`, `get_issue`, `list_comments`, `add_comment`, `create_issue`, `update_issue`, `close_issue` | allow-listed repositories; every write is ExternalEffect, so reading an issue holds the next write for review; from `modules.json` |
| `OpenTail.Modules.Files` | `files.list`, `files.read`, `files.write` | one root, no links followed, write previewed; from code or `modules.json` |
| `OpenTail.Modules.Csv` | `csv.describe`, `csv.read_rows` | one root, quoted fields and embedded newlines |
| `OpenTail.Modules.Email` | `email.list_recent`, `email.read`, `email.send` | over `IInboxAdapter`/`IEmailSender` (IMAP, Gmail, Graph) |
| `OpenTail.Modules.Calendar` | `calendar.*` | over `ICalendarAdapter`; create/delete/respond are previewed |
| `OpenTail.Modules.Sql` | `sql.schema`, `sql.query`, optional `sql.write`, `sql.delete` | seatbelt guard + parameters; **use a least-privilege login** |
| `OpenTail.Modules.Web` | `web.fetch` | host allow-list, public addresses only, redirects re-checked |
| `OpenTail.Modules.Email.Imap` | the Email tools | provider: IMAP and SMTP; `email-imap` in `modules.json` |
| `OpenTail.Modules.Microsoft365` | the Email and Calendar tools | provider: Microsoft Graph; `email-graph`, `calendar-graph` |
| `OpenTail.Modules.Google` | the Email and Calendar tools | provider: Gmail and Google Calendar; `email-gmail`, `calendar-google` |
| `OpenTail.Modules.Calendar.Ics` | the Calendar read tools | provider: an ICS feed; `calendar-ics` |

Every module can be enabled from `modules.json` under any id (the tool prefix); see [MODULES.md](MODULES.md#the-modules-in-this-repository).
| `OpenTail.Modules.OpenApi` | one per operation | verb sets the risk; path/query values cannot change the host; from `modules.json` only named operations are exposed |

Writing your own module needs only `OpenTail.Host.Abstractions`: a factory, a module, each tool's risk, and an `AIFunction` per tool.
[MODULES.md](MODULES.md#writing-a-brick) has a complete example and the rules (anything other people can see is `ExternalEffect`;
mark outside content `ReadsUntrusted`; preview every write).

## Sandbox mode

`OpenTailHostBuilder.UseSandbox(IToolSandbox)` builds a Host whose tool calls are all answered by the sandbox: no module code runs.
Policy, taint, previews, queueing and audit are unchanged, so an agent sees exactly the Host it would see in production. The built-in
`RecordingSandbox` records every call above Read (an approved `mail.send` is recorded, not sent) and answers reads from a function you
pass: a scenario's fixtures for the skills workbench, or `RecordingSandbox.ReadLive` (the real module, Read tools only) for shadow
runs. Load modules for it in `ModuleLoadMode.Describe`, which needs no credentials. It is a test harness, not a security sandbox: it
stops tools from running at all, and says so in `StartupWarnings`.

## What this is not

It is guarded execution under the operator's OS identity, not a sandbox, and a module runs inside the Host process: only install modules
you trust. The SQL guard and the redaction of error text
are best-effort; the real controls are the database login, the allow-lists and the review step. It is a single node (SQLite), has not had an
independent security audit, and its approval page has not been walked through by a second person in a real browser. See
[SECURITY.md](SECURITY.md).

## Licence

The Host, its modules and its integration profiles are licensed under the **PolyForm Strict License 1.0.0**: see
[`../LICENSE-HOST.md`](../LICENSE-HOST.md). Personal and other noncommercial use is permitted; any commercial use, including inside a
business, needs a commercial licence. The NuGet packages carry the licence and ask for acceptance. The shared `OpenTail.Connectivity.*`
libraries keep the repository's licence.
