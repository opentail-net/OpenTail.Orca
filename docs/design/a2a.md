# Design: A2A as a second front door

**Status:** proposed 2026-11-11. Nothing built. Written from reading [`opentail-host`](https://github.com/opentail-net/opentail-host)
at `4b8a8b7` (branch `standard-skill-files`; the approval naming and the re-check changed some lines it cites, see the corrections below), the A2A v1.0 specification and the official .NET SDK as published in November 2026.
Protocol and SDK facts were checked on 2026-11-11. The SDK is a preview: re-check the facts marked *verify* before building.

**Where it fits:** a way in for agents that are not ours, beside MCP. It adds a protocol edge and two internal tools. It does not
change how a call is decided. It depends on [work items](work-and-continuations.md) (the inbox), [where data goes](data-flow.md)
(replies are exits), [roles and permissions](roles-and-permissions.md) (the profile) and [audit](audit-and-evidence.md). The work
design says A2A is "later and only if asked for" (its step 5). This document does not change that decision. It makes the later step
concrete.

## Summary

- **A2A is a protocol edge, not a second decision model.** Every A2A act reaches `ActionQueue.SubmitAsync` as a call with a profile,
  as an MCP call does. Taint, labels, limits, levels, reviewers and the audit trail are the existing ones.
- **Partners are callers.** A partner is a server-generated `partnerId` with one or more API keys. Each key has a profile and the new
  scope `a2a:call`. The profile's tool list is the partner's permission, exactly as for an agent over MCP. Nothing else grants a
  partner anything.
- **Two internal tools carry the A2A-specific risk.** `a2a.submit` takes an inbound task: it reads outside content, so it taints the
  session, and it queues for a person by default. `a2a.reply` sends an answer out: it is an exit, checked against the data-flow rules,
  and it queues by default.
- **Skills are curated.** The operator declares each A2A skill in configuration. A skill names one tool, or one inbox. The Agent Card
  a caller sees lists only what its profile may use.
- **Recommended first cut:** an inbound inbox plus curated skills, JSON-RPC, polling only (no streaming, no push), API-key partners,
  every reply reviewed. Outbound calls to other agents, push, card signing and HTTP+JSON come later, in phases below.

## Corrections from external review (2026-11-11)

An external review was checked against the A2A v1.0 specification (`https://a2a-protocol.org/latest/specification/`) and the SDK's
documentation. Each correction is now reflected in the body above. This is the change log.

1. **Task ids.** Reflected in section 4 (step 5) and section 10 (`a2a_tasks`). The task id is server-generated and is not the work item
   id. First-message handling is crash-safe and idempotent.
2. **`returnImmediately`.** Reflected in "Who decides" and in section 4. `true` returns once the task is created. `false` and an omitted
   value block until a terminal or interrupted state. **Open:** how many blocking waits one partner may hold open at once. That is an
   operational limit, and it must not change what a blocked request returns.
3. **The Agent Card.** Reflected in section 8. The card requires authentication. There is no public card.
4. **Skill selection.** Reflected in section 4 and the contracts. The extension URI is `https://opentail.net/a2a/extensions/skill-selection/v1`.
5. **ListTasks.** Reflected in section 4. Cursor pagination, newest first.
6. **A2A-Version. Still open, for the owner to confirm.** Reflected in sections 1 and 11: 1.0 is the only version served. The two sources
   disagree on an empty value:
   - **The spec** says an empty value is interpreted as 0.3 (§3.6.2). The server then answers `VersionNotSupportedError`, because it
     supports only 1.0.
   - **The SDK's migration guide** (`docs/migration-guide-v1.md` in a2a-dotnet) says empty or missing is accepted and defaults to the
     current version, 1.0. It accepts `0.3` and `1.0` and answers any other value with `VersionNotSupported` (-32009).
   - **A missing header** is not stated in the spec at all. The SDK's rule is the only written answer.
   - **Recommended, for the owner to confirm:** follow the spec for empty (reject as 0.3), and follow the SDK for missing (treat as 1.0).
     Reject `0.3` as this Host does not serve it. Interoperability tests run against the SDK-mapped endpoint for empty, missing, `0.3` and
     `1.0`, and record the SDK's actual result for each.
7. **Partner identity.** Reflected in section 2 and the summary. The `partnerId` is a server-generated, immutable UUID, stored separately
   from the API key name.
8. **Order.** Reflected in the build order. Whole-action binding lands before the inbox is exposed to external partners.

## Who decides, and who is asked

No question reaches the partner from governance. A partner's requests and answers are states of a task. Questions go to people only
inside the Host's review queue.

- **Reviewers** are the people the Host already uses: holders of the `review` role (the review page, the API or a reviewer token),
  narrowed by reviewer group where the policy names one. They are told through the existing channels: the review page and the Teams
  card. Both `a2a.submit` and `a2a.reply` name `Reviewer: Human`, so no AI reviewer may decide them.
- **Step by step.** An inbound message becomes an `a2a.submit` action. With `returnImmediately=true`, the Host returns once the task is
  created, in SUBMITTED, because the queue never waits for a reviewer, and a reviewer decides the action. A blocking request waits
  instead (section 4). When the work is done, the reply becomes an `a2a.reply` action, and a
  reviewer sees the exact text the partner would receive. Only an approved reply can appear in `GetTask`.
- **Governance never asks the partner.** The one exception is INPUT_REQUIRED (phase 3). There an internal agent asks the partner
  through `work.ask_partner`, and the partner answers with a new message on the same task. No human is involved in that exchange.

## Contracts this must satisfy

### The protocol (A2A v1.0)

What the specification requires of a server, and where this design answers it:

| Requirement | Section | Answer here |
|---|---|---|
| Authenticate every incoming request | §7.4 | All A2A routes use the Host's existing sign-in. There is no anonymous route, the card included (see [the card](#8-the-agent-card)). |
| Authorisation is the server's to define; may use skills, actions, data and scopes | §7.5 | Profiles and policy, as today. |
| A task waiting for credentials is `AUTH_REQUIRED`; that state is not authorisation | §7.6.1, §7.6.4 | Not used. The Host never asks a partner for in-band credentials. |
| Serve the card at `/.well-known/agent-card.json` | §8.1, §8.2 | Yes; see the card section. |
| Card signatures (optional): JWS over JCS-canonical JSON, `alg` and `kid` required, expired keys not used | §8.4 | Phase 2. |
| `ListTasks` returns only the caller's tasks; authorisation runs before any query that could reveal existence | §13.1 | Tasks are owned by the partner's subject; foreign and unknown ids give the same answer. |
| Push: the sender should refuse private addresses; the receiver must validate the credential | §13.2 | Phase 3. |
| Errors: TaskNotFound, TaskNotCancelable, PushNotificationNotSupported, UnsupportedOperation, ExtendedAgentCardNotConfigured, VersionNotSupported | §3, §5.4, §10.6, §11.6 | Mapped in [task states](#5-task-states). |

Read as complete sections (through summaries): §4.4 (card), §4.5 (security schemes), §7, §8. Read in part, as summaries: §10 and §11
(bindings), §13.1 and §13.2 (push). Not read: §3 (the abstract definitions), §4.3 (push objects) and §5.4 (the error table). The
specification does not say which error a caller gets for a skill it may not use, nor what an expired token gets. Both are decided here.

The specification's own examples send `A2A-Version: 0.3` while its text describes 1.0. *Verify* against the SDK before relying on
either.

### The SDK (`A2A` 1.0.0-preview4, Apache-2.0, .NET 8 and later)

From the SDK's README and docs. Not checked in its code. *Verify* every signature on the version we pin.

- Hosting: `MapA2A(path)` (JSON-RPC), `MapHttpA2A()` (HTTP+JSON), `MapWellKnownAgentCard(card)`, `AddA2AAgent<THandler>()`.
- The implementer provides `IAgentHandler`: `ExecuteAsync(RequestContext, AgentEventQueue, CancellationToken)` and `CancelAsync`.
  Task storage is `ITaskStore` (Get, Save, Delete, List). `InMemoryTaskStore` is for development and tests only.
- The SDK applies **no authentication or authorisation** ("The SDK does not apply any authentication or authorization to A2A
  endpoints"). Tenant headers are cleared, but "The SDK does not enforce any tenant boundaries."
- Push: the default sender retries in memory and is not durable. Its payloads are not limited to status: they can carry artifacts.
  `IPushNotificationSender` can be replaced.
- Push URL validation is built in: HTTPS only, public addresses only, an optional host allow-list, no redirects, and the connection is
  made only to approved addresses.

Read from the package's XML documentation (2026-11-11), which changes the plan:

- **The engine is not used.** `A2AServer` owns its own task store (`ITaskStore`) and runs an `IAgentHandler`. Using it would give the
  Host a second task store. The plan implements `IA2ARequestHandler` instead, with its eleven operations (send, stream, get, list,
  cancel, subscribe, four push-configuration operations, extended card), and keeps every task in the Host. The endpoint is mapped
  with `MapA2A(path, handler)`.
- **Caller identity is not in the request context.** `A2ARequestContext` carries only extension data. The caller comes from
  `HttpContext.User` through `IHttpContextAccessor`, read with `HostClaims.CallerOf`.
- **Message model.** `Message` has `Role`, `Parts`, `MessageId`, `ContextId`, `TaskId`, `ReferenceTaskIds`, `Extensions` and
  `Metadata`, and no skill field. `Part` has `Text`, `Raw`, `Url`, `Data`, `Filename` and `MediaType`. `AgentTask` has `Id`,
  `ContextId`, `Status`, `History`, `Artifacts` and `Metadata`. `TaskStatus` has `State`, `Message` and `Timestamp`. `TaskState` has
  the eight states used above. The plan selects the skill through the skill-selection extension (section 4): the extension's URI is sent
  with the message, and the skill id is carried in the extension's metadata key (correction 4).
- **No reply trigger exists yet.** `WorkService.CompleteAsync` raises no event. Phase 1 needs an additive `Completed` event, and
  `GetTask` must also create the reply when the item is done, in case the event handler fails.
- **Schema.** `SqliteActionStore` is at schema 12. The A2A tables take 13, with the migration pattern used for `work_items`.
- **Startup checks the plan adds:** `a2a.submit` and `a2a.reply` must be Queue or Deny, never Execute; `a2a.reply` must be reviewed
  by a person with `AllowEdits: false`; `DataFlowMode` must be `Enforce`; the database must be `synchronous=full`; every `a2a:call` key
  must name a profile.

### The Host (read in code, 4b8a8b7)

| Contract | Members A2A relies on | What A2A needs from it |
|---|---|---|
| `ActionQueue.SubmitAsync(ActionRequest)` (`Runtime/ActionQueue.cs:23`) | The decision, branch by branch (next section) | Every A2A act goes through it, unchanged. |
| `ActionRequest` and `ActionRequester` (`Abstractions/Actions/Interfaces.cs:90`, `ActionRecord.cs`) | `ToolName`, `ArgumentsJson`, `Requester`, `SessionId`, `IdempotencyKey`, `Profile` | The requester kind must be `RequesterKind.AiSession` (see the trap below). |
| `IActionStore.CreateAsync` (`Interfaces.cs:40`) | Dedupe on (tool, idempotency key, arguments hash) | A repeated `messageId` with changed text would create a second action, so the adapter checks `messageId` first. |
| `IAuditSink.AppendAsync` (`Interfaces.cs:139`) | Append-only; "must not record argument values or secrets" | A2A events carry hashes and identifiers, never message text. |
| `WorkService` (`Work/WorkItems.cs:213`) | `CreateAsync` (272), `ClaimNextAsync` (318), `Read` (340), `CompleteAsync`, `FailAsync`, `HandBackAsync`, `CancelAsync` (720) | Inbox tasks are work items. `Read` returns the whole item (notes, labels, claimants), so A2A needs its own projection. |
| `SessionRegistry` (`Runtime/SessionRegistry.cs`) | `Start(owner)`, `Taint(session)`, `TaintSubject(subject)`, `IsTainted`, `Exists(session, owner)`; idle TTL 14 days, taint window 30 minutes (`Policy/PolicyConfig.cs:24-25`) | One session per A2A context. Context state must be stored with the context (see [persistence](#10-persistence-and-durability)). |
| `DataLabels` (`Policy/DataLabels.cs`) | `Held`, `Check`, `Gain`, `GainCarried`; subject labels last the taint window (95-110, 176) | Replies are checked here. `DataFlowMode` defaults to `ReportOnly` (`PolicyConfig.cs:62`). |
| `PolicyResolver` (`Policy/PolicyResolver.cs`) | `Admits(tool, profile, localOperator)` (244), `IsAdvertised`, `Resolve` | The partner profile is required. |
| `OpenTailHost.BuildTools(profile)` (`OpenTailHost.cs:267`) | The tools a profile sees | The extended card uses the same function, so the card and `tools/list` cannot disagree. |
| `IdentityLevels` (`Runtime/IdentityLevels.cs`) | `Effective`, `OutOfProfileAsync`, `SuspendAsync` | A2A callers are suspended, and tripped, the same way as MCP callers. |
| `GuardedHttpClientFactory` (`Modules/GuardedHttp.cs`) | `Allowed(uri)`; connect-time address check (71-73); no automatic redirects (30) | Outbound A2A and push delivery (later phases). |
| `StaticTokenTable` and `HostClaims` (`Http/StaticTokenAuthentication.cs`) | Credential to subject, profile and roles | The profile is set from the key's `Profile` only. JWT callers get no profile today. |
| `ApiKeyEntry.Scopes` validation (`Http/HostHttpOptions.cs:155`) | Refuses review, admin and group scopes | A new scope `a2a:call` passes unchanged. |
| `HostHttp` mapping (`Http/HostHttp.cs:125`, 203) | `MapMcp(...).RequireAuthorization(McpPolicy)`; `McpPolicy` requires `mcp:tools` | A2A gets its own policy, requiring `a2a:call`. |
| `WorkModule.DelegateAsync` (`Work/WorkModule.cs:102-119`) | Reads the requester and profile from the call's context; checks `MayDelegateTo` | The pattern for inbox tools. |

**Trap: requester kind.** `ActionQueue.cs:61` sets `localOperator` to true for any requester kind other than `AiSession`. Then
`Admits` (`PolicyResolver.cs:250`) lets a caller with no profile reach every administration tool. Every A2A action must use
`RequesterKind.AiSession`, the same as `QueuedToolFunction.cs:71`. A2A also refuses any call without a profile, rather than
falling back.

**Trap: JWT callers have no profile.** Only `StaticTokenHandler` sets the profile claim (`StaticTokenAuthentication.cs:102`).
`RoleProfiles` is not built ([roles and permissions](roles-and-permissions.md), order step 4). So v1 partners are API keys.

## Existing behaviour the A2A path reuses

Each branch of `ActionQueue.SubmitAsync` (`Runtime/ActionQueue.cs`), and what an A2A call gets from it:

| Branch (line) | Behaviour today | What A2A gets, or must add |
|---|---|---|
| Session (37-44) | Unknown or foreign session: `ArgumentException` | The adapter binds one session per context and owner. Errors map to a generic message. |
| Unknown tool (46-56) | Denied as `unknown_tool` | Skills come from curated configuration, so a partner cannot name a tool that is not in its skills. |
| Profile (58-72) | Refused and audited as `not_in_profile`; a trip for the identity | Partner profile required; a call with no profile is refused at the edge. |
| Access requests (73-85) | Only `requestable` tools | Partner profiles have no requestable tools in v1. |
| Decoy (101-116) | An outbound decoy marker suspends the identity | Replies are outbound, so decoy markers in replies are caught. |
| Suspended identity (118-133) | Refused | This is the kill switch for a partner. |
| Taint (135) | Session or subject taint | Unchanged. |
| Policy resolve (137) | Execute, Queue or Deny per tool and rule | `a2a.submit` and `a2a.reply` default to Queue. A partner-specific mode uses a conditional rule on the `partner` argument (*verify* the rule syntax). |
| Elevated level (139-148) | Above-read tools wait for a person | Unchanged. |
| Soft signals (150-151) | Breadth and repeated attempts raise the level | Unchanged. |
| Data-flow exit (153-175) | Labels held by the session, checked against destinations | `a2a.reply` is an exit. Only enforced when `DataFlowMode` is `Enforce`, so A2A requires it. |
| Labels gained (176-178) | The call's labels join the session | Unchanged. |
| Taint on submit (180-186) | A `ReadsUntrusted` tool taints the session or the subject, when submitted | `a2a.submit` is `ReadsUntrusted`, so every inbound message taints. |
| Queue, with the cap (209-265) | Per-requester pending cap; preview; `Pending` record | The cap bounds a partner's inbox. `a2a.submit` and `a2a.reply` provide previews, so the reviewer reads the exact text. |
| Delay (undo window) | Execute after a window | Not used for A2A in v1. |
| Rate limits (per tool) | Per hour and per day on a tool's policy | Set on `a2a.submit`. The HTTP partition is per subject, so A2A calls share it. |

## The shape of the design

### 1. The protocol edge

- **Routes.** The JSON-RPC endpoint at a path such as `/a2a`, the card at `/.well-known/agent-card.json`, and the extended card at
  `GET /extendedAgentCard`. They sit in the same ASP.NET Core app as `/mcp`, so the allowed-host check, the rate limit, the security
  headers and the body-size limit all apply.
- **Access.** `A2aPolicy` requires the role `a2a:call`. An API key carries it through its `Scopes`. Keys without the scope cannot call
  A2A, so existing agents are unaffected. The SDK does no authentication, so this is the only sign-in there is.
- **Versions.** 1.0 is the only version served. An empty `A2A-Version` is read as 0.3, per the spec, and refused (recommended; the owner
  confirms). A missing header is an open owner decision (recommended: 1.0, as the SDK does). `0.3` and any other value are refused with
  `VersionNotSupported`. Support for 0.3 comes through the SDK's compatibility package if a partner needs it (decision 5). See
  correction 6.
- **Transport.** JSON-RPC first. HTTP+JSON when a partner asks. No gRPC.
- **Streaming and push.** Not in v1. `capabilities.streaming` and `pushNotifications` are false. Partners poll `GetTask`, which is the
  same baseline as MCP tasks ([mcp-tasks.md](mcp-tasks.md)).
- **Headers and bodies.** `tenant` must be empty; anything else is refused, because the Host is single-tenant. Requests are capped at
  `MaxBodyBytes` (1 MB by default). The SDK's security guide asks for a strict JSON content type on JSON-RPC endpoints; whether
  `application/a2a+json` is also accepted for requests is *verify*.

### 2. Partners and profiles

A partner is a `partnerId` with its API keys. The profile of a key lists the tools that key may use:

```json
"Agents": {
  "partner-acme": { "Tools": [ "a2a.submit", "a2a.reply", "erp.query" ] }
}
```

- **Explicit lists only.** No wildcards for partners. `*` does not include administration tools, but a partner should never have
  any of the others by accident. A startup warning names every partner profile with a wildcard.
- **Partner keys set `Scopes` explicitly, to `a2a:call` alone.** A key with no `Scopes` gets `mcp:tools` (`StaticTokenAuthentication.cs:66`),
  so a partner key left at the default could also call the MCP endpoint.
- **Partner identity.** The `partnerId` is a server-generated UUID, immutable, and stored separately from the API key name. A display name
  is for people and can change. One or more API keys link to the same `partnerId`, through an optional partner reference on each key
  entry in the Host's existing credential configuration (no second credential system). Rotating a key does not change the partner's
  identity. The subject is the partner's, so taint, labels and task ownership follow the partner.
- **No administration.** A partner profile never lists an `Administers` tool. The existing grant rule already blocks wildcards from
  reaching them.
- **The adapter sets the partner.** Every `a2a.submit` and `a2a.reply` call carries a `partner` argument: the `partnerId`. The adapter
  takes it from the credential and overwrites any value from the request body. Rules and data-flow destinations match on it.

### 3. Skills

A skill is declared by the operator in configuration. It is never generated from the tool list:

```json
"A2A": {
  "Skills": {
    "vendor-status":       { "Name": "Vendor status", "Description": "Status of one vendor record.", "Tool": "erp.query", "Input": "data" },
    "procurement-request": { "Name": "Procurement request", "Description": "Ask the procurement agents for something.", "Inbox": "procurement" }
  }
}
```

- **A tool skill** (`Tool`) runs one tool. It takes `data` parts only, validated against the tool's parameters. Text parts are refused:
  free text never becomes a tool argument.
- **An inbox skill** (`Inbox`) creates a work item for a profile. The partner cannot choose the profile; the skill decides it.
- **Changing skills** is a configuration change, reviewed in version control. No A2A operation can change skills, cards, profiles or
  policy. Whether card changes should go through the skills review instead is decision 8.

### 4. Inbound tasks (the inbox)

`SendMessage` with an inbox skill. The skill is selected through the skill-selection extension,
`https://opentail.net/a2a/extensions/skill-selection/v1`: the message names the extension in its extensions (the `A2A-Extensions`
header, spec §14.2.2) and carries the skill id under the extension's metadata key (correction 4). The extension is versioned, and its
specification is published at that URI before any partner is onboarded.

1. The adapter checks `(owner, messageId)` in its own tables (section 10). A repeat with the same content returns the existing task. A
   repeat with different content is refused, so `messageId` cannot be reused for a second request. This check comes first, because
   the action store's own dedupe includes the arguments hash.
2. The adapter gets or starts the context's session (`SessionRegistry.Start(owner)`) and submits `a2a.submit` as the partner, with
   `partner` set, the goal and the data parts as arguments, and an idempotency key derived from `messageId`.
3. `a2a.submit` is `ReadsUntrusted` and `Write`. The policy queues it by default. The reviewer sees the goal in the preview, as
   quoted outside content.
4. On approval, the tool body calls `WorkService.CreateAsync(goal, "profile:<target>", from: partner subject, inputs, thread: context)`.
   The work item carries the taint and labels of the submission, through `Carry`, as delegation does.
5. **The task id is not the work item id.** The server generates a persistent task id for each new task (spec §3.4.2): a client
   cannot supply one. A task id must exist before its work item does, so it cannot be the work item's id. The task id is stored in
   `a2a_tasks` (section 10), linked to the `a2a.submit` action, to the work item once it exists, and to the `a2a.reply` action.
   First-message handling is crash-safe and idempotent: a crash between creating the action and storing the mapping must not create a
   second task when the partner retries the same `messageId` (correction 1). Foreign and unknown ids give the same answer.

Message parts: `text` becomes the goal (capped by `WorkConfig.GoalMaxBytes`); `data` becomes the inputs JSON (capped by
`InputsMaxBytes`); `file` is refused in v1 (decision 11 covers attachments later).

**Blocking and `returnImmediately` (correction 2).** `returnImmediately=true` returns once the task is created, in SUBMITTED. `false`,
or an omitted value, waits until the task reaches a terminal state or an interrupted state (input-required or auth-required). The flag
is in spec §3.2.2. A blocked request is never answered with SUBMITTED because a server timeout expired. If the client disconnects or times out,
the task is not discarded or cancelled, and a retry with the same `messageId` from the same partner finds the existing task. The
recommended integration mode is `returnImmediately=true`, because approval is asynchronous. The SDK reads the flag from
`SendMessageConfiguration.ReturnImmediately`. Tests must cover `true`, `false` and omitted.

**Built (03b-3).** A blocking send creates the task exactly as `true` does, then waits for a terminal state (COMPLETED, FAILED, CANCELED or
REJECTED). If `BlockingWait` passes first (1 second to 5 minutes, default 30 seconds), the answer is an error, not a task: JSON-RPC
`InternalError`, with the text "The task <id> is still in progress. Read it with GetTask, or send with returnImmediately set to true.".
No SDK error code means "still running", so `InternalError` is the one that fits. The task is not cancelled. Each partner may have
`MaxBlockingWaitsPerPartner` waits at once (1 to 64, default 4); a further blocking send is refused before any row exists, audited as
`a2a.refused` with reason `too_many_waits`.

**ListTasks (correction 5).** Returns only the caller's tasks. Cursor pagination: `pageToken` in, `nextPageToken` out, and the last
page returns an empty string. Results are sorted newest first by status update time (spec §3.1.4).

**Built (03b-3), and the difference from the spec.** This slice sorts newest first by creation time, then task id. The status update time
would need three stores (the task row, the work item, the reply action) to agree, and the spec's order is not checked here. The page token
names the last row's position (base64url of its creation time and task id), not an offset, so a task created between two pages does not
move the next one. `status` and `statusTimestampAfter` are refused with InvalidParams until state is stored; `historyLength` is ignored
(there is no history).

**Why an action first.** A new partner's requests go to a person until the operator trusts them. `TrustRamp` is per tool, though, not
per partner. To trust one partner and not another, use a conditional rule on the `partner` argument, which the partner cannot set.
*Verify* that rule conditions can read a top-level argument.

### 5. Task states

| A2A state | Host state | Note |
|---|---|---|
| `SUBMITTED` | `a2a.submit` pending a person | "Received, not started." This deliberately differs from [MCP tasks](mcp-tasks.md), which report reviewer waits as `working`. |
| `WORKING` | Work item open, claimed or running; the action executing | Accepted. The status message says what is happening, in general words. |
| `INPUT_REQUIRED` | Not in v1 | Needs a `work.ask_partner` tool (phase 3). |
| `AUTH_REQUIRED` | Not used | The Host does not ask partners for credentials. |
| `COMPLETED` | The reply's artifact has been approved and delivered (`GetTask`) | Artifacts come only from an executed `a2a.reply`. |
| `REJECTED` | Reviewer rejected, changes were requested, or policy refused | Generic text. The reviewer's reason stays internal (decision 9). A changed request is a new message with a new `messageId`. |
| `FAILED` | Work item dead or expired; the tool failed | Generic text. The internal failure kind is not sent. |
| `CANCELED` | The partner cancelled a pending action, or an open work item | An open work item is cancelled with `WorkService.CancelAsync`. |

A partner's `CancelTask` on a **claimed** item returns `TaskNotCancelable` (400). `WorkService.CancelAsync` allows cancelling a claimed
item, but the A2A adapter must not use that path: it would stop an agent mid-work.

### 6. Replies are exits

- **The reply is an action.** When the work item completes, the adapter creates `a2a.reply` with the summary text. `a2a.reply` is
  `ExternalEffect`. Its preview is the text itself. Its policy queues by default and sets `AllowEdits: false`: the reviewer approves
  what was shown.
- **The labels reach the check.** Before submitting `a2a.reply`, the adapter applies the work item's labels and taint to the
  partner's session (`GainCarried`, as `WorkService.Apply` does). The standard exit check then sees them. The item's labels are
  not on the partner's session otherwise.
- **The destination is named.** A label rule names the partner in a destination condition on the `partner` argument (the syntax of
  `DataLabels.Check`; `Allow` names tools, not destinations, so `"a2a.reply:partner=..."` is not a form the Host reads). Tested in
  `A_label_the_completer_held_is_checked_at_the_exit...`:

```json
"DataFlow": { "finance": { "Destinations": [ { "Tool": "a2a.reply", "When": { "arg": "/partner", "equals": "<partnerId>" } } ], "Otherwise": "Deny" } }
```

- **Enforcement is required.** `DataFlowMode` defaults to `ReportOnly`. With that default, a labelled reply would be recorded as
  "would be queued" and sent. The Host must refuse to start with A2A enabled unless `DataFlowMode` is `Enforce`.
- **One path.** `GetTask` returns an artifact only from an executed `a2a.reply`. Nothing else leaves the Host in an A2A response. This is
  the same rule as the MCP tasks design's "one path to results".
- **Direct skills.** The tool runs as an action and its result becomes the text of an `a2a.reply`, which then goes through the same
  review and the same label check.

### 7. Authentication, authorisation and errors

| Case | Answer |
|---|---|
| No credential, or an unknown one | `401` with `WWW-Authenticate`, as today |
| Valid credential without `a2a:call` | `403`, with no hint about the scope |
| A skill the profile does not allow, or an unknown skill | A generic "not available to you" error; never a list of skills |
| A task that is foreign, unknown or not visible | `TaskNotFound`, the same for both |
| `ListTasks` | Only the caller's tasks |
| A suspended identity | Refused like any call, with a generic message |
| Out-of-profile attempts | Audited and counted as a trip (`OutOfProfileAsync`), as for MCP |
| Rate limit | `429`, per subject, as today |

### 8. The agent card

- **The card requires authentication (correction 3).** `/.well-known/agent-card.json` is served only to an authenticated caller. It
  has the name, a generic description, the version, the JSON-RPC interface with `protocolVersion: "1.0"`, the capabilities (no
  streaming, no push), the input and output modes, and the security schemes the Host really accepts: `apiKey` in the `X-API-Key`
  header and `http` bearer. With Entra, an `openIdConnect` scheme. It lists no skills. Partners receive the card's location and their
  credentials out of band. The specification allows direct configuration as a discovery mechanism.
- **Extended card.** `GET /extendedAgentCard` returns, for an authenticated caller, the skills its profile may use, built by the same
  code that builds `tools/list`. Its capabilities say `extendedAgentCard: true`.
- **What the card never shows.** Tool names outside the caller's profile, internal names, module versions, policy and reviewer groups.
- **Decided (correction 3): the card requires authentication,** so an anonymous caller learns nothing, not even that the Host speaks A2A.
  The SDK's security guide also says protected agents should guard the discovery endpoint. A public card with no skills, the
  alternative, is not adopted (decision 1 is the default).
- **Signing (phase 2).** A JWS over the canonical JSON, with `alg` and `kid`, using a key from configuration. *Verify* that the SDK's
  serialisation can be canonicalised to RFC 8785 before relying on it.

### 9. Audit, metrics and retention

- **Audit events**, each without message text: `a2a.received` (task id, partner, skill, message hash, size), `a2a.state` (task id,
  from and to), `a2a.replied` (task id, reply hash, labels checked, outcome), `a2a.refused` (a reason code), `a2a.card_served`
  (rate-limited). The action and work-item records keep their own trails.
- **Metrics** are counted by skill and by profile, never by partner, as in [data flow](data-flow.md).
- **Retention.** Message text lives in the action's arguments and the work item's goal and inputs, so the existing retention purges
  cover it. Audit keeps hashes only, which is what the audit design asks for.

### 10. Persistence and durability

- **Tables.** `a2a_contexts`: owner, context id, session id, thread id, and the taint and labels the context has gathered.
  `a2a_messages`: owner, `messageId` hash, task id, content hash. `a2a_tasks`: task id, owner, the `a2a.submit` action id, the work
  item id (empty until the item exists) and the `a2a.reply` action id (empty until there is one). The task id is the one the partner
  is given. It is not the work item id (section 4, step 5). All three are in the same SQLite file, with a schema bump.
- **Contexts outlive sessions.** A session idles out after 14 days and takes its labels with it. Subject taint lasts 30 minutes. So
  each inbound message re-applies the context's stored taint and labels to its new session. This is the work design's rule 1: waiting
  does not clean a session.
- **Durability.** SQLite defaults to `synchronous=normal`. A power cut can then roll back the last commits (`SqliteDurability.cs`). A
  task id that the partner has been given must survive that, or the partner polls an id that no longer exists and may resubmit. With A2A
  enabled the deployment must set `OPENTAIL_DB_SYNCHRONOUS=full`, and startup should warn if it does not (decision 6).

### 11. Versions, gaps and the SDK

- **Versions.** 1.0 is the only version served (section 1). The specification's examples send `0.3` while its text describes 1.0, and
  the SDK's migration guide differs from the spec on an empty header. Interoperability tests against the SDK-mapped endpoint record the
  SDK's actual answer for empty, missing, `0.3` and `1.0` (correction 6).
- **Gaps in the specification** the design fills itself: which error a caller gets for a skill it may not use (this design: a generic
  "not available"); what an expired token gets (not stated; this design returns `401`); whether a skills array may be empty (*verify*;
  it matters for a profile that may use no skill).
- **The SDK is a preview.** Pin an exact version. Keep the SDK behind one adapter class, so an API change touches one file.

## Outbound (the Host calls other agents): phase 4

- **A remote agent is a module,** like the OpenAPI module: an `AllowedHosts` list, credentials through `ModuleCredentials`, and
  `GuardedHttpClientFactory` for every request (connect-time address check, no automatic redirects, redirects re-checked).
- **Tools per remote skill,** risk `ExternalEffect`, so each call is queued by default and checked by the data-flow rules, because the
  message is data leaving the Host.
- **Responses are outside content.** They are `ReadsUntrusted`, so they taint the session.
- **Card pinning.** The Host stores the remote card's hash and key id. It verifies a signature when one is present (the specification
  says a client should verify at least one signature before trusting a card).
- **Callbacks.** A remote's push to the Host uses the signed callback route from the work design's external waits (step 4), which is
  not built. Polling comes first.

## Push (partner notifications): phase 3

- **Status only.** The SDK's default payload can carry artifacts, which would bypass the reply check. Push must send only the task id and
  state (a custom `IPushNotificationSender`, if the SDK allows it; otherwise no push at all). *Verify.*
- **Only to registered hosts.** A partner may register only a URL under a host the operator has allowed (the SDK's `AllowedHosts`;
  its address checks apply too). HTTPS only; no loopback; an ownership challenge before the first delivery. The SDK's guide asks for
  the last of these.
- **Not the record.** The SDK's sender is in-memory and not durable. Push is a hint. The partner polls `GetTask` for the truth.

## Threats and controls

| Threat | Controls |
|---|---|
| Prompt injection through a partner message | `a2a.submit` taints the session and subject; the claimant's session takes the work's taint (`Apply`); the goal is quoted outside text; text never becomes a tool argument; no A2A route lets a partner choose a profile or a tool outside its skills. |
| A partner's reply leaks labelled data | `a2a.reply` is an exit; the labels reach the check; a rule names the destination; `DataFlowMode` must be `Enforce`; the reviewer sees the text. |
| A partner impersonates another | Each credential is linked to one server-generated `partnerId`; the subject is the partner's. |
| A partner reads another's tasks | Owner checks on every operation; unguessable ids (128 bits); foreign and unknown ids give the same answer; `ListTasks` is scoped. |
| A duplicate or replayed request | `messageId` checked against content before anything is created; idempotency key on the action. |
| Escalation through the card or skills | Curated skills; no administration tools for partners; the card is built from the profile; no operation changes configuration. |
| Taint or labels laundered across a long conversation | Stored with the context and re-applied on every message. |
| Resource exhaustion | Per-subject rate limit; per-requester pending cap; body and goal caps. |
| Information in errors | Generic messages; reviewer reasons and failure kinds stay internal. |
| SSRF through push URLs (phase 3) | Registered hosts only; the SDK's address check; the Host's guard. |
| Card spoofing of a remote agent (phase 4) | Pinning; signature verification when present. |
| A human approver manipulated by partner text | The review page must show partner text as quoted and escaped (*verify* the review page's rendering); approval stays with a person; self-approval is forbidden for administration tools. |

## Build order

**Revised 2026-11-12 (owner agreed).** Phase 0 first, then a thin slice of phase 1: one inbox skill end to end, driven by the SDK's own
client, with `returnImmediately=true`, the task id table, `a2a.submit` and `a2a.reply`. The slice also records the SDK's real answer for
each `A2A-Version` case. A per-partner trust rule and a cap on blocking waits are part of the first cut. The rest of phase 1 (pagination,
the card, the skill-selection extension) follows the slice. The checklist ([governance-checklist.md](governance-checklist.md)) has the items.

- **Phase 0, prerequisites (no A2A yet):** the `a2a:call` role and scope; a startup check that `DataFlowMode` is `Enforce` when A2A is on;
  the durability setting documented and warned; a conditional rule on the `partner` argument, proven in a test; the whole-action
  binding in place, before the inbox is exposed to any external partner (correction 8).
- **Phase 1, inbound inbox:** `SendMessage`, `GetTask`, `CancelTask` over JSON-RPC; the authenticated card; `a2a.submit`
  and `a2a.reply` with previews; the context and message tables; the work-item mapping; the projection of work items. Tests: the
  authorisation matrix; `messageId` reuse; taint; the exit check for labelled replies; foreign and unknown ids; rate limits; out-of-profile
  trips; a context surviving a session expiry with its labels.
- **Phase 2, curated skills:** tool skills with data parts; the extended card; card signing; HTTP+JSON; 0.3 compatibility if needed.
- **Phase 3, push and `INPUT_REQUIRED`:** status-only push to registered hosts; `work.ask_partner` for `INPUT_REQUIRED`.
- **Phase 4, outbound:** remote agents as modules; card pinning; callbacks, once external waits exist.

Each phase gets its own check against the code before it is built, as the work design did.

## Decisions

Decided by the owner on 2026-11-11 ("no idea" on card access was taken as the default):

- **Timing (10):** the owner is asking for A2A now, which is the "asked for" the work design waits on. This is a plan only: nothing
  is built, and Phase 0 and Phase 1 wait for the owner's review of this document.
- **Card (1):** requires authentication (default).
- **Replies (2):** every reply is reviewed in v1.
- **Durability (6):** `OPENTAIL_DB_SYNCHRONOUS=full` is required when A2A is on, for now. Revisit when the Host runs on a server
  database.

Still the proposed defaults, unchanged:

3. **One partner, one `partnerId`, one or more credentials** (replaces the one-key rule; see correction 7). Partner-specific trust through rules on the `partner` argument, not separate tools.
4. **JSON-RPC first**, HTTP+JSON when a partner asks.
5. **A2A-Version 1.0 only**, until a partner needs 0.3.
7. **A generic error for a skill the caller may not use.**
8. **Card and skill changes are configuration only**, not through `skills.publish`.
9. **Reviewer reasons stay internal.**
11. **File parts** are refused in v1; attachments later, with their own size and type limits.

## Verification before building

- **SDK, on the pinned version:** the `IAgentHandler` and `ITaskStore` signatures; the default route names; whether an ASP.NET Core
  policy can guard the JSON-RPC endpoint; whether the push payload can be limited to status; whether the card can be canonicalised for
  signing.
- **Specification, still unread:** §3 (abstract definitions), §4.3 (push objects), §5.4 (error table), §13.1 in full; whether `skills`
  may be empty; how negotiation works for versions and extensions.
- **Host, in code:** the rule syntax for conditions on a top-level argument (`Policy/Rules/Conditions.cs`); the review page's rendering
  of action arguments (escaping); the throughput cost of `synchronous=full`.
- **Vendor claims not used:** the Gravitee pages describe per-skill policy but are thin marketing text; agentgateway documents CEL-based
  access control and A2A support, but not per-skill rules. Neither is evidence for a design decision.

## What is built

Phase 0 (in commit f24f8c5), the first half of the thin slice (03b-1, committed in fae2725) and the second half (03b-2, committed in 23020b1). The slice is OpenTail.Host.A2A, which only a server or a test references; the HTTP host takes it through an extension
seam and never names it. It serves `/a2a` (JSON-RPC, our own `IA2ARequestHandler` over the SDK's `MapA2A`) behind the `a2a:call` role;
enforces `A2A-Version` before the SDK (D6); keeps the task id table (schema 13: `a2a_tasks`, the Host's id, the partner's owner, the content
hash, the work item and the reply action, and no text); submits `a2a.submit` as the partner with the task id as idempotency key. On approval
it creates one work item for the skill's inbox profile, on the task's thread, tainted, carrying what the submit's session holds; it never
returns the item's id. A finished item is reconciled into one queued `a2a.reply` for its summary (or "Done."), in the submit's session and
profile, with the item's taint and labels put on that session first. `GetTask` and `CancelTask` answer for the owner's tasks only. The
completer's session now carries onto its work item (E1); `WorkService` raises `Completed` (E2) and cancels only an open item when asked (E7).
Reused without change: the decision path, profiles, taint, labels, the audit trail, identity levels, the rate limits, the body limit and the
HTTP sign-in. Built in 03b-3: the blocking send with its wait and cap, and `ListTasks` (the caller's tasks only, keyset paging). Not built
yet: the card, the skill-selection extension, push, signing, and outbound calls.
The thin slice's version gate and the SDK facts are in `docs/briefs/a2a-sdk-facts.md`.

## Sources

- A2A v1.0 specification: <https://a2a-protocol.org/latest/specification/> (read in parts, 2026-11-11).
- .NET SDK: <https://github.com/a2aproject/a2a-dotnet>, its push-notifications and security docs (read 2026-11-11).
- `A2A` on NuGet: <https://www.nuget.org/packages/A2A> (prerelease 1.0.0-preview4, updated 2026-11-02).
- Agent Governance Toolkit (for comparison, not adopted): <https://github.com/microsoft/agent-governance-toolkit>.
- agentgateway: <https://github.com/agentgateway/agentgateway> (Linux Foundation, Apache-2.0, CEL access control).
- Gravitee A2A pages and the threat taxonomies found by search were vendor material and are not relied on, except as a checklist.
