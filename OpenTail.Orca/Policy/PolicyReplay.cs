namespace OpenTail.Orca;

/// <summary>One stored action whose handling would differ under a candidate policy.</summary>
public sealed record ReplayChange(string ActionId, string Tool, ToolMode Was, ToolMode WouldBe, string? RuleName, string Rule, DateTimeOffset At, bool Tainted);

public sealed record ReplayReport(
    int Examined,
    int Unchanged,
    int SkippedNotPolicy,
    int SkippedUnknownTool,
    IReadOnlyList<ReplayChange> Changes)
{
    /// <summary>"31 Queue -> Execute (rule internal-small)" style lines, biggest groups first.</summary>
    public IReadOnlyList<string> Summary() => Changes
        .GroupBy(c => (c.Tool, c.Was, c.WouldBe, c.RuleName))
        .OrderByDescending(g => g.Count())
        .Select(g => $"{g.Count(),5}  {g.Key.Tool}: {g.Key.Was} -> {g.Key.WouldBe}" + (g.Key.RuleName is null ? "" : $" (rule {g.Key.RuleName})"))
        .ToList();
}

/// <summary>
/// "What would have happened if this policy had been in force?" Re-evaluates stored actions against a candidate policy and reports
/// the differences. Executes nothing and writes nothing. Each action is judged with the arguments it had, the taint it had, and the
/// time it was created (so a time-of-day rule is judged at that time). Budgets are not replayed: a rule with a budget is reported as
/// "would execute" because whether the budget would have been spent depends on the order everything else happened in.
/// </summary>
public static class PolicyReplay
{
    public static Task<ReplayReport> RunAsync(OpenTailHost candidate, DateTimeOffset? since = null, CancellationToken ct = default) =>
        RunAsync(candidate.Store, candidate.Catalog, candidate.Policy, since, ct);

    /// <summary>The same against a running Host's store and catalog, with a candidate policy that was never started (a proposal).</summary>
    public static async Task<ReplayReport> RunAsync(IActionStore store, ToolCatalog catalog, PolicyResolver policy, DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var rows = await store.QueryAsync(new ActionQuery { UpdatedSince = since, Limit = int.MaxValue }, ct);
        int unchanged = 0, notPolicy = 0, unknownTool = 0;
        var changes = new List<ReplayChange>();

        foreach (var r in rows)
        {
            if (!catalog.TryGet(r.ToolName, out var tool)) { unknownTool++; continue; }

            // Denials that came from something other than policy (a full queue, bad arguments, an unknown tool) are not policy decisions.
            if (r.Status == ActionStatus.Denied && r.Error?.Code is not "denied") { notPolicy++; continue; }

            var d = policy.Resolve(tool.Descriptor, r.Tainted, r.ArgumentsJson, r.CreatedAt, r.Requester.Id);
            if (d.Mode == r.PolicyMode) { unchanged++; continue; }
            changes.Add(new ReplayChange(r.Id, r.ToolName, r.PolicyMode, d.Mode, d.RuleName, d.Rule, r.CreatedAt, r.Tainted));
        }
        return new ReplayReport(rows.Count, unchanged, notPolicy, unknownTool, changes);
    }
}
