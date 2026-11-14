using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>The packet, and the texts in it that a requester, a partner or outside content can write: the only texts the pre-scan reads.</summary>
public sealed record ReviewPacketBuild(JsonObject Packet, IReadOnlyList<(string Field, string Text)> RequesterText);

/// <summary>
/// The packet a model reviewer is shown (section 5.1). Orca builds it from the stored action, the chain of resubmissions it came from, and the
/// store's history of the tool and the requester, and nothing else: the requester's conversation is never part of it, and the requester has
/// no way to write to the reviewer. Values Orca writes (names, decisions, statuses, counts, the operator's guidance) are data, and they are not
/// scanned.
/// </summary>
public static class ReviewPacket
{
    /// <summary>The history the packet's counts cover: this many days, and at most this many rows.</summary>
    public const int HistoryDays = 30;
    public const int HistoryRows = 500;

    /// <summary>How many earlier actions in the resubmission chain the packet walks (as the AI limits do).</summary>
    private const int ChainHops = 100;

    public static async Task<ReviewPacketBuild> BuildAsync(OpenTailHost host, ActionRecord action, ToolRisk? risk, DateTimeOffset now,
        CancellationToken ct = default)
    {
        var requesterText = new List<(string Field, string Text)>();

        // The chain: the earlier attempts of this request, oldest first, each with its decision, who made it, and its reason.
        var chain = new List<ActionRecord>();
        var parent = action.ParentActionId;
        for (var hops = 0; parent is not null && hops < ChainHops; hops++)
        {
            if (await host.Store.GetAsync(parent, ct) is not { } p) break;
            chain.Insert(0, p);
            parent = p.ParentActionId;
        }
        var attempts = new JsonArray();
        foreach (var earlier in chain.Append(action).SelectMany(a => a.Reviews.Select(r => (Action: a, Review: r))))
        {
            var index = attempts.Count;
            var reason = earlier.Review.Reason;
            if (reason is not null) requesterText.Add(($"earlierAttempts[{index}].reason", reason));
            attempts.Add(new JsonObject
            {
                ["decision"] = earlier.Review.Decision.ToString(),
                ["by"] = earlier.Review.Subject is { } s && Subjects.IsAi(s) ? "ai" : "person",
                ["reason"] = reason,
            });
        }

        var similar = await host.Store.QueryAsync(new ActionQuery { ToolName = action.ToolName, Limit = 5 }, ct);
        var history = await host.Store.QueryAsync(new ActionQuery
        {
            RequesterId = action.Requester.Id,
            ToolName = action.ToolName,
            UpdatedSince = now - TimeSpan.FromDays(HistoryDays),
            Limit = HistoryRows,
        }, ct);

        if (action.ArgumentsJson is { } arguments) requesterText.Add(("arguments", arguments));
        if (action.PreviewJson is { } preview) requesterText.Add(("preview", preview));

        var packet = new JsonObject
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
            // The operator's instruction for this tool. The operator wrote it, so it is not scanned.
            ["operatorGuidance"] = action.PolicySnapshot?.AiReviewGuidance,
            // The arguments the requester submitted, as the text it submitted: data, never parsed into instructions.
            ["arguments"] = action.ArgumentsJson,
            ["preview"] = action.PreviewJson,
            ["tainted"] = action.Tainted,
            // Data-flow labels the session has picked up from its reads (labels the operator defined), listed for the reviewer. Not scanned.
            ["sessionLabels"] = Array((action.SessionLabels ?? []).Select(l => (JsonNode)JsonValue.Create(l)!)),
            ["earlierAttempts"] = attempts,
            ["similarRecent"] = Array(similar.Where(s => s.Id != action.Id).Select(s => (JsonNode)new JsonObject
            {
                ["status"] = s.Status.ToString(),
                ["createdAt"] = s.CreatedAt.ToString("u"),
            })),
            ["requesterHistory"] = new JsonObject
            {
                ["windowDays"] = HistoryDays,
                ["rowsAtMost"] = HistoryRows,
                ["counts"] = Array(history.GroupBy(h => h.Status).Select(g => (JsonNode)new JsonObject
                {
                    ["status"] = g.Key.ToString(),
                    ["count"] = g.Count(),
                })),
            },
        };
        return new ReviewPacketBuild(packet, requesterText);
    }

    private static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());
}
