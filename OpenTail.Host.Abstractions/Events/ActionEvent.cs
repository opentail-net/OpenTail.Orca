namespace OpenTail.Host.Abstractions;

public enum ActionEventKind
{
    Queued = 0,
    Approved = 1,
    Rejected = 2,
    ChangesRequested = 3,
    Succeeded = 4,
    Failed = 5,
    Expired = 6,
    Cancelled = 7,
    /// <summary>Only for denials the requester can act on (the pending cap); ordinary policy denials are not events.</summary>
    Denied = 8,
}

/// <summary>
/// Something that happened to an action. Carries identifiers and facts only: never argument values or results.
/// </summary>
public sealed record ActionEvent(
    ActionEventKind Kind,
    string ActionId,
    string ToolName,
    string ModuleId,
    ToolRisk? Risk,
    string RequesterId,
    ActionStatus Status,
    bool Tainted,
    DateTimeOffset At,
    string? Detail = null);

/// <summary>
/// Receives action events. Called after the state change was stored, so a crash in between can lose an event: sinks that
/// need certainty reconcile from the store. Implementations must not throw; if they do the Host audits and carries on.
/// </summary>
public interface IActionEventSink
{
    Task PublishAsync(ActionEvent evt, CancellationToken ct = default);
}
