using System.Text.Json;

namespace OpenTail.Orca.Abstractions;

public enum BudgetScope
{
    /// <summary>Counted per requester (the signed-in person or agent).</summary>
    Requester = 0,
    /// <summary>Counted across everyone.</summary>
    Global = 1,
}

/// <summary>An Execute rule stops matching once <see cref="MaxCount"/> executions of the tool happened within <see cref="Per"/>;
/// the call then waits for a reviewer instead. Counted in the same transaction that creates the action, so it holds across processes.</summary>
public sealed record RuleBudget
{
    public TimeSpan Per { get; init; }
    public int MaxCount { get; init; }
    public BudgetScope Scope { get; init; } = BudgetScope.Requester;
}

/// <summary>
/// A conditional refinement of a tool's policy, written by the operator in the policy file.
/// <para>A rule can tighten (<c>Queue</c>, <c>Deny</c>) or, for a tool whose base mode is <c>Queue</c>, relax to <c>Execute</c>.
/// <c>Deny</c> and <c>Queue</c> rules apply when their condition is true <i>or unknown</i>; an <c>Execute</c> rule applies only when its
/// condition is definitely true and it accounts for every argument in the call.</para>
/// </summary>
public sealed record PolicyRule
{
    public required string Name { get; init; }
    public ToolMode Mode { get; init; } = ToolMode.Queue;

    /// <summary>The condition (a JSON predicate tree). Compiled and validated when the Host starts.</summary>
    public JsonElement When { get; init; }

    /// <summary>Execute rules only: top-level arguments the rule does not need to look at (everything else must be referenced by the condition).</summary>
    public IReadOnlyList<string>? IgnoreArgs { get; init; }

    /// <summary>Execute rules only.</summary>
    public RuleBudget? Budget { get; init; }

    /// <summary>Execute rules only: run the matching calls after this undo window instead of at once (see <c>ToolPolicy.DelayFor</c>).
    /// Not together with a <see cref="Budget"/>.</summary>
    public TimeSpan? DelayFor { get; init; }

    /// <summary>For a rule that queues: who may decide it, how many must approve, how long it waits.</summary>
    public ReviewerKind? Reviewer { get; init; }
    public string? ReviewerGroup { get; init; }
    public int? RequiredApprovals { get; init; }
    public TimeSpan? ExpireAfter { get; init; }
}
