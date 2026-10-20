# OpenTail MCP Host — modular interaction layer (plan)

> **Update (2026-10-16):** the team features (HTTP and sign-in, conditional rules, notifications, resumable work, approval controls) are planned and logged in [`MCP-HOST-V2-PLAN.md`](MCP-HOST-V2-PLAN.md). The Host documentation is in `OpenTail.Host/` (`README.md`, `POLICY.md`, `OPERATIONS.md`, `SECURITY.md`, `PACKAGING.md`).

Status: DRAFT for review, 2026-10-15 (revision 2: adds slice-first ordering, taint rule, preview, trust ramps, blast-radius limits, outbox execution, MCP spec check, observability/versioning, acceptance scenario). Nothing in this plan has been built. Do not start Phase 1 until the open decisions in section 11 are answered.

## 1. Goal

An MCP server ("the Host") that exposes **only the modules a deployment needs**, and routes every
side-effecting action through a **policy** that decides: execute now, queue for approval, or deny.
The AI gets structured feedback on queued actions (approved, rejected, changes requested, failed).

Not a goal: coding tools (C#/Roslyn). Those live in `OpenTail.MCP-src` and are left alone. They could
become a module much later; nothing here depends on that.

## 2. Non-goals / constraints

- `OpenTail.MCP-src` and `extensions/opentail-mcp` are not touched.
- `opentail/OpenTail.Mcp` (the old coding server) is not extended. It is retired once the Host replaces it (Phase 8).
- The Host does not reference `OpenTail.Core`. A deployment must be able to ship without Core, Semantic Kernel, Roslyn or Reactive.
- Module packages reference only `Host.Abstractions` (plus their own third-party packages).
- No LLM calls inside the Host. It is a tool server.
- Safe by default: a tool with no policy entry is **denied**, not executed.

## 3. Findings this plan rests on (verified 2026-10-15)

- `OpenTail.Connectivity.Files` is a dependency-free contracts hub; every connector (Email, Gmail, Graph, IMAP, Ics, Csv, Sql, Web, Social) references only it. They are already leaf modules.
- `Core/Tools/Abstractions` holds `IOpenTailTool` (name, description, `ToAIFunction()`), `IToolInputGuardrail`, `IToolOutputGuardrail`, `ToolGuardrailResult`, `GuardedToolWrapper`, `ToolGuardrailTripwireException`, `JsonToolBase<T>`. Only 13 files use them (11 in Core, 2 in Tests).
- `JsonToolBase<T>` takes `HttpClient` and a logger (Serilog-style `logger.Information`) and writes sidecar files. It is **not** dependency-free and **stays in Core**. Only the other five files move.
- `AIFunction` (`Microsoft.Extensions.AI`) is what the MCP C# SDK uses for tools, so `IOpenTailTool` maps to MCP directly.
- An email approval flow already exists in `Connectivity.Files`: `PolicyEnforcingEmailSender`, `EmailSenderPolicy` (default = sending disabled), `PendingSendRecord(Id, Email, QueuedAt, Status, Note, ProcessedAt)`, `IPendingSendStore` (in-memory only), `IEmailSendApprovalService` (`GetPending`, `Approve`, `Reject(reason)`). Email-only; no AI feedback path; no persistence.
- `OpenTail.OpenApi` depends on Core through global usings (`Core.Connectors`, `Core.Diagnostics`, `Core.Tools.OpenApi`, `Core.Tools.Packaging`) and on Semantic Kernel's OpenAPI plugin. The real extent of the coupling is **unverified** (see Phase 6, step 1).

## 4. Target architecture

```
Host.Abstractions   (net10.0; deps: Microsoft.Extensions.AI only)
   tool contract, guardrails, risk class, policy, action record, outcomes, queue + reviewer interfaces
        ^                         ^
        |                         |
     Host                  Module.* packages
   (MCP SDK, DI,           (one per capability; each exposes AddXxxModule())
   policy engine,
   queue + SQLite store,
   audit, status tools)
```

Dependency rule (enforced by an architecture test, Phase 1): `Host.Abstractions` references nothing but
`Microsoft.Extensions.AI`; modules reference only `Host.Abstractions`; `Host` references `Host.Abstractions` + MCP SDK + SQLite; nothing references `OpenTail.Core` except Core itself and legacy projects.

New projects (names provisional, see section 11): `OpenTail.Host.Abstractions`, `OpenTail.Host`, `OpenTail.Host.Tests`, then `OpenTail.Modules.*`.

## 5. Core model (Host.Abstractions)

### 5.1 Tool contract
Moved unchanged from Core (namespace kept as `OpenTail.Core.Tools.Abstractions` so the 13 call sites compile untouched; revisit rename later).
Additions (new, additive):
- `ToolRisk` enum: `Read`, `Write`, `Destructive`, `ExternalEffect` (sends mail, calls a third party, spends money).
- `ToolDescriptor`: name, description, risk, module id, optional `IdempotentByKey` flag.
- Tools declare risk via an attribute (`[OpenTailTool(Risk = ...)]`) or via a descriptor on the module. Source generator support is optional and deferred.

### 5.2 Policy
`ToolPolicy` per tool name (with module-level and global defaults, most specific wins):
- `Mode`: `Execute` | `Queue` | `Deny`
- `Reviewer`: `Human` | `Ai` | `Either` (routing hint only; the queue does not care who answers)
- `ExpireAfter` (queued actions time out to `Expired`)
- `RateLimit` (optional)
- Policy is loaded from configuration (appsettings), never from tool arguments or model output.
- **Default for any unlisted tool: `Deny`.** Default for `Read`-risk tools in a deployment that sets `DefaultReadMode=Execute`: execute.

### 5.3 Action (the primary record; per action, session optional)
`ActionRecord`:
- `Id` (GUID), `IdempotencyKey` (nullable; same key + tool + args hash returns the existing record instead of a duplicate)
- `ToolName`, `ModuleId`, `Arguments` (JSON), `ArgumentsHash`
- `Requester`: `{ Kind: AiSession | User | System, Id }`
- `SessionId` (**nullable**, a Host-minted handle, see section 13) — actions can be created with no AI session (scripts, imports, admin UI)
- `PolicySnapshot` — the policy as it was when queued; later config changes do not alter it
- `Status` (see 5.4), `CreatedAt`, `UpdatedAt`, `ExpiresAt`
- `Outcome`: `Result` (JSON) or `Error` (code, message, `Retryable`)
- `Reviews[]`: `{ Reviewer, Decision, Reason, EditedArguments?, At }`
- `Attempts` (count) for retry bookkeeping

### 5.4 Lifecycle
```
            Execute policy ──────────────────────────► Executing ─► Succeeded | Failed
Request ─►  Queue policy ─► Pending ─► Approved ─► Executing ─► Succeeded | Failed
            Deny policy ─► Denied          │
                                           ├─► Rejected (reason)
                                           ├─► ChangesRequested (reason, edited args?) ─► AI resubmits (new Pending, linked via ParentActionId)
                                           └─► Expired
```
Rules: terminal states are immutable; `Approved` -> `Executing` is claimed atomically (one executor); a `Failed`
action with `Retryable=true` can be retried by a reviewer, which adds an attempt, not a new record.

### 5.5 Interfaces
- `IActionStore`: create (idempotent), get, query (by status/session/requester/module), transition (compare-and-set on status), append review.
- `IActionQueue`: `SubmitAsync(request) -> ActionRecord`, `ReviewAsync(id, decision)`, `RetryAsync(id)`.
- `IActionExecutor`: runs the tool for an `Approved`/`Execute` action, records outcome.
- `IReviewer`: pluggable decision source (human UI, AI reviewer). The queue exposes pending items; reviewers pull or are pushed to.
- `IAuditSink`: append-only record of every submit, policy decision, review, execution, with timestamps and identity. Never contains secrets (arguments pass through a redaction hook).

### 5.6 Safety additions (revision 2)

**Taint rule (prompt-injection defence).** Tools that read untrusted content (email bodies, web pages, imported files, OpenAPI responses) are marked `ReadsUntrusted` in their descriptor. When a session has invoked any such tool, the session is **tainted**. In a tainted session every tool with risk `ExternalEffect` or `Destructive` is forced to `Queue`, regardless of its configured mode (policy may make it stricter, e.g. `Deny`, never looser). Taint is per session; sessionless actions use the taint flag supplied by the submitter (default: tainted). Taint cannot be cleared by the model.

**Reviewer sees real effects.** The review view shows the exact stored arguments, a rendered diff/preview where the tool provides one, the originating tool call, the taint flag and which untrusted sources were read in the session. It never relies on the model's own summary of what it intends.

**Credentials never reach the model.** Modules resolve credentials from the host's secret store by connection name configured by the operator. Tool arguments cannot name, select or override credentials, endpoints outside the module's allow-list, or file paths outside the module's root.

**Preview (dry-run).** A tool may implement `IPreviewableTool.PreviewAsync(args) -> PreviewResult` (human-readable effect + structured diff, no side effects). For `Queue` mode the preview is generated at submit time and stored on the `ActionRecord`; approval is of that previewed effect. If a preview cannot be produced for a `Destructive`/`ExternalEffect` tool, the module must say so explicitly in its descriptor (`NoPreview = true`) and the reviewer UI flags it.

**Trust ramp.** Optional per-tool setting: start at `Queue`; after `N` consecutive approvals with no edits and no failures, the operator may be offered (never automatic unless configured) promotion to `Execute`, still bounded by blast-radius limits. Demotion on any failure or rejection. All promotions are audited with who/when.

**Blast-radius limits.** Per tool and per module, enforced by the Host even for approved actions: max actions per hour/day, max recipients/rows/bytes per action where the module reports a size measure, max queued actions pending per requester. Exceeding a limit moves the action to `Rejected` (system reviewer, reason recorded) or holds it `Pending`, per policy.

### 5.7 Durable execution (outbox)
Approve-then-execute has a crash window (approved, executing, process dies). Execution therefore runs from an **outbox**: the transition `Approved` and the outbox row are written in one SQLite transaction; a single executor loop claims rows atomically (compare-and-set), runs the tool, and writes the outcome in one transaction. On restart, rows left `Executing` past a lease timeout are re-examined: if the tool declares `IdempotentByKey`, retry with the same idempotency key passed downstream; otherwise the action moves to `Failed(Retryable=true, reason="interrupted; outcome unknown")` and requires a reviewer decision. Never silently re-run a non-idempotent external effect.

### 5.8 Policy as data and "explain"
Policy lives in a declarative config file (JSON/YAML via `Microsoft.Extensions.Configuration`), validated at startup (unknown tool names, impossible combinations fail fast). A CLI/endpoint `explain <tool> <args-json> [--tainted]` returns the decision and the rule(s) that produced it. Policy changes are audited; queued actions keep their `PolicySnapshot`.

### 5.9 Observability and versioning
- OpenTelemetry from the start: one activity per action (tags: tool, module, risk, mode, status, tainted; no argument values), metrics for queue depth, time-to-decision, approval/rejection/failure rates, execution latency.
- Versioned contracts: `Host.Abstractions` has an explicit contract version constant; modules declare the range they support and the Host refuses incompatible modules at startup. The persisted action schema carries a schema version with forward-only migrations; the JSON shape of queued-call responses and built-in tool results is versioned (`"v": 1`).

## 6. Host

- Built on the official `ModelContextProtocol` SDK; stdio transport first, HTTP (Streamable) second.
- Module loading: `HostBuilder.AddModule<T>()` or config-driven list of module ids. Each module registers tools into a `ToolCatalog` (name, descriptor, `AIFunction`). Duplicate tool names fail startup.
- Only tools whose policy is not `Deny` are advertised to the MCP client. This keeps the visible tool list small (better model tool selection) and prevents probing.
- **Execution path for a tool call:**
  1. resolve descriptor + policy (unlisted -> Denied, recorded)
  2. run input guardrails (existing `IToolInputGuardrail`)
  3. policy `Execute`: run, run output guardrails, return result (an `ActionRecord` is still written for audit)
  4. policy `Queue`: create `ActionRecord(Pending)`, return immediately (never block: MCP clients time out)
- **Queued-call response shape** (stable contract, returned as tool result):
  `{ "status": "pending_approval", "actionId": "...", "expiresAt": "...", "next": "call get_action_status" }`
- **Built-in Host tools (always present):** `start_session()`, `get_action_status(actionId)`, `list_my_actions(status?, sessionId?)`, `cancel_action(actionId)` (only while `Pending`, only by the requester), `resubmit_action(actionId, editedArguments)` (only from `ChangesRequested`).
- **Feedback to the AI:** polling tools are the baseline and work with every client. MCP notifications/resources updates are an optional extra later; do not depend on them.
- Session identity: the 2026-07-28 spec has no protocol sessions (section 13). Sessions are explicit: `start_session` mints a server-side handle; clients pass `sessionId` as an ordinary argument. No handle -> `SessionId = null` (sessionless, treated as tainted). `list_my_actions` without a handle returns only that requester's actions. The advertised tool list never varies per caller or session and is sorted by name.
- Storage: SQLite via `Microsoft.Data.Sqlite` (WAL), one file per deployment. Schema versioned; migrations are forward-only scripts. In-memory store for tests.
- Admin surface (Phase 7; a console reviewer from Phase 2 suffices until then): a minimal local HTTP API + page to list/approve/reject/edit. Reuses nothing from FlightDeck/Manager initially; wiring into them is a later option.

## 7. Modules

Each module: one NuGet package, `AddXxxModule(IServiceCollection, Action<Options>)`, declares its tools with risk classes, ships default recommended policy (as documentation/sample config, never applied automatically).

| Module | Wraps | Tools (initial) | Risk |
|---|---|---|---|
| `Modules.Files` | `Connectivity.Files` | read/list within a root | Read |
| `Modules.Email` | Email + inbox connectors | search/read inbox; **send** | Read; ExternalEffect |
| `Modules.Calendar` | Ics / Gmail / Graph | list events; create event | Read; ExternalEffect |
| `Modules.Csv`, `Modules.Sql` | Csv, Sql | query (read-only); write | Read; Write |
| `Modules.Web` | Web | fetch URL | Read (allow-list) |
| `Modules.OpenApi` | importer (Phase 6) | one tool per imported operation | derived from HTTP verb: GET=Read, POST/PUT/PATCH=Write, DELETE=Destructive; overridable per operation |

Module rules: no module references another module; shared types go in `Host.Abstractions` or `Connectivity.Files`. A module never reads policy; it only declares risk.

## 8. Migration of existing pieces

1. **Abstractions move.** Create `OpenTail.Host.Abstractions`; move `IOpenTailTool`, `IToolInputGuardrail`/`IToolOutputGuardrail`/`ToolGuardrailResult`, `GuardedToolWrapper`, `ToolGuardrailTripwireException`. Keep namespace `OpenTail.Core.Tools.Abstractions`. `Core` gets a ProjectReference. `JsonToolBase<T>` stays in Core. Result: the 13 call sites compile unchanged.
2. **Email approval flow becomes the generic queue.** `PendingSendRecord` is superseded by `ActionRecord`; `IEmailSendApprovalService` semantics (approve/reject with reason) are preserved by `IActionQueue.ReviewAsync`. `PolicyEnforcingEmailSender` keeps working for existing users; `Modules.Email` uses the generic queue instead. The old types are marked `[Obsolete]` with a pointer, not deleted, until nothing references them.
3. **OpenApi split (Phase 5):** importer core (spec -> `AIFunction`s with risk classes) moves into `Modules.OpenApi` with no Core dependency; the `Developer*` validation/integration helpers and `CoreTierProviderPolicy` stay in `OpenTail.OpenApi` (Core-dependent).
4. **`OpenTail.Mcp` (old coding server):** retired in Phase 7, after the Host passes the acceptance tests. Not deleted without explicit say-so.

## 9. Phases (each ends with a build + tests; commit locally per phase, never push)

Work happens on a **new branch** (see section 11, decision 3), so the large set of unrelated uncommitted changes in the repo (TUI, web, shadow git) is not mixed in.

**Order is slice-first.** The earliest phases cut one thin path through every layer (real MCP client -> Host -> policy -> queue -> SQLite -> real approval -> execution) before any layer is widened. Layers are then completed in later phases. Rationale: a layer-by-layer build can pass every exit test and still hide a design flaw that only shows end to end.

**Acceptance scenario (the plan's single success test), "inbox triage":** an AI session reads the inbox (untrusted content, session becomes tainted), drafts a reply and requests a send. The send is forced to `Queue`; the reviewer sees exact recipients/body and the taint flag; they reject with a reason; the AI polls, sees `rejected` + reason, resubmits an edited version; it is approved; it is sent exactly once even if the Host is killed mid-execution; the whole sequence appears in the audit trail and in OpenTail telemetry. Every phase from 2 onward must keep this scenario green (with fakes where a real connector is not yet wired).

| Phase | Deliverable | Exit test | Size |
|---|---|---|---|
| 0 | This plan agreed; section 11 answered; branch created. **Spec check:** read the current MCP spec and the pinned C# SDK version for tool annotations (readOnly/destructive/idempotent hints), elicitation, and the async/task mechanism; record findings in section 13 and adjust 5.1/5.4/6 if a standard mechanism fits the queue better than custom polling. | Section 13 filled in with verified facts and SDK version | S |
| 1 | `Host.Abstractions` with moved abstractions + new types (risk, `ReadsUntrusted`, policy, action, lifecycle, preview, interfaces, contract version); architecture test enforcing the dependency rule; Core references it | Core and Tests build unchanged; arch test passes; state-machine unit tests (every legal/illegal transition) | S-M |
| 2 | **Walking skeleton.** Minimal `Host` over stdio with in-memory store, one fake module with one `Read` tool and one `ExternalEffect` tool, deny-by-default policy from a config file, taint rule, queued-call response, `get_action_status`, a console/CLI approve-reject, audit log. Driven by a real MCP client (test client over stdio) | Inbox-triage scenario passes end to end with fakes (no SQLite, no outbox yet); denied tool is not advertised | M |
| 3 | Durability: SQLite store (WAL, schema version), outbox execution with lease/claim, idempotency keys, crash/restart handling, expiry, retry rules (5.7) | Kill-the-process test: pending survives; approved-not-executed executes once after restart; non-idempotent interrupted action lands in `Failed(Retryable)` not re-run; two approvers -> one execution | M-L |
| 4 | Safety features: previews, trust ramps, blast-radius limits, explain command, policy validation, redaction in audit/logs, OpenTelemetry (5.6, 5.8, 5.9) | Unit + scenario tests for each; `explain` output matches behaviour for a table of cases; no argument values in telemetry | M |
| 5 | Real modules: `Files` and `Email` (search/read, send via queue; wraps existing connectors) replacing the fakes | Inbox-triage scenario passes against a fake SMTP/IMAP; old `PolicyEnforcingEmailSender` marked `[Obsolete]` with pointer | M |
| 6 | OpenApi: first **read the importer end to end and record the real Core coupling**; then split importer into `Modules.OpenApi` (no Core dependency, verb-derived risk, `ReadsUntrusted` on response-returning tools by default); Developer* helpers stay behind | Import a sample spec; GET executes, POST queues, DELETE denied by policy; module has no Core reference | M-L (largest unknown) |
| 7 | Admin surface: local HTTP API + minimal page (list, filter, view preview/diff/taint, approve, reject, edit-and-approve); AI-reviewer adapter stub behind `IReviewer`. (A console reviewer from Phase 2 is enough until here.) | API tests + manual walkthrough with a person who is not the author | M |
| 8 | Remaining modules (Calendar, Csv, Sql, Web), NuGet packaging, sample deployment configs ("email-only", "files+openapi"), retire old `OpenTail.Mcp` (only with explicit approval) | Published "email-only" output contains no Core/Semantic Kernel/Roslyn assemblies | M |

Sizes are relative, not time estimates; Phase 6 has the widest range because the Core coupling of the importer is unverified.

## 10. Testing and security

- Unit: state machine, policy resolution (most-specific-wins, deny default), idempotency, redaction.
- Integration: in-process MCP client against the Host over stdio; SQLite restart durability.
- Architecture test: dependency rule from section 4 (fails the build if a module references Core or another module).
- Deployment test: publish the "email-only" sample and assert the output contains no `OpenTail.Core`, `Microsoft.SemanticKernel`, `Microsoft.CodeAnalysis` assemblies.
- Security: policy only from configuration; arguments never alter policy; reviewer identity recorded; arguments redacted in audit and logs; queued arguments stored as submitted plus `EditedArguments` in reviews (original never overwritten); approval of an edited action executes the edited arguments only if the reviewer set them; path/host allow-lists enforced in the module, not trusted from arguments.
- Known limit to state in the docs: the Host is guarded execution under the operator's OS identity, not a sandbox.

## 11. Open decisions (need an answer before Phase 1)

1. **Names.** Proposed `OpenTail.Host.Abstractions`, `OpenTail.Host`, `OpenTail.Modules.<X>`. OK, or different?
2. **Namespace of moved abstractions.** Keep `OpenTail.Core.Tools.Abstractions` (zero churn, slightly misleading) or rename to `OpenTail.Host.Abstractions` (touches 13 files, mechanical). Recommendation: keep now, rename later.
3. **Branch.** `host/modular-mcp` off the current working state, or off a clean commit? (The working tree has many unrelated uncommitted changes; the branch would carry them unless stashed or committed first. Needs your call; I will not commit them.)
4. **Transport for first release.** stdio only, or stdio + HTTP from the start? Recommendation: stdio first (HTTP auth changed in spec 2026-07-28; see section 13).
5. **Reviewer first target.** Human admin page first (Phase 7), or AI reviewer first? Recommendation: human first, AI reviewer as an adapter behind `IReviewer`.
6. **Retention.** How long are terminal actions and audit rows kept? Default proposal: 90 days, configurable.
7. **Multi-tenant?** Assumed one deployment = one customer = one SQLite file. If one Host must serve several customers, the store and policy need a tenant key from Phase 1.

8. **Trust ramps: allowed at all?** Auto-promotion to `Execute` after N clean approvals is a policy choice with risk. Recommendation: ship the mechanism, default off, promotion always requires an operator confirmation.
9. **Taint default for sessionless actions.** Recommendation: tainted unless the submitter explicitly asserts otherwise.
10. **Reviewer identity.** What identifies a human reviewer in the audit trail (Windows user, a configured name, SSO later)? Recommendation: Windows user / configured name now, behind an `IReviewerIdentity` interface.

## 12. Risks

- OpenApi split may be larger than expected (Phase 6 starts with a read-only assessment and can re-scope).
- MCP clients differ in how they surface long-running/pending tool results; the polling design avoids relying on client features, but a model may not poll unprompted. Mitigation: the pending response tells it exactly what to call, and server instructions describe the workflow.
- Approval semantics for destructive/external actions invite duplicate execution on retry; idempotency keys and atomic claim are mandatory, not optional.
- Scope creep into a general workflow engine. Rule: the Host queues and executes single tool calls; multi-step workflows stay in `Core.Workflows`.
- Approval fatigue: a queue that is too noisy gets rubber-stamped. Mitigation: previews, trust ramps, blast-radius limits, and measuring time-to-decision and approval rate in telemetry.
- Taint rule too blunt (forces queueing for sessions that merely read a file). Mitigation: `ReadsUntrusted` is per tool and declared by the module author; tune with real scenarios; policy may only tighten, so a tuning mistake errs safe.
- Spec/SDK drift: MCP and its C# SDK move quickly. Mitigation: Phase 0 spec check, pinned SDK version, contract tests against the in-process client.

## 13. Spec check results (Phase 0, done 2026-10-15)

Sources: modelcontextprotocol.io specification changelog for revision **2026-07-28** (previous: 2025-11-25); NuGet cache shows `ModelContextProtocol` 1.4.1 available (repo pins 1.4.0 in `opentail/Directory.Packages.props`). SDK capability check was a string search of the assemblies (annotation hints, elicitation, task and input-required types are present), **not** a compile-level API check; Phase 2 must confirm the exact API.

Facts and consequences:
1. **Tool annotations** (readOnly / destructive / idempotent hints) exist (since 2025-03-26) and the SDK has them. -> `ToolRisk` maps to annotations when advertising tools (Read -> readOnlyHint; Destructive -> destructiveHint; `IdempotentByKey` -> idempotentHint). Hints are advisory for clients only; the Host's own policy remains authoritative.
2. **The 2026-07-28 revision is stateless:** no `initialize` handshake, no protocol-level sessions, no `Mcp-Session-Id`; `tools/list` must not vary per connection. -> **"Session identity from the MCP connection" (section 6) is not available.** Replacement: sessions are explicit and optional (consistent with "session optional"): a `start_session` built-in tool mints a server-side handle that the client passes as an ordinary argument (`sessionId`) to later calls, per the spec's own guidance ("explicit, server-minted handles passed as ordinary tool arguments"). Taint is tracked against that handle. Calls without a handle are sessionless; sessionless calls are treated as tainted (decision 9). For stdio, one process = one implicit default session may be offered as a convenience, configurable.
3. **Tasks** moved out of core into the official extension `io.modelcontextprotocol/tasks`: polling via `tasks/get`, `tasks/update` for client-to-server input, server may return task handles unsolicited. -> A queued action maps naturally onto a task. Decision: **keep the built-in polling tools (`get_action_status` etc.) as the baseline** because client support for the tasks extension is unknown and the extension is new; **additionally expose a queued action as an MCP task** where the SDK/client support it (Phase 4 stretch; not required for acceptance). `ActionRecord.Id` doubles as the task id.
4. **Elicitation / Multi Round-Trip Requests (MRTR):** server-initiated requests are replaced by `InputRequiredResult` + client retry with `inputResponses`. -> Do NOT use elicitation for approvals: the approver is a separate reviewer, not the calling client. May be used later for "missing argument" prompts. Out of scope for now.
5. **Deprecated:** Roots, Sampling, Logging. -> The Host uses none of them. Logging goes to stderr/OpenTelemetry. Folder access comes from module config, not Roots.
6. **OpenTelemetry trace context** (`traceparent` etc. in `_meta`) is now a documented convention. -> Phase 4 telemetry should read incoming `traceparent` so an action's span joins the caller's trace.
7. **`tools/list` should be deterministic in order** and results carry `ttlMs`/`cacheScope`. -> ToolCatalog sorts tools by name; advertised list depends only on policy, never on session/caller.
8. **Auth:** Streamable HTTP authorization changed (Client ID Metadata Documents preferred; DCR deprecated). -> HTTP transport stays out of the first release (decision 4); revisit auth before enabling it.
9. **Result shape:** all results carry `resultType` (`complete` / `input_required`); the SDK handles this. Built-in tool results still carry our own `"v": 1`.

Plan edits made because of this: sections 5.3 (SessionId definition), 6 (session identity, tool ordering), 11 (decision 4 note).

## 14. Implementation log

- **Phase 0 (done):** spec check, section 13.
- **Phase 1 (done, 81b8a54):** `OpenTail.Host.Abstractions` (+ tests incl. architecture test). Four files moved from Core; `JsonToolBase` stayed. Local builds use `-p:NuGetAudit=false` because of a pre-existing NU1902 on `Microsoft.Build.Tasks.Git` 10.0.300 (not caused by this work).
- **Phase 2 (done):** `OpenTail.Host` + `OpenTail.Host.Tests` (25 tests pass), including the inbox-triage scenario over a real in-process MCP connection (SDK stream transports on pipes). Deviations from the plan text:
  - Tools are served through `Handlers.ListToolsHandler` / `CallToolHandler` instead of `ToolCollection`, because the collection does not keep order and the spec wants a deterministic `tools/list`. Using both duplicates tools; use handlers only.
  - Built-in tools return `JsonElement` (a plain `string` return is JSON-encoded a second time by `AIFunctionFactory`); optional parameters need C# default values (`= null`) or the SDK treats them as required.
  - The console reviewer is not built; tests drive review through `IActionQueue.ReviewAsync`. A real reviewer surface arrives with the admin page (Phase 7).
  - `resubmit_action` was added to `IActionQueue`.
  - Rate limits / blast-radius, previews end-to-end, SQLite, the outbox and OpenTelemetry are Phases 3-4 as planned.
- **Phase 3 (done):** `SqliteActionStore` + `SqliteAuditSink` (WAL, `user_version` migrations, compare-and-set inside `BEGIN IMMEDIATE`), leases on `Executing`, `ActionExecutor.RecoverAsync` (approved-never-started runs once; lapsed lease -> `Failed(interrupted, retryable)`; retried automatically only for `IdempotentByKey` tools, with the same key passed to the tool via `AIFunctionArguments.Context[HostContract.IdempotencyContextKey]`), maintenance sweep (`RunMaintenanceOnceAsync`, also run at stdio startup and every 30 s). 35 Host tests pass, including two hosts on one DB file approving at once (one execution), restart durability and refusal of a newer schema. The outbox is the `Approved` row itself (single table), not a separate outbox table. Known limit: a tool legitimately running longer than the lease (default 5 min) would be treated as interrupted; lease extension is not implemented.
- **Phase 4 (done):** previews stored at submit (`PreviewUnavailable` flag for high-impact tools without one); rate limits (`PerHour`/`PerDay`) enforced at execution so they bind approved actions (`Failed(rate_limited, retryable)`); `MaxPendingPerRequester` (`Denied(queue_full)`); trust ramp (`TrustAdvisor`: eligibility, operator-only `PromoteAsync`, runtime-only, withdrawn on first failure/rejection, never overrides taint; validation requires `Mode=Queue`); `OpenTailHost.Explain`; `Redaction.Scrub` on every error message returned or stored; `HostTelemetry` (ActivitySource + Meter `OpenTail.Host`: `action.submit`/`action.execute` activities, `actions`, `pending_actions`, `time_to_decision`, `execution_duration`; tags never carry argument values). 54 Host tests pass. **Deferred:** reading incoming `traceparent` from MCP `_meta` (the tool call path does not expose the request context to the wrapped function; needs a handler-level change), and exposing queued actions as MCP tasks (stretch, section 13).
- **Rule change found in Phase 5 planning:** modules need the connector contracts, so the dependency rule is now: a module may reference `OpenTail.Host.Abstractions` and `OpenTail.Connectivity.*` projects (leaf connectors, none of which reference Core), but never `OpenTail.Core*`, `OpenTail.Host`, or another module. The architecture test is updated to match.
- **Phase 5 (done):** `OpenTail.Modules.Files` (list/read/write confined to one root; reparse points and absolute/`..` paths refused; read taints by default; write previewed, overwrite needs the flag) and `OpenTail.Modules.Email` (`email.list_recent`/`email.read` untrusted + "do not follow" notice; `email.send` ExternalEffect with preview, recipient/subject validation and optional domain allow-list) over the existing `IInboxAdapter`/`IEmailSender` contracts. `IHostModule`/`IToolRegistry`/`ToolRegistration` moved into `Host.Abstractions` so modules need not reference `OpenTail.Host`. Preview may throw `ArgumentException` -> immediate `Denied(invalid_arguments)` instead of queueing hopeless requests. Inbox-triage runs end to end over MCP with the real email module (fake inbox/sender). 67 Host tests + 27 abstractions/architecture tests pass. Deviation: the old `PolicyEnforcingEmailSender`/`IEmailSendApprovalService` were **not** marked `[Obsolete]` because `Manager` and `EmailAutomation` still use them and warnings are errors repo-wide; they stay until those callers move to the Host queue.
- **Phase 6 (done):** the Core coupling of `OpenTail.OpenApi` was assessed by reading the importer end to end: it is `Microsoft.SemanticKernel.Plugins.OpenApi` (pulls Semantic Kernel) plus a handful of Core types (`OpenApiImportRequest`, `OpenApiProviderCatalog`, `ILogger`/`LogEvent`), and SK's plugin hides the HTTP verb needed for risk classification. Splitting it would have meant carrying Semantic Kernel into every deployment, so instead a **new, self-contained `OpenTail.Modules.OpenApi`** was written on `Microsoft.OpenApi.Readers` alone (references only `Host.Abstractions`); `OpenTail.OpenApi` is untouched. One tool per operation; GET/HEAD = Read, POST/PUT/PATCH = **ExternalEffect** (deviation from the plan's "Write": effects land on a third party, so the taint rule should bind them), DELETE = Destructive, operator `RiskOverrides`/`IncludeOperations`; every operation `ReadsUntrusted` by default; request building escapes path/query values into a fixed base URL (traversal characters in path values refused, redirects not followed, https only unless `AllowInsecureHttp`, response size capped); the API token comes from a named environment variable and never reaches the model, schema or audit; mutating operations get a preview and bad arguments fail fast as `invalid_arguments`. 88 Host tests pass.
- **Phase 7 (done, except the human walkthrough):** `OpenTail.Host.Admin` (ASP.NET Core minimal API on Kestrel, loopback bind enforced, Host-header allow-list against DNS rebinding, bearer token per reviewer on every `/api` call so there are no cookies and no CSRF, tokens compared in constant time, `no-store`/`nosniff`/nonce-CSP; the page writes all dynamic text with `textContent`). Endpoints: list/filter actions, action detail (preview, submitted vs effective arguments, taint flag, reviews, audit), review (approve / reject / request changes / retry; reasons required where the AI needs them; the reviewer identity comes from the token, never the body; edit-and-approve validates the edit through the tool's preview first), trust status/promotion, explain. `ReviewerRunner` + `DelegateReviewer` give the AI-reviewer adapter: it is only offered actions whose policy says `Reviewer = Ai|Either`, its identity is stamped by the runner, errors are audited, abstaining leaves the action for a human. 102 Host tests pass. **Not done:** the planned manual walkthrough with a person who is not the author; the page is untested in a real browser (only its headers and structure are asserted).
- **Phase 8 (done, with two items held back):** modules `Csv` (RFC 4180 reader, one root), `Sql` (read-only `sql.query` through a statement guard and named parameters, optional previewed `sql.write`/`sql.delete`, preview by rolled-back transaction; the guard is a seatbelt, the boundary is a least-privilege login), `Calendar` (over `ICalendarAdapter`; create/delete/respond previewed, delete/respond fetch the event for the preview), `Web` (`web.fetch`: required host allow-list, https only, DNS resolved and pinned at connect time with private/loopback/link-local/metadata/6to4 ranges refused, every redirect re-validated, text/HTML/JSON/XML only, size caps). Shared `RootedPath` moved into `Host.Abstractions` (Files and Csv use it). `DeploymentRunner` (policy file, SQLite, loopback admin page, stdio) plus two sample deployments, `OpenTail.Host.Sample.EmailOnly` and `OpenTail.Host.Sample.FilesOpenApi`, each with a policy file. `scripts/check-host-deployments.ps1` publishes both and checks: no `OpenTail.Core`, Workflows, Semantic Kernel, Roslyn, Reactive, LLamaSharp, Hangfire or Spectre in the output, and each deployment contains only its own modules (**passes**: EmailOnly 40 assemblies / 57 MB incl. MailKit and SQLite native runtimes; FilesOpenApi 16 assemblies / 34 MB); all ten Host packages pack. A real child process over real stdio is exercised (`RealProcessTests`: tool list, read, queued write, durable queue on disk; bad policy exits 2 with the message on stderr and nothing on stdout). Whole solution builds with 0 warnings/errors; the 12 existing tests that touch the moved abstractions pass. **187 Host tests, 27 abstraction/architecture tests.**
  - **Held back, needs a decision:** retiring `opentail/OpenTail.Mcp` (the old coding server). The plan says "only with explicit approval", and it is still referenced by `OpenTail.Tests` and docs, so it was left in place.
  - **Not done:** a real-client check (Claude Desktop or similar) against a sample deployment; the non-author walkthrough of the approval page; reading incoming `traceparent`; exposing queued actions as MCP tasks; lease extension for long-running tools; persisting trust promotions across restarts (deliberately runtime-only).
  - **Known size note:** the sample deployments are framework-dependent and still 34-57 MB because of native runtimes for every platform; a `RuntimeIdentifier`-specific publish would cut that, but was not done.
- **Review pass (after Phase 8):** two fixes found by reading the code again. (1) Approving an action past its `ExpiresAt` before the periodic sweep had marked it Expired used to succeed; approval now expires it and refuses. (2) A call with an unknown `sessionId` surfaced as a generic protocol error (the SDK hides exception messages); `QueuedToolFunction` now returns a structured `invalid_request` the model can act on. **189 Host tests.** Noted, not changed: sessions in `SessionRegistry` are never evicted (memory only grows with `start_session` calls).

- **Retired `OpenTail.Mcp`** (2026-10-15, on the owner's explicit say-so). The successor `C:\Git\OpenTail.MCP-src` covers its tools except `opentail_query_feed` (and the already-disabled bench tools). How to port those, if ever wanted, is in `C:\Git\OpenTail.MCP-src\PORTING-OLD-OPENTAIL-MCP-TOOLS.md`. Removed from `OpenTail.slnx`, folder deleted, docs updated; full solution builds with 0 warnings and 0 errors. The old source stays in git history.
