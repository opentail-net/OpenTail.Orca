# OpenTail Host v4: integrations as data (plan)

> **Archived 2026-11-12.** Read [`OpenTail.Host/STATUS.md`](../../OpenTail.Host/STATUS.md) for what is built and what is still open. The status line below was written when this was a draft and is not kept up to date; the implementation log at the end is the record.

Status: proposal, 2026-10-16. Builds on v3 (modules as bricks, `MCP-HOST-V3-PLAN.md`), which is implemented and pushed.
This plan is the integration-profile work only: it needs nothing from outside and can run end to end on its own. Approvals for business
users, the pilot and later operations work are in [`MCP-HOST-V5-PLAN.md`](../MCP-HOST-V5-PLAN.md), because most of it waits on the owner
(accounts, a tenant, a reviewer, customer conversations).

## 1. The question: where does time pay back most?

The target is businesses. A business adopts this when two things are true: **it reaches their systems**, and **the people who
answer for those systems are comfortable letting an AI act there**. The Host already has the hard part of the second: policy, taint,
review, audit, scoped credentials, process isolation. What it lacks is breadth of integration at low cost, and a reviewer experience
that business users (not developers) will actually use every day.

Ranked by return on time, with the reason:

| Rank | Work | Why it pays |
|---|---|---|
| 1 | **Integration profiles** (OpenAPI plus a small JSON profile; this plan) | Most business systems publish OpenAPI (CRMs, ERPs, ticketing, HR, finance). With profiles, "can it talk to our X?" becomes a day of JSON, not a C# module. Partners and consultants can write them; outside code never runs in the Host. Every later integration is cheaper. |
| 2 | **Approvals where people already are** (v5: Teams and Slack approve/reject, a better review page) | The reviewer is the product's other user. If approving is slow or unclear, people write `Execute` rules to avoid it and the safety model is gone. Most businesses live in Microsoft 365, so Teams first. |
| 3 | **One real pilot** (v5) | Every module is tested against fakes. One live workflow (a mailbox plus a ticket system) finds the real problems and gives a story to sell. |
| 4 | Operations businesses ask for later | Audit export, Postgres for more than one node, a hosted option. Ask a pilot customer before building. |
| Low | More hand-written modules; more isolation hardening; Windows sandboxing | Wait for a customer who needs them. |

This document plans 1. Ranks 2 to 4 are planned in v5.

## 2. Where we are

* `openapi` (`OpenTail.Modules.OpenApi`, 450 lines) exposes named operations of a spec: GET/HEAD are Read, POST/PUT/PATCH ExternalEffect,
  DELETE Destructive, with `RiskOverrides`; responses are untrusted by default; writes get a generic preview; the Host's guarded client and
  the `api` credential are used. Only path and query parameters and JSON bodies; responses returned as is (capped at 64 KB).
* What the hand-written modules add over that, and what a profile must therefore express: argument limits (allow-listed repositories,
  tables, fields; forbidden parameters; fixed parameters), response shaping (keep these fields, cap text, mark untrusted), error mapping
  (429 and 5xx retryable, Retry-After), previews a reviewer can read, idempotency, and for social data pseudonymised authors.
* Hand-written modules that stay code: GitHub Issues (search rewriting, idempotency markers), Reddit (no spec, nested threads), the
  non-HTTP and SDK providers (IMAP, SQL, files, Graph, Google). Candidates to become profiles: YouTube, X, ServiceNow.

## 3. Design

### 3.1 A profile

A profile is a JSON document next to a spec (or inside a package). It never widens anything: the operator's `Network.Hosts`, the policy
and taint still decide.

```json
{
  "Profile": 1,
  "Name": "youtube",
  "Spec": "youtube-v3.openapi.json",
  "DisplayName": "YouTube (read only)",
  "Server": "https://www.googleapis.com/youtube/v3",
  "Hosts": [ "www.googleapis.com" ],
  "Credential": { "Name": "youtube", "Kind": "Header", "HeaderName": "X-Goog-Api-Key" },
  "Untrusted": true,
  "Errors": { "Retryable": [ 429, 500, 502, 503, 504 ], "Messages": { "403": "Quota exceeded or the key lacks access." } },
  "Tools": {
    "search": {
      "Operation": "youtube.search.list",
      "Description": "Search recent videos.",
      "Risk": "Read",
      "Parameters": {
        "part":       { "Fixed": "snippet" },
        "type":       { "Fixed": "video" },
        "q":          { "Required": true, "MaxLength": 256 },
        "channelId":  { "Pattern": "^UC[A-Za-z0-9_-]{22}$", "AllowedFrom": "Config.Channels" },
        "maxResults": { "Max": 50, "Default": 10 },
        "*":          { "Forbidden": true }
      },
      "Response": {
        "Items": "$.items",
        "Fields": { "id": "$.id.videoId", "title": "$.snippet.title", "text": "$.snippet.description",
                    "channel": "$.snippet.channelTitle", "published": "$.snippet.publishedAt" },
        "MaxTextChars": 2000,
        "Pseudonymise": [ "channel" ]
      }
    }
  },
  "Config": { "Channels": { "Type": "string[]", "Required": false } }
}
```

* **Tools are curated**, renamed and described by the profile; the spec only supplies the HTTP shape. Unnamed operations do not exist.
* **Parameters**: `Fixed` (sent, not shown to the model), `Default`, `Required`, `Pattern`, `MaxLength`, `Min`/`Max`, `Enum`,
  `AllowedFrom` (a list in the operator's config, such as allowed repositories or tables), `Forbidden`; `"*": { "Forbidden": true }` refuses
  anything not listed. Body properties use the same rules under `Body`. Checks run in the preview too, so a bad call is refused before review.
* **Risk** is stated per tool (no guessing from the verb), and `Untrusted` per tool or for the whole profile.
* **Response** keeps only the named fields (a small JSONPath subset: `$.a.b`, `$.a[*]`), caps text, and wraps the result in the usual
  untrusted notice. `Pseudonymise` replaces the named fields (and @mentions in text fields) with stable pseudonyms; the key is the
  optional `pseudonym-key` credential.
* **Preview**: a template, `"Preview": "Comment on {table} {record}: {text|cut:300}"`, rendered with the checked arguments. Writes
  without a preview are refused at load.
* **Idempotency**: `"Idempotency": { "Field": "correlation_id", "Lookup": "list_by_correlation" }` sets a field from the Host's key and,
  on a retry, calls a named lookup tool first. Without it a write is not `IdempotentByKey`.
* **Query builder** (for ServiceNow-style encoded queries): `"Query": { "Into": "sysparm_query", "Join": "^", "Terms": { "text":
  "123TEXTQUERY321={}", "state": "state={}" }, "Forbid": [ "^", "\n" ] }` builds one parameter from checked arguments, so the model never
  writes the raw query. This is the one feature added specifically to test whether profiles can replace a security-relevant module.
* **Errors**: listed codes become retryable (`ToolTransientException`, honouring Retry-After); others become a short message, never
  the remote body.

### 3.2 How a profile is enabled

```json
{ "Type": "openapi", "Id": "yt", "Network": { "Hosts": [ "www.googleapis.com" ] },
  "Credentials": { "youtube": { "Env": "YOUTUBE_KEY", "Kind": "Header", "HeaderName": "X-Goog-Api-Key", "Host": "www.googleapis.com" } },
  "Config": { "Profile": "profiles/youtube.json", "Channels": [ "UC..." ] } }
```

* `Config.Profile` names a file, or `"package:OpenTail.Profiles.YouTube/youtube"` for one embedded in a referenced package (so profiles
  ship and version like any brick, and are still only what the deployment references).
* The profile's `Hosts` and credential become the module's manifest: the existing checks (operator hosts within the manifest, credential
  bound to a listed host, describe without secrets, isolation) apply unchanged.
* `--list-modules` shows the profile name and version; `--lint` gets profile checks (a write with no preview, a parameter that is neither
  listed nor forbidden, an untrusted read beside an unreviewed Write tool).
* Plain `Operations` (today's behaviour) keeps working.

### 3.3 Where profile logic lives

In `OpenTail.Modules.OpenApi` (no new contract: a profile is config of an existing module). A `ProfileLoader` validates the document
against its own schema-free typed model (unknown keys refused, like `ModuleContext.Bind`), a `ParameterGuard`, a `ResponseShaper`, and a
`PreviewTemplate`. Target size: under 1,000 lines with tests. If it grows past that, the format is becoming a language: stop and reconsider.

## 4. Phases

| Phase | Size | What | Exit |
|---|---|---|---|
| P1 Profile core | 1 day | Profile model and loader, curated tools, parameter rules, fixed/default values, risk and untrusted per tool, error mapping, preview templates, `Config.Profile` (file) | A hand-written test profile over a test spec passes the existing OpenApi tests plus new ones for each rule; unknown keys and writes without previews are refused at load |
| P2 Responses and privacy | 0.5 day | Field selection, text caps, pseudonymisation, untrusted wrapper | Same output shape as the Social module for the same fake answers |
| P3 Proof: YouTube and X as profiles | 0.5 day | Re-express both; delete their C# from Social (Reddit stays code) | `SocialTests` for YouTube and X pass unchanged against the same fakes, now through `openapi` profiles; Social module shrinks by roughly half |
| P4 ServiceNow as a profile (the hard test) | 1 day | Query builder, `AllowedFrom` for tables, fields and groups, idempotency via correlation id | `ServiceNowTests` pass unchanged, including the injection and correlation-id cases. If they cannot pass without weakening a check, ServiceNow stays code and the plan says why |
| P4b Packaged profiles and lint | 0.5 day | `package:` profiles, `--lint` profile checks, MODULES.md "write a profile", conformance tests any profile can run | An outside project references a profile package and enables it by config only (deployment check) |
About 3.5 days. It settles the module format before anyone outside depends on it, and every phase is checked against tests that already
exist, so nothing here waits on an account, a tenant or a person. Commit and push after each phase.

## 5. Risks and limits

* **The profile becomes a programming language.** Mitigation: the 1,000-line budget, and anything conditional stays code (GitHub's
  search rewriting is the example). Profiles describe; they never compute.
* **Specs are wrong or huge.** Vendor specs have errors and thousands of operations (Graph). Profiles name the few operations they use
  and can override a parameter's shape; a broken spec fails at load with the operation named.
* **A profile is also a trust decision**, smaller than code: it cannot run code or reach unlisted hosts, but it can expose a destructive
  operation with a misleading description. Risk is declared by the profile and shown by `--list-modules`; adoption still needs
  `--allow-execute` for anything but Read; review still applies.
* **Google's APIs use Discovery documents**, not OpenAPI. YouTube needs a converted spec checked into the profile package (and refreshed
  occasionally). If conversion is poor, YouTube stays code; X has a native OpenAPI spec.
* Still not solved here: live verification of any provider (v5's pilot starts it), Windows containment for isolated modules, multi-node.

## 6. Decision for the owner

Profiles in this repository (`OpenTail.Profiles.*` packages) or a separate profiles repository later? Default if not answered: here, until
there are more than a handful.

## 7. Implementation log

| Phase | Commit | What |
|---|---|---|
| P1 | `552dfd0` and the next commit | Profile model and loader, curated tools with stated risks, argument and body rules, previews, error mapping, profile manifest; `IHostModuleFactory.Describe(id, config, baseDirectory)` added with a default. Then: `AllowedFrom` fails closed when the operator configured no list; tool descriptions keep `[METHOD path]`; the operator guide [`OpenTail.Host/PROFILES.md`](../../OpenTail.Host/PROFILES.md). |
| P2 | (this commit) | `Response` rules: `Items`, `Fields` (paths, `\|number`, text templates), `MaxItems`, `MaxTextChars`, `Pseudonymise`, `PseudonymiseMentions`; HMAC pseudonyms with the optional `pseudonym-key` credential; a shaped tool fails rather than pass a non-JSON or cut answer. |
| P3 | (this commit) | `opentail/profiles/youtube` and `opentail/profiles/x` (profile plus spec each); the Social module is Reddit only and refuses old `YouTube`/`X` sections with a pointer to the profiles. The YouTube and X tests run through the shipped profiles against the same fakes, and assert the same behaviour (header key, numeric counts, pseudonymised authors and mentions, errors never empty, retryable rate limits, bad ids refused). Social C# went from 583 to 377 lines (about a third less, not the half estimated), replaced by about 200 lines of profile JSON. Differences from the old code, accepted: YouTube channel names are always pseudonymised (the old code showed allow-listed channels by name); a YouTube search without a channel is allowed when `Channels` is set; X shows pseudonymised author ids instead of names (no join across the answer). The YouTube spec is a hand-written subset (Google publishes Discovery, not OpenAPI); the X spec is a subset of X's published one. The deployment check enables the shipped YouTube profile in an outside project. |
| P4 | (decision, no code) | **ServiceNow stays a hand-written module**, by this plan's own rule. Four of its seven tests need two dependent requests per call, which a profile (one request per tool) cannot express without becoming a workflow language: tickets are named by number but writes, the journal and resolve need the `sys_id` (look up, then act); and a create first looks for its correlation id so a retry after a crash does not open a second ticket (dropping that would weaken a safety check). The query builder is **deferred** until a real filter-language API needs it (Jira JQL, OData `$filter` for Dynamics or SAP, Salesforce SOQL), so its shape comes from that API. |
| P4b | (this commit) | `Config.Profile` accepts `package:Assembly/name`: profiles embedded as resources (`profiles/<name>/<file>`) in a package the deployment references; `OpenTail.Profiles.YouTube` and `OpenTail.Profiles.X` (no code, reference `Modules.OpenApi`, architecture test). `ProfileConformance.Check` loads a profile as the Host would and reports its tools and findings (open doors on writes, unconstrained path parameters, untrusted reads without a Response rule, previews that name no argument, missing descriptions or notes); the same findings are module warnings, so they appear at startup, in `--list-modules` and `--lint`. Every shipped profile has no findings, in file and package form. The deployment check enables the YouTube profile from its package. |
| Licence | (with P4) | The Host, its modules and profiles are licensed under PolyForm Strict 1.0.0 (`LICENSE-HOST.md`, the official text unchanged); their packages carry the file and require acceptance. The shared `OpenTail.Connectivity.*` libraries keep the repository's MIT licence. |
