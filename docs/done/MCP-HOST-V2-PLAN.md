# OpenTail Host v2: from "one developer, one machine" to "a small team"

> **Archived 2026-11-12.** Read [`OpenTail.Host/STATUS.md`](../../OpenTail.Host/STATUS.md) for what is built and what is still open. The status line below was written when this was a draft and is not kept up to date; the implementation log at the end is the record.

Status: plan, revision 2, 2026-10-15. Builds on `MCP-HOST-PLAN.md` (v1, phases 0-8, done) and `OpenTail.Host/PACKAGING.md`.
Branch: `host/modular-mcp`. Rule for this work: commit locally per phase, never push, no subagents except the one
design review the owner asked for (its outcome is section 16).

> **Revision 2.** An independent design review found real holes in revision 1 (rule logic that could let a call
> through, taint that a model could launder, idempotency keys shared across users, agent credentials that could
> approve). They are folded in below; section 16 lists each finding and what was decided. Phase order changed to
> **V1 foundations -> V2 HTTP + sign-in -> V3 conditional policy -> V4 notifications -> V5 resumable -> V6 approval
> controls -> V7 docs/packaging**, because review links need a reachable address, and budgets and ownership need
> identity.

## 1. Why

A real run of the v1 sample with a real MCP client (Claude Code) showed the model works: the AI asks, a reviewer
decides, the AI sees the reason and acts on it. Using it with a team exposes four gaps:

| Gap | What goes wrong today |
|---|---|
| Nobody is told | A queued action sits until it expires. Reviewers must keep a localhost page open. |
| "Queue every risky call" does not scale | After ten approvals people click yes without reading. The policy cannot say "mail to our own domain is fine, anything else waits". |
| One user, stdio, loopback | No sign-in, no per-person identity, requesters are "anonymous", one process per client. |
| The AI's turn ends before the decision arrives | The chat is closed when the approval comes in. Nothing tells the model next time. |

Defects found while reading the code (fixed in V1; they matter the moment there is more than one caller):

- `get_action_status`, `cancel_action`, `resubmit_action` accept any `actionId`/`sessionId` from any caller (IDOR; a caller
  can become another requester by naming their session).
- Sessions are in memory only: a restart gives a tainted conversation a **clean** session. Independently, the model can
  call `start_session` at any time and get a clean one, and taint is applied when a tool *executes*, not when it is
  *submitted*, so two parallel calls ("read mail" + "send") both see a clean session.
- The idempotency unique index is `(tool, idem_key, args_hash)`: another user sending the same key and arguments gets
  the first user's record, result included.
- Trust promotions are an in-memory set per process.

## 2. Principles (v1 principles, restated, plus two)

1. Policy comes only from the operator's file. Never from arguments, model output or tool results.
2. Deny by default. Rules may loosen Queue to Execute only where the operator wrote an explicit `Execute` rule, and a
   tainted caller is never loosened (no per-rule opt-out: an opt-in would be an injection target).
3. Taint is one-way, survives restarts, and follows the *person/credential*, not just the chat (see V1).
4. Audit never contains argument values or secrets. Notifications never contain argument values by default.
5. Modules stay ignorant of all of this.
6. Every new capability is optional and lives in a package a stdio-only deployment does not have to pull in.
7. **Fail closed.** Anything the Host cannot evaluate, parse or attribute resolves to the stricter outcome.
8. **Agents cannot approve.** Credentials that may call tools can never call review endpoints.

## 3. Spec and platform check (done 2026-10-15)

- **MCP authorization (2026-07-28):** an MCP server is an OAuth 2.1 *resource server*. It MUST publish OAuth 2.0
  Protected Resource Metadata (RFC 9728) and answer 401 with `WWW-Authenticate: Bearer resource_metadata="..."`. Clients
  find the authorization server from that. Servers validate tokens for *this* resource (audience) and must not pass
  them through. Client registration (Client ID Metadata Documents) is the authorization server's business.
- **C# SDK 1.4.1** (`ModelContextProtocol.AspNetCore`, in the NuGet cache):
  `AddMcpServer(...).WithHttpTransport(o => o.Stateless = true)`, `MapMcp()`, `AddAuthentication().AddJwtBearer().AddMcp(o => o.ResourceMetadata = ...)`
  which serves `/.well-known/oauth-protected-resource` and produces the 401 challenge. The caller's `ClaimsPrincipal` is
  `request.User` (`MessageContext.User`) in handlers. Stateless mode has no server-to-client messages and no
  `Mcp-Session-Id`; that fits the Host (its sessions are its own handles) and the 2026-07-28 direction.
- **Tasks extension (`io.modelcontextprotocol/tasks`):** the right long-term shape for "pending approval" (task handle,
  `tasks/get`, `notifications/tasks`), but SDK 1.4.1 lacks it and few clients negotiate it. **Decision:** not now; V5
  builds what works with every client today. Mapping kept for later: `Pending/Approved/Executing -> working`,
  `Succeeded -> completed`, `Failed/Denied/Rejected/Expired -> failed`, `Cancelled -> cancelled`,
  `ChangesRequested -> failed` with the reason.
- **Origin checks:** Streamable HTTP requires servers to validate `Origin` (DNS rebinding). The SDK does not; we add it.

## 4. Package and layering changes

```
Host.Abstractions   + CallerIdentity (namespaced subjects), ActionEvent/IActionEventSink, new ActionRecord fields,
                      admission checks on IActionStore, an in-transaction transition overload
Host                + persisted sessions and subject taint, rules engine, notices, approval controls, events
Host.Notifications  NEW  durable outbox + dispatcher + webhook and Slack channels + reminders
Host.Http           NEW  Streamable HTTP, JWT + API-key sign-in, protected-resource metadata, admin mount, limits
Host.Admin          approval page + API, refactored to run standalone (loopback) or mounted by Host.Http
```

A stdio-only deployment is unchanged in size. `Host.Notifications` depends on `Host.Abstractions` +
`Microsoft.Data.Sqlite`. `Host.Http` depends on `Host`, `Host.Admin`, `ModelContextProtocol.AspNetCore` and
`Microsoft.AspNetCore.Authentication.JwtBearer`. Fourteen packages, one version (`host-v*` tags). SQLite is required for
sessions-across-restart, outbox, notices and trust persistence; the in-memory store stays for tests and keeps small twins
only where tests need them.

## 5. Phase V1: foundations (identity, taint, ownership, atomic admission, events)

1. **Namespaced subjects.** `CallerIdentity(Subject, DisplayName, Roles, Kind, Source)`. Subject strings are namespaced so
   different mechanisms can never collide: `local` (stdio), `admin:{name}` (static reviewer tokens), `jwt:{issuer}|{sub}`,
   `key:{name}` (API keys), `ai:{name}` (AI reviewers). An optional operator `People` alias map in the policy file groups
   several subjects as one human (used by self-approval, V6).
2. **Identity travels explicitly.** It is read from the MCP request (`request.User`, or `local` on stdio) **once, in the
   handler**, and put on `ActionRequest.Caller`. The executor, recovery, sweep and dispatchers never read ambient context
   (an `AsyncLocal` is used only to hand the value from the handler to `QueuedToolFunction`, which copies it into the request
   immediately).
3. **Requester = caller subject.** On stdio every caller is `local`, so v1 behavior is kept ("mine" = everything on this
   deployment); sessions are for taint and audit, not ownership. Over HTTP, requester = the authenticated subject.
   `MaxPendingPerRequester` and budgets therefore cannot be reset by opening a new session.
4. **Ownership on every caller-facing read and write.** `get_action_status`, `cancel_action`, `resubmit_action`,
   `wait_for_action`, `get_updates`, `list_my_actions`: only the requester's own actions; others answer `not_found`
   (same shape as a missing id). Sessions are owner-bound; a foreign session id answers like an unknown one.
5. **Persisted sessions + subject taint.** `ISessionStore` (in-memory and SQLite): `id, owner, tainted, created_at,
   last_seen_at`, idle TTL (default 14 days) with a sweep. Plus `subject_taint(subject, tainted_until)`.
   - Taint is set **at submit** of any call to a `ReadsUntrusted` tool (before it runs, even if queued), on the session
     and on the subject; the executor keeps tainting at execution too.
   - A tainted subject stays tainted for `TaintWindow` (default 30 minutes, policy file; `0` = per-session only), and
     **a new session inherits it**. So `start_session` cannot launder, and a restart cannot launder.
   - Reading the *result* of a `ReadsUntrusted` action through `get_action_status` / `wait_for_action` / (later) notices
     taints the reader the same way. Notices never carry results.
6. **Idempotency scoped to the requester.** Schema 2 adds `requester_id` to the unique index and to the lookup.
7. **Atomic admission.** `IActionStore.TryCreateAsync(record, admissions)` where an admission is
   `(filter, window, maxCount)` evaluated by SQL `COUNT` inside the `BEGIN IMMEDIATE` transaction that inserts the row.
   Used for `MaxPendingPerRequester` now and for budgets in V3, so limits cannot overshoot even across processes.
8. **In-transaction transitions.** `TransitionAsync(id, expected, Func<ActionRecord, Transition?>)` where the function
   returns the next status and updated record (or `null` to refuse), decided inside the transaction. Needed by V6.
9. **Events.** `ActionEvent` + `IActionEventSink.PublishAsync`; the queue/executor publish queued, approved, rejected,
   changes_requested, succeeded, failed, expired, cancelled, denied(queue_full). Default sink no-op; exceptions are caught
   and audited (`event_sink_error`), never raised into the action path.
10. **Record fields** (JSON column): `PolicyRule`, `ReviewerGroup`, `RequiredApprovals`. Old rows read as defaults.
11. **Trust promotions persisted** (`trust_promotions(tool, by, at)`, shared by all processes on the file); a Deny/Queue
    rule or the taint override always beats a promotion.
12. **Schema 1 -> 2** migration: an automatic backup first (`VACUUM INTO <db>.bak-v1-<utc>`), then the migration. An older
    Host refuses a newer database (already true); the docs say the migration is one-way and name the backup.

Exit: v1 tests pass (with `local`-requester adjustments documented), new tests for session persistence across restart,
subject-taint inheritance, taint at submit with a parallel read+send, IDOR, idempotency across two requesters, atomic
admission under concurrency (threads and two connections), sink failures, migration + backup.

## 6. Phase V2: HTTP transport and sign-in

**Goal:** one hosted instance for a small team; each agent and each reviewer is a known identity.

- **Transport.** `Host.Http.MapHost(app, host, options)`: `AddMcpServer(...).WithHttpTransport(Stateless = true)` using the
  Host's handlers, `MapMcp("/mcp").RequireAuthorization()`. The handler turns `request.User` into the explicit caller.
- **Sign-in modes** (`HostHttpOptions.Auth`):
  1. `Jwt` - OAuth 2.1 resource server: `Authority`/`Audience`/`RequiredScope` (default `mcp:tools`); subject `jwt:{iss}|{sub}`;
     roles from a configurable claim. `AddMcp` publishes protected-resource metadata and the 401 challenge, so standard MCP
     clients can sign in against Entra ID, Keycloak, Auth0 and so on. Audience must equal this resource; tokens are never forwarded.
  2. `ApiKeys` - machine agents and simple installs: `DeploymentRunner --new-api-key <name>` generates 32 random bytes, prints
     the key **once** and the `sha256:` hash to put in config. Config stores hashes only, constant-time compare, one subject
     (`key:{name}`) and a tool scope per key. **An API key can never carry a reviewer or admin role.**
  3. `None` - only when bound to loopback.
- **Reviewer vs agent separation.** Review endpoints (`/api/...`) require the `opentail.review` role/scope; trust promotion and
  other operator actions require `opentail.admin`. Tokens that carry only `mcp:tools` are rejected there, and API keys are
  rejected there. Static admin tokens (`name:token[:role,role...]`) keep working: with no explicit roles a token is both
  reviewer and admin (single-operator compatibility); with explicit roles they are honored exactly. The review record stores
  the identity the server authenticated; the request body can never set it.
- **Admin mounted in the same app.** `AdminServer` refactored into `MapAdmin(IEndpointRouteBuilder, ...)` taking an authenticator;
  standalone loopback `StartAsync` keeps working exactly as before.
- **Hardening.** `Origin` allow-list (default: reject browser origins), `Host` allow-list from `PublicBaseUrl`, body limit 1 MB on
  `/mcp`, per-subject rate limit (default 120/min), security headers on the admin page, `/healthz` (anonymous, no detail), refuse to
  start on a non-loopback address without auth, and without TLS unless `TrustProxy` says an upstream proxy terminates it.
- **Runner.** `DeploymentRunner --http --urls ... --public-url ... --auth jwt|apikeys` (stdio stays the default). Background
  services: maintenance sweep, notification dispatcher, AI reviewer runner.
- **Tests.** In-process Kestrel on port 0 with the SDK HTTP client; JWT with a test signing key via a configuration hook;
  metadata + challenge; wrong audience; expired token; hashed keys; Origin rejection; two-subject isolation; an agent credential
  calling a review endpoint (must be 403); a **real Claude Code client over HTTP** with an API key header.

## 7. Phase V3: conditional policy

**Goal:** the operator can say "fine automatically / waits / never" in terms of arguments, time and volume.

### Rule model (per tool, in the policy file)
```json
"email.send": {
  "Mode": "Queue", "Reviewer": "Human", "ExpireAfter": "08:00:00",
  "Rules": [
    { "Name": "never-to-competitors", "Mode": "Deny",
      "When": { "arg": "/to", "any": { "domainIn": ["rival.example"] } } },
    { "Name": "internal-small", "Mode": "Execute", "IgnoreArgs": ["subject"],
      "When": { "all": [
        { "arg": "/to", "all": { "domainIn": ["corp.example"] } },
        { "arg": "/bcc", "absentOrEmpty": true },
        { "arg": "/attachments", "countAtMost": 0 },
        { "time": { "days": ["Mon","Tue","Wed","Thu","Fri"], "from": "08:00", "to": "19:00", "zone": "Europe/Amsterdam" } } ] },
      "Budget": { "Per": "24:00:00", "MaxCount": 50, "Scope": "Requester" } },
    { "Name": "finance-wording", "Mode": "Queue", "ReviewerGroup": "finance",
      "When": { "arg": "/body", "matches": "(?i)invoice|payment" } }
  ]
}
```
- **Conditions:** a JSON predicate tree: `all`, `any`, `not`, and leaves: `arg` (JSON Pointer) with `equals`, `in`, `startsWith`,
  `endsWith`, `contains`, `matches`, `domainIn`, `lt/lte/gt/gte`, `exists`, `absentOrEmpty`, `countAtMost`; array arguments take
  `all`/`any` quantifiers over items; a `time` leaf (days, window, IANA zone, from the injected `TimeProvider`).
- **Three-valued logic (True / False / Unknown).** A leaf is Unknown when the path is missing, the type is wrong, a regex times
  out, an address does not parse, or anything throws. `not(Unknown) = Unknown`; `all` is False if any child is False, else
  Unknown if any is Unknown, else True; `any` is True if any child is True, else Unknown if any is Unknown, else False; a quantifier
  over an empty array is Unknown. **An Execute rule applies only when its condition is True. A Deny or Queue rule applies when its
  condition is True *or Unknown*.** So malformed or missing data can only ever move the outcome toward the stricter side, and a
  `not` cannot flip it.
- **Argument coverage (fail closed).** An Execute rule matches only if **every top-level argument present in the call** is
  referenced by some leaf in the rule (by the first segment of its pointer) or listed in the rule's `IgnoreArgs`. A model that adds
  `bcc` to an "internal" email therefore cannot ride the rule. (Validation warns when an Execute rule's tool has required
  arguments not covered.)
- **Combining.** Start from the tool's base mode. Among matching rules: any `Deny` wins; else any `Queue` wins (and can set
  `Reviewer`, `ReviewerGroup`, `RequiredApprovals`, `ExpireAfter` for that queue); an `Execute` rule applies only if the base is
  `Queue`, no stricter rule matched, and the caller is **not tainted** (the existing taint override stays absolute; there is no
  `AllowWhenTainted`). Rules never revive a denied tool. A promotion never beats a matching Deny/Queue rule.
- **Budgets** (`MaxCount` per window; scope `Requester` or `Global`) make an Execute rule stop matching once spent. Enforced
  through the V1 atomic admission (SQL count inside the insert transaction), so it holds across processes. When the admission
  fails the call is created as Pending instead. (`MaxSum` is out of scope.)
- **Hostile-input hygiene.** `matches` uses `RegexOptions.NonBacktracking` plus a 50 ms timeout. `domainIn` accepts only bare
  addresses (`^[^@\s<>,;"]+@[^@\s<>,;"]+$`), takes the part after the last `@`, lowercases, converts IDN to punycode, rejects a trailing
  dot and anything that fails conversion, and compares exactly (so `corp.example.evil.com` and `a@corp.example@evil.com` never match).
  Numbers compare as decimals. Rules re-run on **reviewer-edited arguments at approval time**; an edit that now matches a Deny rule is refused.
- **Audit and record:** the winning rule goes in the policy-decision audit entry and `ActionRecord.PolicyRule`.

### Operator tooling
- **Startup validation:** unknown operators, bad regex, bad zone, empty or duplicate rule names, Execute rules on a tool whose base
  is not `Queue`, all fail fast. Unknown argument paths are allowed (schemas vary) but reported as warnings by `--explain`.
- **`--explain <tool> '<json>' [--tainted]`:** prints the decision and a per-rule trace (each leaf True/False/Unknown and why).
- **Policy replay:** `--replay-policy new.json [--since 7d]` re-evaluates stored actions against a candidate policy and prints the
  differences ("31 queued -> would auto-execute (rule internal-small)"). Nothing is executed. The `time` leaf is evaluated at each
  record's `CreatedAt`, and the output says so. Replay uses each record's recorded taint.

## 8. Phase V4: notifications

**Goal:** reviewers learn about a pending action where they already are.

- **Durable outbox, not a transactional one.** Events are published after the store commits, so a crash in between could lose one.
  The sweep therefore *reconciles*: every `Pending` action without a `queued` outbox row gets one; reminders are derived from the state
  of Pending actions each sweep (idempotent `dedupe_key = remind:{id}:{n}`). Other events (failed, expired) are best-effort and
  documented as such; the AI side learns outcomes through V5 regardless.
- **Outbox** in the same SQLite file: `id, dedupe_key UNIQUE, channel, event_json, status, attempts, next_attempt_at, last_error,
  created_at, sent_at`. A dispatcher claims due rows with a lease, sends, retries with backoff (30 s doubling, 6 attempts, then `dead` +
  audit). At-least-once; receivers dedupe on `X-OpenTail-Event-Id`.
- **Channels:** `webhook` (HMAC-SHA256 over `timestamp.body`, header `X-OpenTail-Signature: t=...,v1=...`) and `slack`
  (incoming-webhook, Block Kit, a link button). Secrets come from environment-variable *names*, never inline. (Teams, ntfy and
  escalation are cut; any of them is a small channel later.)
- **Routes** (`notifications.json`): `{ "On": ["queued","reminder"], "Tools": ["email.*"], "MinRisk": "Write", "To": ["finance-slack"] }`.
  Events: `queued`, `reminder`, `decided` (callback for unattended agents), `failed`, `expired`. `Reminder { After, Every, Max }`.
- **Content rules.** Event, action id, tool, module, risk, requester display name, tainted flag, expiry, review URL. **No argument
  values or preview text** unless that route sets `IncludePreviewSummary`. Everything interpolated into Slack text is escaped and
  length-capped; model-controlled text never goes into a URL.
- **Review URL** = `PublicBaseUrl/#<actionId>`; opening it needs the reviewer's sign-in (no magic approve links).
- **Egress safety.** `https` required (loopback http for tests), no redirects, 10 s timeout, response body never logged.
- **Tests:** a local receiver (signature check, retry then success, dead letter, dedupe, reconcile after a simulated crash, reminders
  idempotent across restarts) and a property-style test that fails if any argument value appears in any payload.

## 9. Phase V5: resumable work

The protocol cannot push to a closed chat, so the Host makes sure the model learns at its next opportunity and, for unattended
agents, can be called back.

1. **`notices` piggyback.** A tool response gets a `notices` field **only when non-empty**, outside the tool's own result, with at
   most 5 entries: decisions on this caller's earlier actions it has not been told about. Reviewer reasons are presented as quoted
   data (`"reason": "..."`, never as free text). Marked delivered with a compare-and-set (`UPDATE ... WHERE delivered_at IS NULL`).
   The originating session's notices are delivered first. Notices never contain results.
2. **`get_updates`** (explicit pull, optional cursor) and **`wait_for_action(actionId, timeoutSeconds<=25)`** (long poll, polls the
   store so it works across processes).
3. **`list_my_actions`**: all of the caller's actions, newest first, `status`/`since` filters.
4. **Server instructions** tell the model: at the start of a conversation call `get_updates`; after `pending_approval`, say it is
   waiting and carry on, do not poll in a loop.
5. **Agent callback:** a notification route `On: ["decided"]` to a webhook resumes an external runner. No new mechanism.
6. **Retention.** Sessions: idle TTL (V1). Notices, outbox rows and, optionally, terminal action rows (`RetentionDays`, default
   off, 90 recommended; arguments live in action rows) are purged by the sweep; the audit trail (no argument values) stays.

## 10. Phase V6: approval controls

1. **Reviewer groups.** A rule or tool may require `ReviewerGroup`; only reviewers carrying that group may decide it. **AI reviewers
   never satisfy a group** and never count toward `RequiredApprovals > 1`.
2. **Self-approval.** `SelfApproval: Allow | Forbid` per tool (default `Allow`: the person using the assistant is normally the right
   approver). `Forbid` compares namespaced subjects through the operator's `People` alias map.
3. **N-of-M approval** (`RequiredApprovals: n`, default 1). Built on the V1 in-transaction transition: inside the `BEGIN IMMEDIATE`
   transaction decide eligibility (group, self-approval, duplicate approver) and whether this is the n-th distinct approval.
   `Pending -> Pending` becomes a legal transition for partial approvals. An edit by an approver **resets earlier approvals**
   (they approved different arguments); Deny/Queue rules re-run on the effective arguments. `Retry` (Failed -> Approved) and the
   recovery auto-retry go through the same gate. One rejection/change request by an eligible reviewer ends it.
4. **Admin API/page** show group, "1 of 2", the matched rule and the audit trail.

## 11. Phase V7: documentation, samples, packaging

- `OpenTail.Host/OPERATIONS.md` (topology, secrets, backups of the SQLite file, upgrades and the one-way schema migration, log fields,
  metrics, the policy replay workflow, incident checklist) and `OpenTail.Host/SECURITY.md` (threat model, each control and its limits).
- README updates; `PACKAGING.md` (14 packages); `scripts/check-host-deployments.ps1` gains both packages and a team-sample publish check.
- Sample `OpenTail.Host.Sample.TeamHttp` (Files + OpenApi + notifications + API keys) with policy, notifications and a README run-through.
- Implementation log appended below as phases finish.

## 12. Security review table

| Threat | Control |
|---|---|
| Read/cancel another user's action (IDOR), use another's session | Ownership on all caller-facing reads/writes, owner-bound sessions (V1) |
| Idempotency key collision across users | Key scoped to requester (V1) |
| Restart or `start_session` launders taint; parallel read+send race | Persisted sessions, subject taint with window, taint at submit (V1) |
| Reading an untrusted result through a status tool escapes taint | Reads of such results taint the reader (V1/V5); notices carry no results |
| Rule bypass by crafted/missing/extra arguments | Three-valued logic, argument coverage, exact domain match, NonBacktracking regex, re-run on edits (V3) |
| Budget/queue-cap overshoot across processes | Atomic admission in the insert transaction (V1/V3) |
| Agent credential approves its own call | Separate `opentail.review` role; API keys can never review; promote needs admin (V2) |
| Identity collisions (`alice` vs a GUID) | Namespaced subjects + `People` alias map (V1/V6) |
| N-of-M: edit after earlier approvals, retry bypass | Approvals reset on edit; Retry/recovery gated (V6) |
| Notification leak / forged webhook / SSRF | No args by default, escaping; HMAC + event id; https, no redirects (V4) |
| Forwarded link approves | No magic links; sign-in required (V4) |
| DNS rebinding / CSRF on admin and MCP | Host + Origin allow-lists, bearer tokens (no cookies), CSP (V2) |
| Token confusion / passthrough | Audience check, no forwarding, scope required (V2) |
| API key theft | Hashed at rest, constant-time compare, per-key scope, generator tool (V2) |
| DoS | Body limit, per-subject rate limit, queue caps, outbox attempt cap (V2/V4) |
| Secrets in logs/audit | Env-var names only; redaction; audit has no argument values |
| Data kept forever | Optional action retention, outbox/notice purge (V5) |

## 13. Tests per phase (exit gates)

V1: sessions across restart, subject-taint inheritance, taint at submit with parallel calls, IDOR, idempotency across requesters,
admission under concurrency, sink isolation, migration + backup. V2: JWT / API key / None, metadata + challenge, audience, Origin/Host,
rate limit, TLS/bind refusals, isolation, agent-credential-cannot-review, real client over HTTP. V3: every operator; the Unknown cases
(`not` over a missing path, empty array, wrong type, hostile strings); argument coverage; combining table; budgets; time with a fake clock;
validation errors; replay output. V4: signature, retry/backoff, dead letter, dedupe, reconcile, reminders, no-argument-leak property.
V5: notices once and only to the owner, `wait_for_action` wake-up and timeout, instructions, purge. V6: group gating, 2-of-3, duplicate
approver, edit resets, self-approval forbidden, AI approvals don't count, retry gating. V7: packages restore from a clean feed, forbidden-assembly
and size checks.

Every phase ends with the whole solution building at 0 warnings/0 errors (`-p:NuGetAudit=false`, known NU1902), all Host tests green,
a local commit on `host/modular-mcp`, and an entry in the log below. Nothing is pushed.

## 14. Order and why

V1 -> V2 -> V3 -> V4 -> V5 -> V6 -> V7. Identity first (everything keys off it, and three items are security fixes). HTTP next:
notification review links must be reachable, and per-requester limits mean nothing without identity. Policy before notifications so
notifications can route by risk and rule. Resumable needs principals and events. Controls last: they sit on identity, the rules engine
and the in-transaction transition.

## 15. Out of scope here

- MCP tasks extension (SDK lacks it; mapping documented in section 3).
- Approve/reject buttons inside Slack (needs a public interactive endpoint with signature verification; link buttons are the safe first step).
- Teams/ntfy channels, escalation, `MaxSum` budgets, an HTTP policy-simulate endpoint (CLI `--explain` covers it), `OnBehalfOf`.
- OIDC login for the approval page itself (put it behind your SSO proxy, or use JWT bearer tokens).
- Multi-tenant isolation (one deployment = one tenant), clustering (SQLite is single-node).

## 16. Design review outcome

An independent review (Opus, read-only, against the code and revision 1) returned 20 findings. Decisions:

| # | Finding | Decision |
|---|---|---|
| 1 | "Failed leaf = false" is unsafe under `not`; typos in Deny paths; vacuous quantifiers | **Adopted**: three-valued logic (section 7) |
| 2 | Execute rule ignores fields it does not name (`bcc`) | **Adopted**: argument coverage / `IgnoreArgs` |
| 3 | Taint launderable via `start_session`, race at execute time, result reads | **Adopted**: subject taint + window, taint at submit, reads taint; `AllowWhenTainted` cut |
| 4 | Idempotency key global across users | **Adopted** (V1 schema 2) |
| 5 | N-of-M needs in-transaction decision; edits; Retry/recovery | **Adopted** (V1 overload, V6); kept, built last |
| 6 | Reviewer/requester identity namespaces | **Adopted**: namespaced subjects, `People` map, AI never counts |
| 7 | Agent credentials could approve; promote too open | **Adopted**: separate review/admin roles |
| 8 | Phase order; drop session-scoped budgets | **Adopted**: new order, `Scope: Session` removed |
| 9 | Budget/cap overshoot across processes | **Adopted**: atomic admission via SQL count |
| 10 | "Transactional outbox" claim false | **Adopted**: renamed durable outbox + reconcile |
| 11 | Stdio ownership underspecified | **Adopted**: requester = subject (`local`), sessions for taint |
| 12 | `AsyncLocal` identity leaks into background work | **Adopted**: explicit on `ActionRequest`, never read outside the handler |
| 13 | Notices shape and CAS | **Adopted** |
| 14 | Trust promotion per process | **Adopted**: persisted; rules beat promotion |
| 15 | One-way migration | **Adopted**: automatic backup |
| 16 | Cut scope | **Adopted**: Teams, ntfy, escalation, `MaxSum`, `OnBehalfOf`, HTTP simulate endpoint cut; N-of-M kept but last |
| 17 | API key length not checkable on hashes | **Adopted**: `--new-api-key` generator |
| 18 | `domainIn` normalization | **Adopted** |
| 19 | Action rows keep arguments forever | **Adopted**: optional retention |
| 20 | Time leaf in replay | **Adopted**: evaluated at `CreatedAt` |

## Implementation log

(appended as phases complete)

- **V1 done** (foundations). Namespaced subjects and `CallerIdentity`; sessions are owner-bound and persisted (SQLite schema 2) with a per-subject taint window (default 30 min) so `start_session` and restarts cannot launder taint; taint is set at submit and when an untrusted result is read through a status tool; every caller-facing read/write is scoped to the caller; idempotency keys are scoped to the requester; limits are counted inside the insert transaction (`TryCreateAsync` + `Admission`, holds across processes); in-transaction transitions and `Pending->Pending` (for V6); `IActionEventSink` with failure isolation; trust promotions persisted; schema 1->2 migration takes a `VACUUM INTO` backup first. 205 Host tests (16 new) + 27 abstraction tests pass; solution builds 0/0. Note: stdio callers are all the single subject `local`, so on stdio "my actions" is everything on the deployment; the pending cap is therefore per deployment on stdio.

- **V2 done** (HTTP + sign-in). New package `OpenTail.Host.Http`: Streamable HTTP (stateless, SDK 1.4.1) with JWT resource-server sign-in (audience, scope, issuer, protected-resource metadata + 401 challenge via `AddMcp`) and hashed API keys (`--new-api-key`; keys can never review); reviewer tokens/roles (`name:token|review,admin,group:x`) that can never call tools; admin page + API refactored into `AdminEndpoints` (standalone loopback server unchanged) and mounted behind the same sign-in; reviewer identity recorded from the authenticated principal (`ActionReview.Subject/Groups`), promotion needs the admin role; Origin/Host allow-lists, security headers, per-subject rate limit, body limit, startup refusals (open beyond loopback, plain http beyond loopback without `TrustProxy`, host built without `UseHttpSignIn`). `HttpDeploymentRunner` (`--http config.json`) and sample `OpenTail.Host.Sample.TeamHttp`. Verified with 22 new tests (227 total) and a real Claude Code client over HTTP with an API key: signed in as `key:alice-agent`, queued write, reviewer (admin token) asked for changes, the client read the reason; the API key calling `/api` got 403. ModelContextProtocol packages bumped 1.4.0 -> 1.4.1.

- **V3 done** (conditional policy). Per-tool `Rules` in the policy file: Deny/Queue/Execute rules over a JSON predicate language (all/any/not, `arg` JSON-Pointer tests: equals, in, startsWith, endsWith, contains, matches, domainIn, lt/lte/gt/gte, exists, absentOrEmpty, countAtMost/AtLeast, all/any quantifiers, `time`). **Three-valued logic** (True/False/Unknown): an Execute rule needs a definite True and must account for every top-level argument (or list it in `IgnoreArgs`); Deny/Queue rules apply on True or Unknown; `not(Unknown)` stays Unknown. Regex is NonBacktracking with a 50 ms timeout; `domainIn` is exact, punycode-normalised and refuses display names, lists, quoted locals, double @. No taint opt-out. Budgets (`MaxCount` per window, scope Requester/Global) are counted atomically in the insert transaction and fall back to Queue when spent. Queue rules can set reviewer, group, required approvals, expiry. A promotion never beats a matching rule. Reviewer edits are re-checked against Deny rules. Startup validation names every mistake with its path. `--explain <tool> <json> [--tainted] [--at ...]` prints a per-rule trace; `--replay-policy new.json [--since 7d]` reports what stored actions would have done under a candidate policy (nothing executes). **Bug found on the way:** the v1 sample policies used `"24:00:00"`, which the JSON TimeSpan format rejects; policy files now accept `24:00:00`, `1.00:00:00`, `90s/30m/8h/2d` (regression tests load every sample policy). 367 Host tests (140 new).

- **V4 done** (notifications). New package `OpenTail.Host.Notifications` (depends on Abstractions + SQLite only): a **durable outbox** in the queue database (unique dedupe key per event+channel, lease so two senders never send one, exponential backoff 30 s doubling to 1 h, dead after 6 attempts with an audit entry), channels `webhook` (HMAC-SHA256 over `t.body`, `X-OpenTail-Signature`/`X-OpenTail-Event-Id`, https only, no redirects, 10 s, status code only in errors) and `slack` (Block Kit, link button, escapes `& < > * _ ~ | ` and control characters so values cannot inject mentions or links), routes by event/tool glob/minimum risk, reminders (`After`/`Every`/`Max`; only the latest due reminder after downtime), and a scheduler that **reconciles** pending actions whose queued event was lost (recent ones only, `ReconcileWindow` 6 h). Messages carry facts only (no argument values, no preview) unless a route sets `IncludePreviewSummary`. Secrets are env-var names; every mistake in the file is a startup error. Wired into `HttpDeploymentRunner --notifications <file>`; sample `notifications.team.json`. Verified with 34 tests (401 total) and a live run: queue over HTTP with an API key -> signed webhook received and verified by an independent receiver (no argument values in it) -> approve -> `succeeded` event. Plan note: the outbox is "durable", not "transactional": events are written after the state change, which the scheduler reconciles for `queued`; other events are best-effort.

- **V5 done** (resumable work). Decisions on earlier actions are remembered per caller (SQLite schema 3, `notices`) and ride on the next tool response of any kind as a `notices` field (only when non-empty, at most 5, the originating session first, handed to exactly one response by compare-and-set; reviewer reasons are separate quoted fields; **never a result**). New built-ins `get_updates` (notices + what is still waiting) and `wait_for_action` (long poll up to 25 s, works across processes, own actions only; returns at once for someone elses id); `list_my_actions` gains `since`/`limit`; reading a status counts as being told; reading an untrusted result through `wait_for_action` taints the reader. Server instructions now tell the model to call `get_updates` at the start of a conversation and not to poll in a loop. Retention: `RetentionDays` deletes finished actions (they hold argument values) while the audit trail stays; notices older than 30 days are purged. 418 tests (17 new).

- **V6 done** (approval controls). `ReviewAuthority` is the queue's rule for who may decide (the page only mirrors it as `canDecide` + the reason): a **reviewer group** (tool- or rule-level) must be carried by the server-authenticated reviewer; an **AI reviewer** never decides a human-only action, never satisfies a group, and never counts toward several required approvals (the runner stamps `ai:<name>`, so a reviewer cannot claim to be a person); **SelfApproval: Forbid** refuses the requester (judged through the policy `People` map so `admin:alice` and `key:alices-agent` can be the same human; an unknown reviewer identity is refused when it matters; rejecting your own request is always allowed); **RequiredApprovals (1-5)** is decided inside the store transaction (`Pending -> Pending` for partial approvals; duplicate approver refused, two names for one person count once; **an edit voids approvals given before it**; one eligible rejection ends it; `Retry` passes the same gate). Admin API/page show `approvals x of y`, group, rule, `canDecide` and the reason. Startup refuses RequiredApprovals outside 1-5, a trust ramp with several approvers, non-namespaced or duplicated People entries. Verified: 17 new tests incl. eight reviewers approving at once and two hosts on one database, both executing exactly once (435 Host tests, 27 abstraction tests; solution 0/0).

- **V7 done** (documentation, samples, packaging). New: `OpenTail.Host/POLICY.md` (policy and rule reference, pitfalls, explain/replay workflow), `OPERATIONS.md` (HTTP and sign-in, reviewer tokens/roles, TLS and proxy, notifications and delivery guarantees, backups, the one-way schema migration, monitoring, troubleshooting table), `SECURITY.md` (threat model, controls by threat, and an explicit list of what it does not do), README and `PACKAGING.md` rewritten for 14 packages. `scripts/check-host-deployments.ps1` now publishes three samples (EmailOnly 40 assemblies, FilesOpenApi 16, TeamHttp 27), packs 14 packages with one version, and restores, builds **and runs** a consumer outside the repo from the packed feed. Architecture tests enforce the one-way layering (Host -> Admin -> Http; Notifications -> Abstractions only), that no Host package references Core or a module, and that versioning covers every Host package by name.

## Acceptance (2026-10-16)

Solution builds 0 warnings / 0 errors (also `OpenTail.Private.slnx`); 435 Host tests and 30 abstraction/architecture tests pass; the deployment check passes. With a real client (Claude Code 2.1.300) over signed-in HTTP: an agent queued a write and ended its turn; a reviewer asked for changes; a **new conversation** called `get_updates`, read the reviewer reason, resubmitted exactly what was asked (linked to the original), a reviewer approved, and the file exists. Earlier runs: API key refused on the review API (403); signed webhook verified by an independent receiver with no argument values in it.

## What is left (honest list)

- Not walked through by a second person in a real browser: the approval page (only API calls, headers and structure are tested).
- Single node (SQLite); no load or soak test; no independent security audit.
- MCP tasks extension and Slack approve-buttons are not implemented (SDK 1.4.1 lacks tasks; mapping documented in section 3).
- Packages are not published anywhere; the samples still use project references; the module contract is not frozen.
- Sign-in tested with a local signing key, not against a live identity provider (Entra/Keycloak/Auth0).
- Notifications tested against a scripted handler and a local receiver, not a live Slack workspace.
- `OpenTail.Core` does not pack (missing icon.png) - pre-existing, not touched.
