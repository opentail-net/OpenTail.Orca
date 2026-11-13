using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.AspNetCore.Http;
using OpenTail.Host.Http;

namespace OpenTail.Host.A2A;

/// <summary>
/// The A2A request handler of this Host (decision D4): our own <see cref="IA2ARequestHandler"/>, not the SDK's <c>A2AServer</c>. The SDK's
/// JSON-RPC processor calls it after sign-in, authorisation and the version gate. This slice serves SendMessage for new tasks with
/// <c>returnImmediately=true</c>, GetTask and CancelTask. Everything else answers UnsupportedOperation.
/// The caller comes only from the HTTP context (decision D5). <c>requestContext</c> is null here (no extension options are registered) and
/// is never read.
/// </summary>
internal sealed class A2ARequestHandler(OpenTailHost host, A2aOptions config, IHttpContextAccessor accessor, A2ATaskTable tasks) : IA2ARequestHandler
{
    private const int MaxMessageIdLength = 256;
    private const string NotFound = "The task was not found.";

    // ---------------------------------------------------------------------------------------------------------- SendMessage

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
    {
        var caller = Caller() ?? throw new A2AException("This credential cannot use A2A.", A2AErrorCode.InvalidRequest);
        var message = request.Message ?? throw await RefuseAsync(caller, "no_message", A2AErrorCode.InvalidParams, "A message is required.", cancellationToken);

        // Continuing a task (a taskId, or a contextId that names one of this partner's tasks) is not served in this slice.
        if (!string.IsNullOrEmpty(message.TaskId)
            || (!string.IsNullOrEmpty(message.ContextId) && tasks.FindByContext(caller.PartnerId, message.ContextId) is not null))
            throw await RefuseAsync(caller, "continuation", A2AErrorCode.UnsupportedOperation, "Continuing a task is not served yet.", cancellationToken);
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.MessageId.Length > MaxMessageIdLength)
            throw await RefuseAsync(caller, "message_id", A2AErrorCode.InvalidParams, "A messageId of 1 to 256 characters is required.", cancellationToken);

        // Parts: one text part is the goal, at most one data part is the inputs. A file is refused. Metadata and extensions are not read.
        string? goal = null;
        string? inputsRaw = null;
        var texts = 0;
        var datas = 0;
        foreach (var part in message.Parts)
        {
            switch (part.ContentCase)
            {
                case PartContentCase.Text:
                    texts++;
                    goal = part.Text;
                    break;
                case PartContentCase.Data:
                    datas++;
                    inputsRaw = part.Data!.Value.GetRawText();
                    break;
                case PartContentCase.Raw:
                case PartContentCase.Url:
                    throw await RefuseAsync(caller, "file_part", A2AErrorCode.ContentTypeNotSupported, "File parts are not accepted; send text and data parts.", cancellationToken);
                default:
                    throw await RefuseAsync(caller, "empty_part", A2AErrorCode.InvalidParams, "Every part needs text or data.", cancellationToken);
            }
        }
        if (texts != 1 || string.IsNullOrWhiteSpace(goal))
            throw await RefuseAsync(caller, "goal", A2AErrorCode.InvalidParams, "Send one text part: the goal.", cancellationToken);
        if (datas > 1)
            throw await RefuseAsync(caller, "inputs", A2AErrorCode.InvalidParams, "Send at most one data part: the inputs.", cancellationToken);

        var work = host.Policy.Config.Work;
        var goalBytes = Encoding.UTF8.GetByteCount(goal);
        if (goalBytes > work.GoalMaxBytes)
            throw await RefuseAsync(caller, "goal_too_large", A2AErrorCode.InvalidParams, "The goal is too long.", cancellationToken);
        var inputs = inputsRaw ?? "";
        var inputBytes = Encoding.UTF8.GetByteCount(inputs);
        if (inputBytes > work.InputsMaxBytes)
            throw await RefuseAsync(caller, "inputs_too_large", A2AErrorCode.InvalidParams, "The inputs are too large.", cancellationToken);

        // The skill: the caller's profile must list a2a.submit, and exactly one skill must be configured (decision D10).
        if (config.Skills.Count != 1 || !ProfileLists(caller.Profile, A2ASubmitModule.Tool))
            throw await RefuseAsync(caller, "skill_unavailable", A2AErrorCode.InvalidParams, "That capability is not available to you.", cancellationToken);
        var skillId = config.Skills.Keys.Single();

        // Blocking sends are not served yet. Refused before the table is touched, so a refused send leaves no row behind.
        if (request.Configuration?.ReturnImmediately != true)
            throw await RefuseAsync(caller, "blocking", A2AErrorCode.UnsupportedOperation,
                "Blocking sends are not served yet: send with returnImmediately set to true.", cancellationToken);

        // Step 2: one transaction finds or creates the row for (partner, messageId).
        var hash = ContentHash(skillId, goal, inputs);
        var now = DateTimeOffset.UtcNow;
        var (row, outcome) = tasks.GetOrCreate(caller.PartnerId, message.MessageId, hash, skillId, now);
        if (outcome == A2ATaskTable.Outcome.ReusedWithOtherContent)
            throw await RefuseAsync(caller, "message_id_reused", A2AErrorCode.InvalidParams, "This messageId was already used for a different request.", cancellationToken);
        if (outcome == A2ATaskTable.Outcome.Created)
            await host.Audit.AppendAsync(new AuditEntry(now, "a2a.received", null, A2ASubmitModule.Tool, caller.Subject,
                $"task={row.TaskId} partner={caller.PartnerId} skill={skillId} hash={hash} bytes={goalBytes + inputBytes}"), cancellationToken);

        // Step 3: submit the action with the task id as its idempotency key. A crash after the insert is healed here: the retry submits
        // the same arguments with the same key, and the store returns the action it already has.
        if (row.SubmitActionId is null)
        {
            var session = host.Sessions.Start(caller.Subject);
            var args = new JsonObject
            {
                ["partner"] = caller.PartnerId,
                ["taskId"] = row.TaskId,
                ["skill"] = skillId,
                ["goal"] = goal,
                ["inputs"] = inputs,
            };
            var submitted = await host.Queue.SubmitAsync(
                new ActionRequest(A2ASubmitModule.Tool, args.ToJsonString(), new ActionRequester(RequesterKind.AiSession, caller.Subject), session, row.TaskId)
                {
                    Profile = caller.Profile,
                }, cancellationToken);
            tasks.SetSubmitAction(row.TaskId, submitted.Id, DateTimeOffset.UtcNow);
            row = row with { SubmitActionId = submitted.Id };
        }

        // Step 4: returnImmediately=true returns now, with the task as it stands.
        var action = await ActionOf(row, cancellationToken);
        return new SendMessageResponse { Task = TaskOf(row, action, caller.Subject) };
    }

    // ---------------------------------------------------------------------------------------------------------- GetTask, CancelTask

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
    {
        var caller = Caller() ?? throw new A2AException("This credential cannot use A2A.", A2AErrorCode.InvalidRequest);
        // Only this partner's tasks. A foreign id and an unknown id get the same answer (decision D11).
        var row = tasks.FindByTask(caller.PartnerId, request.Id) ?? throw new A2AException(NotFound, A2AErrorCode.TaskNotFound);
        return TaskOf(row, await ActionOf(row, cancellationToken), caller.Subject);
    }

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
    {
        var caller = Caller() ?? throw new A2AException("This credential cannot use A2A.", A2AErrorCode.InvalidRequest);
        var row = tasks.FindByTask(caller.PartnerId, request.Id) ?? throw new A2AException(NotFound, A2AErrorCode.TaskNotFound);
        var action = await ActionOf(row, cancellationToken);
        if (action is not { Status: ActionStatus.Pending })
            throw new A2AException("The task can no longer be cancelled.", A2AErrorCode.TaskNotCancelable);
        try
        {
            // The requester is the partner, the same ActionRequester the submit used, so the queue accepts it.
            var cancelled = await host.Queue.CancelAsync(action.Id, new ActionRequester(RequesterKind.AiSession, caller.Subject), cancellationToken);
            return TaskOf(row, cancelled, caller.Subject);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // The action moved on between the read and the cancel.
            throw new A2AException("The task can no longer be cancelled.", A2AErrorCode.TaskNotCancelable);
        }
    }

    // ---------------------------------------------------------------------------------------------------------- not served in this slice

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("ListTasks");

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => Refuse("Streaming");

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => Refuse("Resubscribe");

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("Push notifications");

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("Push notifications");

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("Push notifications");

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("Push notifications");

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestContext? requestContext, CancellationToken cancellationToken = default)
        => throw Unsupported("The extended agent card");

    /// <summary>A stream that refuses at its first step, so the SDK reports UnsupportedOperation.</summary>
    private static async IAsyncEnumerable<StreamResponse> Refuse(string what, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (ct.IsCancellationRequested) yield break;
        throw Unsupported(what);
    }

    private static A2AException Unsupported(string what) => new($"{what} is not served by this Host yet.", A2AErrorCode.UnsupportedOperation);

    // ---------------------------------------------------------------------------------------------------------- helpers

    /// <summary>The caller: the partner the key names, and the profile it is bound to. Null when the request has no partner.</summary>
    private A2ACaller? Caller()
    {
        var user = accessor.HttpContext?.User;
        if (user is null || HostClaims.SubjectOf(user) is null) return null;
        var partner = user.FindFirst(HostClaims.Partner)?.Value;
        return partner is null ? null : new A2ACaller(partner, user.FindFirst(HostClaims.Profile)?.Value);
    }

    private bool ProfileLists(string? profile, string tool) =>
        profile is not null && host.Policy.Config.Agents.TryGetValue(profile, out var agent) && agent.Tools.Contains(tool, StringComparer.Ordinal);

    /// <summary>Audits a refusal with its reason code (never the request's text), and returns the error to throw.</summary>
    private async Task<A2AException> RefuseAsync(A2ACaller caller, string reason, A2AErrorCode code, string message, CancellationToken ct)
    {
        await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "a2a.refused", null, A2ASubmitModule.Tool, caller.Subject, reason), ct);
        return new A2AException(message, code);
    }

    private async Task<ActionRecord?> ActionOf(A2ATaskTable.Row row, CancellationToken ct) =>
        row.SubmitActionId is { } id ? await host.Store.GetAsync(id, ct) : null;

    /// <summary>The A2A task as the partner may see it (decision D11). Reviewer reasons never appear; the status text is generic.</summary>
    private static AgentTask TaskOf(A2ATaskTable.Row row, ActionRecord? action, string partnerSubject)
    {
        var (state, text) = StateOf(action, partnerSubject);
        return new AgentTask
        {
            Id = row.TaskId,
            ContextId = row.ContextId,
            Status = new global::A2A.TaskStatus
            {
                State = state,
                Timestamp = action?.UpdatedAt ?? row.UpdatedAt,
                Message = new Message { MessageId = row.TaskId + ":status", Role = Role.Agent, Parts = [Part.FromText(text)] },
            },
        };
    }

    private static (TaskState State, string Text) StateOf(ActionRecord? action, string partnerSubject)
    {
        if (action is null || action.Status == ActionStatus.Pending) return (TaskState.Submitted, "Received, not started.");
        switch (action.Status)
        {
            case ActionStatus.Approved:
            case ActionStatus.Executing:
            case ActionStatus.Succeeded:
                return (TaskState.Working, "Accepted, in progress.");
            case ActionStatus.Failed:
                return (TaskState.Failed, "The request could not be completed.");
            case ActionStatus.Cancelled when action.Resolution?.Resolver == partnerSubject:
                return (TaskState.Canceled, "Cancelled.");
            default:
                // Denied, rejected, changes requested, expired, or cancelled by someone else: refused, with no reason given.
                return (TaskState.Rejected, "The request was not accepted.");
        }
    }

    /// <summary>The content hash kept in the table: skill, goal and inputs, each length-prefixed so no two requests share an encoding.</summary>
    internal static string ContentHash(string skill, string goal, string inputs)
    {
        var encoded = $"{skill.Length}:{skill}|{goal.Length}:{goal}|{inputs.Length}:{inputs}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded))).ToLowerInvariant();
    }

    private sealed record A2ACaller(string PartnerId, string? Profile)
    {
        /// <summary>The subject the partner's requests are recorded under: the partner id, never the key name.</summary>
        public string Subject => "partner:" + PartnerId;
    }
}
