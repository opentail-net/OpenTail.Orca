using System.Text.Json;

namespace OpenTail.Host;

/// <summary>How people decided the actions that followed one skill version: each decided action in a session after the skill was served.</summary>
public sealed record SkillVersionOutcome(string Skill, string Version, int Decided, int Approved, int Rejected, int ChangesRequested, DateTimeOffset LastServed)
{
    /// <summary>The share of decided actions people turned down or sent back.</summary>
    public double TurnedDown => Decided == 0 ? 0 : (Rejected + ChangesRequested) / (double)Decided;

    /// <summary>95% Wilson interval of <see cref="TurnedDown"/>.</summary>
    public (double Low, double High) Interval
    {
        get
        {
            if (Decided == 0) return (0, 1);
            const double z = 1.959963984540054;
            double n = Decided, p = TurnedDown, z2 = z * z;
            var centre = (p + z2 / (2 * n)) / (1 + z2 / n);
            var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / (1 + z2 / n);
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }
    }
}

/// <summary>A suggestion to move a skill's label back. Like the trust ramp, the Host suggests and never acts: rolling back is a
/// skills.publish of the earlier version, through the same approval.</summary>
public sealed record RollbackSuggestion(string Skill, string Current, string Previous, string Why);

/// <summary>
/// Outcomes per skill version after release, in the customer's own AI client: the skill_served audit says which version was served in a
/// session, and the decisions people made on the actions that followed say how it went. A version whose actions are turned down noticeably
/// more often than the version before it gets a rollback suggestion.
/// </summary>
public static class SkillOutcomes
{
    public static async Task<IReadOnlyList<SkillVersionOutcome>> CollectAsync(OpenTailHost host, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var serveTools = host.Catalog.All.Where(t => t.Descriptor.AuditResult?.Event == "skill_served")
            .ToDictionary(t => t.Descriptor.Name, t => t.Descriptor.AuditResult!.Field, StringComparer.Ordinal);
        var counts = new Dictionary<(string Skill, string Version), Tally>();

        var served = new List<ActionRecord>();
        foreach (var tool in serveTools.Keys)
            served.AddRange(await host.Store.QueryAsync(new ActionQuery { ToolName = tool, Status = ActionStatus.Succeeded, UpdatedSince = since, Limit = int.MaxValue }, ct));

        foreach (var bySession in served.Where(s => s.SessionId is not null).GroupBy(s => s.SessionId!))
        {
            var serves = bySession.Select(s => (s.CreatedAt, Label: Field(s.ResultJson, serveTools[s.ToolName]))).Where(s => s.Label?.Contains('@') == true).ToList();
            var later = await host.Store.QueryAsync(new ActionQuery { SessionId = bySession.Key, Limit = int.MaxValue }, ct);
            foreach (var a in later.Where(a => a.Reviews.Any(r => r.Decision is ReviewDecision.Approve or ReviewDecision.Reject or ReviewDecision.RequestChanges)))
            {
                if (serveTools.ContainsKey(a.ToolName)) continue;
                var last = a.Reviews.Last(r => r.Decision is ReviewDecision.Approve or ReviewDecision.Reject or ReviewDecision.RequestChanges).Decision;
                // Every skill served in the session before the action shares in its outcome.
                foreach (var (at, label) in serves.Where(s => s.CreatedAt <= a.CreatedAt).DistinctBy(s => s.Label))
                {
                    var split = label!.LastIndexOf('@');
                    var key = (label[..split], label[(split + 1)..]);
                    if (!counts.TryGetValue(key, out var t)) counts[key] = t = new Tally();
                    t.Decided++;
                    if (last == ReviewDecision.Approve) t.Approved++;
                    if (last == ReviewDecision.Reject) t.Rejected++;
                    if (last == ReviewDecision.RequestChanges) t.Changes++;
                    if (at > t.Last) t.Last = at;
                }
            }
        }

        return [.. counts.Select(kv => new SkillVersionOutcome(kv.Key.Skill, kv.Key.Version, kv.Value.Decided, kv.Value.Approved, kv.Value.Rejected, kv.Value.Changes, kv.Value.Last))
            .OrderBy(o => o.Skill, StringComparer.Ordinal).ThenBy(o => int.TryParse(o.Version, out var v) ? v : int.MaxValue)];
    }

    /// <summary>
    /// For each skill, the most recently served version against the one served before it. Suggested when both have at least
    /// <paramref name="minCases"/> decided actions, the newer one is turned down at least <paramref name="worseBy"/> more often, and the
    /// low end of its interval is above the older one's rate (so the difference is not just noise).
    /// </summary>
    public static IReadOnlyList<RollbackSuggestion> Suggest(IReadOnlyList<SkillVersionOutcome> outcomes, int minCases = 20, double worseBy = 0.15)
    {
        var list = new List<RollbackSuggestion>();
        foreach (var g in outcomes.GroupBy(o => o.Skill))
        {
            var byServed = g.OrderByDescending(o => o.LastServed).ToList();
            if (byServed.Count < 2) continue;
            var (current, previous) = (byServed[0], byServed[1]);
            if (current.Decided < minCases || previous.Decided < minCases) continue;
            if (current.TurnedDown - previous.TurnedDown < worseBy || current.Interval.Low <= previous.TurnedDown) continue;
            list.Add(new RollbackSuggestion(g.Key, current.Version, previous.Version,
                $"version {current.Version}: {Pct(current.TurnedDown)} of {current.Decided} decided actions turned down; " +
                $"version {previous.Version}: {Pct(previous.TurnedDown)} of {previous.Decided}. Roll back with skills.publish of version {previous.Version}."));
        }
        return list;
    }

    private sealed class Tally
    {
        public int Decided, Approved, Rejected, Changes;
        public DateTimeOffset Last = DateTimeOffset.MinValue;
    }

    private static string Pct(double d) => (d * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string? Field(string? json, string field)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
