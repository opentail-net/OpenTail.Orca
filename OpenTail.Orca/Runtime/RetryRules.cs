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
    /// <item>An unknown outcome on a tool that is not idempotent by key is retried only by a person who records, in the review, the check that the effect did not happen.</item>
    /// </list>
    /// </summary>
    public static string? Refusal(ActionError? error, bool idempotentByKey, bool byAi, string? reconciliation)
    {
        if (error is null) return "Only a failed action with a recorded error can be retried.";
        if (!error.Retryable) return "This failure is not retryable.";
        if (byAi && !idempotentByKey) return "An AI reviewer retries only a tool that is idempotent by key.";
        if (error.Code == UnknownOutcomeCode && !idempotentByKey && string.IsNullOrWhiteSpace(reconciliation))
            return "The outcome of this run is unknown. Record in the review the check that the effect did not happen, then retry.";
        return null;
    }
}
