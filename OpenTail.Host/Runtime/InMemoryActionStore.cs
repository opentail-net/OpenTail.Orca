namespace OpenTail.Host;

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

    public Task<ActionRecord?> TryCreateAsync(ActionRecord record, IReadOnlyList<Admission> admissions, CancellationToken ct = default)
    {
        if (!ActionStateMachine.IsValidInitial(record.Status))
            throw new InvalidOperationException($"{record.Status} is not a valid initial status.");
        lock (_gate) return Task.FromResult(CreateLocked(record, admissions));
    }

    private ActionRecord? CreateLocked(ActionRecord record, IReadOnlyList<Admission> admissions)
    {
        if (record.IdempotencyKey is { } key)
        {
            // Scoped to the requester: someone else's key and arguments are a different action.
            var k = (record.Requester.Id, record.ToolName, key, record.ArgumentsHash);
            if (_byKey.TryGetValue(k, out var existing)) return _byId[existing];
        }
        foreach (var a in admissions)
            if (Matching(a.Filter, a.Window).Count() >= a.MaxCount) return null;
        if (record.IdempotencyKey is { } key2)
            _byKey[(record.Requester.Id, record.ToolName, key2, record.ArgumentsHash)] = record.Id;
        _byId[record.Id] = record;
        return record;
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
        string id, ActionStatus expected, Func<ActionRecord, ActionTransition?> decide, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var current) || current.Status != expected)
                return Task.FromResult<ActionRecord?>(null);
            if (decide(current) is not { } t) return Task.FromResult<ActionRecord?>(null);
            if (!ActionStateMachine.CanTransition(expected, t.Next, current.Error?.Retryable ?? false))
                return Task.FromResult<ActionRecord?>(null);
            var updated = t.Record with { Status = t.Next, UpdatedAt = _clock.GetUtcNow() };
            _byId[id] = updated;
            return Task.FromResult<ActionRecord?>(updated);
        }
    }
}

public sealed class InMemoryAuditSink : IAuditSink, IAuditReader
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

    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        lock (_entries) _entries.Add(entry);
        return Task.CompletedTask;
    }
}
