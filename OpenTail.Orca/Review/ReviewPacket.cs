using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>
/// The packet a model reviewer is shown (section 5.1). Orca builds it from the stored action, the store's history of the tool and the requester,
/// and nothing else: the requester's conversation is never part of it, and the requester has no way to write to the reviewer. Every free-text
/// value in it is data (a JSON string), not an instruction.
/// </summary>
public static class ReviewPacket
{
    public static async Task<JsonObject> BuildAsync(OpenTailHost host, ActionRecord action, ToolRisk? risk, CancellationToken ct = default)
    {
        var similar = await host.Store.QueryAsync(new ActionQuery { ToolName = action.ToolName, Limit = 5 }, ct);
        var history = await host.Store.QueryAsync(
            new ActionQuery { RequesterId = action.Requester.Id, ToolName = action.ToolName, Limit = int.MaxValue }, ct);

        return new JsonObject
        {
            ["tool"] = new JsonObject
            {
                ["name"] = action.ToolName,
                ["module"] = action.ModuleId,
                ["risk"] = risk?.ToString() ?? "not loaded",
            },
            ["policy"] = new JsonObject
            {
                ["mode"] = action.PolicyMode.ToString(),
                ["rule"] = action.PolicyRule,
                ["reviewer"] = action.PolicySnapshot?.Reviewer.ToString(),
                ["aiMaxRisk"] = action.PolicySnapshot?.AiMaxRisk.ToString(),
            },
            // The arguments the requester submitted, as the text it submitted: data, never parsed into instructions.
            ["arguments"] = action.ArgumentsJson,
            ["preview"] = action.PreviewJson,
            ["taint"] = new JsonObject
            {
                ["tainted"] = action.Tainted,
                ["sources"] = Array(action.SessionLabels ?? []),
            },
            ["earlierAttempts"] = Array(action.Reviews.Select(r => (JsonNode)new JsonObject
            {
                ["decision"] = r.Decision.ToString(),
                ["reason"] = r.Reason,
            })),
            ["similarRecent"] = Array(similar.Where(s => s.Id != action.Id).Select(s => (JsonNode)new JsonObject
            {
                ["status"] = s.Status.ToString(),
                ["createdAt"] = s.CreatedAt.ToString("u"),
            })),
            ["requesterHistory"] = Array(history.GroupBy(h => h.Status).Select(g => (JsonNode)new JsonObject
            {
                ["status"] = g.Key.ToString(),
                ["count"] = g.Count(),
            })),
        };
    }

    private static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
}
