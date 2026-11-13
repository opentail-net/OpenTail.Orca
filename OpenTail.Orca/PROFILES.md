# Integration profiles

An integration profile turns an OpenAPI document into a safe, curated module **without writing code**: you write one JSON file that says
which operations become tools, what they are called, how risky they are, what the model may send to each, and what a reviewer reads
before approving. The `openapi` module does the rest. A profile can be written by anyone who knows the API; it never runs code inside the
Host, and it can never widen access: the operator's hosts, the policy, taint and review still decide.

This page describes what is implemented today (v4 P1 to P4b). Planned additions are listed at the end and marked as such.

* [Licence](#licence)
* [Why profiles](#why-profiles)
* [Shipped profiles](#shipped-profiles)
* [A complete example](#a-complete-example)
* [How a call flows](#how-a-call-flows)
* [Reference: the profile file](#reference-the-profile-file)
* [Reference: argument rules](#reference-argument-rules)
* [Reference: previews](#reference-previews)
* [Reference: responses](#reference-responses)
* [Reference: errors](#reference-errors)
* [Enabling a profile in modules.json](#enabling-a-profile-in-modulesjson)
* [What is checked when a profile loads](#what-is-checked-when-a-profile-loads)
* [Security model](#security-model)
* [Writing a profile, step by step](#writing-a-profile-step-by-step)
* [Checking a profile (conformance)](#checking-a-profile-conformance)
* [Shipping a profile in a package](#shipping-a-profile-in-a-package)
* [Testing a profile](#testing-a-profile)
* [Troubleshooting](#troubleshooting)
* [Limits today, and what is planned](#limits-today-and-what-is-planned)

## Licence

Profiles in `opentail/profiles`, like the Host and its modules, are licensed under PolyForm Strict 1.0.0 (`opentail/LICENSE-HOST.md`):
noncommercial use only; commercial use needs a commercial licence. A profile you write yourself is yours to license as you like.

## Why profiles

Most business systems publish an OpenAPI document. Importing one directly exposes the API as it is: hundreds of operations, every
parameter, risk guessed from the HTTP verb, and raw responses. A hand-written module fixes that but costs code to write and maintain.

A profile sits between the two:

| | Plain OpenAPI import | Profile | Hand-written module |
|---|---|---|---|
| Which operations exist | the ones you list | the ones the profile names, under its own names | the ones the code registers |
| Risk | guessed from the verb | stated per tool | stated per tool |
| What the model may send | every parameter in the spec | only what the rules allow, checked | whatever the code checks |
| Reviewer sees | method and URL | a sentence in the profile's words | whatever the code writes |
| What comes back | the whole answer | only the fields the profile names, text cut, authors as pseudonyms | whatever the code returns |
| Errors | status plus a piece of the remote body | the profile's messages, never the remote body | whatever the code writes |
| Needs C# | no | no | yes |

Use a hand-written module only when the safety depends on logic a profile cannot express (rewriting a search query, detecting a
duplicate after a crash, flattening a thread). GitHub Issues is that kind of module; most REST APIs are not.

## Shipped profiles

Profiles that come with OpenTail ship as packages: reference `OpenTail.Profiles.YouTube`, `OpenTail.Profiles.X`, `OpenTail.Profiles.Jira` or `OpenTail.Profiles.Confluence` (each brings
`OpenTail.Modules.OpenApi` with it) and name the profile as `package:<package>/<profile>`. The sources live in `opentail/profiles/<name>/`
(the profile and the spec it was written against); a file path to a copy works too.

### YouTube (`profiles/youtube`)

```json
{ "Type": "openapi", "Id": "yt", "Network": { "Hosts": [ "www.googleapis.com" ] },
  "Credentials": { "youtube": { "Env": "YOUTUBE_KEY", "Kind": "Header", "HeaderName": "X-Goog-Api-Key", "Host": "www.googleapis.com" },
                   "pseudonym-key": { "Env": "SOCIAL_PSEUDONYM_KEY", "Kind": "Secret" } },
  "Config": { "Profile": "package:OpenTail.Profiles.YouTube/youtube", "Channels": [ "UCxxxxxxxxxxxxxxxxxxxxxx" ] } }
```

| Tool | What it does | The model may set |
|---|---|---|
| `yt.search` | recent public videos matching words, newest first | `q` (required, 256), `order` (date, relevance, viewCount), `channelId` (one of `Channels`), `maxResults` (1 to 50, default 10), `publishedAfter` |
| `yt.get_video` | one video with view, like and comment counts | `id` (11 characters) |
| `yt.get_comments` | top comments under a video | `videoId`, `order` (relevance, time), `maxResults` (1 to 100) |

* The API key goes in the `X-Goog-Api-Key` header, never in the URL. Channel names and comment authors are pseudonyms, and so are
  @mentions in titles, descriptions and comments.
* `Channels` is optional. With it, a search may name only those channels; without it, searches cover all of YouTube and cannot name a
  channel. (Unlike the old Social module, a search without a channel is still allowed when `Channels` is set.)
* Google publishes a Discovery document, not OpenAPI, so `youtube.openapi.json` is a hand-written subset of the three operations, with
  Google's method ids. Each call costs quota (search 100 units, the others 1).

### X (`profiles/x`)

```json
{ "Type": "openapi", "Id": "x", "Network": { "Hosts": [ "api.x.com" ] },
  "Credentials": { "x": { "Env": "X_BEARER_TOKEN", "Host": "api.x.com" } },
  "Config": { "Profile": "package:OpenTail.Profiles.X/x" } }
```

| Tool | What it does | The model may set |
|---|---|---|
| `x.search` | public posts from the last seven days; X search syntax (`-is:retweet`, `lang:en`, `conversation_id:<id>` for a thread) | `query` (required, 512), `max_results` (10 to 100), `sort_order` |
| `x.get_post` | one post by id | `id` (digits) |

* Authors are shown as **pseudonymised account ids**, not names: X returns names in a separate list (`includes.users`) and a profile
  reads one item at a time (see the limits below). @mentions in the text are pseudonymised.
* `x.openapi.json` is a subset of X's published OpenAPI document with the same operation ids, so the full document can replace it.
  Recent search needs a paid X API tier.

### Jira (`profiles/jira`)

```json
{ "Type": "openapi", "Id": "jira", "Network": { "Hosts": [ "acme.atlassian.net" ] },
  "Credentials": { "jira": { "Env": "JIRA_API_TOKEN", "Kind": "Basic", "UserName": "bot@acme.example", "Host": "acme.atlassian.net" } },
  "Config": { "Profile": "package:OpenTail.Profiles.Jira/jira", "ServerUrl": "https://acme.atlassian.net", "Projects": [ "OPS", "IT" ] } }
```

| Tool | What it does | The model may set |
|---|---|---|
| `jira.search_issues` | issues in one allowed project, newest update first, through the [query builder](#reference-queries) (JQL) | `project` (required, one of `Projects`), `status`, `type`, `priority`, `assignee` (account id), `labels`, `updated_since`, `created_since`, `text`, `maxResults` (up to 50) |
| `jira.get_issue` | one issue by key: summary, status, people, dates, description | `issueIdOrKey` (`OPS-123`) |
| `jira.list_comments` | an issue's comments, newest first | `issueIdOrKey`, `maxResults` |

* **Searches are limited to `Projects`; reading by key is not.** `get_issue` and `list_comments` read any issue the account can
  see, so give the profile an account that sees only the allowed projects.
* Version 2 of the REST API, so descriptions and comments are plain text (wiki markup), not Atlassian Document Format.
* Read only. Search results stop at 50 (the next-page token is not passed on).
* `jira.openapi.json` is a subset written from Atlassian's reference with its operation ids; **not yet run against a live site.**

### Confluence (`profiles/confluence`)

```json
{ "Type": "openapi", "Id": "wiki", "Network": { "Hosts": [ "acme.atlassian.net" ] },
  "Credentials": { "confluence": { "Env": "CONFLUENCE_API_TOKEN", "Kind": "Basic", "UserName": "bot@acme.example", "Host": "acme.atlassian.net" } },
  "Config": { "Profile": "package:OpenTail.Profiles.Confluence/confluence", "ServerUrl": "https://acme.atlassian.net", "Spaces": [ "ENG", "HR" ] } }
```

| Tool | What it does | The model may set |
|---|---|---|
| `wiki.search_pages` | pages in one allowed space, newest change first, through the [query builder](#reference-queries) (CQL) | `space` (required, one of `Spaces`), `text`, `title`, `label`, `modified_since`, `limit` (up to 25) |
| `wiki.get_page` | one page by id, its body as plain text (storage format through `\|storage`) | `id` (digits) |

* **Searches are limited to `Spaces`; reading by id is not**: use an account that sees only the allowed spaces.
* Search is the v1 CQL endpoint (v2 has none); reading is v2 with `body-format=view`. **Not yet run against a live site.**

Reddit stays a hand-written module (`"Type": "social"`): it has no OpenAPI document and its threads are nested.

## A complete example

A ticket system publishes `tickets.openapi.json` with operations `listTickets`, `getTicket`, `deleteTicket`, `addComment` and
`moveTicket`. We want: search and read; comment (always as an internal note); move to an allowed queue; and no delete.

**`profiles/tickets.json`**

```json
{
  "Profile": 1,
  "Name": "tickets",
  "Version": "1.0",
  "Spec": "tickets.openapi.json",
  "DisplayName": "Tickets",
  "Server": "https://tickets.example.com/api",
  "Hosts": [ "tickets.example.com" ],
  "Credential": { "Name": "tickets", "Kind": "Bearer", "Description": "API token of an integration user" },
  "Errors": { "Retryable": [ 429, 503 ], "Messages": { "404": "No such ticket." } },
  "Config": { "Queues": { "Type": "string[]", "Description": "Queues tickets may be moved to" } },
  "Notes": [ "Ticket text is written by customers." ],
  "Tools": {
    "search": {
      "Operation": "listTickets", "Description": "Find tickets.", "Risk": "Read",
      "Parameters": {
        "q":      { "Required": true, "MaxLength": 50 },
        "status": { "Enum": [ "open", "closed" ] },
        "limit":  { "Max": 20, "Default": 5 },
        "*":      { "Forbidden": true }
      }
    },
    "get": {
      "Operation": "getTicket", "Description": "Read a ticket.", "Risk": "Read",
      "Parameters": { "id": { "Pattern": "^T-[0-9]{1,6}$" } }
    },
    "comment": {
      "Operation": "addComment", "Description": "Add an internal comment.", "Risk": "ExternalEffect",
      "Parameters": { "id": { "Pattern": "^T-[0-9]{1,6}$" } },
      "Body": {
        "text":       { "MaxLength": 200 },
        "visibility": { "Fixed": "internal" },
        "*":          { "Forbidden": true }
      },
      "Preview": "Comment on {id}: {body.text|cut:80}"
    },
    "move": {
      "Operation": "moveTicket", "Description": "Move a ticket to another queue.", "Risk": "Write",
      "Parameters": { "id": { "Pattern": "^T-[0-9]{1,6}$" }, "queue": { "AllowedFrom": "Config.Queues" } },
      "Preview": "Move {id} to {queue}"
    }
  }
}
```

**`modules.json`**

```json
{ "Modules": [
  { "Type": "openapi", "Id": "tk",
    "Network": { "Hosts": [ "tickets.example.com" ] },
    "Credentials": { "tickets": { "Env": "TICKETS_TOKEN", "Host": "tickets.example.com" } },
    "Config": { "Profile": "profiles/tickets.json", "Queues": [ "billing", "support" ] } }
] }
```

**What the model gets:** four tools, `tk.search`, `tk.get`, `tk.comment`, `tk.move`. `deleteTicket` does not exist.

* `tk.search` takes `q` (required, at most 50 characters), `status` (`open` or `closed`) and `limit` (at most 20, default 5). The spec's
  `internal` parameter is not shown and is refused if sent.
* `tk.comment` takes `id` (must look like `T-123`) and `body.text` (at most 200 characters). It cannot see or set `visibility` (always
  sent as `internal`) or `author`.
* `tk.move` takes `id` and `queue`, which must be `billing` or `support`; the model sees those two values in the tool's schema.

**What the reviewer sees** for a comment: `Comment on T-1: We are replacing the toner today.` For a move: `Move T-1 to billing`.

**Policy** (`--adopt-policy tk` writes this suggestion; reads run, everything else waits for a reviewer):

```json
{ "Tools": { "tk.search": { "Mode": "Execute" }, "tk.get": { "Mode": "Execute" },
             "tk.comment": { "Mode": "Queue", "ExpireAfter": "24:00:00" }, "tk.move": { "Mode": "Queue", "ExpireAfter": "24:00:00" } } }
```

This example is the one the tests use (`OpenTail.Orca.Tests/OpenApiProfileTests.cs`, against a local fake server).

## How a call flows

1. The model sees only the profile's tools, with schemas narrowed by the rules (hidden parameters removed, limits and allowed values added).
2. A call arrives. The Host's policy decides `Execute`, `Queue` or `Deny`, and taint can turn `Execute` into `Queue` for high-impact tools.
3. For tools that are not Read, the **preview** runs first: the rules are applied (refuse what may not be set, check every value, add
   defaults and fixed values) and the preview sentence is rendered. A value that breaks a rule is **refused at once** (`Denied`,
   `invalid_arguments`), before anyone is asked to review.
4. When the call runs (immediately, or after approval), the same rules are applied again, the request is built against the fixed server
   (arguments can never change scheme, host or base path), and sent through the Host's guarded client, which attaches the credential only
   for its bound host.
5. The answer comes back marked as outside content (unless the profile says otherwise). If the tool has a `Response` rule, only the
   fields it names reach the model, text is cut and authors are pseudonyms. Errors use the profile's wording.

Read tools have no preview, so for them a broken rule fails the call when it runs (`Failed`).

## Reference: the profile file

Unknown keys are refused, everywhere in the file: a typo is an error, not something silently ignored. Keys are case-insensitive.

| Key | Required | Meaning |
|---|---|---|
| `Profile` | yes | Format version. Must be `1`. |
| `Name` | yes | Short name, shown in `--list-modules` and errors. |
| `Version` | no | The profile's own version, shown to operators. |
| `Spec` | yes | Path to the OpenAPI document (v2 or v3, JSON or YAML), relative to the profile file. |
| `DisplayName`, `Publisher`, `Description` | no | Shown to operators (manifest). |
| `Server` | no | Base URL of the API. Must fall within `Hosts`. Without it, the spec's first server is used (the guarded client still reaches only the operator's hosts). |
| `Hosts` | yes | The hosts the profile talks to: `api.example.com`, `*.example.com`, or an origin `https://host:8443`. The operator's `Network.Hosts` must fall within these. |
| `Credential` | no | `{ "Name", "Kind", "HeaderName", "Description", "Optional" }`. `Kind`: `Bearer` (default), `Basic`, `Header` (needs `HeaderName`). The operator maps `Name` under `Credentials` and binds it to a host. |
| `Untrusted` | no | Responses are outside content and taint the caller. Default `true`. A tool can override it. |
| `Errors` | no | See [errors](#reference-errors). |
| `Config` | no | Settings the operator gives next to `Profile` in modules.json: `{ "Name": { "Type": "string[]" or "string", "Required": bool, "Description": "..." } }`. Rules refer to them with `AllowedFrom`. |
| `Notes` | no | Plain statements for the operator ("ticket text is written by customers"). |
| `Tools` | yes | Tool name to tool. Names are lower case letters, digits and `_` (up to 48); the model sees `<module id>.<name>`. |

**A tool:**

| Key | Required | Meaning |
|---|---|---|
| `Operation` | yes | The spec's `operationId` (or the generated name of an operation without one, as `--list-modules` of a plain import shows it). |
| `Risk` | yes | `Read`, `Write`, `ExternalEffect` or `Destructive`. Stated, never guessed. Anything other people can see (a comment, a message, a ticket) is `ExternalEffect`: taint holds only `ExternalEffect` and `Destructive`. |
| `Description` | no | What the model reads. Default: the spec's summary. Write it for the model: what the tool does and when to use it. The HTTP method and path are always appended (`Add an internal comment. [POST /tickets/{id}/comments]`), so everyone can see what the tool really calls. |
| `Untrusted` | no | Overrides the profile's `Untrusted` for this tool. |
| `Parameters` | no | Rules per path or query parameter; `"*"` applies to every parameter not named. See [rules](#reference-argument-rules). |
| `Body` | no | Rules per top-level property of the JSON body; `"*"` as above. |
| `Preview` | for every tool that is not `Read` | The sentence a reviewer reads. See [previews](#reference-previews). |
| `Response` | no | What of the answer reaches the model. See [responses](#reference-responses). Without it, the answer as the API sends it. |
| `Query` | no | Typed filters the Host turns into one filter expression (JQL, OData, SOQL, Zendesk, Freshdesk). `Read` tools only. See [queries](#reference-queries). |

## Reference: argument rules

A rule applies to one parameter (under `Parameters`) or one top-level body property (under `Body`). A parameter or property without a
rule is taken as the spec describes it, unless `"*"` has a rule; `"*": { "Forbidden": true }` is the usual way to say "nothing else".

| Rule | Effect on what the model sees | Effect on a call |
|---|---|---|
| `"Fixed": value` | hidden | always sent with this value; refused if the model sends it |
| `"Forbidden": true` | hidden | refused if sent; never sent |
| `"Default": value` | shown with `default`, not required | sent when the model leaves it out |
| `"Required": true/false` | listed (or not) as required | overrides the spec |
| `"Pattern": "regex"` | shown as `pattern` | the value must match (.NET regular expression, non-backtracking, 200 ms limit) |
| `"MaxLength": n` | shown as `maxLength` | at most n characters |
| `"Min": n`, `"Max": n` | shown as `minimum`, `maximum` | the value must be a number in range |
| `"Enum": [ ... ]` | shown as `enum` | must be one of the values (exact) |
| `"AllowedFrom": "Config.Name"` | shown as `enum` with the operator's values | must be one of the operator's list (case-insensitive). **No list configured means nothing is allowed.** |
| `"Description": "..."` | replaces the spec's description | none |

Rules combine (a value can have `Pattern` and `MaxLength`). `Forbidden` cannot be combined with `Fixed`, `Default` or `Required`; `Fixed`
and `Default` cannot both be set.

Fixed and default values are written as JSON: `"Fixed": "internal"`, `"Default": 5`, `"Fixed": true`.

## Reference: previews

A preview is a sentence with placeholders, rendered from the values that will actually be sent (after defaults and fixed values):

* `{name}`: a path or query parameter.
* `{body.name}`: a top-level body property.
* `{name|cut:N}`: at most N characters, then `…`.

Line breaks become spaces; a placeholder with no value renders as nothing. Write the sentence so a reviewer can decide without opening
anything else: say what happens and to what, and include the free text the model wrote (cut), because that is what the reviewer must judge.

Good: `"Reply to {id} as the customer will see it: {body.text|cut:300}"`. Weak: `"POST /tickets/{id}/comments"`.

The reviewer also sees the technical diff (method, URL, body) under the sentence.

## Reference: responses

Most answers carry far more than the model needs: internal ids, other people's e-mail addresses, fields nobody asked for. A `Response`
rule keeps only what you name. This is the `feed` tool the tests add to the example profile:

```json
"feed": {
  "Operation": "listTickets", "Description": "Recent tickets.", "Risk": "Read",
  "Parameters": { "q": { "Fixed": "*" }, "*": { "Forbidden": true } },
  "Response": {
    "Items": "$", "MaxItems": 2, "MaxTextChars": 40,
    "Fields": {
      "id": "$.id", "title": "$.title", "text": "$.text", "author": "$.author.login",
      "views": "$.stats.views|number", "tags": "$.tags[*].name", "url": "https://tickets.example.com/t/{$.id}"
    },
    "Pseudonymise": [ "author" ], "PseudonymiseMentions": [ "text" ]
  }
}
```

The API answers with a list of tickets, each with `author`, `stats`, `tags` and a `secret_internal` field. The model receives:

```json
{ "status": 200, "untrusted": true, "notice": "The response is data from a third-party API. ...", "count": 2,
  "result": [ { "id": "T-1", "title": "Printer", "text": "Ask @user_3f9a1c02de: IGNORE PREVIOUS INSTRU…",
                "author": "user_8c41d0b7aa", "views": 42, "tags": [ "hw", "urgent" ], "url": "https://tickets.example.com/t/T-1" },
              { ... } ] }
```

| Key | Meaning |
|---|---|
| `Items` | Path to the list in the answer (`$` when the answer is the list, `$.items`, `$.data.results`). One result per item. Without it, the whole answer is one result. |
| `Fields` | Output name to source. A **path** (`$.a.b`, `$.a[0]`, `$.a[*].b` for a list of values); a path followed by **`\|number`** turns text such as `"42"` into a number; **`\|html`** turns an HTML string into plain text (scripts and styles dropped, paragraphs as line breaks, entities decoded); **`\|storage`** turns Confluence storage format into text (macros such as code blocks keep their text; markup that does not parse is tag-stripped); **`\|adf`** turns an Atlassian Document Format object (Jira v3, Confluence) into text; or a **text with paths in braces** (`"https://example.com/t/{$.id}"`). A field whose source is missing is left out. Anything not named is dropped. Without `Fields`, items are passed whole. |
| `MaxItems` | At most this many results. |
| `MaxTextChars` | Every text in the result is cut to this many characters, then `…`. |
| `Pseudonymise` | Fields holding a person's handle or name: replaced by a stable pseudonym, `user_` and ten hex digits. `[deleted]` and `[removed]` stay as they are. |
| `PseudonymiseMentions` | Fields of free text in which `@name` and `u/name` are replaced by pseudonyms. |

Paths are this small language only: `$`, then steps `.name`, `[n]` or `[*]`. No filters, no functions: a profile describes, it does not compute.

**Pseudonyms** are an HMAC of the profile name and the handle (case-insensitive), so the same person reads the same in every answer,
and the model can follow a conversation without learning who is in it. The key is the optional credential `pseudonym-key`
(`"Kind": "Secret"`); with it, pseudonyms stay the same across restarts and processes. Without it, a random key per process is used, so
they change on restart. A profile that pseudonymises lists `pseudonym-key` among its credentials (optional) and says so in its notes.

When a tool has a `Response` rule and the API does not answer with complete JSON (or the answer is larger than `MaxResponseBytes`),
the call **fails**; the raw answer is never passed on instead.

## Reference: queries

Some APIs take one filter expression: Jira's JQL, OData's `$filter`, Salesforce's SOQL, Zendesk and Freshdesk search. **The model
never writes the expression.** The profile declares typed filters; the model sets their values; the Host writes the expression,
quoting and escaping every value for the language, and refuses a value it cannot quote safely. The parameter that carries the
expression is hidden from the model, and sending it is refused.

```json
"search_issues": { "Operation": "searchForIssuesUsingJql", "Risk": "Read",
  "Parameters": { "maxResults": { "Fixed": 20 }, "*": { "Forbidden": true } },
  "Query": { "Language": "jql", "Target": "jql",
             "Where": "issuetype != Epic", "OrderBy": "ORDER BY updated DESC",
             "Filters": {
               "project":       { "Field": "project", "AllowedFrom": "Config.Projects", "Required": true },
               "status":        { "Field": "status", "Enum": [ "Open", "In Progress", "Done" ] },
               "updated_since": { "Field": "updated", "Type": "date", "Op": "ge" },
               "labels":        { "Field": "labels", "Type": "list" },
               "text":          { "Type": "text" } } } }
```

With `{"project":"OPS","status":"In Progress","labels":["vpn"],"text":"printer jam"}` the Host sends
`jql=(issuetype != Epic) AND project = "OPS" AND status = "In Progress" AND labels in ("vpn") AND text ~ "printer jam" ORDER BY updated DESC`.

| Key | Meaning |
|---|---|
| `Language` | `jql`, `cql` (Confluence; written exactly as JQL), `odata`, `soql`, `zendesk` or `freshdesk`. |
| `Target` | The query parameter that receives the expression, or `body.<field>` for a body field. |
| `Select` | SOQL only, required: `SELECT Id, Subject FROM Case`. `WHERE` is added when there are clauses. |
| `Where` | A clause always ANDed in, as written. It is the profile's, so it is trusted: use it to pin a project or `type:ticket`. |
| `OrderBy` | Appended as written (JQL, SOQL): `ORDER BY updated DESC`, `ORDER BY LastModifiedDate DESC LIMIT 50`. |
| `MaxLength` | Longest expression sent (default 2000). Freshdesk allows 512. |
| `Filters` | Argument name to filter. Names are lower case letters, digits and `_`, and must differ from the operation's parameters. |

**A filter:** `Field` (the API's field name: letters, digits, `_ . -`, or `cf[123]`; not needed for free text in JQL and Zendesk),
`Type` (`string` default, `number`, `bool`, `date`, `datetime`, `text` for free-text search, `list` for any of several strings),
`Op` (`eq` default, `ne`, `gt`, `ge`, `lt`, `le`; not for `text` or `list`), `Required`, `Description`, and the value checks of
[argument rules](#reference-argument-rules): `Enum`, `AllowedFrom`, `Pattern`, `MaxLength` (default 200), `Min`, `Max`, plus
`MaxItems` for lists (default 20).

How values are written:

| Language | Strings | Dates | Free text | Lists |
|---|---|---|---|---|
| `jql` | `"..."`, `\` and `"` escaped | `"2026-10-01"`, `"2026-10-01 08:00"` | `text ~ "..."`, Lucene operator characters replaced by spaces | `f in ("a", "b")` |
| `odata` | `'...'`, `'` doubled | `2026-10-01`, `2026-10-01T08:00:00Z` | `contains(Field,'...')` | `(f eq 'a' or f eq 'b')` |
| `soql` | `'...'`, `\` and `'` escaped | as OData | `Field LIKE '%...%'`, `%` and `_` escaped | `f IN ('a', 'b')` |
| `zendesk` | `"..."`; a `"` inside is refused (no escape exists) | `2026-10-01` | `"..."` | `f:"a" f:"b"` |
| `freshdesk` | `'...'`; quotes inside are refused | `'2026-10-01'` | not available | `(f:'a' OR f:'b')` |

Every value is refused if it contains a control character (a newline, NUL). Zendesk clauses are joined by spaces, Freshdesk's by
`AND` inside the double quotes its `query` parameter needs; Freshdesk supports only `eq`, `gt` (`:>`) and `lt` (`:<`). The preview
values include `{query}`, the expression as sent. The quoting rules are from the vendors' documentation as generally known and
have not been run against the live APIs yet.

## Reference: errors

```json
"Errors": { "Retryable": [ 429, 503 ], "Messages": { "404": "No such ticket.", "403": "The integration user lacks access." } }
```

* A status in `Retryable` (default `429, 500, 502, 503, 504`) fails the call as **retryable**, with "try again after N s" when the API
  sends `Retry-After`.
* `Messages` gives the text for a status. Without one: "`<DisplayName>` answered HTTP `<code>`."
* Every failure has a **kind**, which the Host's retry and inspection rules act on, and a code `http_<status>`. By default 401, 403
  and 407 are `Auth`, 408, 429, 502, 503 and 504 (and anything in `Retryable`) `Transient`, 507 `Environment`, other 4xx `Contract`
  and other 5xx `Bug`. `Kinds` overrides it per status, for an API that uses a status its own way:
  `"Kinds": { "404": "Auth" }` for an API that answers 404 for something the caller may not see (GitHub does, for private
  repositories). When unsure, choose the stricter kind: `Auth` over `Contract`, anything over `Transient`.
* **The remote body is never shown.** It can contain anything, including text written to steer the model.
* Redirects are not followed.

## Enabling a profile in modules.json

```json
{ "Type": "openapi", "Id": "tk",
  "Network": { "Hosts": [ "tickets.example.com" ] },
  "Credentials": { "tickets": { "Env": "TICKETS_TOKEN", "Host": "tickets.example.com" } },
  "Config": { "Profile": "profiles/tickets.json", "Queues": [ "billing", "support" ] } }
```

* `Config.Profile` is the profile file, relative to `modules.json`, or `package:<Assembly>/<profile>` for a profile inside a package the
  deployment references (see [shipping a profile in a package](#shipping-a-profile-in-a-package)). A package that is not referenced is
  refused with its name; nothing is loaded from a folder of packages.
* Besides the profile's own settings, `Config` accepts `ServerUrl` (another base URL within the profile's `Hosts`, such as a test
  instance), `AllowInsecureHttp` (plain http, for local testing only) and `MaxResponseBytes` (default 65536). Anything else is refused.
* The module's id (`tk`) prefixes every tool and names the policy fragment (`policy.d/tk.json`). Two instances of one profile need two ids.
* Everything else works as for any module: `--list-modules` shows the profile's tools and risks, `--adopt-policy tk` writes its suggested
  policy, `"Isolation": { "Mode": "Process" }` runs it in a worker, `Enabled: false` retires it. See [MODULES.md](MODULES.md).

## What is checked when a profile loads

The Host refuses to start, naming the problem, when:

* the file is not valid JSON or has an unknown key;
* `Profile`, `Name`, `Spec`, `Hosts` or `Tools` is missing, or `Profile` is not `1`;
* a tool has no `Risk`, or is not `Read` and has no `Preview`;
* a tool names an operation the spec does not have, or a rule names a parameter or body property the operation does not take;
* a rule combines `Forbidden` with `Fixed`, `Default` or `Required`, or `Fixed` with `Default`; a `Pattern` is not a valid expression;
* `AllowedFrom` names something that is not a `string[]` setting under `Config`;
* `Server` (or the operator's `ServerUrl`) is outside `Hosts`;
* modules.json gives a setting the profile does not have, or leaves out one marked `Required`;
* the operator's `Network.Hosts` are outside the profile's `Hosts` (the usual module check).

## Security model

**What a profile cannot do:** run code; reach a host the operator did not list; see or attach a credential except the one the operator
mapped and bound to a host; change policy, taint or review; make an argument change the scheme, host or base path of a request; show the
remote body in errors.

**What a profile can still get wrong**, so read it before enabling it: it states each tool's risk and description, so a careless or
malicious profile could call a destructive operation `Read` or describe it misleadingly. Defences: `--list-modules` shows every tool with
its risk, and every tool's description ends with its HTTP method and path (a `Read` tool ending in `[DELETE ...]` stands out); `--adopt-policy` refuses `Execute` for anything but `Read` without `--allow-execute`; taint and review still
apply to whatever the profile calls `ExternalEffect` or `Destructive`. Treat a profile from someone else like a small module: read it.

**Writes are not retried automatically.** A profile tool is not idempotent, so a write interrupted by a crash waits for a reviewer
instead of being sent again.

## Writing a profile, step by step

1. **Get the spec.** Download the vendor's OpenAPI document and keep it next to the profile (it is the contract you tested against).
2. **List what the agent needs to do**, in plain words, and find the operation for each (`operationId`).
3. **For each tool, decide the risk.** Read only? `Read`. Changes only your own data that you can restore? `Write`. Anything another
   person can see, or that leaves the company? `ExternalEffect`. Deletes or closes? `Destructive`.
4. **Close every door.** Start each tool with `"*": { "Forbidden": true }` under `Parameters` (and `Body` for writes), then allow
   parameters one by one, each with the tightest rule that still works: a `Pattern` for ids, `Enum` or `AllowedFrom` for choices,
   `MaxLength` for text, `Max` for page sizes, `Fixed` for anything the model should never choose (visibility, author, tenant).
5. **Write previews for people**, not developers.
6. **Write the descriptions for the model**: what the tool does, what the arguments mean, when not to use it.
7. **Map errors**: the codes that mean "later" under `Retryable`, a sentence for the common failures under `Messages`.
8. **Add `Notes`** for the operator: who writes the content (customers? anyone?), what the credential needs to allow.
9. **Load it**: `my-host --policy policy.json --modules modules.json --list-modules`, then `--adopt-policy <id>`.
10. **Test it** against a fake server (below), then once against the real service with a test account.

## Checking a profile (conformance)

Every profile can be checked without a Host, an account or the network:

```csharp
var report = OpenTail.Modules.OpenApi.ProfileConformance.Check("profiles/crm/crm.json");   // or "package:Contoso.Profiles.Crm/crm"
Assert.Empty(report.Findings);                                                              // a profile ready to ship has none
foreach (var t in report.Tools) Console.WriteLine($"{t.Name} {t.Risk} untrusted={t.ReadsUntrusted} preview={t.Preview}");
```

`Check` loads the profile and its spec exactly as the Host does, so every [load-time error](#what-is-checked-when-a-profile-loads)
throws with its reason. It returns each tool as the model would see it (name, risk, description with `[METHOD path]`, the narrowed JSON
schema) and the **findings**: what the profile probably should not leave open.

| Finding | Why it matters |
|---|---|
| `has no Notes` | The operator should know who writes the content and what the credential must allow. |
| `tool 'x' has no Description` | The model reads the spec's summary, written for developers. |
| `tool 'x' is <risk> and lets the model set parameters the profile does not name (...)` | A write with an open door; add `"*": { "Forbidden": true }`. |
| `tool 'x' is <risk> and lets the model set any body field` | The same for the body. |
| `tool 'x': the Preview names no argument` | Every call reads the same to a reviewer, who cannot tell them apart. |
| `tool 'x': path parameter 'p' has no Pattern, Enum or AllowedFrom` | Path values choose which record is touched; constrain them. |
| `tool 'x' has no Response rule, so the whole answer reaches the model` | Outside content the model does not need, and data you may not want to pass on. |
| `tool 'x': the Response rule has no Fields` | The same, per item. |
| `tool 'x' is marked as not returning outside content` | Only true when nobody outside the deployment can write what it returns. |

The same findings appear as warnings when the Host loads the profile: at startup, in `--list-modules` and in `--lint`
(`warning`-free output means the profile has none). Put `ProfileConformance.Check` with `Assert.Empty(report.Findings)` in your own tests
or CI; OpenTail's tests do this for every shipped profile, in both file and package form.

## Shipping a profile in a package

A profile package is a class library with **no code**: the profile and its spec as resources named `profiles/<name>/<file>`, and a
reference to `OpenTail.Modules.OpenApi` so that referencing the profile brings the module.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Contoso.Profiles.Crm</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenTail.Modules.OpenApi" Version="0.1.*" />
    <EmbeddedResource Include="crm\*.json" LogicalName="profiles/crm/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

The deployment references the package and enables it with `"Profile": "package:Contoso.Profiles.Crm/crm"`. Profiles then version,
ship and update like any other package, and an operator sees the package name and version in `--list-modules`. OpenTail's own profile
packages are built this way (`OpenTail.Profiles.YouTube`, `OpenTail.Profiles.X`); an architecture test checks that they hold no code.

## Testing a profile

`OpenTail.Orca.Tests/OpenApiProfileTests.cs` is a template: it writes the spec and profile to a temporary folder, starts a local fake
server (`StubServer`) that records each request, loads the module from modules.json with `AllowInsecureHttp`, and checks:

* only the profile's tools exist, with the stated risks;
* the model's schema hides what it must not set and shows limits and allowed values;
* each rule refuses what it should (one case per rule), and nothing reaches the server when it does;
* fixed and default values are actually sent;
* response rules keep only the named fields, cut text, turn text into numbers, and pseudonymise authors and mentions (stable with a key);
* previews read as intended, and bad arguments to writes are `Denied` before review;
* error messages and retryable codes, and that the remote body never appears.

Copy it, swap in the spec and profile, keep the cases.

## Troubleshooting

| Message | Cause |
|---|---|
| `not a valid profile: ... could not be mapped` | An unknown or misspelt key; the message names it. |
| `tool 'x' names operation 'y', which the spec does not have` | Use the spec's `operationId`; check case. |
| `has a rule for 'p', which operation 'y' does not take` | The parameter is a header (not supported yet) or misspelt. |
| `a Write tool needs a Preview template` | Every tool that is not `Read` needs `Preview`. |
| `Config.X is not a setting of profile ...` | modules.json gives a setting the profile does not declare. |
| `'queue' must be one of Config.Queues, and the operator has not configured any` | Add the list next to `Profile` in modules.json. |
| `The server ... is not one of profile ...'s hosts` | `ServerUrl` or `Server` outside `Hosts`. |
| `Argument 'x' cannot be set` | The model sent something fixed or forbidden (expected, if the model guessed). |
| `'x' is pseudonymised but is not one of Fields` | `Pseudonymise` and `PseudonymiseMentions` name output fields; add the field to `Fields`. |
| `'...' is not a path` | Paths start with `$`: `$.items`, `$.a.b`, `$.tags[*].name`. |
| `... did not answer with complete JSON` | The tool has a `Response` rule and the answer was not JSON, or was cut at `MaxResponseBytes` (raise it in modules.json). |
| `Policy names unknown tool 'x.y'` | The policy mentions a tool the profile does not have (renamed or removed). |
| `Profile package X is not referenced by this deployment` | Add the package to the deployment project. |
| `X has no profiles/<name>/<name>.json (profiles in it: ...)` | The part after the `/` names a profile the package does not contain. |
| `warning: profile x: ...` in `--lint` | A conformance finding; see [checking a profile](#checking-a-profile-conformance). |

## Limits today, and what is planned

Today:

* Path and query parameters and JSON bodies only. Header and cookie parameters, form bodies and file uploads are not supported.
* Body rules apply to top-level properties; nested objects are passed as the model sends them (use `"*": { "Forbidden": true }` and
  allow only flat fields when that matters).
* Without a `Response` rule, answers are returned as the API sends them (up to `MaxResponseBytes`), wrapped with the untrusted notice.
* Response fields come from one item at a time: a result cannot combine data from two places in the answer (such as a user list
  elsewhere in the document). An API that returns authors that way (X's `includes.users`) shows author ids, pseudonymised.

Planned (the v4 plan is in `../docs/done/MCP-HOST-V4-PLAN.md`):

* More shipped profiles on the [query builder](#reference-queries): Zendesk, Freshdesk (Jira is shipped). ServiceNow stays a hand-written module:
  its writes need a lookup before the call, which profiles do not do.

