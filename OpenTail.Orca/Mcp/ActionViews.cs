using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>The stable, versioned JSON the AI sees. Never includes argument values.</summary>
internal static class ActionViews
{
    public const int Version = 1;

    public static string StatusName(ActionStatus s) => s switch
    {
        ActionStatus.Pending => "pending_approval",
        ActionStatus.ChangesRequested => "changes_requested",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>Result of a tool call: complete, or a pointer to poll.</summary>
    public static string Response(ActionRecord r)
    {
        var o = Base(r);
        switch (r.Status)
        {
            case ActionStatus.Succeeded:
                o["result"] = r.ResultJson is null ? null : JsonNode.Parse(r.ResultJson);
                break;
            case ActionStatus.Pending when r.RunAt is { } at:
                o["status"] = "scheduled";
                o["runsAt"] = at;
                o["next"] = "Accepted: it runs at runsAt unless a person stops it. Carry on; call get_action_status later to see how it went.";
                break;
            case ActionStatus.Pending:
                o["expiresAt"] = r.ExpiresAt;
                o["next"] = "Call get_action_status with this actionId to see the decision.";
                break;
            case ActionStatus.Denied or ActionStatus.Failed:
                o["error"] = Error(r);
                break;
        }
        return o.ToJsonString();
    }

    /// <summary>Full status for get_action_status / list_my_actions, including reviewer feedback.</summary>
    public static JsonObject Detail(ActionRecord r)
    {
        var o = Base(r);
        o["tool"] = r.ToolName;
        o["createdAt"] = r.CreatedAt;
        o["updatedAt"] = r.UpdatedAt;
        o["attempts"] = r.Attempts;
        o["expiresAt"] = r.ExpiresAt;
        if (r.ParentActionId is not null) o["resubmissionOf"] = r.ParentActionId;
        if (r.Status == ActionStatus.Succeeded && r.ResultJson is not null) o["result"] = JsonNode.Parse(r.ResultJson);
        if (r.Error is not null) o["error"] = Error(r);
        if (r.Reviews.Count > 0)
        {
            var reviews = new JsonArray();
            foreach (var rv in r.Reviews)
            {
                var item = new JsonObject
                {
                    ["decision"] = rv.Decision.ToString().ToLowerInvariant(),
                    ["reason"] = rv.Reason,
                    ["at"] = rv.At,
                };
                if (rv.EditedArgumentsJson is not null) item["reviewerEditedArguments"] = true;
                reviews.Add(item);
            }
            o["reviews"] = reviews;
        }
        if (r.Status == ActionStatus.ChangesRequested)
            o["next"] = "Fix the issues in the review reason and call resubmit_action.";
        return o;
    }

    public static string DetailJson(ActionRecord r) => Detail(r).ToJsonString();

    private static JsonObject Base(ActionRecord r) => new()
    {
        ["v"] = Version,
        ["status"] = StatusName(r.Status),
        ["actionId"] = r.Id,
    };

    private static JsonObject Error(ActionRecord r) => new()
    {
        ["code"] = r.Error?.Code,
        ["message"] = r.Error?.Message,
        ["retryable"] = r.Error?.Retryable ?? false,
        ["kind"] = r.Error is { } e ? FailureKinds.Name(e.EffectiveKind) : null,
    };
}

/// <summary>Failure kinds as the model, clients and operators see them.</summary>
public static class FailureKinds
{
    /// <summary>Lower case with underscores: <c>transient</c>, <c>unknown_outcome</c>.</summary>
    public static string Name(ToolFailureKind kind) => kind switch
    {
        ToolFailureKind.UnknownOutcome => "unknown_outcome",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
