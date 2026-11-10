namespace OpenTail.Host;

/// <summary>
/// A store that can commit a change to an action together with the audit entry that records it, in one transaction. Only the SQLite store
/// implements it, and the Host uses it only when the audit trail is that same database (see <see cref="AtomicTrail"/>). Then a change cannot
/// be recorded without its entry, nor an entry without its change. Otherwise the entry is appended after the change, as it always was.
/// </summary>
internal interface IAtomicAuditStore
{
    /// <summary>Applies the update if the status is still <paramref name="expected"/> and the transition is legal, and appends the entry built
    /// from the updated record in the same transaction. Throws if the entry cannot be written: then the change is not applied either.</summary>
    Task<ActionRecord?> TransitionAuditedAsync(string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default);

    /// <summary>Like <see cref="TransitionAuditedAsync(string, ActionStatus, ActionStatus, Func{ActionRecord, ActionRecord}, Func{ActionRecord, AuditEntry}, CancellationToken)"/>,
    /// but the next status is decided inside the transaction (for example "is this the n-th distinct approval?"). Null from <paramref name="decide"/> writes nothing.</summary>
    Task<ActionRecord?> TransitionDecidedAuditedAsync(string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default);

    /// <summary>Creates the record if the admissions allow it, and appends the entry for the created record in the same transaction. An idempotent
    /// repeat returns the existing record and writes nothing; a refusal returns null and writes nothing.</summary>
    Task<ActionRecord?> TryCreateAuditedAsync(ActionRecord record, IReadOnlyList<Admission> admissions, Func<ActionRecord, AuditEntry> audit,
        CancellationToken ct = default);
}

/// <summary>Decides whether a change and its audit entry can be committed together.</summary>
internal static class AtomicTrail
{
    /// <summary>The store as an atomic one, when it can commit an entry with a change and the trail is that same database; otherwise null.</summary>
    public static IAtomicAuditStore? For(IActionStore store, IAuditSink audit) =>
        store is IAtomicAuditStore atomic && audit is SqliteAuditSink sink && ReferenceEquals(sink.Store, store) ? atomic : null;
}
