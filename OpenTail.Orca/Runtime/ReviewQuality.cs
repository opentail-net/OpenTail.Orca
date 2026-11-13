namespace OpenTail.Orca;

/// <summary>How one reviewer decided one tool's actions over a period. Figures for the operator and the reviewer, never for other reviewers.</summary>
/// <param name="MedianSecondsToDecide">From the action being queued to this reviewer's decision.</param>
/// <param name="QuickApprovals">Approvals made within <see cref="ReviewQuality.QuickSeconds"/> of the action being queued.</param>
/// <param name="BurstApprovals">Approvals made in a run of at least <see cref="ReviewQuality.BurstSize"/> within one minute.</param>
/// <param name="ApprovedThenFailed">Approved actions whose run then failed.</param>
public sealed record ReviewerToolQuality(
    string Reviewer, string Tool, int Decisions, int Approved, int Rejected, int ChangesRequested,
    double MedianSecondsToDecide, int QuickApprovals, int BurstApprovals, int ApprovedThenFailed)
{
    public double ApprovalRate => Decisions == 0 ? 0 : Approved / (double)Decisions;
}

/// <summary>A tool's decisions across reviewers, and what they suggest for its policy. The Host suggests; people change policy.</summary>
public sealed record ToolReviewSummary(string Tool, int Decisions, int Approved, int Rejected, string? Suggestion);

/// <summary>
/// Watching the reviewers (smaller extensions design): approval rates, time to decision, approvals too fast to have been read, bursts.
/// From the reviews the Host already stores; nothing is collected for it. Reported to the operator, never used to block a reviewer.
/// Planted test approvals are designed only and not built.
/// </summary>
public static class ReviewQuality
{
    public const double QuickSeconds = 5;
    public const int BurstSize = 10;

    /// <summary>A tool approved this often, over at least this many decisions, is a candidate for less friction (a delay, or the trust ramp).</summary>
    public const double FrictionCandidateRate = 0.98;
    public const int MinDecisionsForSuggestion = 50;

    public static async Task<(IReadOnlyList<ReviewerToolQuality> Reviewers, IReadOnlyList<ToolReviewSummary> Tools)> CollectAsync(
        IActionStore store, DateTimeOffset since, CancellationToken ct = default)
    {
        var actions = await store.QueryAsync(new ActionQuery { UpdatedSince = since, Limit = int.MaxValue }, ct);
        var decisions = actions
            .SelectMany(a => a.Reviews
                .Where(r => r.Decision is ReviewDecision.Approve or ReviewDecision.Reject or ReviewDecision.RequestChanges && r.At >= since)
                .Where(r => r.Reviewer != "system" && !Subjects.IsAi(r.Subject ?? ""))
                .Select(r => (Action: a, Review: r, Who: r.Subject ?? r.Reviewer)))
            .ToList();

        var reviewers = new List<ReviewerToolQuality>();
        foreach (var byReviewer in decisions.GroupBy(d => d.Who, StringComparer.Ordinal))
        {
            // Bursts across all tools: a reviewer clearing a queue clears every tool in it.
            var approvals = byReviewer.Where(d => d.Review.Decision == ReviewDecision.Approve).OrderBy(d => d.Review.At).ToList();
            var inBurst = new HashSet<(string, DateTimeOffset)>();
            for (var i = 0; i < approvals.Count; i++)
            {
                var j = i;
                while (j + 1 < approvals.Count && approvals[j + 1].Review.At - approvals[i].Review.At <= TimeSpan.FromMinutes(1)) j++;
                if (j - i + 1 >= BurstSize)
                    for (var k = i; k <= j; k++) inBurst.Add((approvals[k].Action.Id, approvals[k].Review.At));
            }

            foreach (var byTool in byReviewer.GroupBy(d => d.Action.ToolName, StringComparer.Ordinal))
            {
                var list = byTool.ToList();
                var seconds = list.Select(d => Math.Max(0, (d.Review.At - d.Action.CreatedAt).TotalSeconds)).Order().ToList();
                var approved = list.Where(d => d.Review.Decision == ReviewDecision.Approve).ToList();
                reviewers.Add(new ReviewerToolQuality(byReviewer.Key, byTool.Key, list.Count,
                    approved.Count,
                    list.Count(d => d.Review.Decision == ReviewDecision.Reject),
                    list.Count(d => d.Review.Decision == ReviewDecision.RequestChanges),
                    seconds.Count == 0 ? 0 : seconds[seconds.Count / 2],
                    approved.Count(d => (d.Review.At - d.Action.CreatedAt).TotalSeconds < QuickSeconds),
                    approved.Count(d => inBurst.Contains((d.Action.Id, d.Review.At))),
                    approved.Count(d => d.Action.Status == ActionStatus.Failed)));
            }
        }

        var tools = decisions.GroupBy(d => d.Action.ToolName, StringComparer.Ordinal).Select(g =>
        {
            var n = g.Count();
            var ok = g.Count(d => d.Review.Decision == ReviewDecision.Approve);
            var rejected = g.Count(d => d.Review.Decision == ReviewDecision.Reject);
            string? suggestion = n >= MinDecisionsForSuggestion && ok / (double)n >= FrictionCandidateRate
                ? "approved almost always: a candidate for a delay instead of a queue, or the trust ramp"
                : n >= 10 && rejected / (double)n >= 0.2 ? "often rejected: keep it queued" : null;
            return new ToolReviewSummary(g.Key, n, ok, rejected, suggestion);
        }).OrderBy(t => t.Tool, StringComparer.Ordinal).ToList();

        return (reviewers.OrderBy(r => r.Reviewer, StringComparer.Ordinal).ThenBy(r => r.Tool, StringComparer.Ordinal).ToList(), tools);
    }
}
