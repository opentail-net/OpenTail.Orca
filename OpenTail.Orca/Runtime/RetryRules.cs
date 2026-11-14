namespace OpenTail.Orca;

/// <summary>P0 (F14, F11): when a failed action may be retried. It applies to every reviewer and every route, not only to the page.</summary>
public static class RetryRules
{
    /// <summary>The error code of a run whose outcome is unknown: the Host stopped while the tool was running (see the recovery path in ActionExecutor).</summary>
    public const string UnknownOutcomeCode = "interrupted";

    /// <summary>
    /// Why a retry is refused, or null when it may proceed.
    /// <list type="bullet">
    /// <item>No recorded error, or a failure that is not retryable: refused for everyone.</item>
    /// <item>An AI reviewer retries only a tool that is idempotent by key.</item>
    /// <item>An unknown outcome on a tool that is not idempotent by key is retried only by a person, under the tool's <paramref name="setting"/>
    /// (D-32): <c>Never</c> refuses it; <c>Once</c> allows one retry with a reason, and a second unknown outcome is final; <c>WithReason</c>
    /// allows every retry with a reason. Null (an action queued before the setting) is <c>Once</c>, the default.</item>
    /// </list>
    /// A tool idempotent by key is not limited by the setting.
    /// </summary>
    /// <param name="priorRetries">The retries already made on this action (its earlier <c>Retry</c> decisions).</param>
    public static string? Refusal(ActionError? error, bool idempotentByKey, bool byAi, string? reconciliation,
        UnknownOutcomeRetry? setting = null, int priorRetries = 0)
    {
        if (error is null) return "Only a failed action with a recorded error can be retried.";
        if (!error.Retryable) return "This failure is not retryable.";
        if (byAi && !idempotentByKey) return "An AI reviewer retries only a tool that is idempotent by key.";
        // A tool that reports an unknown outcome itself (its error code is then "unknown_outcome") is one too: the setting and the reason apply to it.
        if (error.EffectiveKind == ToolFailureKind.UnknownOutcome && !idempotentByKey)
        {
            var mode = setting ?? UnknownOutcomeRetry.Once;
            if (mode == UnknownOutcomeRetry.Never)
                return "The outcome of this run is unknown, and this tool is set never to retry an unknown outcome (UnknownOutcomeRetry Never).";
            if (mode == UnknownOutcomeRetry.Once && priorRetries > 0)
                return "The outcome of this run is unknown again after a retry. A second unknown outcome on this action is final (UnknownOutcomeRetry Once).";
            if (string.IsNullOrWhiteSpace(reconciliation))
                return "The outcome of this run is unknown. Record in the review the check that the effect did not happen, then retry.";
        }
        return null;
    }
}
