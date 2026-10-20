// The Tasks extension and alternate results are marked experimental (MCPEXP002) in SDK 2.2.0; the Host uses them deliberately.
#pragma warning disable MCPEXP002
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace OpenTail.Host;

/// <summary>The action a queued tool call just created, for the call's handler to turn into a task. The handler opens a capture
/// before invoking the tool and the tool records into it (an AsyncLocal set inside the tool would not flow back out).</summary>
internal static class ActionCapture
{
    private static readonly AsyncLocal<Box?> Current = new();

    internal sealed class Box : IDisposable
    {
        public ActionRecord? Record { get; set; }
        public void Dispose() => Current.Value = null;
    }

    public static Box Begin() => Current.Value = new Box();

    public static void Record(ActionRecord record)
    {
        if (Current.Value is { } box) box.Record = record;
    }
}

/// <summary>
/// Queued and delayed actions as MCP Tasks (the <c>io.modelcontextprotocol/tasks</c> extension, protocol 2026-07-28): the task id is
/// the action id, the action store is the task store, and reading a task goes through the same code as <c>get_action_status</c>.
/// Only for a client that opted in on that request, and only when the call did not finish inline. See docs/design/mcp-tasks.md.
/// </summary>
internal static class ActionTasks
{
    public const string ExtensionId = "io.modelcontextprotocol/tasks";

    /// <summary>Waiting for a person: people are slow, so checking often helps nobody.</summary>
    public static readonly TimeSpan PollWhileReviewed = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PollWhileRunning = TimeSpan.FromSeconds(2);

    /// <summary>The first protocol revision with the Tasks extension. Revisions are dates, so later ones compare greater.</summary>
    public const string TasksRevision = "2026-07-28";

    public static bool IsTasksRevision(string? protocolVersion) =>
        protocolVersion is { Length: 10 } v && string.CompareOrdinal(v, TasksRevision) >= 0;

    /// <summary>The client may receive a task on this request: the 2026-07-28 revision or later, and the extension in its
    /// capabilities (from initialize, or in the request's own metadata).</summary>
    public static bool OptedIn(JsonRpcRequest? request)
    {
        if (!IsTasksRevision(request?.Context?.ProtocolVersion)) return false;
        if (request?.Context?.ClientCapabilities?.Extensions?.ContainsKey(ExtensionId) == true) return true;
        return request?.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["extensions"] is JsonObject ext && ext.ContainsKey(ExtensionId);
    }

    /// <summary>The call becomes a task when its action is still waiting (for a reviewer or an undo window) or running.</summary>
    public static bool Unfinished(ActionRecord r) => r.Status is ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Executing;

    public static McpTaskStatus StatusOf(ActionRecord r) => r.Status switch
    {
        ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Executing => McpTaskStatus.Working,
        // The requester's own cancel is a cancellation; a person's Stop is a decision, and the model needs to read it.
        ActionStatus.Cancelled when !r.Reviews.Any(v => v.Decision == ReviewDecision.Reject) => McpTaskStatus.Cancelled,
        _ => McpTaskStatus.Completed,
    };

    public static string? MessageOf(ActionRecord r) => r.Status switch
    {
        ActionStatus.Pending when r.RunAt is { } at => $"Runs at {at:u} unless a person stops it.",
        ActionStatus.Pending => r.ExpiresAt is { } e ? $"Waiting for a reviewer; expires at {e:u}." : "Waiting for a reviewer.",
        ActionStatus.Approved or ActionStatus.Executing => "Running.",
        _ => null,
    };

    public static long PollIntervalMs(ActionRecord r, DateTimeOffset now)
    {
        var interval = r.Status switch
        {
            ActionStatus.Pending when r.RunAt is { } at => Clamp(at - now, TimeSpan.FromSeconds(1), PollWhileReviewed),
            ActionStatus.Pending => PollWhileReviewed,
            _ => PollWhileRunning,
        };
        return (long)interval.TotalMilliseconds;

        static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
    }

    /// <summary>How long the client can expect the task to be readable: a pending action until it expires plus result retention; a
    /// finished one until it is purged. Null when nothing purges it.</summary>
    public static TimeSpan? TimeToLive(ActionRecord r, int retentionDays, DateTimeOffset now)
    {
        if (retentionDays <= 0) return null;
        var from = r.Status == ActionStatus.Pending && r.ExpiresAt is { } e ? e : r.CreatedAt;
        var left = from + TimeSpan.FromDays(retentionDays) - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public static CreateTaskResult Created(ActionRecord r, int retentionDays, DateTimeOffset now) => new()
    {
        TaskId = r.Id,
        Status = StatusOf(r),
        CreatedAt = r.CreatedAt,
        LastUpdatedAt = r.UpdatedAt,
        TimeToLive = TimeToLive(r, retentionDays, now),
        PollIntervalMs = PollIntervalMs(r, now),
        StatusMessage = MessageOf(r),
    };

    /// <summary>The tool result a finished task carries: the action's detail, as get_action_status gives it, marked as an error for
    /// anything but success, so the model reads a reviewer's reason exactly as it would there.</summary>
    public static CallToolResult ResultOf(ActionRecord r) => new()
    {
        IsError = r.Status != ActionStatus.Succeeded,
        Content = [new TextContentBlock { Text = ActionViews.DetailJson(r) }],
    };

    public static GetTaskResult Read(ActionRecord r, int retentionDays, DateTimeOffset now)
    {
        var ttl = TimeToLive(r, retentionDays, now);
        var poll = PollIntervalMs(r, now);
        return StatusOf(r) switch
        {
            McpTaskStatus.Working => new WorkingTaskResult
            {
                TaskId = r.Id, CreatedAt = r.CreatedAt, LastUpdatedAt = r.UpdatedAt, TimeToLive = ttl, PollIntervalMs = poll,
                StatusMessage = MessageOf(r), ResultType = "complete",
            },
            McpTaskStatus.Cancelled => new CancelledTaskResult
            {
                TaskId = r.Id, CreatedAt = r.CreatedAt, LastUpdatedAt = r.UpdatedAt, TimeToLive = ttl, PollIntervalMs = poll,
                StatusMessage = "Cancelled by the requester.", ResultType = "complete",
            },
            _ => new CompletedTaskResult
            {
                TaskId = r.Id, CreatedAt = r.CreatedAt, LastUpdatedAt = r.UpdatedAt, TimeToLive = ttl, PollIntervalMs = poll,
                Result = JsonSerializer.SerializeToElement(ResultOf(r), McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>()),
                ResultType = "complete",
            },
        };
    }

    public static string TaskIdOf(JsonRpcRequest request, string method) =>
        request.Params?["taskId"]?.GetValue<string>() is { Length: > 0 } id ? id
        : throw new McpProtocolException($"Missing params.taskId for {method}", McpErrorCode.InvalidParams);

    /// <summary>The same answer for an unknown id and someone else's, so task ids cannot be probed.</summary>
    public static McpProtocolException NotFound(string taskId) => new($"Unknown task: '{taskId}'", McpErrorCode.InvalidParams);

    public static void Gate(JsonRpcRequest request, string method)
    {
        if (!IsTasksRevision(request.Context?.ProtocolVersion))
            throw new McpProtocolException($"The method '{method}' needs protocol 2026-07-28 or later.", McpErrorCode.MethodNotFound);
        if (!OptedIn(request))
            throw new MissingRequiredClientCapabilityException(
                new ClientCapabilities { Extensions = new Dictionary<string, object> { [ExtensionId] = new JsonObject() } },
                $"The request requires the '{ExtensionId}' client extension capability.");
    }
}
