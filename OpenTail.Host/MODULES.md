# Modules as bricks

A module is one NuGet package that adds a capability to the Host: GitHub issues, files, an imported API. You add it by **referencing
the package and naming it in `modules.json`**. Nothing else changes, and adding a module never widens access on its own: the policy
still decides, and a tool with no policy entry is not offered to the model.

## Enabling a module

1. Reference the package in the deployment project (`dotnet add package OpenTail.Modules.GitHubIssues`). That is the trust decision:
   only code the deployment was built with can be enabled. The Host never loads DLLs from a folder.
2. Describe the instance in `modules.json`:

   ```json
   { "Modules": [
       { "Type": "github-issues", "Id": "github",
         "Network": { "Hosts": [ "api.github.com" ] },
         "Credentials": { "github": { "Env": "GITHUB_TOKEN", "Kind": "Bearer", "Host": "api.github.com" } },
         "Config": { "Repositories": [ "acme/support" ], "AllowedLabels": [ "bug", "question" ] } },
       { "Type": "files", "Id": "docs", "Config": { "Root": "D:/shared/docs" } }
   ] }
   ```
3. Look before you run: `my-host --policy policy.json --modules modules.json --list-modules` shows every module, its version and contract,
   each tool with its risk and what the policy does with it, and warnings.
4. Adopt the module's suggested policy, or write your own (below).
5. Start with `--modules modules.json` (both runners accept it). Each module is listed on stderr at startup.

A deployment program can name no modules at all and rely on the file: `return await HttpDeploymentRunner.RunAsync(args, "my-host");`.
Modules added in code with `builder.AddModule(...)` still work and can be mixed with configured ones.

| Field | Meaning |
|---|---|
| `Type` | The factory, as the package names it (`files`, `openapi`, `github-issues`). |
| `Id` | The instance: lower case, no dots. Every tool is named `<id>.<tool>`; the policy fragment is `policy.d/<id>.json`. Two instances of one type need two ids. |
| `Enabled` | `false` switches the module off (see *Removing a module*). |
| `AllowDefaultRead` | Off by default: the module's Read tools need explicit policy entries, even if `DefaultReadMode` is `Execute`. The module labels its own tools as Read; turn this on only when you have checked that. |
| `Network.Hosts` | Hosts the module may reach: `api.example.com` (https, default port), `*.example.com` (subdomains, not the apex), or an origin `https://ghe.corp:8443` / `http://127.0.0.1:5123`. When the module's manifest declares hosts, every entry here must fall within them: you can narrow, never widen. |
| `Network.AllowPrivateNetwork` | Allow names that resolve to private, loopback or link-local addresses (an on-premises server). An IP-literal origin you list explicitly is always allowed. |
| `Network.Timeout`, `Network.MaxResponseBytes` | Per request; defaults 30 s and 4 MB. |
| `Credentials` | Name to source, see below. A module can only ask for names mapped under its own entry. |
| `Config` | The module's own settings; see each module. Unknown settings are refused. Relative paths are relative to `modules.json`. |
| `FactoryAssemblies` (top level) | Extra assembly name prefixes to search for factories besides `OpenTail.Modules.`, for third-party bricks (`"Contoso.Bricks."`). Still only assemblies the deployment references. |

A module that is listed but whose package is not referenced stops startup with the list of types that are present. A package that is
referenced but not listed is reported and stays off.

## Credentials

```json
"Credentials": {
  "github": { "Env": "GITHUB_TOKEN", "Kind": "Bearer", "Host": "api.github.com" },
  "crm":    { "OAuth2": { "TokenUrl": "https://login.example/oauth/token", "ClientIdEnv": "CRM_ID", "ClientSecretEnv": "CRM_SECRET", "Scope": "crm.read" },
              "Host": "api.crm.example" },
  "legacy": { "File": "secrets/legacy.key", "Kind": "Header", "HeaderName": "X-Api-Key", "Host": "legacy.example" },
  "db":     { "Env": "DB_PASSWORD", "Kind": "Secret" }
}
```

* Exactly one source: `Env` (re-read on every use, so rotating the variable needs no restart), `File` (re-read on every use), or `OAuth2`
  client credentials (cached until a minute before expiry, refreshed by one caller at a time, dropped when a request comes back 401).
  `"ClientAuthentication": "Basic"` sends the client id and secret as a Basic header instead of in the form (Reddit needs this).
* `Kind`: `Bearer`, `Basic` (with `UserName` or `UserNameEnv`), `Header` (with `HeaderName`), or `Secret` (handed to the module for
  non-HTTP use such as a database login).
* `Host` binds an HTTP credential to one host pattern, which must be one of the module's `Network.Hosts`. Requests to any other host
  never carry it, and a redirect that leaves the host drops it.
* The module never learns where a secret came from. Every mapped secret is redacted by exact value from errors, the audit trail and
  anything that flows back to the model: from startup (when the Host checks the sources exist), and again each time one is read.
* `--explain`, `--list-modules`, `--adopt-policy` and `--test-policy` build modules **without** reading credentials. Starting the server
  checks that every mapped variable and file exists and refuses to start otherwise.

## Policy for a module

Every module can suggest a policy. Look at it, then adopt it:

```
my-host --policy policy.json --modules modules.json --adopt-policy github
```

* It is written to `policy.d/github.json` next to your policy file. Your own file is never rewritten; add `"Include": ["policy.d"]` to it
  once (the command tells you if it is missing).
* A fragment may only contain `Tools`, and only tools of the module it is named after. A suggestion that names another module's tools,
  sets global keys (`TaintWindow`, `DefaultReadMode`, `People`, ...), or names tools the module did not register is refused.
* A suggestion that would run anything but a Read tool without review is refused unless you pass `--allow-execute` after reading it.
* Running it again shows the difference and changes nothing unless you pass `--replace`. The same tool may not have entries in two places.

`--lint` (also printed by `--list-modules`) warns about tools that are denied because nothing names them, Read tools that run because of
`DefaultReadMode`, tools that run without review, and especially a **Write** tool that can run without review in a module that returns
outside content. Taint holds only `Destructive` and `ExternalEffect` tools, so text an outsider wrote could trigger that Write tool.

Check expected decisions in CI with a test file:

```json
[ { "Name": "triage comments run", "Tool": "github.add_comment", "Args": { "repo": "acme/triage", "number": 1, "body": "x" }, "Expect": "Execute" },
  { "Name": "not after reading an issue", "Tool": "github.add_comment", "Args": { "repo": "acme/triage", "number": 1, "body": "x" }, "Tainted": true, "Expect": "Queue" } ]
```

`my-host --policy policy.json --modules modules.json --test-policy cases.json` exits 1 if any case does not match.

## Removing a module

Set `"Enabled": false` first. The Host then tolerates the module's policy entries (with a warning) instead of refusing to start, expires
its pending actions (`module_removed`), and clears any trust promotions of its tools, so reinstalling starts again from the policy file.
Approved actions that had not started fail as `unknown_tool`. Then remove the package, and later the entry and the fragment.

## The modules in this repository

| Type | Package | Tools | Notes |
|---|---|---|---|
| `github-issues` | `OpenTail.Modules.GitHubIssues` | `search_issues`, `get_issue`, `list_comments` (Read, outside content); `add_comment`, `create_issue`, `update_issue` (ExternalEffect); `close_issue` (Destructive) | Allow-listed repositories only. Search qualifiers that reach other repositories, organisations or users are refused and the allowed repositories are added by the module. `update_issue` cannot change state. Writes carry a hidden marker so a retry after a crash does not post twice. Rate limits fail as retryable. GitHub Enterprise via `ApiBaseUrl`. |
| `servicenow` | `OpenTail.Modules.ServiceNow` | `search_records`, `get_record`, `list_journal` (Read, outside content); `add_comment`, `create_record`, `update_record` (ExternalEffect); `resolve_incident` (Destructive) | Table API, allow-listed `Tables` (default `incident`) and `WritableFields`; state changes only through `resolve_incident`; optional `AssignmentGroups` allow-list; `AllowCustomerComments: false` allows work notes only. Search takes filters (text, state, priority, group, updated since), never a raw encoded query. Creates carry a `correlation_id`, so a retry after a crash finds the record; an interrupted comment waits for a reviewer. Credential `servicenow`: OAuth2 client credentials or Basic for an integration account. |
| `storage` | `OpenTail.Modules.Storage` | `list` (Read), `read` (Read, outside content) | One S3 bucket (or an S3-compatible store through `ServiceUrl` and `ForcePathStyle`), fixed by configuration; `Prefixes` (required) is the allow-list, compared exactly, checked before any request. Reads stop at `MaxReadBytes` (256 KB), `.gz` is decompressed with a cap, PDFs and Office files come back as text; archives and objects that need a restore are refused, cold-storage reads only with `ReadColdStorage`. Credentials `access-key` and `secret-key` (both `Secret`), or neither for the ambient identity (an IAM role, preferable). No write, delete or presign tools. Not yet run against real S3. |
| `social` | `OpenTail.Modules.Social` | `search`, `get_post`, `get_thread` (Read, outside content); `sentiment` (Read, local) | Reddit (`Config.Reddit`, optional `Subreddits` allow-list). Credential `reddit`: app-only OAuth2 with `"ClientAuthentication": "Basic"`. Authors and @mentions are pseudonyms unless `PseudonymiseAuthors: false`; map `pseudonym-key` to keep them stable. No posting. Results stay in the action store: set `RetentionDays`. YouTube and X are profiles (next rows). |
| `openapi`, `"Profile": "package:OpenTail.Profiles.YouTube/youtube"` | `OpenTail.Profiles.YouTube` | `search`, `get_video`, `get_comments` (Read, outside content) | Integration profile (source in `opentail/profiles/youtube`). Credential `youtube`: `Kind: Header`, `HeaderName: X-Goog-Api-Key`. Optional `Channels`. See [PROFILES.md](PROFILES.md#shipped-profiles). |
| `openapi`, `"Profile": "package:OpenTail.Profiles.X/x"` | `OpenTail.Profiles.X` | `search`, `get_post` (Read, outside content) | Integration profile (source in `opentail/profiles/x`). Credential `x`: Bearer (a tier with recent search). See [PROFILES.md](PROFILES.md#shipped-profiles). |
| `openapi`, `"Profile": "package:OpenTail.Profiles.Jira/jira"` | `OpenTail.Profiles.Jira` | `search_issues`, `get_issue`, `list_comments` (Read, outside content) | Integration profile. Credential `jira`: `Kind: Basic` with the account's email as `UserName` and an API token. `ServerUrl` is the site; `Projects` limits searches. See [PROFILES.md](PROFILES.md#jira-profilesjira). |
| `openapi`, `"Profile": "package:OpenTail.Profiles.Confluence/confluence"` | `OpenTail.Profiles.Confluence` | `search_pages`, `get_page` (Read, outside content) | Integration profile. Credential `confluence`: `Kind: Basic`, email as `UserName`, API token. `ServerUrl` is the site; `Spaces` limits searches. See [PROFILES.md](PROFILES.md#confluence-profilesconfluence). |
| `files` | `OpenTail.Modules.Files` | `list`, `read` (Read), `write` (Write) | One root. `read` returns PDF, Office and OpenDocument files as their text (whole files up to `MaxDocumentBytes`, 10 MB; text cut at `MaxReadBytes`); `ExtractDocuments: false` turns it off. `write` is Write risk: queue it when untrusted text can reach the model. |
| `openapi` | `OpenTail.Modules.OpenApi` | one per named operation, or the tools of a profile | `Config.Operations` is required: nothing is exposed unless named. Token as credential `api`. Hosts are whatever you list. Or `Config.Profile`: an integration profile (spec plus JSON) that names the tools, their risk and previews, and limits each argument; its hosts and credential become the manifest. See [PROFILES.md](PROFILES.md). |
| `csv` | `OpenTail.Modules.Csv` | `describe`, `read_rows` (Read) | One `Root`; `Delimiter`. Read only. |
| `skills` | `OpenTail.Modules.Skills` | `find`, `get` (Read, the business's own content); `publish` (ExternalEffect) | Serves published skill versions from `Store` (one folder per version, `labels.json`), at `Label` (default `production`). `publish` adds a version and moves a label, which is also how a rollback is done; its preview is the change request (diff, reason, scenario report). Suggested policy: publish queued, `Reviewer: Human`, `SelfApproval: Forbid`. Optional `PolicyFolder`: `get` warns when a policy changed since the skill was tested. Every `get` is audited as `skill_served`. Adds a line to the server instructions. Use the id `skills`. Skills are tested in the skills workbench, a separate repository. Reference: [OpenTail.Modules.Skills/README.md](../OpenTail.Modules.Skills/README.md); setup and publishing: [guides/skills.md](../docs/guides/skills.md). |
| `web` | `OpenTail.Modules.Web` | `fetch` (Read, outside content) | Reaches only the entry's `Network.Hosts` (the Host's guarded client enforces them). |
| `sql` | `OpenTail.Modules.Sql` | `schema`, `query` (Read); `write` (Write) and `delete` (Destructive) with `AllowWrites` | `Provider`: `sqlite` built in; `sqlserver` (also Fabric and Synapse, [guide](../docs/guides/fabric-synapse.md)), `postgres`, `redshift` ([guide](../docs/guides/redshift.md)), `mysql` and `mariadb` (both through `MySqlConnector`), `snowflake` when the deployment also references `Microsoft.Data.SqlClient`, `Npgsql`, `MySqlConnector` or `Snowflake.Data`; `oracle` when it references `OpenTail.Modules.Sql.Oracle` ([guide](../docs/guides/oracle.md)); `databricks` when it references `OpenTail.Modules.Sql.Databricks` (the SQL Statement Execution API over the guarded HTTP client, so list the workspace in `Network.Hosts`; [guide](../docs/guides/databricks.md)); or `ProviderFactory`. No password in `ConnectionString`: map the `password` (or `connection`) credential. `schema` and `query` always use a read-only connection (SQLite: `Mode=ReadOnly`; Postgres and MySQL: also inside `START TRANSACTION READ ONLY`, `ReadOnlyQueryTransaction` to change). With `AllowWrites` on a server database, `write` and `delete` need a second login: `WriteConnectionString` with the `write-password` credential, or the `write-connection` credential (`SharedLoginForWrites: true` opts out, unsafe). The guard refuses quoting it cannot model for the provider's dialect: Postgres `$` and `E'...'`, backslashes in literals (Postgres, MySQL), MySQL `#`, `/*!` and `--` without a space, nested comments, Snowflake `//`, `$` and `;` inside `[ ]`, Oracle `q'...'` and `DBMS_`/`UTL_` packages, Databricks `r'...'`, `${...}`, `http_request` and `read_files`. Snowflake is read-only (no `AllowWrites`), signs in with the `private-key` credential (key pair), binds `@name` by position, adds `LIMIT MaxRows + 1` to a query without one and tags each query `opentail:<action id>` ([Snowflake guide](../docs/guides/snowflake.md)). `schema` returns `table_schema, table_name, column_name, data_type` (Postgres without its system schemas, MySQL for the connected database only), takes an optional `table`, stops at `MaxSchemaRows` (2000), and `Schemas` limits what it reports. Previews on MySQL are not trial-run while the database has tables that ignore rollback (not InnoDB). See [SQL databases](#sql-databases). SQL Server, Postgres, MySQL 8.4, MariaDB 11.4 and Oracle Database Free are tested against real servers in the `sql-servers` workflow; Snowflake is **not yet tested against a real account**. |
| `email-imap` | `OpenTail.Modules.Email.Imap` | `list_recent`, `read` (Read, outside content); `send` (ExternalEffect) unless `AllowSend: false` | `UserName`, `Imap.Host`, `Smtp.Host`; credential `password` (an app password), optional `smtp-password`; `AllowedRecipientDomains`. `LookbackDays` (default 365): how far back `list_recent` and `read` search; the IMAP adapter's own default is 30 days. |
| `email-graph` | `OpenTail.Modules.Microsoft365` | as `email-imap` | `TenantId`, `ClientId`, `Mailbox`; credential `client-secret`. Application permissions Mail.Read (+ Mail.Send). |
| `calendar-graph` | `OpenTail.Modules.Microsoft365` | `list_calendars`, `list_events` (Read); `create_event`, `respond` (ExternalEffect), `delete_event` (Destructive) unless `ReadOnly` | Same settings and credential as `email-graph`; `AllowedAttendeeDomains`. Calendars.Read (or ReadWrite). |
| `files-graph` | `OpenTail.Modules.Microsoft365` | `search`, `list`, `read` (Read, outside content) | `TenantId`, `ClientId`, `Drives` (required: drive ids of the libraries or OneDrives it may reach); credential `client-secret`. Application permission `Sites.Selected`, with read granted to those sites only. `read` returns the text of Word, PDF, Excel and PowerPoint files (whole files up to `MaxFileBytes`, 10 MB; text cut at `MaxTextChars`). Reads with the app's access, not the asker's. **Not yet run against a real tenant.** |
| `pages-graph` | `OpenTail.Modules.Microsoft365` | `list_pages`, `read_page` (Read, outside content) | SharePoint site pages (the modern wiki). `TenantId`, `ClientId`, `Sites` (required: site ids `host,guid,guid`); credential `client-secret`; `Sites.Selected` with read on those sites. A page's text is its text web parts, in layout order. **Not yet run against a real tenant.** |
| `email-gmail` | `OpenTail.Modules.Google` | as `email-imap` | `ClientId`, `Account`; credentials `client-secret` and `refresh-token` (Secret). Consent scopes gmail.readonly (+ gmail.send). |
| `drive-google` | `OpenTail.Modules.Google` | `search`, `list`, `read` (Read, outside content) | `SharedDrives` (required: shared drive ids); credential `service-account` (the JSON key, Secret). Add the service account as a Viewer of those drives. A file outside them is refused after its metadata is read, even if the account can see it. Docs and Slides are exported as text, Sheets as CSV; other files are downloaded (up to `MaxFileBytes`, 10 MB) and their text extracted. **Not yet run against Google.** |
| `calendar-google` | `OpenTail.Modules.Google` | as `calendar-graph` | Same settings and credentials as `email-gmail`; `ReadOnly`, `AllowedAttendeeDomains`. Scope calendar.readonly (or calendar). |
| `calendar-ics` | `OpenTail.Modules.Calendar.Ics` | `list_calendars`, `list_events` (Read) | `Url`, or the `url` credential when the feed URL carries a token. Fetched through the guarded client: list its host. |

`Email` and `Calendar` hold the tools and know no mail or calendar service; a provider package (`Email.Imap`, `Microsoft365`, `Google`,
`Calendar.Ics`) completes them, so a deployment downloads only the client library it uses. IMAP, SMTP, the Graph and Google SDKs and database drivers
open their own connections: they do not go through the Host's HTTP guard (each manifest says so). All of these modules can still be added
in code with `builder.AddModule(...)`, as before.

### SQL databases

The module connects with the logins you give it; the logins are the boundary, the guard is a seatbelt. `OpenTail.Host.Sample.SqlServer`
is a whole deployment for SQL Server (read login, optional write login, a policy for each). Set up:

- **Two logins with `AllowWrites`.** The main login only reads (it serves `schema` and `query`); the write login has only
  `INSERT`, `UPDATE` and `DELETE` on the tables the AI may change. Then a statement the guard misreads still cannot write.
- **Postgres:** a role with `CONNECT`, `USAGE` on the schemas and `SELECT` on the tables; not a superuser, and none of
  `pg_read_server_files`, `pg_write_server_files`, `pg_execute_server_program`, `pg_signal_backend`. As server-side backstops,
  `ALTER ROLE agent_ro SET default_transaction_read_only = on` and `ALTER ROLE agent_ro SET statement_timeout = '15s'`. TLS:
  `SSL Mode=VerifyFull`. A write preview advances sequences and fires triggers; triggers that reach outside the database (for
  example `dblink`) then run during the preview, so set `PreviewByRollback: false` if you have them.
- **MySQL:** `SELECT` on the one database only; no `FILE`, `PROCESS` or `SUPER`. Set `max_execution_time` for the login's
  sessions. TLS: `SslMode=VerifyFull`. Add `ConvertZeroDateTime=true` to the connection string, or zero dates read as
  `<unreadable value (datetime)>`.
- **SQL Server:** a login mapped to `db_datareader` only (and a second one with `INSERT`, `UPDATE`, `DELETE` grants for writes),
  `Encrypt=true` and a trusted certificate.
- **Timeouts:** `CommandTimeoutSeconds` (15) bounds each command; a preview holds the write's row locks for that long at most.
- **Error messages** from the database can name the server, database and login. Previews pass them on cut to 300 characters; a
  failing tool call reports the driver's message. Treat them as visible to the model and to reviewers.
- **Values:** dates are ISO 8601, numbers without the culture, Postgres arrays and hstore as JSON, `NaN` and infinities as text;
  a value the driver cannot read becomes `<unreadable value (type)>` instead of failing the query.

GitHub Issues config: `Repositories` (required, `owner/name`), `ApiBaseUrl` (default `https://api.github.com`), `AllowedLabels`,
`MaxBodyChars` (4000), `MaxResults` (20, at most 50), `MaxWriteChars` (20000). Use a fine-grained token limited to those repositories
with *Issues: read and write* (read-only if you only enable the read tools).

## Writing a brick

**For a REST API with an OpenAPI document, write a profile instead** ([PROFILES.md](PROFILES.md)): one JSON file, no code, and the same
safety (curated tools, stated risks, argument rules, previews). Write a brick when the safety needs logic a profile cannot express, or the
service is not HTTP.

A brick references only `OpenTail.Host.Abstractions`:

```csharp
[assembly: HostContractVersion(HostContract.Version)]           // required: the contract this was built against
[assembly: HostModuleFactory(typeof(TicketsFactory))]

public sealed class TicketsFactory : IHostModuleFactory
{
    public string Type => "tickets";

    public ModuleManifest Describe(string id, JsonElement config) => new()
    {
        Id = id, DisplayName = "Tickets", NetworkHosts = ["api.tickets.example"],
        Credentials = [new CredentialRequirement("tickets", CredentialKind.Bearer, "API token")],
        SuggestedPolicyJson = $$"""{ "Tools": { "{{id}}.get": { "Mode": "Execute" }, "{{id}}.comment": { "Mode": "Queue" } } }""",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext ctx, CancellationToken ct = default) =>
        ValueTask.FromResult<IHostModule>(new TicketsModule(ctx.Id, ctx.Http.Create("tickets"), Describe(ctx.Id, ctx.Config)));
}

public sealed class TicketsModule(string id, HttpClient http, ModuleManifest manifest) : IHostModule
{
    public string Id => id;
    public ModuleManifest Manifest => manifest;

    public void Register(IToolRegistry r)
    {
        r.Add(new ToolDescriptor($"{id}.get", "Read a ticket.", ToolRisk.Read, id) { ReadsUntrusted = true },
            AIFunctionFactory.Create((int number) => http.GetStringAsync($"https://api.tickets.example/tickets/{number}"), $"{id}.get", "Read a ticket."));
        r.Add(new ToolDescriptor($"{id}.comment", "Comment on a ticket.", ToolRisk.ExternalEffect, id),
            AIFunctionFactory.Create((int number, string text) => "...", $"{id}.comment", "Comment on a ticket."));
    }
}
```

Rules a brick must follow (the Host enforces the first five):

* Name every tool `<id>.<name>` and register it under the module's own id.
* Declare the assembly's contract version.
* Use `ctx.Http` for HTTP (it carries the credential and the host rules) and `ctx.Credentials` for anything else. Do not read
  credentials or call the network in `CreateAsync`; do it when a tool runs.
* Get risk right. **Anything that changes something other people can see is `ExternalEffect`**, not `Write`: taint holds only
  `Destructive` and `ExternalEffect`. Mark tools that return text from outside the deployment `ReadsUntrusted`.
* Give every non-Read tool an `IPreviewableTool`, and throw `ArgumentException` from the preview for arguments that can never be valid
  (the call is then refused at once instead of waiting for a reviewer).
* For a temporary failure (rate limit, 5xx), throw `ToolTransientException`: the action fails as retryable. Never sleep inside a tool.
* If a tool may be re-run after a crash (`IdempotentByKey`), use the idempotency key in `AIFunctionArguments.Context` to make the second
  run a no-op. The requester and action id are there too, read-only.
* Label what your tools return when the kind of data is fixed (`Labels = ["personal"]` on a mail read); leave it to the operator's
  `"Labels"` in modules.json when it depends on the instance (a database, a folder).
* Say where your results count things: `Counts = new ResultCounts { Records = "/rows" }` on a tool that returns rows, messages or
  files; `EntityKeys = "/rows/*/customer_id"` when the result names stable business ids; `RecipientArgs = ["/to", "/cc"]` on an
  outbound tool. The Host measures from these paths (no code runs in the module) for its usage rollups; it stores entity ids only as
  salted hashes. Without `Counts` the Host still counts calls and result bytes.
* Keep secrets and argument values out of exception messages.

## Running a module in its own process

```json
{ "Type": "github-issues", "Id": "github", ...,
  "Isolation": { "Mode": "Process",
                 "Command": [ "systemd-run", "--uid=otail-mod", "-p", "PrivateNetwork=yes", "--pipe", "--wait", "--quiet", "--" ],
                 "CallTimeout": "00:01:00" } }
```

* The worker is the deployment's own program started with `--module-worker` (both runners handle the flag), so it runs exactly the
  packages the deployment references. `WorkerCommand` names another program when needed; `Command` is a prefix the worker runs under
  (another account, no network, a container that has the program).
* **HTTP goes through the Host.** The module's HTTP clients send each request over the pipe; the Host applies the entry's `Network` rules,
  attaches the credential for the bound host and returns the answer. The worker never holds an HTTP token and needs no network of its own,
  so it can run with networking off. Only `Kind: Secret` credentials are handed to the worker, by name, when a tool asks.
* **Environment:** the worker gets only what a runtime needs (PATH, TEMP, HOME, DOTNET_ROOT, ...) plus `Isolation.Environment`, which may
  not name a credential's variable.
* **Limits:** each call has `CallTimeout` (60 s), the start `StartTimeout` (30 s), each message `MaxMessageBytes` (16 MB). A worker that
  hangs, crashes, breaks the protocol or answers too much is stopped; the call fails as retryable and the next call starts a new worker,
  which must come back with the same tools.
* Tools, risks, previews, taint, errors and policy behave exactly as in process. `--list-modules` says "runs in its own process";
  describing (`--list-modules`, `--explain`, `--adopt-policy`) builds the module in process, since that reads no credential and calls nothing.
* **What it does not do on its own:** on Windows there is no built-in account or network sandbox; a module that opens its own sockets
  (IMAP, SQL, the Graph SDK) needs network in the worker. Without `Command`, the worker has the Host's OS identity and file access.

## What a module can still do

A module in the Host process runs with the Host's OS identity. The rules above are guard rails against mistakes and injected input,
not containment: its own code can open sockets, read files and ignore its manifest. Install in process only modules you or someone you
trust built; run others in their own process under a separate account without network (above).
