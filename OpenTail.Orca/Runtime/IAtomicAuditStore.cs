namespace OpenTail.Orca;

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

    /// <summary>Like <see cref="TransitionAuditedAsync(string, ActionStatus, ActionStatus, Func{ActionRecord, ActionRecord}, Func{ActionRecord, AuditEntry}, CancellationToken)"/>,
    /// with admissions checked in the same transaction. A refusal stores the record <paramref name="refused"/> gives, and the entry is built from that record.</summary>
    Task<ActionRecord?> TransitionAdmittedAuditedAsync(string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord> refused, Func<ActionRecord, AuditEntry> audit,
        CancellationToken ct = default);

    /// <summary>Like <see cref="TryCreateAuditedAsync"/>, with a refusal storing what <paramref name="refused"/> gives (null: nothing is stored),
    /// and the entry built from the record that was stored, in the same transaction.</summary>
    Task<ActionRecord?> TryCreateAdmittedAuditedAsync(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused,
        Func<ActionRecord, AuditEntry> audit, CancellationToken ct = default);
}

/// <summary>Decides whether a change and its audit entry can be committed together.</summary>
internal static class AtomicTrail
{
    /// <summary>The store as an atomic one, when it can commit an entry with a change and the trail is that same database; otherwise null.</summary>
    public static IAtomicAuditStore? For(IActionStore store, IAuditSink audit) =>
        store is IAtomicAuditStore atomic && audit is SqliteAuditSink sink && ReferenceEquals(sink.Store, store) ? atomic : null;

    /// <summary>A change to an action and the audit entry that records it: one transaction when the trail is the store's own database, so the
    /// change cannot happen without its entry, nor the entry without the change. Otherwise the change is applied, then its entry is written.</summary>
    public static async Task<ActionRecord?> ChangeAsync(IActionStore store, IAuditSink audit, string id, ActionStatus expected, ActionStatus next,
        Func<ActionRecord, ActionRecord> update, Func<ActionRecord, AuditEntry> entry, CancellationToken ct)
    {
        if (For(store, audit) is { } atomic)
            return await atomic.TransitionAuditedAsync(id, expected, next, update, entry, ct);
        var done = await store.TransitionAsync(id, expected, next, update, ct);
        if (done is not null) await audit.AppendAsync(entry(done), ct);
        return done;
    }

    /// <summary>As <see cref="ChangeAsync"/>, for a change whose next status is decided inside the store's transaction.</summary>
    public static async Task<ActionRecord?> DecideAsync(IActionStore store, IAuditSink audit, string id, ActionStatus expected,
        Func<ActionRecord, ActionTransition?> decide, Func<ActionRecord, AuditEntry> entry, CancellationToken ct)
    {
        if (For(store, audit) is { } atomic)
            return await atomic.TransitionDecidedAuditedAsync(id, expected, decide, entry, ct);
        var done = await store.TransitionAsync(id, expected, decide, ct);
        if (done is not null) await audit.AppendAsync(entry(done), ct);
        return done;
    }

    /// <summary>Creates an action and writes its entry in the same transaction when the trail is the store's own database. An idempotent repeat
    /// returns the existing action and writes nothing, since nothing changed; a refusal returns null and writes nothing.</summary>
    public static async Task<ActionRecord?> CreateAsync(IActionStore store, IAuditSink audit, ActionRecord record,
        IReadOnlyList<Admission> admissions, Func<ActionRecord, AuditEntry> entry, CancellationToken ct)
    {
        if (For(store, audit) is { } atomic)
            return await atomic.TryCreateAuditedAsync(record, admissions, entry, ct);
        var created = admissions.Count > 0 ? await store.TryCreateAsync(record, admissions, ct) : await store.CreateAsync(record, ct);
        if (created is not null && created.Id == record.Id) await audit.AppendAsync(entry(created), ct);
        return created;
    }

    /// <summary>A change to an action with admissions (a rate limit), and the audit entry that records it: one transaction when the trail is the
    /// store's own database. A refusal stores the record it names, and the entry records that.</summary>
    public static async Task<ActionRecord?> ChangeAdmittedAsync(IActionStore store, IAuditSink audit, string id, ActionStatus expected,
        ActionStatus next, Func<ActionRecord, ActionRecord> update, IReadOnlyList<Admission> admissions,
        Func<int, ActionRecord, ActionRecord> refused, Func<ActionRecord, AuditEntry> entry, CancellationToken ct)
    {
        if (For(store, audit) is { } atomic)
            return await atomic.TransitionAdmittedAuditedAsync(id, expected, next, update, admissions, refused, entry, ct);
        var done = await store.TransitionAsync(id, expected, next, update, admissions, refused, ct);
        if (done is not null) await audit.AppendAsync(entry(done), ct);
        return done;
    }

    /// <summary>Creates an action, subject to admissions, and writes the entry of what was stored in the same transaction when the trail is the
    /// store's own database. A refusal stores what <paramref name="refused"/> gives, or nothing for null.</summary>
    public static async Task<ActionRecord?> CreateAdmittedAsync(IActionStore store, IAuditSink audit, ActionRecord record,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused, Func<ActionRecord, AuditEntry> entry, CancellationToken ct)
    {
        if (For(store, audit) is { } atomic)
            return await atomic.TryCreateAdmittedAuditedAsync(record, admissions, refused, entry, ct);
        var created = await store.TryCreateAsync(record, admissions, refused, ct);
        if (created is not null && created.Id == record.Id) await audit.AppendAsync(entry(created), ct);
        return created;
    }

    /// <summary>The entry of a change the executor records, stamped with the record's own update time.</summary>
    public static Func<ActionRecord, AuditEntry> Entry(string evt, string? detail) =>
        d => new AuditEntry(d.UpdatedAt, evt, d.Id, d.ToolName, d.Requester.Id, detail);

    /// <summary>As <see cref="Entry(string, string?)"/>, with the detail worked out from the record as committed.</summary>
    public static Func<ActionRecord, AuditEntry> EntryWith(string evt, Func<ActionRecord, string?> detail) =>
        d => new AuditEntry(d.UpdatedAt, evt, d.Id, d.ToolName, d.Requester.Id, detail(d));
}
