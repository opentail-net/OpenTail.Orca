namespace OpenTail.Orca;

/// <summary>Read side of an audit sink, for the admin surface. Optional: sinks that cannot be read just do not implement it.</summary>
public interface IAuditReader
{
    IReadOnlyList<AuditEntry> Read(string? actionId = null);

    /// <summary>The newest entries of the whole trail, oldest of them first.</summary>
    IReadOnlyList<AuditEntry> ReadRecent(int limit) => [.. Read().TakeLast(limit)];
}

/// <summary>A reviewer backed by a delegate; handy for rule-based reviewers and tests.</summary>
public sealed class DelegateReviewer(string name, Func<ActionRecord, CancellationToken, Task<ActionReview?>> decide) : IReviewer
{
    public string Name => name;
    public Task<ActionReview?> ReviewAsync(ActionRecord action, CancellationToken ct = default) => decide(action, ct);
}

public sealed record ReviewerRunReport(int Offered, int Decided, int Skipped);

/// <summary>
/// Offers pending actions to a non-human reviewer. The reviewer only ever sees actions whose policy snapshot
/// allows it (<c>Reviewer = Ai</c> or <c>Either</c>); human-only actions are never offered. Decisions go through the
/// same queue path as any other review, so they are audited and subject to the same compare-and-set.
/// </summary>
public sealed class ReviewerRunner(OpenTailHost host)
{
    public async Task<ReviewerRunReport> RunOnceAsync(IReviewer reviewer, CancellationToken ct = default)
    {
        int offered = 0, decided = 0, skipped = 0;
        var pending = await host.Store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = int.MaxValue }, ct);
        foreach (var action in pending)
        {
            // D-20 and D-30: a reviewer is offered only the actions its policy lets an AI decide and that are assigned to it. An action it is not
            // offered is never shown to it, so no packet is built for it.
            if (!AiReviewRules.Offered(action, Subjects.Ai(reviewer.Name))) { skipped++; continue; }
            // Beyond the policy's say-so, the AI limits (risk ceiling, taint, repeated refusals): what an AI may not decide, it never sees.
            if (!(await host.Queue.AiVerdictAsync(action, ReviewDecision.Reject, ct: ct)).Allowed) { skipped++; continue; }

            offered++;
            ActionReview? decision;
            try { decision = await reviewer.ReviewAsync(action, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "reviewer_error", action.Id, action.ToolName,
                    reviewer.Name, ex.GetType().Name), ct);
                continue;
            }
            if (decision is null) continue; // abstain: leave it for a human

            // The reviewer's identity is whatever the runner says, never what the reviewer claims. So is what it saw (audit D6): the hash
            // of the action exactly as handed over, and the action itself when the policy keeps it.
            var input = System.Text.Json.JsonSerializer.Serialize(action);
            var stamped = decision with
            {
                Reviewer = reviewer.Name, At = DateTimeOffset.UtcNow, Subject = Subjects.Ai(reviewer.Name), Groups = [],
                AiInputHash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))),
                AiInput = host.Policy.Config.AiReviewInput == AiInputKeep.Full ? input : null,
            };
            try
            {
                // The queue audits every AI decision as ai_review, whichever channel it came by (D-29).
                await host.Queue.ReviewAsync(action.Id, stamped, ct);
                decided++;
            }
            catch (InvalidOperationException) { /* someone else decided first */ }
            catch (UnauthorizedAccessException) { /* past an AI limit (audited by the queue as review_refused): left for a person */ }
        }
        return new ReviewerRunReport(offered, decided, skipped);
    }

}
