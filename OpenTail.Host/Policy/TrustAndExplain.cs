namespace OpenTail.Host;

public sealed record TrustSuggestion(string Tool, int CleanApprovals, int Required, bool Eligible, bool AlreadyPromoted);

/// <summary>
/// Trust ramp: reports when a queued tool has earned promotion to Execute. It never promotes by itself.
/// Promotions are stored with the database (every process on it agrees) and are withdrawn automatically on the
/// first failure or rejection of that tool. A Deny/Queue rule or the taint override always beats a promotion.
/// </summary>
public sealed class TrustAdvisor(PolicyResolver resolver, IActionStore store, IAuditSink audit, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TrustSuggestion> EvaluateAsync(string tool, CancellationToken ct = default)
    {
        var required = resolver.Config.Tools.TryGetValue(tool, out var p) ? p.TrustRamp?.AfterCleanApprovals ?? 0 : 0;
        var rows = await store.QueryAsync(new ActionQuery { ToolName = tool, Limit = int.MaxValue }, ct);

        // Walk newest to oldest over decided actions; stop at the first thing that is not a clean approval.
        var clean = 0;
        foreach (var r in rows.Where(r => r.PolicyMode == ToolMode.Queue).OrderByDescending(r => r.CreatedAt))
        {
            if (r.Status is ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Executing or ActionStatus.Cancelled
                or ActionStatus.Expired) continue;
            if (IsCleanApproval(r)) clean++;
            else break;
        }

        return new TrustSuggestion(tool, clean, required, required > 0 && clean >= required, resolver.IsPromoted(tool));
    }

    /// <summary>Operator confirmation. Returns false when the tool is not eligible.</summary>
    public async Task<bool> PromoteAsync(string tool, string @operator, CancellationToken ct = default)
    {
        var s = await EvaluateAsync(tool, ct);
        if (!s.Eligible || s.AlreadyPromoted) return false;
        resolver.Promote(tool, @operator, _clock.GetUtcNow());
        await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "trust_promoted", null, tool, @operator,
            $"after {s.CleanApprovals} clean approvals"), ct);
        return true;
    }

    internal async Task DemoteIfFailedAsync(ActionRecord r, CancellationToken ct)
    {
        if (!resolver.IsPromoted(r.ToolName)) return;
        if (r.Status is not (ActionStatus.Failed or ActionStatus.Rejected)) return;
        if (resolver.Demote(r.ToolName))
            await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "trust_demoted", r.Id, r.ToolName, "system", r.Status.ToString()), ct);
    }

    private static bool IsCleanApproval(ActionRecord r) =>
        r.Status == ActionStatus.Succeeded
        && r.Attempts == 1
        && r.Reviews.Count == 1
        && r.Reviews[0].Decision == ReviewDecision.Approve
        && r.Reviews[0].EditedArgumentsJson is null;
}

public sealed record ExplainResult(
    string Tool,
    bool Known,
    ToolRisk? Risk,
    ToolMode ConfiguredMode,
    ToolMode EffectiveMode,
    string Rule,
    bool Tainted,
    bool Advertised,
    bool Promoted,
    IReadOnlyList<string> Notes);
