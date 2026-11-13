using System.Text.Json.Serialization;

namespace OpenTail.Orca.Abstractions;

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

    /// <summary>A held scheduled approval is withdrawn: it stops counting, and the action needs a new approval to be scheduled again. Recorded by
    /// the system when a run is held (late, or its requester suspended), never submitted by a reviewer.</summary>
    Withdraw = 4,
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

    /// <summary>The binding digest (<see cref="ActionBinding"/>) of the action with <see cref="EditedArgumentsJson"/> in place of the
    /// submitted arguments, recorded with the review as <see cref="EditedArgumentsHash"/> is. The claim recomputes the binding and runs the
    /// edit only if it still matches. Null for a review without an edit, for a tool that is not loaded or arguments that are not JSON, and
    /// for reviews stored before this was recorded (such an edit is refused at the claim).</summary>
    public string? EditedBindingDigest { get; init; }

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

/// <summary>The outcomes an <see cref="ActionResolution"/> can have.</summary>
public static class ResolutionOutcomes
{
    /// <summary>Approved (or its undo window elapsed with nobody stopping it), and claimed to run.</summary>
    public const string Allowed = "allowed";

    /// <summary>Rejected by a reviewer.</summary>
    public const string Denied = "denied";

    /// <summary>Not decided in time, or its module was switched off before it was decided.</summary>
    public const string Expired = "expired";

    /// <summary>Stopped before it was decided or ran: by its requester, by a person allowed to review it, or by an operator.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Approved, but the approval no longer held at its claim, so nothing ran.</summary>
    public const string ExecutionDenied = "execution_denied";
}

/// <summary>
/// How an action was finally resolved: what was approved or refused, under which policy, with which binding digest, why, when and by
/// whom. Written in the same write as the status change it describes, so the two cannot disagree. It holds no arguments and no results.
/// </summary>
/// <param name="Outcome">One of <see cref="ResolutionOutcomes"/>.</param>
/// <param name="Resolver">The reviewer's subject (their display name when there is no subject), the requester or operator who stopped it,
/// or <c>system</c>.</param>
/// <param name="At">When the resolution was made.</param>
/// <param name="PolicyVersion">The policy hash the action was decided under.</param>
/// <param name="BindingDigest">The binding the approval held for: the last reviewer edit's, else the submitted action's. Null when none was
/// recorded (a tool that is not loaded, or arguments that are not JSON).</param>
/// <param name="ReasonCode">Why, as a short code such as <c>approval_expired</c>; null when there is no code.</param>
public sealed record ActionResolution(string Outcome, string Resolver, DateTimeOffset At, string? PolicyVersion, string? BindingDigest,
    string? ReasonCode);

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

    /// <summary>SHA-256 binding over the whole action (<c>sha256:</c>): operation, requester, tool and its schema version, arguments
    /// (RFC 8785) and policy version. Set when the action is created. Null when the tool is unknown or the arguments are not JSON.
    /// <see cref="ArgumentsHash"/> is unchanged and still covers the raw argument text.</summary>
    public string? BindingDigest { get; init; }

    /// <summary>How the action was finally resolved (see <see cref="ActionResolution"/>). Null while it is open, for an action decided by policy
    /// alone (a denial at submission, or one that ran without a reviewer), and for records stored before this was recorded.</summary>
    public ActionResolution? Resolution { get; init; }

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

    /// <summary>The binding the approval holds for: the last reviewer edit's binding if there is an edit, else <see cref="BindingDigest"/>.
    /// The claim checks it against the binding it recomputes. Derived, so it is not stored.</summary>
    [JsonIgnore]
    public string? EffectiveBindingDigest =>
        Reviews.LastOrDefault(r => r.EditedArgumentsJson is not null) is { } edit ? edit.EditedBindingDigest : BindingDigest;

    /// <summary>This record with its resolution set: the outcome, who resolved it, the reason code and the time, under the policy and binding
    /// the action holds. Call it inside the write of the change it describes, so the status and its resolution are written together.</summary>
    public ActionRecord WithResolution(string outcome, string resolver, string? reasonCode, DateTimeOffset at) =>
        this with { Resolution = new ActionResolution(outcome, resolver, at, PolicyHash, EffectiveBindingDigest, reasonCode) };
}
