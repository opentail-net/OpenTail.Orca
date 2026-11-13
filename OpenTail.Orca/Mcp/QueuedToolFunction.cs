using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>
/// What the MCP client sees for a module tool: same name and schema plus two optional arguments
/// (<c>sessionId</c>, <c>idempotencyKey</c>). Calls go through the queue, never straight to the tool.
/// </summary>
internal sealed class QueuedToolFunction : AIFunction
{
    private readonly ToolRegistration _tool;
    private readonly IActionQueue _queue;
    private readonly NoticeService _notices;
    private readonly JsonElement _schema;

    public QueuedToolFunction(ToolRegistration tool, IActionQueue queue, NoticeService notices)
    {
        _tool = tool;
        _queue = queue;
        _notices = notices;
        _schema = AugmentSchema(tool.Function.JsonSchema);
    }

    public override string Name => _tool.Descriptor.Name;
    public override string Description => _tool.Descriptor.Description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        string? sessionId = null, idempotencyKey = null;
        var rest = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            switch (key)
            {
                case "sessionId": sessionId = AsString(value); break;
                case "idempotencyKey": idempotencyKey = AsString(value); break;
                default: rest[key] = value; break;
            }
        }

        // The caller is read here, once, at the edge of the MCP request, and carried explicitly from now on.
        var caller = CallerContext.Current;
        ActionRecord record;
        try
        {
            record = await Submit(caller, sessionId, idempotencyKey, rest, ct);
            ActionCapture.Record(record);
        }
        catch (ArgumentException ex)
        {
            // Tell the model what to fix instead of a generic protocol error (the SDK hides exception messages).
            return new System.Text.Json.Nodes.JsonObject
            {
                ["v"] = ActionViews.Version,
                ["status"] = "error",
                ["error"] = new System.Text.Json.Nodes.JsonObject { ["code"] = "invalid_request", ["message"] = Redaction.Scrub(ex.Message) },
            }.ToJsonString();
        }
        // Anything decided while the caller was away rides along (only when there is something to say).
        return await _notices.AttachAsync(ActionViews.Response(record), caller.Subject,
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId, ct);
    }

    private Task<ActionRecord> Submit(CallerIdentity caller, string? sessionId, string? idempotencyKey, Dictionary<string, object?> rest, CancellationToken ct)
    {
        return _queue.SubmitAsync(new ActionRequest(
            Name,
            JsonSerializer.Serialize(rest),
            new ActionRequester(RequesterKind.AiSession, caller.Subject),
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey) { Profile = caller.Profile }, ct);
    }

    private static string? AsString(object? v) => v switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        _ => v.ToString(),
    };

    private static JsonElement AugmentSchema(JsonElement inner)
    {
        var node = JsonNode.Parse(inner.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        var props = node["properties"] as JsonObject ?? [];
        props["sessionId"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Optional. Handle from start_session; lets the Host track this conversation.",
        };
        props["idempotencyKey"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Optional. Reuse the same key when retrying so the action is not duplicated.",
        };
        node["properties"] = props;
        return JsonSerializer.SerializeToElement(node);
    }
}
