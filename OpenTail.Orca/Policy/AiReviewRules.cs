namespace OpenTail.Orca;

/// <summary>What an AI reviewer counts, gathered by the queue: the inputs to <see cref="AiReviewRules.Check"/> that need the store.</summary>
public sealed record AiReviewHistory(int AiRejectionsInChain, int AiApprovalsInWindow, int AiRefusalsInWindow = 0);

/// <summary>
/// The limits on an AI reviewer (v6 R1), on top of <see cref="ReviewAuthority"/>. An AI reviewer can be talked into "yes", so what it may
/// decide is bounded by what being fooled would cost: never a destructive action, outside effects only when the operator opted in, never
/// for a requester that read outside content, never with edited arguments, never as the second pair of eyes, not after it has turned the
/// same request down repeatedly, and only so many approvals per requester. Pure: the queue enforces it, the runner uses it to decide
/// what to offer.
/// </summary>
public static class AiReviewRules
{
    /// <summary>The highest risk an AI may decide for a tool when its policy does not say.</summary>
    public const ToolRisk DefaultCeiling = ToolRisk.Write;

    public static ToolRisk Ceiling(ToolPolicy? policy) => policy?.AiMaxRisk ?? DefaultCeiling;

    private static readonly System.Text.RegularExpressions.Regex ReviewerNamePattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A name an AI reviewer may be assigned under (D-20): letters, digits, '.', '_' and '-', starting with a letter or digit.</summary>
    public static bool IsValidReviewerName(string name) => ReviewerNamePattern.IsMatch(name);

    /// <summary>The startup warnings for AI-enabled queued tools with no reviewer list (D-36): any AI reviewer may decide them (or refuse them, when
    /// the tool is Human-only with AiMayRefuse), which is the older rule kept for compatibility. Listing the reviewers is the safer setting. One
    /// warning per tool.</summary>
    public static IReadOnlyList<string> UnassignedAiTools(PolicyConfig config) =>
        config.Tools
            .Where(t => t.Value.AiReviewers is null && t.Value.Mode == ToolMode.Queue
                && (t.Value.Reviewer is ReviewerKind.Ai or ReviewerKind.Either || t.Value.AiMayRefuse))
            .Select(t => t.Value.Reviewer is ReviewerKind.Ai or ReviewerKind.Either
                ? $"tool '{t.Key}' lets an AI reviewer decide it and names no reviewer (\"AiReviewers\"): any configured AI reviewer may decide it."
                : $"tool '{t.Key}' lets an AI reviewer refuse it and names no reviewer (\"AiReviewers\"): any configured AI reviewer may refuse it.")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>May this AI subject decide this action's tool at all (D-20, D-36)? With no assignment list, any AI reviewer may, as before. With
    /// one, only the names on it. The queue checks this at the decision, and the runner before the reviewer is shown the action.</summary>
    public static bool Assigned(ActionRecord r, string aiSubject)
    {
        if (r.PolicySnapshot?.AiReviewers is not { } names) return true;
        return Subjects.IsAi(aiSubject) && names.Contains(aiSubject[3..], StringComparer.Ordinal);
    }

    /// <summary>Is this action offered to this AI reviewer at all (D-20, D-30): its policy lets an AI decide, and the action is assigned to it?
    /// The runner, the list and the read all use this, so they agree on what an AI reviewer sees.</summary>
    public static bool Offered(ActionRecord r, string aiSubject) =>
        ((r.PolicySnapshot?.Reviewer is ReviewerKind.Ai or ReviewerKind.Either) || r.PolicySnapshot?.AiMayRefuse == true) && Assigned(r, aiSubject);

    /// <summary>The audit line for an AI decision (D-29): what decided and under which instructions. The checks themselves stay on the stored
    /// review: they are the model's words and may quote arguments, which the audit trail never holds.</summary>
    public static string AuditDetail(ActionReview r) =>
        $"{r.Decision}; model {Short(r.AiModel)}; prompt {Short(r.AiPromptVersion)}; {r.AiChecks?.Count ?? 0} check(s) reported";

    private static string Short(string? s) => s is null ? "unknown" : s.Length <= 100 ? s : s[..100] + "...";

    /// <summary>May an AI reviewer make this decision? <paramref name="risk"/> is null when the tool is no longer loaded (treated as
    /// destructive). <paramref name="history"/> may be null when only the action itself is being judged (whether to offer it at all).</summary>
    public static ReviewVerdict Check(ActionRecord r, ReviewDecision decision, ToolRisk? risk, bool editsArguments, AiReviewHistory? history)
    {
        var policy = r.PolicySnapshot;
        var effective = risk ?? ToolRisk.Destructive;
        // F20, D-2, D-3 (P2): the Destructive ban, the risk ceiling and the taint rule stop an AI approving. They do not stop it refusing,
        // because a refusal runs nothing.
        var approval = decision is ReviewDecision.Approve or ReviewDecision.Retry;

        if (approval && effective == ToolRisk.Destructive)
            return ReviewVerdict.No("Destructive actions are never approved by an AI reviewer.");
        if (approval && Rank(effective) > Rank(Ceiling(policy)))
            return ReviewVerdict.No($"{effective} actions of this tool are above what an AI reviewer may approve (AiMaxRisk {Ceiling(policy)}).");
        if (approval && r.Tainted && effective == ToolRisk.ExternalEffect)
            return ReviewVerdict.No("The requester had read outside content before asking, so an AI reviewer may not approve this.");

        var max = policy?.AiMaxRejections ?? 2;
        if (history is not null && max > 0 && history.AiRejectionsInChain >= max)
            return ReviewVerdict.No($"An AI reviewer has turned this request down {history.AiRejectionsInChain} times, so it may not decide it again.");

        // D-21 and D-31 (P2): a refusal budget per requester and tool. Past it, the AI may not refuse more in the window; a person decides.
        if (!approval && history is not null && policy?.AiRefusalBudget is { MaxCount: > 0 } refusals && history.AiRefusalsInWindow >= refusals.MaxCount)
            return ReviewVerdict.No($"An AI reviewer has refused {history.AiRefusalsInWindow} of this requester's requests for this tool within {refusals.Per}; it may not refuse more in this window.");

        if (decision is ReviewDecision.Approve or ReviewDecision.Retry)
        {
            if (editsArguments)
                return ReviewVerdict.No("An AI reviewer cannot change the arguments; it can approve, reject or ask for changes.");
            if (policy?.SelfApproval == SelfApproval.Forbid)
                return ReviewVerdict.No("This action needs four eyes: a person other than the requester approves it, not an AI.");
            if (history is not null && policy?.AiBudget is { MaxCount: > 0 } budget && history.AiApprovalsInWindow >= budget.MaxCount)
                return ReviewVerdict.No($"An AI reviewer has approved {history.AiApprovalsInWindow} of these within {budget.Per}, so it may not approve more in this window.");
        }
        return ReviewVerdict.Yes;
    }

    /// <summary>Risk as AI authority sees it: Read, then Write, then ExternalEffect. Destructive is outside the scale (never).</summary>
    private static int Rank(ToolRisk risk) => risk switch
    {
        ToolRisk.Read => 0,
        ToolRisk.Write => 1,
        ToolRisk.ExternalEffect => 2,
        _ => 3,
    };

    /// <summary>An AI review that turned the action down (reject or ask for changes).</summary>
    internal static bool IsAiRefusal(ActionReview review) =>
        review.Subject is { } s && Subjects.IsAi(s) && review.Decision is ReviewDecision.Reject or ReviewDecision.RequestChanges;

    internal static bool IsAiApproval(ActionReview review) =>
        review.Subject is { } s && Subjects.IsAi(s) && review.Decision is ReviewDecision.Approve or ReviewDecision.Retry;
}
