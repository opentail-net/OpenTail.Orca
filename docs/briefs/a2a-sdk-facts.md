# A2A SDK facts (batch 03a)

Read-only. No repo code or package changed. Labels: **RUN** = executed in the probe; **SOURCE** = read in the package (surface dump) or the SDK source at commit `bc49650b` (named in the nuspec); **DOCS** = package README only.

Scratch project, outside the repo, in the session scratchpad folder: `a2a-sdk-probe`. Probe `A2aProbe\Program.cs`; transcript `out\probe-results.txt` (RUN); surface dump `out\surface.txt` (SOURCE); SDK source `src\a2a-dotnet-bc49650b09711d2bdadbdbdca7aca3e86a872780\` (SOURCE); pin test `pins\`.

## 1. Packages

- Restored exactly: A2A 1.0.0-preview4 and A2A.AspNetCore 1.0.0-preview4 (RUN). Both ship net8.0 and net10.0 (SOURCE nuspec); the probe targets net10.0 (RUN).
- Licence: Apache-2.0 for both. Authors: "Community Contributors" (SOURCE nuspec).
- net10.0 dependencies (SOURCE nuspec): Microsoft.Extensions.AI.Abstractions 10.11.0, Microsoft.Extensions.Logging.Abstractions 10.0.13. The net8.0 group adds System.Text.Json, System.Net.ServerSentEvents and System.Linq.AsyncEnumerable 10.0.13. The restored graph adds nothing else (RUN).
- **Conflict (RUN):** `Directory.Packages.props` pins Microsoft.Extensions.AI.Abstractions 10.8.3 and sets `CentralPackageTransitivePinningEnabled`. In a scratch copy that adds PackageVersion entries for both A2A packages, restore fails with NU1109: "Detected package downgrade: Microsoft.Extensions.AI.Abstractions from 10.11.0 to centrally defined 10.8.3". With 10.11.0 in the copy, restore succeeds. Without the PackageVersion entries the restore fails with NU1010. The repo file was not changed. No other repo pin conflicts.

## 2. The server seam

- `IA2ARequestHandler` (SOURCE, A2A.dll) has 11 members. Each takes `A2ARequestContext requestContext` and `CancellationToken cancellationToken = default`:
  - `Task<AgentTask> CancelTaskAsync(CancelTaskRequest)`
  - `Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig)`
  - `Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest)`
  - `Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest)`
  - `Task<AgentTask> GetTaskAsync(GetTaskRequest)`
  - `Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest)`
  - `Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest)`
  - `Task<ListTasksResponse> ListTasksAsync(ListTasksRequest)`
  - `Task<SendMessageResponse> SendMessageAsync(SendMessageRequest)`
  - `IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest)`
  - `IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest)`
  A host class implementing all 11 compiled and served SendMessage (RUN, Part 1).
- The SDK's implementation is `A2AServer` (SOURCE). `AddA2AAgent<THandler>(card)` registers A2AServer and InMemoryTaskStore (RUN).
- `MapA2A(IEndpointRouteBuilder, IA2ARequestHandler, string path)` and `MapA2A(IEndpointRouteBuilder, string path)` (handler from DI) return `IEndpointConventionBuilder` (SOURCE). `RequireAuthorization()` on the result gives 401 with no credential (RUN); the same holds for `MapWellKnownAgentCard` (RUN).
- Caller identity: `A2ARequestContext` exposes only extension sets (SOURCE). RUN: a handler given `IHttpContextAccessor` read the user set earlier in the pipeline ("alice"). The one SDK hook given an HttpContext is `A2AExtensionOptions.CanActivate(HttpContext, A2AExtensionRegistration, CancellationToken)` (SOURCE), which runs only with extension options.
- `requestContext` is **null** when no `A2AExtensionOptions` is registered (SOURCE `src/A2A.AspNetCore/A2AJsonRpcProcessor.cs:107-111`; RUN). The interface declares it nullable (`A2ARequestContext?`); corrected in batch 03b-1.

## 3. SendMessage

- Types (SOURCE): `SendMessageRequest { Configuration, Message, Metadata, Tenant }`. `SendMessageConfiguration { AcceptedOutputModes, HistoryLength, ReturnImmediately (bool, default false), TaskPushNotificationConfig }` (ReturnImmediately at `src/A2A/Models/SendMessageConfiguration.cs:18`). `SendMessageResponse { Message, Task, PayloadCase }`.
- `Message { ContextId, Extensions, MessageId, Metadata, Parts, ReferenceTaskIds, Role, TaskId }`. `Part { Text, Raw, Url, Data, MediaType, Filename, Metadata }`, built with FromText, FromRaw, FromUrl or FromData. `Role` is Unspecified, User or Agent.
- Wire form (RUN): `"role":"ROLE_USER"` and `"ROLE_AGENT"`; states `"TASK_STATE_WORKING"` and `"TASK_STATE_COMPLETED"`; `tenant` sits at the top of `params`.
- Blocking: the SDK waits when ReturnImmediately is false (SOURCE `src/A2A/Server/A2AServer.cs:31-43`). RUN: the default call took 1542 ms and returned COMPLETED; ReturnImmediately=true returned in 3 ms with WORKING. On a host-implemented handler the SDK waits for nothing; the handler decides.
- Identifiers (RUN): a taskId is generated when absent; a contextId is kept when supplied and generated otherwise.
- Pass-through (RUN): message and request Metadata, message Extensions, and text, data and raw parts reach the agent unchanged.
- Tenant (RUN, SOURCE): on JSON-RPC the handler sees `Tenant = null` though the body carried `"tenant-x"`. `A2AHttpRequestPolicy.NormalizeIncomingRequest` clears it (SOURCE `A2AJsonRpcProcessor.cs:350`) and emits an Activity event `a2a.tenant.ignored`.

## 4. GetTask, CancelTask, ListTasks

- Types (SOURCE): `GetTaskRequest { HistoryLength, Id, Tenant }`; `CancelTaskRequest { Id, Metadata, Tenant }`; `ListTasksRequest { ContextId, HistoryLength, IncludeArtifacts, PageSize, PageToken, Status, StatusTimestampAfter, Tenant }`; `ListTasksResponse { NextPageToken, PageSize, Tasks, TotalSize }`.
- Paging (SOURCE): default PageSize 50 (`InMemoryTaskStore.cs:77`); PageSize must be 1 to 100 (`A2AServer.cs:202`); PageToken is a decimal offset (`InMemoryTaskStore.cs:65-67`). RUN: pageSize 0 rejected; pageSize 1 returned one task with `nextPageToken "1"`.
- Errors (RUN; all HTTP 200 with a JSON-RPC error):
  - task not found: -32001, TASK_NOT_FOUND (GetTask and CancelTask on an unknown id);
  - not cancelable: -32002, TASK_NOT_CANCELABLE ("Task is already in a terminal state.");
  - unsupported operation: -32004, UNSUPPORTED_OPERATION (streaming off; extended card not supported);
  - invalid pageSize: -32602 InvalidParams, with no ErrorInfo data;
  - version not supported: -32009, VERSION_NOT_SUPPORTED (section 5).

## 5. A2A-Version

- Check (SOURCE `src/A2A.AspNetCore/A2AVersionHeader.cs:14, 26-39`): "0.3" and "1.0" are supported; absent or empty is accepted; every present value must be supported. It runs first in the JSON-RPC processor (`A2AJsonRpcProcessor.cs:13-24, 85`).
- RUN, JSON-RPC, custom handler (the check runs before the handler):
  - missing: 200, handled (result); empty: 200, handled (result);
  - 0.3: 200, handled (result); 1.0: 200, handled (result);
  - 2.0: 200, error -32009, id null, ErrorInfo data.
- Override: no configuration option appears in the public surface (SOURCE). MapA2A runs the check; a host avoids it only by writing its own endpoint over the handler (not run). The HTTP+JSON binding has its own filter (SOURCE `A2AEndpointRouteBuilderExtensions.cs:232-234, 456-461`; not run).

## 6. A2A-Extensions and metadata

- Negotiated only when `A2AExtensionOptions` is registered (SOURCE `A2AJsonRpcProcessor.cs:107-111`). RUN: plain MapA2A ignores the header and sends no A2A-Extensions response header.
- Parsing (SOURCE `src/A2A/Server/A2AExtensionIdentifiers.cs:6-36`): comma-separated; empty elements dropped; spaces and tabs trimmed; any other whitespace or control character gives InvalidRequest. Case and versions are kept exactly. Activated extensions return in A2A-Extensions (SOURCE `src/A2A.AspNetCore/A2AExtensionNegotiation.cs:26-29`; not run).
- Message Extensions and Metadata pass through unvalidated (RUN; no validation found on the path, SOURCE).

## 7. Agent card

- Types (SOURCE): `AgentCard { Capabilities, DefaultInputModes, DefaultOutputModes, Description, DocumentationUrl, IconUrl, Name, Provider, SecurityRequirements, SecuritySchemes, Signatures, Skills, SupportedInterfaces, Version }`; `AgentInterface { ProtocolBinding, ProtocolVersion, Tenant, Url }`; `AgentSkill { Id, Name, Description, Tags, Examples, InputModes, OutputModes, SecurityRequirements }`; `AgentCapabilities { ExtendedAgentCard, Extensions, PushNotifications, Streaming }` (nullable bools).
- `MapWellKnownAgentCard` serves GET `/.well-known/agent-card.json` as JSON with ETag and Cache-Control (default max-age one hour) (SOURCE `A2AEndpointRouteBuilderExtensions.cs:153-164, 186-188`). RUN: 200, application/json.
- Empty skills: RUN, serialised and served as `"skills":[]`.
- Off: do not call the mapping (SOURCE). Behind authorisation: RUN, 401 on the returned builder's RequireAuthorization.
- Extended card: no HTTP route (RUN: GET /extendedAgentCard gives 404). It exists only as the JSON-RPC method GetExtendedAgentCard, which returned UnsupportedOperation (-32004) while `SupportsExtendedAgentCard` was false (RUN; SOURCE A2AServerOptions).

## 8. Content types and client

- Content types (RUN): application/json, application/a2a+json, no Content-Type and text/plain were all accepted and handled. No media-type check was found in the processor (SOURCE `A2AJsonRpcProcessor.cs:78-110`). Responses are application/json; JSON-RPC errors are HTTP 200 (RUN), though `JsonRpcResponseResult`'s remarks say 400 (SOURCE).
- Client: `A2AClient(Uri baseUrl, HttpClient? httpClient = null)` (SOURCE). The repo's tests already run a real Kestrel server on 127.0.0.1:0 (`OpenTail.Orca.Tests/StubServer.cs:1-30`); the probe did the same, with no WebApplicationFactory or TestServer.
- Example, run as written (RUN, probe Part 3), against a real socket:

```csharp
using var client = new A2AClient(new Uri(baseUrl + "/a2a"), http);
var sent = await client.SendMessageAsync(new SendMessageRequest
{
    Message = new Message { MessageId = "m-1", Role = Role.User, Parts = [Part.FromText("hi")] },
    Configuration = new SendMessageConfiguration { ReturnImmediately = true },
});
var task = await client.GetTaskAsync(new GetTaskRequest { Id = sent.Task!.Id });
Console.WriteLine(task.Status.State);
```

  Output: `Working`, then `Completed` two seconds later (RUN). A JSON-RPC error reaches the client as `A2AException` with its message (RUN); the type has an `ErrorCode` property (SOURCE).

## 9. Surprises and unstable points

1. `requestContext` is null with no extension options (RUN); the interface declares it nullable.
2. The version check accepts "0.3" and an empty header (RUN). `a2a.md` correction 6 assumed an empty value would be refused.
3. JSON-RPC errors are HTTP 200 (RUN); the remarks say 400 (SOURCE).
4. The MapA2A remarks say tenant fields are cleared, and they are (RUN, SOURCE). No handler sees a tenant on this binding.
5. Wire enums use `ROLE_*` and `TASK_STATE_*` names (RUN). Invalid pageSize carries no ErrorInfo; the other errors do (RUN).
6. `RequestContext`'s constructor is Obsolete for the compiler's required-members rule. `ImplicitOAuthFlow` and `PasswordOAuthFlow` are Obsolete (SOURCE surface). No member is marked Experimental (SOURCE).
7. A preview package: changes after preview4 are not established.

## 10. Not established

- Extension activation with `A2AExtensionOptions`: source only.
- The version matrix on the SDK's own server: run on the host-implemented seam only. The check sits in the shared processor (SOURCE), so the result should carry over, but it is not run.
- Caller identity through `A2AServer`, and any version after preview4.
