using OpenTail.Orca.Abstractions;

namespace OpenTail.Orca;

/// <summary>
/// The three settings of the reviewer-gate plan (section 1a, section 3): <c>UnknownOutcomeRetry</c> (D-32), <c>AiOwnRequest</c> (D-37) and
/// <c>DefaultAiReviewers</c> (D-36). Each is set per tool, or as a policy-wide default, and each has a strict value that applies when neither
/// says otherwise. <see cref="Resolve"/> is called once, when the queue queues an action, and its result is stored in the action's policy
/// snapshot: the snapshot then says what applied, and every later check reads it in one place.
/// </summary>
public static class PolicySettings
{
    /// <summary>The snapshot of a queued action, with the three settings resolved (the tool's own value, else the policy-wide default, else the
    /// strict value). A Destructive tool's unknown outcome is never retried unless the tool itself says otherwise.</summary>
    public static ToolPolicy Resolve(ToolPolicy tool, ToolRisk risk, PolicyConfig config) => tool with
    {
        AiReviewers = tool.AiReviewers ?? config.DefaultAiReviewers,
        AiOwnRequest = tool.AiOwnRequest ?? config.AiOwnRequest ?? OpenTail.Orca.Abstractions.AiOwnRequest.ForbidApproval,
        UnknownOutcomeRetry = tool.UnknownOutcomeRetry
            ?? (risk == ToolRisk.Destructive ? OpenTail.Orca.Abstractions.UnknownOutcomeRetry.Never
                : config.UnknownOutcomeRetry ?? OpenTail.Orca.Abstractions.UnknownOutcomeRetry.Once),
    };

    /// <summary>A tool an AI reviewer may decide or refuse: a queued tool with Reviewer Ai or Either, or with AiMayRefuse.</summary>
    public static bool IsAiOpen(ToolPolicy tool) =>
        tool.Mode == ToolMode.Queue && (tool.Reviewer is ReviewerKind.Ai or ReviewerKind.Either || tool.AiMayRefuse);

    /// <summary>The loosenings a policy file sets (section 1a). Each prints a startup warning, naming the setting and the tool or the default, so
    /// the operator sees what was granted. The strict values print nothing. <paramref name="riskOf"/> gives a tool's risk (null when the tool is
    /// not loaded): a Destructive tool that sets <c>Once</c> is looser than its strict value, <c>Never</c>.</summary>
    public static IReadOnlyList<string> Loosenings(PolicyConfig config, Func<string, ToolRisk?> riskOf)
    {
        var warnings = new List<string>();
        if (config.DefaultAiReviewers is { Count: > 0 } defaults)
            warnings.Add($"DefaultAiReviewers names {string.Join(", ", defaults)}: they may decide every AI-decidable tool that names no AiReviewers of its own.");
        if (config.UnknownOutcomeRetry == OpenTail.Orca.Abstractions.UnknownOutcomeRetry.WithReason)
            warnings.Add("UnknownOutcomeRetry is WithReason for every tool: a person may retry an unknown outcome again and again, with a reason.");
        if (config.AiOwnRequest == OpenTail.Orca.Abstractions.AiOwnRequest.Allow)
            warnings.Add("AiOwnRequest is Allow for every tool: an AI reviewer may approve a request from its own identity or its own person.");
        foreach (var (name, tool) in config.Tools.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (tool.UnknownOutcomeRetry == OpenTail.Orca.Abstractions.UnknownOutcomeRetry.WithReason)
                warnings.Add($"tool '{name}' sets UnknownOutcomeRetry WithReason: a person may retry an unknown outcome again and again, with a reason.");
            if (tool.UnknownOutcomeRetry == OpenTail.Orca.Abstractions.UnknownOutcomeRetry.Once && riskOf(name) == ToolRisk.Destructive)
                warnings.Add($"tool '{name}' is Destructive and sets UnknownOutcomeRetry Once: a person may retry its unknown outcome once, with a reason (its strict value is Never).");
            if (tool.AiOwnRequest == OpenTail.Orca.Abstractions.AiOwnRequest.Allow)
                warnings.Add($"tool '{name}' sets AiOwnRequest Allow: an AI reviewer may approve a request from its own identity or its own person.");
        }
        return warnings;
    }

    /// <summary>The notices of AI-open tools that no reviewer will decide: no list of their own, and no <c>DefaultAiReviewers</c> (D-36, section 1a).
    /// A notice, not a loosening: nothing is granted, so it is kept apart from <see cref="Loosenings"/>. Both runners print it at startup.</summary>
    public static IReadOnlyList<string> Notices(PolicyConfig config) =>
        config.Tools
            .Where(t => IsAiOpen(t.Value) && t.Value.AiReviewers is not { Count: > 0 } && config.DefaultAiReviewers is not { Count: > 0 })
            .Select(t => NoticeFor(t.Key))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>The notice for one tool, in the words <see cref="Notices"/> uses.</summary>
    public static string NoticeFor(string tool) =>
        $"tool '{tool}' is open to an AI reviewer but names none, and no DefaultAiReviewers is set: no AI reviewer will decide it.";

    /// <summary>The startup refusals of the reviewer list (D-36). With two or more AI reviewers configured (in the HTTP config, or in code for the
    /// standalone server), an AI-open tool must name its reviewers, by its own list or by <c>DefaultAiReviewers</c>: which of them decides it is
    /// then the operator's choice, not the Host's.</summary>
    public static IReadOnlyList<string> Refusals(PolicyConfig config, int configuredAiReviewers)
    {
        if (configuredAiReviewers < 2 || config.DefaultAiReviewers is { Count: > 0 }) return [];
        return config.Tools
            .Where(t => IsAiOpen(t.Value) && t.Value.AiReviewers is not { Count: > 0 })
            .Select(t => $"tool '{t.Key}' is open to AI reviewers and names none, and no DefaultAiReviewers is set, while {configuredAiReviewers} AI reviewers are configured: name the reviewers it is for.")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
