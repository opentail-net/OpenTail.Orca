namespace OpenTail.Orca.Abstractions;

/// <summary>
/// Where a tool's result (and arguments) say how much it touched, so the Host can count records, recipients and distinct business
/// records per identity without reading content. Paths are JSON Pointers (<c>/rows</c>); in <see cref="EntityKeys"/> a <c>*</c>
/// segment stands for every item of an array (<c>/rows/*/customer_id</c>). Declared by the module on its descriptor, like
/// <see cref="ToolDescriptor.AuditResult"/>; the Host measures, the module runs no extra code.
/// </summary>
public sealed record ResultCounts
{
    /// <summary>Into the result: an array (its length is the record count) or a whole number.</summary>
    public string? Records { get; init; }

    /// <summary>Into the result: the stable ids of the business records returned (customer id, invoice number). The Host never stores
    /// them as given: it keeps a salted hash per deployment, only to count distinct values.</summary>
    public string? EntityKeys { get; init; }

    /// <summary>Into the arguments: the addresses an outbound call reaches. An array counts each item; a string counts one.</summary>
    public IReadOnlyList<string> RecipientArgs { get; init; } = [];
}

/// <summary>What one successful action touched, measured by the Host from <see cref="ResultCounts"/>. No values, only counts.</summary>
public sealed record ResultStats(int? Records = null, int? Recipients = null, int? Entities = null);
