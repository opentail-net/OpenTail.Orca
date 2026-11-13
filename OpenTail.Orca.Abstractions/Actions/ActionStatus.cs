namespace OpenTail.Orca.Abstractions;

public enum ActionStatus
{
    Pending = 0,
    Approved = 1,
    Executing = 2,
    Succeeded = 3,
    Failed = 4,
    Denied = 5,
    Rejected = 6,
    ChangesRequested = 7,
    Expired = 8,
    Cancelled = 9,
}

/// <summary>Legal lifecycle transitions. Terminal states are immutable.</summary>
public static class ActionStateMachine
{
    /// <summary>States an action may be created in.</summary>
    public static bool IsValidInitial(ActionStatus s) =>
        s is ActionStatus.Pending or ActionStatus.Executing or ActionStatus.Denied;

    public static bool IsTerminal(ActionStatus s, bool retryable = false) => s switch
    {
        ActionStatus.Succeeded or ActionStatus.Denied or ActionStatus.Rejected
            or ActionStatus.ChangesRequested or ActionStatus.Expired or ActionStatus.Cancelled => true,
        ActionStatus.Failed => !retryable,
        _ => false,
    };

    /// <param name="retryable">Only meaningful for <see cref="ActionStatus.Failed"/>: a retryable failure may be re-approved.</param>
    public static bool CanTransition(ActionStatus from, ActionStatus to, bool retryable = false) => from switch
    {
        // Pending -> Pending: a partial approval (fewer distinct approvals than required so far).
        ActionStatus.Pending => to is ActionStatus.Pending or ActionStatus.Approved or ActionStatus.Rejected
            or ActionStatus.ChangesRequested or ActionStatus.Expired or ActionStatus.Cancelled,
        // Approved -> Failed: refused before it could run, when an admission (a rate limit) is already at its limit at the claim.
        ActionStatus.Approved => to is ActionStatus.Executing or ActionStatus.Failed,
        ActionStatus.Executing => to is ActionStatus.Succeeded or ActionStatus.Failed,
        ActionStatus.Failed => retryable && to is ActionStatus.Approved,
        _ => false,
    };
}
