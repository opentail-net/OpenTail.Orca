namespace OpenTail.Host.Abstractions;

public enum ToolMode
{
    /// <summary>Safest value, and the default for any tool without a policy entry.</summary>
    Deny = 0,
    /// <summary>Hold for a reviewer decision.</summary>
    Queue = 1,
    /// <summary>Run immediately (still audited).</summary>
    Execute = 2,
}

/// <summary>May the person (or the person's agent) who asked also approve? Allow is the default: the user of an assistant is normally the right approver.</summary>
public enum SelfApproval
{
    Allow = 0,
    /// <summary>Four eyes: the approver must be someone other than the requester (judged through the policy's People map).</summary>
    Forbid = 1,
}

public enum ReviewerKind
{
    Human = 0,
    Ai = 1,
    Either = 2,
}

/// <summary>Per-hour / per-day ceilings. Zero means no limit for that window.</summary>
public sealed record RateLimit(int PerHour = 0, int PerDay = 0);

/// <summary>
/// Optional trust ramp. After <see cref="AfterCleanApprovals"/> consecutive approvals with no edits and no failures,
/// the Host reports the tool as eligible for promotion to Execute. Promotion is always an explicit operator act.
/// Zero disables the ramp.
/// </summary>
public sealed record TrustRamp(int AfterCleanApprovals = 0)
{
    /// <summary>When promoted, run with this undo window instead of at once: the middle rung between Queue and Execute.</summary>
    public TimeSpan? DelayFor { get; init; }
}

/// <summary>Operator-supplied configuration for one tool. Comes only from configuration, never from arguments or model output.</summary>
public sealed record ToolPolicy
{
    public ToolMode Mode { get; init; } = ToolMode.Deny;
    public ReviewerKind Reviewer { get; init; } = ReviewerKind.Human;
    public TimeSpan? ExpireAfter { get; init; }
    public RateLimit? RateLimit { get; init; }
    public TrustRamp? TrustRamp { get; init; }

    /// <summary>Only reviewers carrying this group may decide the action. Null: any reviewer.</summary>
    public string? ReviewerGroup { get; init; }

    /// <summary>Distinct approvals needed before the action runs.</summary>
    public int RequiredApprovals { get; init; } = 1;

    public SelfApproval SelfApproval { get; init; } = SelfApproval.Allow;

    /// <summary>May a reviewer change the arguments when approving? False: approve or reject what was asked, nothing else (the agent can
    /// be sent back to resubmit). Required to be false for administration tools.</summary>
    public bool AllowEdits { get; init; } = true;

    /// <summary>Execute with an undo window: an Execute call is announced and runs after this long unless someone stops it. Only for tools
    /// above Read; taint and Queue rules still queue. See <see cref="Delay"/> for the default length.</summary>
    public TimeSpan? DelayFor { get; init; }

    /// <summary>Execute with the default undo window (<see cref="DefaultDelay"/>) when no <see cref="DelayFor"/> is given.</summary>
    public bool Delay { get; init; }

    /// <summary>The undo window when a policy asks for one without a length (owner's decision, 2026-10-18).</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(10);

    /// <summary>When the undo window would end outside the policy's <c>WorkingHours</c>, run at the next opening plus the window instead,
    /// so someone is around to stop it. Opt-in per tool (owner's decision).</summary>
    public bool DelayUntilWorkingHours { get; init; }

    /// <summary>The undo window this policy asks for, or null for none.</summary>
    public TimeSpan? EffectiveDelay => DelayFor ?? (Delay ? DefaultDelay : null);

    /// <summary>Treat this tool as an administration tool (see <see cref="ToolDescriptor.Administers"/>) although its module does not.
    /// Only tightens: setting it false never unmarks a module's administration tool.</summary>
    public bool Administers { get; init; }

    /// <summary>Labels the operator adds to what this tool returns (see <see cref="ToolDescriptor.Labels"/>). Only adds: a module's labels stay.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>The highest risk an AI reviewer may decide for this tool (when <see cref="Reviewer"/> lets one). Null means
    /// <see cref="ToolRisk.Write"/>. <see cref="ToolRisk.ExternalEffect"/> must be set explicitly; <see cref="ToolRisk.Destructive"/> is
    /// never decided by an AI, whatever this says.</summary>
    public ToolRisk? AiMaxRisk { get; init; }

    /// <summary>After this many AI rejections or change requests in one resubmission chain, only people decide. Zero turns the limit off.</summary>
    public int AiMaxRejections { get; init; } = 2;

    /// <summary>A ceiling on approvals by AI reviewers for this tool, per requester (or across everyone); past it, people decide.</summary>
    public RuleBudget? AiBudget { get; init; }

    /// <summary>Conditional refinements (see <see cref="PolicyRule"/>). Not copied into the per-action policy snapshot.</summary>
    public IReadOnlyList<PolicyRule>? Rules { get; init; }

    public static ToolPolicy Denied { get; } = new();

    /// <summary>Combining rules can only tighten: Deny is stricter than Queue, which is stricter than Execute.</summary>
    public static ToolMode Stricter(ToolMode a, ToolMode b) => (ToolMode)Math.Min((int)a, (int)b);
}
