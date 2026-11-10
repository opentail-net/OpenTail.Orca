namespace OpenTail.Host;

/// <summary>
/// A store that can commit an action's transition together with the audit entry that records it, in one transaction. Only the SQLite
/// store implements it, and the executor uses it only when the audit trail is that same database. Then an outcome cannot be recorded
/// without its entry, nor an entry without its outcome. Otherwise the entry is appended after the transition, as it always was.
/// </summary>
internal interface IAtomicAuditStore
{
    /// <summary>Applies the transition if the status is still <paramref name="expected"/>, and appends <paramref name="audit"/> of the updated
    /// record in the same transaction. Throws if the entry cannot be written: then the transition is not applied either.</summary>
    Task<ActionRecord?> TransitionAuditedAsync(string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default);
}
