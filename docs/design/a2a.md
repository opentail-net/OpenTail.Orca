# Design: A2A as a second front door

**Status:** the current design and the checkbox plan. Phase 0 and the thin slice of phase 1 are built (2026-11-13); the rest of phase 1 and
phases 2 to 4 are not. **What to do next is in [The plan](#the-plan).**

**The record is in [`a2a-history.md`](a2a-history.md):** the corrections from the external review of 2026-11-11 (the text below cites them
as "correction 1" to "correction 8"), the build order as it was argued, the verification notes and the commit-by-commit account of what was built.

**Provenance.** Proposed 2026-11-11, from reading Orca at `4b8a8b7`, the A2A v1.0 specification and the official .NET SDK as published in
November 2026. The contract sections below keep the line references of that reading. The SDK is a preview: re-check the facts marked *verify* before building.

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

## Who decides, and who is asked

No question reaches the partner from governance. A partner's requests and answers are states of a task. Questions go to people only
inside Orca's review queue.

- **Reviewers** are the people Orca already uses: holders of the `review` role (the review page, the API or a reviewer token),
  narrowed by reviewer group where the policy names one. They are told through the existing channels: the review page and the Teams
  card. Both `a2a.submit` and `a2a.reply` name `Reviewer: Human`, so no AI reviewer may decide them.
- **Step by step.** An inbound message becomes an `a2a.submit` action. With `returnImmediately=true`, Orca returns once the task is
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
| Authenticate every incoming request | §7.4 | All A2A routes use Orca's existing sign-in. There is no anonymous route, the card included (see [the card](#8-the-agent-card)). |
| Authorisation is the server's to define; may use skills, actions, data and scopes | §7.5 | Profiles and policy, as today. |
| A task waiting for credentials is `AUTH_REQUIRED`; that state is not authorisation | §7.6.1, §7.6.4 | Not used. Orca never asks a partner for in-band credentials. |
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
  Orca a second task store. The plan implements `IA2ARequestHandler` instead, with its eleven operations (send, stream, get, list,
  cancel, subscribe, four push-configuration operations, extended card), and keeps every task in Orca. The endpoint is mapped
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

### Orca (read in code, 4b8a8b7)

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
- **Headers and bodies.** `tenant` must be empty; anything else is refused, because Orca is single-tenant. Requests are capped at
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
  entry in Orca's existing credential configuration (no second credential system). Rotating a key does not change the partner's
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

| A2A state | Orca state | Note |
|---|---|---|
| `SUBMITTED` | `a2a.submit` pending a person | "Received, not started." This deliberately differs from [MCP tasks](mcp-tasks.md), which report reviewer waits as `working`. |
| `WORKING` | Work item open, claimed or running; the action executing | Accepted. The status message says what is happening, in general words. |
| `INPUT_REQUIRED` | Not in v1 | Needs a `work.ask_partner` tool (phase 3). |
| `AUTH_REQUIRED` | Not used | Orca does not ask partners for credentials. |
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
  `DataLabels.Check`; `Allow` names tools, not destinations, so `"a2a.reply:partner=..."` is not a form Orca reads). Tested in
  `A_label_the_completer_held_is_checked_at_the_exit...`:

```json
"DataFlow": { "finance": { "Destinations": [ { "Tool": "a2a.reply", "When": { "arg": "/partner", "equals": "<partnerId>" } } ], "Otherwise": "Deny" } }
```

- **Enforcement is required.** `DataFlowMode` defaults to `ReportOnly`. With that default, a labelled reply would be recorded as
  "would be queued" and sent. Orca must refuse to start with A2A enabled unless `DataFlowMode` is `Enforce`.
- **One path.** `GetTask` returns an artifact only from an executed `a2a.reply`. Nothing else leaves Orca in an A2A response. This is
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
  streaming, no push), the input and output modes, and the security schemes Orca really accepts: `apiKey` in the `X-API-Key`
  header and `http` bearer. With Entra, an `openIdConnect` scheme. It lists no skills. Partners receive the card's location and their
  credentials out of band. The specification allows direct configuration as a discovery mechanism.
- **Extended card.** `GET /extendedAgentCard` returns, for an authenticated caller, the skills its profile may use, built by the same
  code that builds `tools/list`. Its capabilities say `extendedAgentCard: true`.
- **What the card never shows.** Tool names outside the caller's profile, internal names, module versions, policy and reviewer groups.
- **Decided (correction 3): the card requires authentication,** so an anonymous caller reads nothing from it. It can still tell that A2A is on: the card and `/a2a` answer 401 when A2A is enabled and 404 when it is not (observed in batch 04). That is accepted (planner's delegated decision, 2026-11-13): section 7 promises 401 with `WWW-Authenticate` for a missing credential, and `/mcp` answers the same way.
  The SDK's security guide also says protected agents should guard the discovery endpoint. A public card with no skills, the
  alternative, is not adopted (decision 1 is the default).
- **Built (batch 04), and the open point.** The card is built as described above, with a `private` cache header (the SDK's default is
  `public`), an ETag that the SDK sends behind sign-in, and a `card_served` audit at most once per partner in 10 minutes. The status an
  anonymous caller gets is not what the decision above says: 401 with A2A on, 404 with it off (RUN: `An_anonymous_caller_gets_401...`).
  So an anonymous caller can tell that A2A is on. The options are in `docs/briefs/a2a-reviewer-view.md`; the planner decides.
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

## Outbound (Orca calls other agents): phase 4

- **A remote agent is a module,** like the OpenAPI module: an `AllowedHosts` list, credentials through `ModuleCredentials`, and
  `GuardedHttpClientFactory` for every request (connect-time address check, no automatic redirects, redirects re-checked).
- **Tools per remote skill,** risk `ExternalEffect`, so each call is queued by default and checked by the data-flow rules, because the
  message is data leaving Orca.
- **Responses are outside content.** They are `ReadsUntrusted`, so they taint the session.
- **Card pinning.** Orca stores the remote card's hash and key id. It verifies a signature when one is present (the specification
  says a client should verify at least one signature before trusting a card).
- **Callbacks.** A remote's push to Orca uses the signed callback route from the work design's external waits (step 4), which is
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
| SSRF through push URLs (phase 3) | Registered hosts only; the SDK's address check; Orca's guard. |
| Card spoofing of a remote agent (phase 4) | Pinning; signature verification when present. |
| A human approver manipulated by partner text | The review page shows partner text whole and as text: the page writes every value with `textContent`, and the API gives the whole summary (batch 04 B1, RUN). Teams cards escape it, so no markup, link or line break from the partner survives (B2, RUN), and a card does not offer approve on a preview it has cut, on the card or on the click (B3, RUN). Approval stays with a person; self-approval is forbidden for administration tools. By default Teams may approve an `a2a.reply` (B4, RUN; a record for the owner). |

## The plan

Tick a box when the work is merged, and add a line to the record in [`a2a-history.md`](a2a-history.md). This file is the one place for
A2A work. The item-level record of phases 0 and 1 (each check, with its commit) is the section after the plan,
[Item-level checklist, phases 0 and 1](#item-level-checklist-phases-0-and-1), migrated from the governance checklist. The boxes ticked in
"Done" come from that record and from the code check of batch 06; they were not re-checked when this list was written (2026-11-13),
except the startup checks in `A2AFrontDoor.cs`.

### Done

- [x] **Phase 0, prerequisites** (`f24f8c5`; `A2aPhase0Tests`).
  - [x] The `a2a:call` role and scope.
  - [x] Startup checks: `DataFlowMode` is `Enforce`; `OPENTAIL_DB_SYNCHRONOUS=full`; work items on; the policy has `a2a.submit` and `a2a.reply`.
  - [x] Partner ids, one or more credentials each.
  - [x] A conditional rule on the `partner` argument, proven by a test.
  - [x] The whole-action binding, before any partner reaches the inbox.
- [x] **Phase 1, the thin slice** (`fae2725` to `7b6003c`; `A2aSliceTests`, `A2aKitTests`, `ReviewViewTests`).
  - [x] `/a2a` over JSON-RPC behind the `a2a:call` role; `A2A-Version` enforced before the SDK.
  - [x] `SendMessage`: `returnImmediately` true; false or omitted waits up to `BlockingWait`, with a cap per partner.
  - [x] `GetTask`; `CancelTask` (a pending submit or an open work item); `ListTasks` (own tasks, keyset paging).
  - [x] The task id table (`a2a_tasks`, schema 13), holding no text.
  - [x] `a2a.submit`: queued for a person; on approval one tainted work item for the skill's inbox profile.
  - [x] `a2a.reply`: one queued reply per finished item; an exit, reviewed by a person, no edits; the item's taint and labels apply.
  - [x] The authenticated agent card, with a `card_served` audit.
  - [x] Partner text on the review page and in Teams: shown as text; a cut Teams preview cannot be approved.
  - [x] The kit: sample config and policy, `scripts/a2a-smoke.ps1`, `docs/guides/a2a.md`.
  - [x] Teams `tasks` and `work` commands, read-only.
  - [x] `ApprovePartnerRepliesInChat` (default true): the switch that stops Teams approving a partner reply.

### Open: before a first external partner

- [ ] Startup: check the rules on `a2a.submit` as the reply's rules are checked. Today only the tool's own reviewer is pinned, so
  a rule can name another reviewer (reviewer gate, G3).
- [ ] Test: an AI reviewer may refuse an `a2a.reply` where the policy says `AiMayRefuse`, and can never approve one (reviewer gate, G8).
- [ ] A live run in a real Teams tenant, including whether a bare `@name` in a card becomes a mention. *Owner.*
- [ ] Measure the throughput cost of `synchronous=full`.
- [ ] Read the parts of the specification not yet read: §3, §4.3, §5.4, §13.1 in full, and how versions and extensions are
  negotiated; whether `skills` may be empty.
- [ ] Decide which profiles may use an inbox skill. *Owner.*
- [ ] The boxes still open in the item-level checklist below:
  - [ ] the review of the task-id handling against the specification (correction 1);
  - [ ] whether `application/a2a+json` is accepted on requests;
  - [ ] the owner's confirmation of the plan defaults the code already follows.

### Open: the rest of phase 1

- [ ] More than one skill per Orca (today a request is served only when exactly one skill is configured).
- [ ] The skill-selection extension, so a partner can name the skill.
- [ ] Contexts and multi-turn (today a continuation is refused).
  - [ ] A context table; taint and labels stored with the context and re-applied on every message.
  - [ ] Test: a context survives a session expiry with its labels.
- [ ] Partners authenticated by JWT (today an API key only).

### Open: phase 2, curated skills

- [ ] Tool skills, with data parts.
- [ ] The extended card (`GetExtendedAgentCard` answers UnsupportedOperation today).
- [ ] Card signing. *Verify first:* whether the card can be canonicalised for signing.
- [ ] HTTP+JSON, when a partner asks.
- [ ] 0.3 compatibility, only if a partner needs it.
- [ ] File parts, with their own size and type limits (refused today).

### Open: phase 3, push and `INPUT_REQUIRED`

- [ ] *Verify first:* whether the SDK lets a push payload be limited to the task id and state. If not, no push.
- [ ] Status-only push, to registered hosts only: HTTPS, no loopback, an ownership challenge before the first delivery.
- [ ] `work.ask_partner` for `INPUT_REQUIRED`.
- [ ] Streaming and resubscribe, if a partner needs them.

### Open: phase 4, outbound

- [ ] A remote agent as a module: allowed hosts, module credentials, the guarded HTTP client.
- [ ] One tool per remote skill, risk `ExternalEffect`; responses are outside content and taint the session.
- [ ] Card pinning, and signature verification when a signature is present.
- [ ] Callbacks, once external waits exist in the work design.

### Not planned as stated

- The "out-of-profile trip" test: no partner-driven path makes a call outside its profile, and a profile cannot lose a tool while
  Orca runs. The reasoning is in the history file.
- Relaxing the human-only pins on submits and replies is the reviewer gate's P7, not this plan's.

Each phase gets its own check against the code before it is built.

## Item-level checklist, phases 0 and 1

Migrated unchanged from the governance checklist on 2026-11-13 (its sections B and C). It is the detailed record behind "Done" in
the plan above, and it holds a few boxes that are still open. "Decided" in these items means the list in "Decisions" below.

### Phase 0 (prerequisites)

Go-ahead given 2026-11-12. Committed in `f24f8c5` (batch 01) unless a line says otherwise.

- [x] B.0: fold the corrections into the body of `a2a.md`, so the body and the corrections no longer disagree (task ids in section 4,
  the partner identity in section 2, the versions in sections 1 and 11, the authenticated card in section 8, the skill-selection
  extension and ListTasks in section 4, `a2a_tasks` in section 10, the build order). Committed in `15b9e8b` (2026-11-12). The
  correction 6 A2A-Version question is still open for the owner.
- [x] The role and scope `a2a:call` (`Roles` in `OpenTail.Orca.Admin/AdminServer.cs`), accepted by the API key scope validation
  (`HostHttpOptions.cs`, which refuses only review, admin and group scopes today). Committed in `f24f8c5`. The validation
  needed no change: it already accepts `a2a:call`. Tests: `A2aPhase0Tests` (2): the key passes validation; a key with only `a2a:call`
  gets 403 on `/mcp` and on `/api/actions`, with controls for a tools key and a reviewer.
- [x] Startup check: `DataFlowMode` must be `Enforce` when A2A is configured (it defaults to `ReportOnly`). Committed in `f24f8c5`.
  Test: `Enabled_a2a_requires_dataflow_enforce`.
- [x] Startup check: `OPENTAIL_DB_SYNCHRONOUS=full` when A2A is configured. It reads the store's resolved `Synchronous`, not the
  environment text; only `full` passes (`extra` is refused too, as written); a store that is not SQLite is refused. Committed in
  `f24f8c5`. Tests: `Enabled_a2a_requires_full_durability_as_the_store_resolved_it` and
  `Enabled_a2a_starts_when_the_store_runs_with_full_durability_and_refuses_normal`.
- [x] Startup check: a key with `a2a:call` must name a profile. Also checked: it carries no other scope, and names a listed partner.
  Committed in `f24f8c5`. Tests: `A_partner_key_must_name_a_profile`, `A_partner_key_carries_a2a_call_alone`,
  `A_partner_key_must_name_a_listed_partner`.
- [x] Test: a conditional rule on a top-level argument (`partner`) works in `Policy/Rules/Conditions.cs`. Committed in `f24f8c5`.
  Test: `A_rule_on_the_partner_argument_executes_one_partner_and_queues_another`. No production change.
- [x] Partner identity foundation: a `partnerId` (UUID), stored separately from the key name, with credentials linked to it through an
  optional partner reference on each API key entry (`a2a.md`, correction 7). Committed in `f24f8c5`. `--new-partner` makes
  the id, as the phase 0 brief asks. **Open for the owner:** `a2a.md` section 2 says the id is server-generated; the brief has the
  operator generate it. Built as the brief says. Tests: `New_partners_are_fresh_uuids_with_a_trimmed_name_and_pass_the_checks`,
  `Partner_ids_must_be_uuids_and_unique_and_partners_need_a_name`, `Several_keys_may_name_one_partner`.
- Test counts after phase 0 (a full run, Debug): Abstractions 70 of 70; Orca 1221 of 1221 (1204 before, plus 17 in `A2aPhase0Tests`). `ContractFreezeTests` passes, and
  `contract-v1.txt` is unchanged.

### Phase 1 (inbound inbox)

C.0 Thin slice first (agreed 2026-11-12):

- [x] One inbox skill end to end: `SendMessage` with `returnImmediately=true`, `GetTask`, the task id table, `a2a.submit` and `a2a.reply`,
  driven by the SDK's own client in a test. Committed in `fae2725` to `7b6003c`. The card, ListTasks and blocking sends came after the
  slice (batches 04 and 03b-3); the skill-selection extension has not.
- [x] The same test records what the SDK-mapped endpoint does for an empty, a missing, a `0.3` and a `1.0` `A2A-Version` header. The
  owner's version decision is then made on observed behaviour. **Done (03b-1):** `The_version_matrix_gives_the_outcomes_of_D6...` records
  this Orca's outcomes; the SDK's raw results are in `docs/briefs/a2a-sdk-facts.md` section 5 (RUN). Decision D6 is under Decided.
- [x] A per-partner trust rule: a conditional rule on `partner` makes one partner's submit run and leaves another's queued. **Built and
  tested (batch 04, C1):** `C1_a_per_partner_rule_runs_one_partners_submit_and_leaves_the_other_queued`. The rule is for `a2a.submit`
  only: a reply is never `Execute` (a startup check). Each operator writes the rule for each partner; the sample has none.
- [x] A cap on blocking waits per partner before any partner connects. **Built (03b-3):** `MaxBlockingWaitsPerPartner`, default 4, from
  1 to 64 (under Decided). A refused send creates no row.

C.1 Blockers:

- [x] Whole-action binding (A.2) is in place before any external partner is connected. The review's rule. (Committed in `15b9e8b`.)
- [ ] Task ids: a separate persistent `a2a_tasks` id, linked to the `a2a.submit` action, the work item and the `a2a.reply` action. The
  server generates it (spec §3.4.2). First-message handling is crash-safe and idempotent: a retry with the same `messageId` finds the
  existing task (correction 1). **Partly (03b-1, 03b-2):** the id is generated by Orca, the table is in schema 13, `messageId` reuse is
  refused for different content, a crash between the row and the submit, or between the item and its link, is healed by the retry (tested),
  and the links to the work item and the reply action are set. The spec's own review of correction 1 is still open.
- [x] `returnImmediately`: honoured as the spec says, with the one answer the Decided section records. `true` returns once the task is
  created. `false` and omitted wait for a terminal state (COMPLETED, FAILED, CANCELED, REJECTED); if the wait passes first the caller gets an
  error that names the task, never SUBMITTED or WORKING, and a disconnect discards nothing (correction 2). Tests for `true`, `false` and
  omitted. **Done (03b-3):** the wait (`BlockingWait`), the cap (`MaxBlockingWaitsPerPartner`), the timeout error, the abort, and the
  rejected-during-wait case are tested. The interrupted states (input-required, auth-required) are not in this slice's states.
- [x] `A2A-Version` (correction 6): the spec and the SDK disagree on an empty header. Recommended: empty is rejected as 0.3, per the spec; a
  missing header follows the SDK, as 1.0. The owner confirms both. Interop tests against the SDK-mapped endpoint for empty, missing, `0.3`
  and `1.0`, recording the SDK's actual result for each. **Done (03b-1):** enforced by Orca before the SDK (decision D6, recorded under
  Decided); the version matrix test covers missing, empty, `0.3`, `1.0`, `2.0` and the repeated and comma-separated cases.
- [x] ListTasks: cursor pagination with `pageToken` and `nextPageToken`, newest first (correction 5). **Done (03b-3):** keyset paging over
  creation time and task id, newest first by creation (a decision, under Decided); only the caller's tasks; `status` and
  `statusTimestampAfter` are refused, not ignored. The order differs from the spec's status update time; `a2a.md` says so.
- [x] Authenticated card (decided, correction 3): `/.well-known/agent-card.json` requires authentication. **Built (batch 04):** the card,
  its authorisation (401, 403, and 200 for a2a:call), its cache and its audit. Decided (option c, `a2a.md` section 8): an anonymous caller
  gets 401 with A2A on and 404 with it off, so it can tell that A2A is on; that is accepted. The extended card is not built:
  `GetExtendedAgentCard` answers UnsupportedOperation.
- [ ] Skill selection extension at `https://opentail.net/a2a/extensions/skill-selection/v1`, declared in the card and sent in the message
  (correction 4). The extension's specification is published at that URI before any partner is onboarded. **Not built.**
- [x] Tools `a2a.submit` (reads outside content, queued) and `a2a.reply` (an exit, queued, edits not allowed), each with a preview.
  **Done (03b-1 and 03b-2):** both are built with a preview. At startup `a2a.reply` must be Queue or Deny, Reviewer Human, `AllowEdits` false,
  and no rule may be Execute or name a reviewer that is not a person.
- [x] Map work items and reply actions to A2A task states. **Done (03b-2):** the reply decides first, then the item; a task with no item reads as before.
- [x] JSON-RPC endpoint, and the authentication matrix. **Built:** `/a2a` over `MapA2A` with a role requirement; 401 with no credential,
  403 for a key without `a2a:call` (and for an `mcp:tools` key); foreign and unknown ids alike; the body limit and the rate limit apply.
  The card and ListTasks are built since (batches 04 and 03b-3).
- [ ] Tests: task-id creation after a crash, `messageId` reuse, `returnImmediately`, ListTasks pagination and ordering, `A2A-Version`, taint
  on submit, a labelled reply refused, foreign and unknown ids, rate limit, out-of-profile trip, and a context that survives a session
  expiry. **Covered now:** the crash, `messageId` reuse, `returnImmediately` (true, false, omitted), `A2A-Version`, taint on submit, the
  labelled reply refused, foreign and unknown ids, the rate limit, the work item, the reply (approved and rejected), cancel, dead and
  expired items, the restart, ListTasks, the startup checks, and a submit session that expires before its item completes (batch 06, R5).
  **Not covered, and not written as stated:** contexts (not built), and the out-of-profile trip (`a2a-history.md`, build order, phase 1, with the reason).

C.2 Verify before building:

- [x] SDK: `MapA2A` returns a builder that accepts `RequireAuthorization`, and an ASP.NET policy can guard the endpoint. **Closed:**
  `docs/briefs/a2a-sdk-facts.md` section 2 (RUN: 401 without a credential).
- [ ] Specification: `skills` may be empty? Check. Sections 3, 4.3, 5.4 and the full text of 13.1 were not read. **Partly:** the SDK
  serialises and serves an empty list (RUN, `a2a-sdk-facts.md` section 7); the specification text is still unread.
- [x] Check the SDK's actual behaviour on the endpoint for each `A2A-Version` case (see C.1). The SDK's migration guide says empty and
  missing are accepted and default to 1.0, which contradicts the spec for empty. The spec is silent on missing. **Closed on the SDK side:**
  the SDK accepts missing, empty and `0.3` and refuses `2.0` (RUN; `a2a-sdk-facts.md` section 5). The spec side is still open.
- [ ] Specification: `application/a2a+json` accepted on requests. **Open.**
- [x] The review page escapes partner text when it shows a queued request. **Closed (batch 04, B1, RUN):** `ReviewViewTests.B1_...`: the
  page writes every value with `textContent`, and the API gives the summary whole.
- [x] Rule syntax for conditions on a top-level argument. **Closed:** the phase 0 test `A_rule_on_the_partner_argument...` (RUN).
- [x] `IA2ARequestHandler` (not `A2AServer`), with `A2A` and `A2A.AspNetCore` 1.0.0-preview4 added to `Directory.Packages.props`. Restore was
  tested in a scratch project. **Done (03b-1, D1):** both packages are pinned, `Microsoft.Extensions.AI.Abstractions` is raised to 10.11.0,
  and the handler is ours (`A2ARequestHandler`, not `A2AServer`).

C.3 Still to decide while building (not blockers if the first partner accepts the defaults):

- [ ] Skill visibility: which profiles may use an inbox skill. Any profile that lists `a2a.submit`, or a per-skill list. **Open.** Today a
  request is served when the caller's profile lists `a2a.submit` (`A2ARequestHandler.SendMessageAsync`); there is no per-skill list.
- [x] Work items are read through the work store (`IWorkStore.Get`), not `WorkService.Read`. `Read` taints the reader's session and renews
  the lease. **Done (03b-2):** `A2AWork` and the handler read `host.Work.Store`.
- [x] Caller identity comes from `IHttpContextAccessor`, which needs registering. **Done (03b-1):** registered by the HTTP host; the caller is
  read from the partner and profile claims, never from the request object.
- [x] Schema: one bump for both the binding and A2A, or two. **One bump (03b-1):** schema 13 creates `a2a_tasks`; the binding has no table of its own.
- [x] Owner decision: how many blocking waits one partner may hold open at once. **Decided (planner, 2026-11-13): 4**, from 1 to 64 (under Decided).
- [ ] Plan defaults that the owner has not yet confirmed: JSON-RPC first (decision 4); a generic error for a skill the caller may not use
  (7); card and skill changes are configuration only (8); reviewer reasons stay internal (9); file parts refused in v1 (11). **What the code
  does:** JSON-RPC only; "That capability is not available to you." for a skill the caller may not use; the card and the skill come from
  configuration; a task never shows a reviewer's reason (test `A_reviewer_approval_gives_WORKING...`); file parts are refused. The owner
  has not confirmed these.

## Decisions

### Waiting for the owner

- [ ] Review of this design before phases 2 to 4.
- [ ] Which profiles may use an inbox skill.
- [ ] The live Teams tenant check (needs a tenant).
- [ ] Confirm the planner's decisions made while the owner was away, listed in [`docs/briefs/README.md`](../briefs/README.md).
  One of them is the `A2A-Version` rule (D6): a missing header is 1.0; `1.0` is accepted; an empty value and `0.3` are refused.

### Settled

| By | Decisions |
|---|---|
| The owner, 2026-11-11 | A2A is asked for now. The card requires authentication. Every reply is reviewed in v1. `OPENTAIL_DB_SYNCHRONOUS=full` is required when A2A is on, until Orca runs on a server database. |
| The owner, 2026-11-12 | A thin slice first, with a per-partner trust rule and a cap on blocking waits in the first cut. |
| The owner, 2026-11-13 | A2A ships inside `OpenTail.Orca.Server`, not as its own package. Teams may approve a partner reply by default, with a switch to turn that off. The 400-character card preview limit is kept. |
| Proposed defaults, unchanged | One partner, one `partnerId`, one or more credentials; trust through rules on the `partner` argument. JSON-RPC first. A2A-Version 1.0 only. A generic error for a skill the caller may not use. Card and skill changes are configuration only. Reviewer reasons stay internal. File parts refused in v1. |

### Settled, item by item

Migrated unchanged from the governance checklist's "Decided" list on 2026-11-13. Where a line says the owner is still to confirm, the
box for it is under "Waiting for the owner".

- A2A-Version (decision D6, the planner's delegated decision, 2026-11-13): missing is 1.0; `1.0` is accepted; an empty value, `0.3`, and
  anything else are refused with the SDK's VersionNotSupported error (HTTP 200, -32009), after sign-in and before the SDK. A header sent more
  than once, or a comma-separated list, is accepted only when every value is `1.0`. Empty and `0.3` follow the spec; missing follows the SDK.
- Timing: wanted now (owner, 2026-11-11).
- The card requires authentication; partners receive the card location and credentials out of band (owner default; second review, 2026-11-12).
- `returnImmediately=true` is the recommended integration mode; blocking requests follow the spec exactly (second review, 2026-11-12).
- The skill-selection extension URI is `https://opentail.net/a2a/extensions/skill-selection/v1` (second review, 2026-11-12).
- Partner ids are server-generated UUIDs, separate from key names; credentials use Orca's existing configuration (second review, 2026-11-12).
- `1.0` is the only version served. Empty is rejected as 0.3, per the spec (recommended; owner to confirm). The missing-header rule is
  an open owner decision (recommended: 1.0, as the SDK does).
- Replies are reviewed; durability is `synchronous=full` when A2A is on (owner, 2026-11-11).
- A2A is built as a thin slice first, with a per-partner trust rule and a cap on blocking waits in the first cut (owner, 2026-11-12).
- A blocking send that is not terminal when `BlockingWait` passes is answered with an error, never with a non-terminal task (planner's delegated decision, 2026-11-13). No A2A error code means "still running", so the error is InternalError, with text that names the task id and says to read it with GetTask. The task is not cancelled.
- The default cap on blocking sends is 4 waiting sends per partner, from 1 to 64 (planner's delegated decision, 2026-11-13).
- ListTasks orders by creation time, then task id, newest first, because the status update time lives in three stores (planner's delegated decision, 2026-11-13). `a2a.md` records the difference from the spec.

## Sources

- A2A v1.0 specification: <https://a2a-protocol.org/latest/specification/> (read in parts, 2026-11-11).
- .NET SDK: <https://github.com/a2aproject/a2a-dotnet>, its push-notifications and security docs (read 2026-11-11).
- `A2A` on NuGet: <https://www.nuget.org/packages/A2A> (prerelease 1.0.0-preview4, updated 2026-11-02).
- Agent Governance Toolkit (for comparison, not adopted): <https://github.com/microsoft/agent-governance-toolkit>.
- agentgateway: <https://github.com/agentgateway/agentgateway> (Linux Foundation, Apache-2.0, CEL access control).
- Gravitee A2A pages and the threat taxonomies found by search were vendor material and are not relied on, except as a checklist.
