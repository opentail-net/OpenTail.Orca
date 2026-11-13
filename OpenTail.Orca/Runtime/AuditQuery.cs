namespace OpenTail.Orca;

/// <summary>
/// A filter over the audit trail, for pages and counts. Every field is optional; an empty query matches the whole trail. Filters are applied in
/// the store, so a page costs what it returns, not the size of the trail.
/// </summary>
public sealed record AuditQuery
{
    /// <summary>Only entries of this event (for example <c>ai_review</c>).</summary>
    public string? Event { get; init; }

    /// <summary>Only entries whose actor is exactly this (for example <c>ai:bot</c>).</summary>
    public string? Actor { get; init; }

    /// <summary>Only entries whose detail starts with this text (for example <c>Approve;</c>).</summary>
    public string? DetailStartsWith { get; init; }

    /// <summary>Only entries at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only entries with a sequence number below this: the cursor of the page before. Null starts from the newest.</summary>
    public long? Before { get; init; }

    /// <summary>The most entries a page returns (<see cref="Page"/> only; <see cref="IAuditQuery.Count"/> ignores it).</summary>
    public int Limit { get; init; } = 20;
}

/// <summary>An audit entry with its sequence number. The sequence number is the page cursor: it only grows, and it never changes.</summary>
public sealed record AuditRow(long Seq, AuditEntry Entry);

/// <summary>
/// Filtered, paged and counted reads of the audit trail. Implemented where the trail is kept, so a query does not load the whole trail
/// (<see cref="IAuditReader.Read"/> does). Both read methods return what matches; neither changes anything.
/// </summary>
public interface IAuditQuery
{
    /// <summary>At most <see cref="AuditQuery.Limit"/> matching entries, newest first, each with its sequence number.</summary>
    IReadOnlyList<AuditRow> Page(AuditQuery query);

    /// <summary>How many entries match (ignores <see cref="AuditQuery.Before"/> and <see cref="AuditQuery.Limit"/>).</summary>
    long Count(AuditQuery query);

    /// <summary>The distinct actors that have an entry of this event, in order.</summary>
    IReadOnlyList<string> Actors(string evt);
}
