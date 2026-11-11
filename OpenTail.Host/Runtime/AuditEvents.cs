namespace OpenTail.Host;

/// <summary>
/// Audit event names for the approval path. They follow docs/design/governance-naming-plan.md: Microsoft's ADR-0030 vocabulary
/// where the concept matches. Event names are part of the audit chain's hash, so a name stored in a chain that must be kept
/// cannot change without a migration.
/// </summary>
public static class AuditEvents
{
    /// <summary>An action was queued for a reviewer (a delayed action keeps the name "delayed").</summary>
    public const string ApprovalRequested = "approval_requested";

    /// <summary>A reviewer approved the action. The detail is the reviewer's name.</summary>
    public const string ApprovalAllowed = "approval_allowed";

    /// <summary>A reviewer rejected the action. The detail is the reviewer's name. Changes requested keep their own name.</summary>
    public const string ApprovalDenied = "approval_denied";

    /// <summary>A waiting action expired before a decision.</summary>
    public const string ApprovalExpired = "approval_expired";

    /// <summary>A waiting action was cancelled by its requester or by an operator.</summary>
    public const string ApprovalCancelled = "approval_cancelled";

    /// <summary>An approved action was claimed to run: the approval is spent. Written in the claim's own transaction.</summary>
    public const string ApprovalConsumed = "approval_consumed";

    /// <summary>An approved action was refused at its claim because its approval no longer holds. The detail is the reason code, and
    /// nothing ran.</summary>
    public const string ExecutionDenied = "execution_denied";
}
