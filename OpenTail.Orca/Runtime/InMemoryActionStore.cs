namespace OpenTail.Orca;

/// <summary>Process-local store for tests and the walking skeleton. Same semantics the durable store must keep.</summary>
public sealed class InMemoryActionStore(TimeProvider? clock = null) : IActionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActionRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Requester, string Tool, string Key, string Hash), string> _byKey = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<ActionRecord> CreateAsync(ActionRecord record, CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        lock (_gate) return Task.FromResult(CreateLocked(record, [])!);
    }

    public Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, CancellationToken ct = default) =>
        TryCreateAsync(record, admissions, static _ => null, ct);

    public Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?> refused,
        CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        lock (_gate) return Task.FromResult(CreateLocked(record, admissions, refused));
    }

    private ActionRecord? CreateLocked(ActionRecord record, IReadOnlyList<Admission> admissions, Func<int, ActionRecord?>? refused = null)
    {
        if (record.IdempotencyKey is { } key)
        {
            // Scoped to the requester: someone else's key and arguments are a different action.
            var k = (record.Requester.Id, record.ToolName, key, record.ArgumentsHash);
            if (_byKey.TryGetValue(k, out var existing)) return _byId[existing];
        }
        var refusedBy = FirstRefusal(admissions);
        var toStore = refusedBy < 0 ? record : refused?.Invoke(refusedBy);
        if (toStore is null) return null;
        if (toStore.IdempotencyKey is { } key2)
            _byKey[(toStore.Requester.Id, toStore.ToolName, key2, toStore.ArgumentsHash)] = toStore.Id;
        _byId[toStore.Id] = toStore;
        return toStore;
    }

    /// <summary>The index of the first admission already at its limit, or -1. Called under the lock.</summary>
    private int FirstRefusal(IReadOnlyList<Admission> admissions)
    {
        for (var i = 0; i < admissions.Count; i++)
            if (Matching(admissions[i].Filter, admissions[i].Window).Count() >= admissions[i].MaxCount) return i;
        return -1;
    }

    private IEnumerable<ActionRecord> Matching(ActionQuery query, TimeSpan? window = null)
    {
        var since = window is { } w ? _clock.GetUtcNow() - w : (DateTimeOffset?)null;
        return _byId.Values
            .Where(r => query.Status is null || r.Status == query.Status)
            .Where(r => query.AnyOfStatus is not { Count: > 0 } any || any.Contains(r.Status))
            .Where(r => query.SessionId is null || r.SessionId == query.SessionId)
            .Where(r => query.RequesterId is null || r.Requester.Id == query.RequesterId)
            .Where(r => query.ModuleId is null || r.ModuleId == query.ModuleId)
            .Where(r => query.ToolName is null || r.ToolName == query.ToolName)
            .Where(r => query.UpdatedSince is null || r.UpdatedAt >= query.UpdatedSince)
            .Where(r => query.CreatedBefore is null || r.CreatedAt < query.CreatedBefore)
            .Where(r => since is null || r.UpdatedAt >= since);
    }

    public Task<int> CountAsync(ActionQuery query, TimeSpan? window = null, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Matching(query, window).Count());
    }

    public Task<ActionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_byId.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<ActionRecord>> QueryAsync(ActionQuery query, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var matched = Matching(query);
            IReadOnlyList<ActionRecord> rows = (query.NewestFirst
                    ? matched.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id, StringComparer.Ordinal)
                    : matched.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal))
                .Take(Math.Max(1, query.Limit))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, ActionStatus next,
        Func<ActionRecord, ActionRecord> update, CancellationToken ct = default) =>
        TransitionAsync(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, ct);

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, ActionStatus next, Func<ActionRecord, ActionRecord> update,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord> refused, CancellationToken ct = default) =>
        TransitionCore(id, expected, current =>
            ActionStateMachine.CanTransition(expected, next, current.Error?.Retryable ?? false)
                ? new ActionTransition(next, update(current))
                : null, admissions, refused);

    public Task<ActionRecord?> TransitionAsync(
        string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide, CancellationToken ct = default) =>
        TransitionCore(id, expected, decide, [], null);

    private Task<ActionRecord?> TransitionCore(string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide,
        IReadOnlyList<Admission> admissions, Func<int, ActionRecord, ActionRecord>? refused)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var current) || current.Status != expected)
                return Task.FromResult<ActionRecord?>(null);
            var refusedBy = FirstRefusal(admissions);
            ActionTransition? t;
            if (refusedBy >= 0)
            {
                if (refused is null) return Task.FromResult<ActionRecord?>(null);
                var alt = refused(refusedBy, current);
                t = new ActionTransition(alt.Status, alt);
            }
            else t = decide(current);
            if (t is null) return Task.FromResult<ActionRecord?>(null);
            if (!ActionStateMachine.CanTransition(expected, t.Next, current.Error?.Retryable ?? false))
                return Task.FromResult<ActionRecord?>(null);
            var updated = t.Record with { Status = t.Next, UpdatedAt = _clock.GetUtcNow() };
            _byId[id] = updated;
            return Task.FromResult<ActionRecord?>(updated);
        }
    }
}

public sealed class InMemoryAuditSink : IAuditSink, IAuditReader, IAuditQuery
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IReadOnlyList<AuditEntry> Read(string? actionId = null)
    {
        lock (_entries) return _entries.Where(e => actionId is null || e.ActionId == actionId).ToList();
    }

    // The sequence number of an entry is its place in the trail, which only grows: entries are never removed.
    public IReadOnlyList<AuditRow> Page(AuditQuery query)
    {
        lock (_entries)
        {
            var rows = new List<AuditRow>();
            for (var i = _entries.Count - 1; i >= 0 && rows.Count < query.Limit; i--)
            {
                var seq = i + 1L;
                if (query.Before is { } before && seq >= before) continue;
                if (Matches(_entries[i], query)) rows.Add(new AuditRow(seq, _entries[i]));
            }
            return rows;
        }
    }

    public long Count(AuditQuery query)
    {
        lock (_entries) return _entries.LongCount(e => Matches(e, query));
    }

    public IReadOnlyList<string> Actors(string evt)
    {
        lock (_entries)
            return _entries.Where(e => e.Event == evt && e.Actor is not null).Select(e => e.Actor!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
    }

    private static bool Matches(AuditEntry e, AuditQuery q) =>
        (q.Event is null || e.Event == q.Event) && (q.Actor is null || e.Actor == q.Actor)
        && (q.DetailStartsWith is null || (e.Detail?.StartsWith(q.DetailStartsWith, StringComparison.Ordinal) ?? false))
        && (q.Since is null || e.At >= q.Since);

    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        lock (_entries) _entries.Add(entry);
        return Task.CompletedTask;
    }
}
