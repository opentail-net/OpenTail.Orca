namespace OpenTail.Orca.Abstractions;

/// <summary>Static facts a module declares about one tool. Never contains policy.</summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolRisk Risk,
    string ModuleId)
{
    /// <summary>The tool returns content from outside the deployment's trust boundary (mail, web, imported files).
    /// Invoking it taints the session.</summary>
    public bool ReadsUntrusted { get; init; }

    /// <summary>Calling twice with the same idempotency key has the same effect as calling once (downstream honours the key).</summary>
    public bool IdempotentByKey { get; init; }

    /// <summary>The module cannot produce a preview for this tool; reviewers are told so explicitly.</summary>
    public bool NoPreview { get; init; }

    /// <summary>True for tools whose effects reach outside the deployment or destroy data.</summary>
    public bool IsHighImpact => Risk is ToolRisk.Destructive or ToolRisk.ExternalEffect;

    /// <summary>After a successful run, the Host appends an audit entry named <see cref="ResultAudit.Event"/> whose detail is the
    /// result's text field <see cref="ResultAudit.Field"/> (an identifier such as <c>invoice-check@3</c>, never content).</summary>
    public ResultAudit? AuditResult { get; init; }

    /// <summary>An administration tool: it changes how agents behave (publishing a skill) rather than doing the business's work. The Host
    /// never grants it through a wildcard or to a credential without a profile, and requires a strict review policy for it.</summary>
    public bool Administers { get; init; }

    /// <summary>Where the result says how many records it returned (and the arguments how many recipients), for the Host's usage
    /// counters. Optional: without it the Host still counts calls and result bytes.</summary>
    public ResultCounts? Counts { get; init; }

    /// <summary>Kinds of sensitive data the tool returns (<c>finance</c>, <c>personal</c>, <c>hr</c>, <c>customer</c>, <c>secret</c>, or the
    /// operator's own). A session that calls it gains them, and outbound calls are checked against the policy's data-flow rules.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
}

/// <summary>An audit entry the Host writes from a field of a tool's result (see <see cref="ToolDescriptor.AuditResult"/>).</summary>
public sealed record ResultAudit(string Event, string Field);
