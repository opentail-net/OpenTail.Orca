# OpenTail Host v3: modules as bricks (plan)

> **Archived 2026-11-12.** Read [`OpenTail.Orca/STATUS.md`](../../OpenTail.Orca/STATUS.md) for what is built and what is still open. The status line below was written when this was a draft and is not kept up to date; the implementation log at the end is the record.

Status: plan only, nothing implemented. Written 2026-10-16 on branch `host/modular-mcp`.
Builds on [MCP-HOST-PLAN.md](MCP-HOST-PLAN.md) and [MCP-HOST-V2-PLAN.md](MCP-HOST-V2-PLAN.md).

## 1. Goal

An operator adds a capability such as `GitHubIssues` (first), later `ServiceNow`, by installing **one NuGet package** and adding a few lines of
config. The package carries the tools, their risk, the sign-in method for that system, a suggested policy, and a statement of what it
touches. Removing the package removes the capability. Nothing else in the Host changes, and a new module can never widen access
by itself: policy still decides, and a tool with no policy entry stays denied.

## 2. Where we are

| Already true | Not yet true |
|---|---|
| Each module is its own package and references only `Host.Abstractions` (+ small connectivity libs); an architecture test enforces it | Adding a module needs a code edit and rebuild: `builder.AddModule(...)` in a deployment's `Main` |
| Host discovers tools through `IHostModule`; knows no module by name | Nothing is discovered from a folder or config |
| `IHostModule` already has `MinContractVersion` / `MaxContractVersion` | No manifest: what a module needs (hosts, secrets, scopes) is only in its docs |
| Deny by default; risk declared per tool | No suggested policy ships with a module; operators write entries by hand |
| Generic `OpenApi` module can import any OpenAPI spec | Auth is one bearer token from an env var. No OAuth client-credentials, no session or cookie flows, no paging helper |
| 14 packages pack with one version; a clean-feed consumer builds | Nothing published; samples use project references; contract not frozen |
| | Modules run in the Host process with its identity |
| | No real service module yet (GitHub Issues is first, ServiceNow later) |

## 3. Design decisions

1. **Keep compile-time references as the trust boundary.** Discovery by config selects among modules that are *already in the
   deployment's dependency set*. It never loads arbitrary DLLs from disk. "Present" means "referenced by the deployment app", and
   "enabled" means "named in config". This keeps supply chain control with whoever builds the deployment. A folder-of-DLLs plug-in model
   is explicitly out of scope until out-of-process execution exists (phase W6).
2. **One factory type per module package**, found by a convention attribute (`[assembly: HostModuleFactory(typeof(GitHubIssuesFactory))]`),
   so a deployment `Main` can call `builder.AddModulesFromConfig(config)` and the Host enumerates the loaded assemblies. No reflection over
   arbitrary types, no scanning of directories.
3. **The manifest is data the module returns, not a promise the Host trusts.** Declared network hosts and scopes are *enforced* where the
   Host can (HTTP client handler with a host allow-list given to the module) and *displayed* where it cannot. The manifest never grants
   anything; policy and operator config do.
4. **Suggested policy is a file, never applied automatically.** The operator opts in (`--adopt-policy servicenow`) and the result is an
   ordinary policy entry list they can read and edit. Defaults are conservative: reads `Execute`, everything else `Queue`.
5. **Credentials are named, not carried.** A module asks the Host for a named credential (`servicenow`); the operator maps the name to a
   source (environment variable, a file, later a vault). The model and the arguments never see it, and the audit trail never holds it.
6. **Contract changes are semver-major after the first publish.** Freeze first, then build real modules against it.

## 4. Phases

Each phase ends with: build 0 warnings, tests pass, docs updated, committed locally, not pushed unless asked.

### W1. Contract freeze review and manifest (about 1 day)

* Add to `Host.Abstractions`: `ModuleManifest` (id, display name, version, publisher, description; `RequiredCredentials` (name, kind, description);
  `NetworkHosts` (patterns); `RequiredScopes` per system; `ConfigSchema` (JSON schema string for the module's config section);
  `SuggestedPolicy` (embedded resource path)).
* `IHostModule.Manifest` as a **default interface member** returning a minimal manifest derived from `Id`, so every existing module still compiles.
* Review `IHostModule`, `IToolRegistry`, `ToolDescriptor`, `IPreviewableTool` for anything we would regret: result envelope, untrusted-output flag,
  paging convention, cancellation, per-call context (caller subject, correlation id) passed read-only to tools. Decide, then (after W5) freeze and document the compatibility promise in `PACKAGING.md`.
* Print each module's manifest at startup (to stderr) and in the admin page "Modules" tab (read-only).
* Tests: manifest default; old modules unchanged; contract-version mismatch gives a clear startup error naming the module.

### W2. Credentials and outbound HTTP for modules (about 2 days)

* `ICredentialProvider` in Abstractions: `GetAsync(name)` returns a short-lived secret or token object; sources: env var, file, and a
  `client_credentials` OAuth2 provider (token cache, refresh, clock skew, single-flight); basic auth; API-key header; OAuth2 with
  refresh token as a later addition.
* `IModuleHttpClientFactory`: gives a module an `HttpClient` that already enforces the manifest's `NetworkHosts` allow-list, public-address
  rule (reusing the Web module's checks, moved to `Connectivity.Web`), redirect re-checks, size and time limits, secret redaction in errors.
* Operator config maps credential names to sources. Missing credential = module refuses to start with a message naming the credential.
* The existing `OpenApi` module moves onto this, keeping `AuthTokenEnvVar` as a shortcut. Gains OAuth2 client-credentials, paging helper,
  and per-operation risk overrides.
* Tests: token refresh, concurrent callers share one refresh, host allow-list blocks others, redirect to other host blocked, secret never in audit or errors.

### W3. Config-driven enablement (about 1 day)

* `modules.json` (or a `Modules` section in `http.json`): list of `{ "Id": "servicenow", "Enabled": true, "Config": { ... }, "Credentials": { "servicenow": { "Env": "SN_TOKEN" } } }`.
* `AddModulesFromConfig`: finds factories in loaded assemblies, validates `Config` against the module's schema, builds the module.
  A module present but not listed is **not** enabled and is logged. A module listed but not present is a startup error (never a silent skip).
* Startup refuses duplicate ids and duplicate tool names; tool names are namespaced by module id as today.
* `--list-modules` prints present, enabled, tool count and risks, and which tools have no policy entry (so are denied).
* Tests: enable, disable, missing package, bad config, duplicate ids, unlisted module stays off.

### W4. Suggested policy and adoption (about 1 day)

* Modules ship `suggested-policy.json` as an embedded resource, same format as POLICY.md, with a comment on why for each entry.
* `--adopt-policy <module>` merges it into the policy file as a **proposed** change and shows the diff; `--explain` and `--replay-policy`
  work on the result. Never applied without the operator running it.
* Policy lint: warn on a module with enabled tools and no policy entries; warn on `Execute` for any non-`Read` tool.
* Add **policy tests**: a file of expected decisions (`call`, `args`, `expect: Queue|Execute|Deny`) run by `--test-policy` and usable in CI.
* Tests: adoption diff, idempotent re-adopt, lint warnings, policy-test pass and fail.


### W5. First real brick: GitHub Issues (about 2 days)

Chosen because it can be tested for real today: free, a documented REST API, real tokens (a fine-grained PAT, later a GitHub App), and
issue text is written by strangers, which is the same prompt-injection case a service desk presents. ServiceNow follows only when a real
instance exists (W5b). PeopleSoft is dropped until the interface is known.

**`OpenTail.Modules.GitHubIssues`**
* Tools: `gh.search_issues`, `gh.get_issue`, `gh.list_comments` (Read, `ReadsUntrusted`, bodies length-capped, allow-listed repos only);
  `gh.add_comment` (Write, previewed); `gh.create_issue`, `gh.update_issue` (labels, assignees, state; Write, previewed, idempotent by a hidden marker);
  `gh.close_issue` (Destructive). No raw API passthrough, no repository administration, no delete.
* Config: allow-list of `owner/repo`, allowed labels, max body length, API base URL (so GitHub Enterprise works), credential name.
* Auth: PAT first (W2 credential kind); GitHub App installation tokens as a follow-up, since a team would really use those.
* Handles rate limits (`Retry-After`, secondary limits), pagination and ETags. Issue and comment text is returned as data, with the author
  and their association to the repo shown, so a reviewer can see who wrote it.
* Suggested policy: reads `Execute`; comments and creates `Queue`; an `Execute` rule for comments in one named test repo only, with a daily
  budget; close is `Queue` with a reviewer group.
* **Live test:** a throwaway repo under the operator's account. Read an issue containing an injected instruction, confirm the next write is
  forced to `Queue`, approve it, confirm the comment exists. Failure paths (403, 404, 422, 429, redirects) are tested against a stub server.
* Ships with: manifest, config schema, suggested policy, a README table of what it can and cannot do, and a sample deployment line.

### W5b. ServiceNow (only when an instance exists; about 3 days)

Same shape, built first against a stub of the Table API. Not started. By then W5 will have shown whether the contract is right.

### W6. Packaging and publishing (about 1 day, plus operator decisions)

* Decide the feed with the operator (nuget.org, GitHub Packages, or a private feed) and the package ids and publisher name.
  Connectivity packages go to the same feed.
* Tag `host-v0.1.0` after W1 to W5; switch samples to package references; extend `check-host-deployments.ps1` to build a consumer that adds
  GitHubIssues by **package plus config only** (no edits to `Main` beyond the runner call).
* Add a documented minimal project (or `dotnet new` template) for authors: "write a brick in 30 lines".
* Package signing and a supply-chain note in SECURITY.md (what a module can do with the Host's identity).

### W8. Social listening module (next after W6; about 3 days)

`OpenTail.Connectivity.Social` already has connectors for Reddit, Twitter/X (recent search), YouTube (Data API v3) and Meta (Graph), text
extractors, and a sentiment scorer. Nothing exposes them through the Host. This phase turns them into a read-only brick, `OpenTail.Modules.Social`,
once W1 to W6 have settled the contract and the published path.

* Tools (all Read, all `ReadsUntrusted`; the content is the most hostile text in the whole system): `social.search` (platform, query, since,
  limit), `social.get_post`, `social.get_thread`, `social.sentiment` (scores text the caller already holds; no network).
* No posting, replying, liking or messaging in this phase. Anything that writes to a platform is a separate later brick behind `Queue` with
  a reviewer group.
* Config: platforms enabled, per-platform credential names (W2), query and subreddit/channel allow-lists, per-platform rate budgets and
  caps on results and text length, optional author-redaction (handles replaced by stable pseudonyms) for privacy.
* Manifest declares exactly the hosts used (`oauth.reddit.com`, `api.twitter.com`, `www.googleapis.com`, `graph.facebook.com`); W2's
  allow-listed HTTP client enforces it.
* Honest unknowns to resolve first: which platforms you still want (Twitter/X search needs a paid API tier; Meta needs an app review), and
  whether the existing connectors still match current API terms. The connectors have not been run in this work; each platform gets a stub
  test first and a live test only where a credential is available. Reddit and YouTube are the cheapest to test for real.
* Suggested policy: all tools `Execute` for callers, but every result taints, so any later write or send in the same window is held for a reviewer.
* Exit: stub suites per platform; one live platform (Reddit or YouTube) read end to end; injected post text proven to force `Queue` on a
  following write; consumer adds the module by package plus config only.
### W7. Later, when outside authors ship modules (they are expected eventually)

* **Out-of-process modules:** a child process per module, own OS account or container, stdio protocol (the same shape as MCP tools), credentials
  passed per call, Host enforces timeouts and output size. This is what turns "trust the module author" into "contain the module".
* Pluggable store (Postgres) for multi-node.
* MCP tasks, Slack approve buttons.

## 5. Order, size and exit

| Phase | Size | Exit test |
|---|---|---|
| W1 manifest (contract freeze comes after W5) | 1 day | existing modules and samples unchanged; manifests print |
| W2 credentials + HTTP | 2 days | OAuth refresh and host allow-list tests pass; no secret in audit |
| W3 config enablement | 1 day | package present but unlisted stays off; listed but missing fails to start |
| W4 suggested policy + policy tests | 1 day | adopt, diff, lint, and `--test-policy` work |
| W5 GitHub Issues | 2 days | live test on a throwaway repo; injected issue text forces review; stub suite for failures |
| contract freeze | 0.5 day | `HostContract.Version = 1`, compatibility promise in PACKAGING.md |
| W6 publish | 1 day + decisions | clean-feed consumer adds GitHubIssues by package plus config only |
| W8 Social listening (read-only) | 3 days | one live platform; injected post forces review |
| W5b ServiceNow, W7 isolation | later | when an instance exists / when outside authors arrive |

Original estimate was 8 days (11 with Social); the review below re-estimates to 13-15 days to a published first brick, and Social as a connector rewrite. W1 to W4 are generic and benefit every later brick. W5 is the proof that the contract is
right, so **do not freeze until W5 has been drafted against it**: build the manifest (W1), build W5 on top, change the contract once if it
needs it, then freeze.

## 6. Risks

* **A module is the biggest trust decision.** It runs with the Host's identity. Until W7, only install modules you or someone you trust built;
  say so in SECURITY.md and in the manifest display.
* **Over-wide imported APIs.** An OpenAPI import of a big system exposes hundreds of operations. Mitigation: bricks ship curated tools, and
  OpenApi imports require an operation allow-list in config.
* **Untrusted text everywhere.** Issues, tickets and comments are written by outsiders. Every read is `ReadsUntrusted`, and the live test
  in W5 must prove the injected-issue case.
* **Contract churn.** Mitigated by building GitHub Issues before the freeze.
* **Honest limits that remain:** single node; no live-IdP or live-Slack verification; approval page not walked through in a real browser.
  Those are tracked in v2 and are not solved here.

## 7. Repo shape (decided 2026-10-16)

The Host and modules (about 4% of the C# lines) sit beside a much larger engine-and-apps stack that the outer repo's Pro editions, Bench and
slow tests build on (29 `opentail/` projects are reachable from the outer repo). Nothing in the Host or modules references `OpenTail.Core`, but
`OpenTail.Core` references `OpenTail.Orca.Abstractions`, so Abstractions must stay a leaf. Eleven projects with no source (four dummy or scratch
projects and seven empty leftovers) were moved to `opentail/_to_delete/`; both solutions still build with 0 warnings and 0 errors. The rest of the
sorting is a decision for the owner and is listed in the conversation that produced this plan, not acted on here.

## 8. Questions for the operator

1. A throwaway GitHub repo and a fine-grained token for the W5 live test: can you create them? (Needs only Issues read/write on that one repo.)
2. Which package feed, and which publisher and package id prefix? (needed for W6)
3. Is anyone besides you going to write bricks soon? (decides when W7 starts)
## 9. Review outcome (Opus, 2026-10-16) - supersedes conflicting text above

An independent Opus review read this plan against the code and returned 18 findings. Three core claims were checked by hand and are **true**:
taint holds only `Destructive`/`ExternalEffect` tools (`ToolDescriptor.cs:21`, `PolicyResolver.cs:184`); an unlisted `Read` tool follows
`DefaultReadMode`, not deny (`PolicyResolver.cs:119-123`); and `MinContractVersion` as a default interface member always reports the loaded
Host's version (`IHostModule.cs:17-18`), so the check at `ToolCatalog.cs:24` can never fire for a module that relies on it. The rest were taken
on the reviewer's citations and will be re-verified when each phase starts. Outcome: nearly all adopted.

**Must change before W5 (blockers)**
1. **Writes to outside systems are `ExternalEffect`.** Every GitHub/ServiceNow write tool (`add_comment`, `create_issue`, `update_issue`, close) is
   `ExternalEffect` so taint forces review. W8's "any later write is held" is only true for high-impact tools; say so. Decide separately whether
   taint should also cover plain `Write`. Lint: an `Execute` rule on a high-impact module tool next to `ReadsUntrusted` siblings warns.
2. **Config-enabled modules do not inherit `DefaultReadMode`.** A module's self-declared `Read` tools are not auto-executed or advertised unless the
   operator opts in per module (`"AllowDefaultRead": true`). `--list-modules` lists default-read tools separately and no longer says "denied" for them.
3. **Contract version is explicit.** Remove the default implementations; modules state the range, stamped by an assembly attribute
   `[assembly: HostContractVersion(1)]` the Host reads. (`Version` is already 1; the W1 line about setting it was wrong.) Test: a module built against
   a different version fails with a clear message.
4. **The HTTP allow-list is a guard rail, not containment.** In-process code can always make its own `HttpClient`, and IMAP/SQL are not HTTP.
   Say so in SECURITY.md. Allowed hosts come from **operator config intersected with the manifest**, never the manifest alone; each credential is bound
   to its host inside the handler (the token is attached only for that host; GitHub Enterprise base URL handled). True containment is W7.
5. **Suggested-policy adoption can only narrow or fill in.** It accepts only `Tools` entries whose names carry the module's own prefix and exist in its
   catalog; global keys (`TaintWindow`, `DefaultReadMode`, `People`...), other modules' tools and anything it cannot parse are rejected.
   `Execute` on a non-`Read` tool is a separate explicit opt-in. Written to `policy.d/<module>.json` (needs include support) so the operator's file and
   comments are not rewritten.

**Should-fix, folded into phases**
* **Layering (W2):** the guarded-HTTP interface goes in Abstractions, the implementation in Host (or a new `Host.Net`); the architecture tests are
  updated deliberately. "Move Web checks to Connectivity.Web" is dropped.
* **Credentials (W2):** a provider scoped per module to the names mapped in its own config entry; exact-value redaction of every secret handed out
  (not regex); no remote error bodies in exceptions beyond a short scrubbed excerpt; rotation (re-read env/file sources, drop the cache on 401).
* **Construction (W2/W3):** factories are async: `CreateAsync(ModuleContext{config, credentials, http, clock, logger})`. Caller context reaches tools
  through `AIFunctionArguments.Context` from the action record, as the idempotency key does, never through `CallerContext`.
* **Discovery (W3):** use `DependencyContext` and load only assemblies matching a configured type; not `AppDomain.GetAssemblies()`. Config entries are
  `{ "Type": "openapi", "Id": "crm", ... }` so one factory can serve several instances. Stdio gets a `--modules` flag. Split **describe** (`--explain`,
  `--list-modules`, `--adopt-policy`, never needs credentials) from **run**. Refuse start when `PublishTrimmed`.
* **Config validation (W3):** typed options with `Validate()`; no JSON-Schema dependency; `ConfigSchema` becomes documentation only.
* **Uninstall (W3/W4):** define it. Policy entries for a known-removed module warn instead of failing startup; its pending actions expire; its
  trust promotions are cleared. Test reinstall.
* **GitHub tool design (W5):** `search_issues` strips/rejects `repo:`, `org:`, `user:` qualifiers and appends the allow-listed repos server-side;
  `update_issue` cannot change state (closing is only `close_issue`); idempotency by listing recent items, not by search (eventual consistency); on
  `Retry-After` fail with a retryable error instead of sleeping inside the 5-minute lease.
* **Live test (W5):** the injected issue is written by a **second account** (the operator's own issues are author-association OWNER, not a stranger).
  Also test: an `Execute` rule present and the caller tainted still queues; a write to a non-allow-listed repo is refused by the module; an extra
  argument cannot ride an `Execute` rule.
* **Audit (new, W1):** record module id and version on each action (schema migration v4, with the existing backup step).
* **Admin page:** HTML-escape module-authored manifest text. Add a manifest-format version.
* **Authors (W6):** a conformance kit (`Host.Testing`) so outside authors can test against the contract; upgrade notes for contract changes.
* **Social (W8):** the connectors are batch ingestion code with a cache, swallow errors into empty results (the model would read "nothing found"),
  use unauthenticated `www.reddit.com`, and have a trimming bug (`TrimStart('r','/')` turns "rust" into "ust"). So W8 is a **rewrite as stateless
  clients with an injected HTTP client**, 5+ days, with pseudonymised authors by default and a required `RetentionDays` (default 0 keeps forever,
  which would hold third parties' personal data indefinitely). It stays on the plan as a later phase and does not block W6.
* **W7:** pick one credential model now (in-process provider, with the same interface used by the out-of-process host) so bricks do not change twice.

**Re-estimate:** W1 1.5 d (adds explicit version, audit migration), W2 4-5 d, W3 2 d, W4 1.5 d, W5 3-4 d, freeze 0.5 d, W6 1.5 d: **about 14-16
days** to a published first brick. W8: 5+ days.

## 10. Implementation log (2026-10-16)

Done, committed locally on `host/modular-mcp`, not pushed: W1 to W5 and the contract freeze. Every review blocker (section 9, items 1-5) is
addressed; the should-fix items are folded in as listed below.

| Phase | Commit | What was built |
|---|---|---|
| W1-W4 | `9bb652b` | `ModuleManifest` (format version, hosts, credentials, notes, suggested policy). Contract version read from `[assembly: HostContractVersion]` compiled into each module (the default interface members are gone). Tool names and module ids enforced at registration; module version recorded on each action; requester and action id passed to tools in `AIFunctionArguments.Context`. Named, per-module credentials (env and file re-read on every use, OAuth2 client credentials with single-flight refresh and 401 invalidation) and exact-value redaction of every secret handed out. Guarded HTTP client (operator hosts intersected with the manifest, https unless an origin is listed, public addresses unless allowed, checked on the connected address, redirects re-checked, credential only for its bound host). `NetworkRules` shared with the Web module (in Abstractions, so no layering change). `modules.json`, factory discovery through deps.json (referenced packages only), describe vs run (credentials only checked when running), `Enabled: false` retires a module, configured modules do not inherit `DefaultReadMode`. Factories for `files` and `openapi` (operation allow-list required). Policy `Include` of `policy.d` fragments, `--adopt-policy`, `--list-modules`, `--lint`, `--test-policy`. |
| W5 | `a9e436c` | `OpenTail.Modules.GitHubIssues` (7 tools, writes `ExternalEffect`, close `Destructive`), `ToolTransientException`, factories get config in `Describe`. Tests against a stateful fake GitHub over real HTTP. The deployment script packs 15 packages and builds an outside app that names no module in code. |
| Admin | `4bd34a6` | Read-only Modules view on the approval page (`/api/modules`), module version on action detail. |
| Freeze + docs | (this commit) | `ContractFreezeTests` with `contract-v1.txt`; `MODULES.md`; README, POLICY, OPERATIONS, SECURITY, PACKAGING updated. |

**How the review's points were handled**

* *Taint and Write tools (1):* bricks mark anything other people can see `ExternalEffect`; `--lint` warns about a `Write` tool that can run
  unreviewed next to tools that read outside content; POLICY.md and SECURITY.md say plainly that taint holds high-impact tools only.
  Extending taint to all `Write` tools was **not** done: it would hold `files.write` for every caller that read a file, and the lint covers
  the dangerous case. Decide later if needed.
* *DefaultReadMode (2):* configured modules need explicit entries unless `AllowDefaultRead`; `--explain` and `--list-modules` say why.
* *Contract version (3):* assembly attribute, test with a dynamic assembly built for contract 2.
* *Allow-list is a guard rail (4):* documented as such; hosts are operator config intersected with the manifest; credentials bound to hosts.
* *Adoption can only narrow (5):* own registered tools only, no global keys, `Execute` on non-Read needs `--allow-execute`, written to a
  fragment that the loader also restricts to the module's prefix; the operator's file is never rewritten.
* *Layering:* `NetworkRules` went to Abstractions (pure BCL), the guarded client to Host; no architecture rule had to change. Host gained
  one package, `Microsoft.Extensions.DependencyModel`.
* *Construction and caller context:* async factories with `ModuleContext`; context keys come from the stored action, so they are right
  during approval runs and recovery.
* *Discovery:* `DependencyContext`, not `AppDomain.GetAssemblies()`; extra assembly prefixes via `FactoryAssemblies`. Not done: refusing
  to start when trimmed (no reliable runtime signal); the missing-type error mentions trimming instead.
* *Config keyed by Type + Id; describe vs run; typed config with unknown keys refused:* done. JSON Schema dropped.
* *Uninstall:* done as `Enabled: false`. Approved-but-unstarted actions of a removed module fail as `unknown_tool` (Approved cannot move
  to Expired in the state machine).
* *GitHub tool design and live test:* qualifiers, state, marker by listing, no sleeping, all done and tested against the fake. The live
  test on github.com (second account writes the injected issue) is **not done**: it needs a throwaway repository and a token (section 8).
* *Audit of module version:* stored in the action record (JSON column), so no schema migration was needed.
* *Manifest text on the admin page:* written with `textContent`, like everything else on the page.
* *Conformance kit for outside authors (`Host.Testing`):* not done; MODULES.md has the rules and a complete example. Do it with W7.

**Verification:** solution builds with 0 warnings and 0 errors; 469 Host tests and 32 abstraction/architecture/contract tests pass;
`check-host-deployments.ps1` passes (three samples, fifteen packages one version, two outside consumers). One intermittent failure was seen
once in about 26 full runs (`NotificationTests.Routes_filter_by_tool_pattern_and_minimum_risk`, which this work did not touch); it did not
reproduce in 15 isolated runs and is not explained.

**Still open (as of the first log):** W6, the live GitHub test, W5b ServiceNow, W8 Social, W7 out-of-process modules.

### Second implementation log (2026-10-16)

The operator asked for W7, ServiceNow, Social and modules.json loading for every module. Done, committed locally, not pushed:

| Commit | What |
|---|---|
| `f866e08` | The intermittent test failure explained and fixed: `ClearAllPools()` in tests disposed connections that parallel tests were using (the notification sink swallowed the error, so the outbox row was missing). Tests clear only their own database's pools; 50 consecutive runs pass. |
| `a16eb96` | Factories for `csv`, `web`, `sql`; configurable module ids; provider packages `Modules.Email.Imap` (`email-imap`), `Modules.Calendar.Ics` (`calendar-ics`), `Modules.Microsoft365` (`email-graph`, `calendar-graph`), so `Email` and `Calendar` stay provider-neutral and a deployment downloads only the client library it uses. |
| `f927911` | W5b `Modules.ServiceNow` (Table API), built against a stateful fake: structured search (no raw encoded query), allow-listed tables/fields/groups, correlation id on creates, resolve is Destructive. |
| `9dae9b6` | W8 `Modules.Social` (Reddit, YouTube, X), read only, rewritten as stateless clients over the guarded client; pseudonymised authors; Host OAuth2 `ClientAuthentication: Basic`. |
| (this commit) | W7 process isolation: `"Isolation": { "Mode": "Process" }`, worker = the deployment's own program (`--module-worker`), brokered HTTP, Secret-only credential hand-over, empty environment, timeouts, size limits, restart. |

Decisions taken without asking: Social covers Reddit, YouTube and X (Meta's search needs an app review); the sentiment word list is copied
into the module so the package does not ship the old connectors; ServiceNow comments are not idempotent (an interrupted one waits for a
reviewer) while creates are (correlation id). Afterwards: `Modules.Google` (`email-gmail`, `calendar-google`; OAuth refresh token as two
Secret credentials).

**Verification:** both solutions build with 0 warnings and 0 errors; 499 Host tests (5 consecutive runs) and 32 abstraction tests pass;
`check-host-deployments.ps1` packs 23 packages at one version, enables eleven module packages in an outside project by package plus config
only, and runs that project as its own module worker.

**Still open:** W6 publishing (feed choice); live tests against github.com, a ServiceNow instance, Reddit/YouTube/X and Microsoft 365 (all
tested against fakes only; Gmail and Google Calendar only described, never called); a conformance kit for outside authors; OS-level containment on Windows.
