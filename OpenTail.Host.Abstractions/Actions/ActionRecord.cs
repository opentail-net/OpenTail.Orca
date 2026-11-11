namespace OpenTail.Host.Abstractions;

public enum RequesterKind
{
    AiSession = 0,
    User = 1,
    System = 2,
}

public sealed record ActionRequester(RequesterKind Kind, string Id);

/// <param name="Retryable">A reviewer may retry the failed action (and the Host may, for transient failures of tools idempotent by key).</param>
public sealed record ActionError(string Code, string Message, bool Retryable)
{
    /// <summary>What kind of failure this was. Null on records written before failure kinds existed; read it with
    /// <see cref="EffectiveKind"/>.</summary>
    public ToolFailureKind? Kind { get; init; }

    /// <summary>How long the tool asked callers to wait (a <c>Retry-After</c>), when it said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The kind, or one inferred from the code for older records: <c>tool_unavailable</c>, <c>rate_limited</c> and
    /// <c>slow_down</c> are transient, <c>interrupted</c> an unknown outcome, guardrails and refusals policy, anything else a bug.</summary>
    public ToolFailureKind EffectiveKind => Kind ?? Code switch
    {
        "tool_unavailable" or "rate_limited" or "slow_down" => ToolFailureKind.Transient,
        "interrupted" => ToolFailureKind.UnknownOutcome,
        "guardrail_blocked" or "guardrail_tripwire" or "denied" or "not_in_profile" or "suspended" => ToolFailureKind.Policy,
        "invalid_arguments" => ToolFailureKind.Contract,
        _ => ToolFailureKind.Bug,
    };
}

public enum ReviewDecision
{
    Approve = 0,
    Reject = 1,
    RequestChanges = 2,
    Retry = 3,
}

public sealed record ActionReview(
    string Reviewer,
    ReviewDecision Decision,
    string? Reason,
    DateTimeOffset At)
{
    /// <summary>Arguments the reviewer set; the originally submitted arguments are never overwritten.</summary>
    public string? EditedArgumentsJson { get; init; }

    /// <summary>The argument digest of <see cref="EditedArgumentsJson"/>, recorded when the review is stored. The claim runs the edited
    /// arguments only if they still match it. Null for a review without an edit, and for reviews stored before this was recorded.</summary>
    public string? EditedArgumentsHash { get; init; }

    /// <summary>The namespaced subject (see <see cref="Subjects"/>) the server authenticated, never taken from a request body.
    /// <see cref="Reviewer"/> is the display name.</summary>
    public string? Subject { get; init; }

    /// <summary>Reviewer groups the server authenticated for this reviewer (for example "finance").</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>An AI reviewer: the model that decided (for the audit trail).</summary>
    public string? AiModel { get; init; }

    /// <summary>An AI reviewer: the version of the instructions it decided under.</summary>
    public string? AiPromptVersion { get; init; }

    /// <summary>An AI reviewer: the checks it says it made, in its own words.</summary>
    public IReadOnlyList<string>? AiChecks { get; init; }

    /// <summary>An AI reviewer: SHA-256 of the action exactly as the Host handed it over (what the reviewer saw, besides its own
    /// instructions, which <see cref="AiPromptVersion"/> names). Set by the Host, never by the reviewer.</summary>
    public string? AiInputHash { get; init; }

    /// <summary>An AI reviewer: the input itself, when the policy keeps it (<c>AiReviewInput: Full</c>). Held with the action, purged
    /// with it.</summary>
    public string? AiInput { get; init; }
}

/// <summary>One check in the decision on a call, in the order the checks ran: what it concluded, on what evidence (never argument
/// values), and whether it changed the outcome so far.</summary>
public sealed record DecisionStep(string Check, string Result, string? Evidence = null, bool Changed = false);

/// <summary>The primary record of the queue. One per tool call that reached the Host. Session is optional.</summary>
public sealed record ActionRecord
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string ModuleId { get; init; }

    /// <summary>Version of the module that was loaded when the action was submitted (from its assembly), so an audit can tell which code ran.</summary>
    public string? ModuleVersion { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string ArgumentsHash { get; init; }
    public required ActionRequester Requester { get; init; }
    public required ActionStatus Status { get; init; }
    public required ToolMode PolicyMode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public int SchemaVersion { get; init; } = HostContract.ActionSchemaVersion;

    /// <summary>Host-minted session handle; null for sessionless actions (scripts, imports, admin UI).</summary>
    public string? SessionId { get; init; }

    public string? IdempotencyKey { get; init; }

    /// <summary>The previous action this one resubmits (after ChangesRequested).</summary>
    public string? ParentActionId { get; init; }

    /// <summary>Policy as it was when queued; later configuration changes do not alter it.</summary>
    public ToolPolicy? PolicySnapshot { get; init; }

    /// <summary>The session had read untrusted content (or was sessionless) when this was submitted.</summary>
    public bool Tainted { get; init; }

    public string? PreviewJson { get; init; }

    /// <summary>Which conditional rule (policy) decided this action, if one did.</summary>
    public string? PolicyRule { get; init; }

    /// <summary>Every check the call passed, in order, written once when the decision was made. Empty for actions recorded before
    /// decision traces existed.</summary>
    public IReadOnlyList<DecisionStep> DecisionTrace { get; init; } = [];

    /// <summary>SHA-256 of the whole effective policy the action was decided under (<c>sha256:</c> and 64 hex digits).</summary>
    public string? PolicyHash { get; init; }

    /// <summary>Only reviewers carrying this group may decide the action. Null: any reviewer.</summary>
    public string? ReviewerGroup { get; init; }

    /// <summary>Distinct approvals needed before the action runs (default 1).</summary>
    public int RequiredApprovals { get; init; } = 1;

    /// <summary>High-impact tool with no preview: the reviewer UI must say so rather than imply a safe-looking summary.</summary>
    public bool PreviewUnavailable { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int Attempts { get; init; }

    /// <summary>While Executing: when the executor's claim lapses. A stale lease means the process died mid-run.</summary>
    public DateTimeOffset? LeaseUntil { get; init; }
    public string? ResultJson { get; init; }

    /// <summary>Size of <see cref="ResultJson"/> in UTF-8 bytes, measured when the action succeeded.</summary>
    public long? ResultBytes { get; init; }

    /// <summary>What the successful result touched (records, recipients, distinct entities), from the tool's
    /// <see cref="ToolDescriptor.Counts"/>. Counts only: entity keys are never stored here.</summary>
    public ResultStats? ResultStats { get; init; }

    /// <summary>The requester's agent profile when the action was submitted (see <see cref="CallerIdentity.Profile"/>).</summary>
    public string? Profile { get; init; }

    /// <summary>For the reviewer: the labels the session held when this was submitted, with the tools they came from
    /// (<c>finance (erp.query)</c>). Empty when none.</summary>
    public IReadOnlyList<string> SessionLabels { get; init; } = [];

    /// <summary>Execute with an undo window: the action is Pending until this time, then runs unless someone stopped it. Null for an
    /// ordinary queued action, which waits for a reviewer.</summary>
    public DateTimeOffset? RunAt { get; init; }

    public ActionError? Error { get; init; }

    /// <summary>A failed action the Host will try again by itself at this time: a transient failure of a tool idempotent by key, or a
    /// call held because its route was paused. Null when nothing is scheduled.</summary>
    public DateTimeOffset? RetryAt { get; init; }
    public IReadOnlyList<ActionReview> Reviews { get; init; } = [];

    /// <summary>Arguments to run: the last reviewer-edited arguments if any, else as submitted.</summary>
    public string EffectiveArgumentsJson =>
        Reviews.LastOrDefault(r => r.EditedArgumentsJson is not null)?.EditedArgumentsJson ?? ArgumentsJson;
}
