namespace OpenTail.Orca.Abstractions;

public sealed record ActionQuery
{
    public ActionStatus? Status { get; init; }
    public string? SessionId { get; init; }
    public string? RequesterId { get; init; }
    public string? ModuleId { get; init; }
    public string? ToolName { get; init; }

    /// <summary>Match any of these statuses (in addition to <see cref="Status"/> when both are set).</summary>
    public IReadOnlyList<ActionStatus>? AnyOfStatus { get; init; }

    /// <summary>Only rows last updated at or after this instant.</summary>
    public DateTimeOffset? UpdatedSince { get; init; }

    /// <summary>Only rows created strictly before this instant.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    public int Limit { get; init; } = 100;

    /// <summary>Order by creation time, newest first (default: oldest first).</summary>
    public bool NewestFirst { get; init; }
}

/// <summary>
/// A limit checked in the same transaction that inserts a record: refuse when at least <see cref="MaxCount"/> records
/// already match <see cref="Filter"/> (restricted to the last <see cref="Window"/> by update time, when given).
/// Because the count and the insert are one atomic step, limits hold even with several processes on one database.
/// </summary>
public sealed record Admission(ActionQuery Filter, TimeSpan? Window, int MaxCount);

/// <summary>The outcome of a decision made inside the store's transaction.</summary>
public sealed record ActionTransition(ActionStatus Next, ActionRecord Record);

/// <summary>Durable store of actions. Transitions are compare-and-set so concurrent reviewers/executors cannot double-apply.</summary>
public interface IActionStore
{
    /// <summary>Creates the record, or returns the existing one with the same (tool, idempotency key, arguments hash).</summary>
    Task<ActionRecord> CreateAsync(ActionRecord record, CancellationToken ct = default);

    /// <summary>Like <see cref="CreateAsync"/>, but first checks every admission inside the same transaction.
    /// Returns null when an admission refuses (nothing is stored). An idempotent repeat returns the existing record.</summary>
    Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, CancellationToken ct = default);

    /// <summary>Like <see cref="TryCreateAsync(ActionRecord, IReadOnlyList{Admission}, CancellationToken)"/>, but a refused admission does not
    /// have to mean nothing is stored: <paramref name="refused"/> is given the index of the admission that refused and returns the record to
    /// store instead (for example a failed one), or null to store nothing. Either way the decision and the store are one transaction.</summary>
    Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused,
        CancellationToken ct = default);

    Task<ActionRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Counts matching records (ignores <c>Limit</c>).</summary>
    Task<int> CountAsync(ActionQuery query, TimeSpan? window = null, CancellationToken ct = default);

    Task<IReadOnlyList<ActionRecord>> QueryAsync(ActionQuery query, CancellationToken ct = default);

    /// <summary>Applies <paramref name="update"/> only if the stored status is still <paramref name="expected"/> and the transition is legal.
    /// Returns the updated record, or null if the precondition failed.</summary>
    Task<ActionRecord?> TransitionAsync(
        string id,
        ActionStatus expected,
        ActionStatus next,
        Func<ActionRecord, ActionRecord> update,
        CancellationToken ct = default);

    /// <summary>Compare-and-set where the next status is decided <i>inside</i> the transaction from the stored record
    /// (for example "is this the n-th distinct approval?"). <paramref name="decide"/> returns null to refuse; the transition
    /// must still be a legal one.</summary>
    Task<ActionRecord?> TransitionAsync(
        string id,
        ActionStatus expected,
        Func<ActionRecord, ActionTransition?> decide,
        CancellationToken ct = default);

    /// <summary>Like the first overload, but the admissions are checked in the same transaction first. When one is already at its limit,
    /// <paramref name="refused"/> is given its index and the stored record, and returns the record to store instead (for example the action
    /// failed as rate-limited). Nothing is claimed in that case, so a limit is never exceeded by callers that arrive together.</summary>
    Task<ActionRecord?> TransitionAsync(
        string id,
        ActionStatus expected,
        ActionStatus next,
        Func<ActionRecord, ActionRecord> update,
        IReadOnlyList<Admission> admissions,
        Func<int, ActionRecord, ActionRecord> refused,
        CancellationToken ct = default);
}

public sealed record ActionRequest(
    string ToolName,
    string ArgumentsJson,
    ActionRequester Requester,
    string? SessionId = null,
    string? IdempotencyKey = null,
    string? ParentActionId = null)
{
    /// <summary>The caller's agent profile (see <see cref="CallerIdentity.Profile"/>). A tool outside it is refused and audited.
    /// Null: no profile, every tool policy allows.</summary>
    public string? Profile { get; init; }
}

public interface IActionQueue
{
    /// <summary>Applies policy: executes, queues, or denies. Never blocks waiting for a reviewer.</summary>
    Task<ActionRecord> SubmitAsync(ActionRequest request, CancellationToken ct = default);

    Task<ActionRecord> ReviewAsync(string actionId, ActionReview review, CancellationToken ct = default);

    Task<ActionRecord> CancelAsync(string actionId, ActionRequester requester, CancellationToken ct = default);

    /// <summary>After ChangesRequested: submits a new action linked to the old one. Same requester only.</summary>
    Task<ActionRecord> ResubmitAsync(string actionId, string? editedArgumentsJson, ActionRequester requester, CancellationToken ct = default,
        string? profile = null);
}

public interface IActionExecutor
{
    /// <summary>Runs an approved (or execute-mode) action and records the outcome.</summary>
    Task<ActionRecord> ExecuteAsync(string actionId, CancellationToken ct = default);
}

/// <summary>A decision source (human UI, AI reviewer). The queue does not care who answers.</summary>
public interface IReviewer
{
    string Name { get; }
    Task<ActionReview?> ReviewAsync(ActionRecord action, CancellationToken ct = default);
}

public sealed record AuditEntry(
    DateTimeOffset At,
    string Event,
    string? ActionId,
    string? ToolName,
    string? Actor,
    string? Detail);

/// <summary>Append-only. Implementations must not record argument values or secrets in <see cref="AuditEntry.Detail"/>.</summary>
public interface IAuditSink
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record PreviewResult(string Summary, string? DiffJson = null);

/// <summary>Optional: a tool that can describe its effect without performing it.
/// Throw <see cref="ArgumentException"/> when the arguments can never be valid: the Host then refuses the call immediately
/// (<c>invalid_arguments</c>) instead of queueing it for a reviewer.</summary>
public interface IPreviewableTool
{
    Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default);
}
