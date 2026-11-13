using System.Collections.Concurrent;

namespace OpenTail.Orca;

public sealed record SessionRow(string Id, string Owner, bool Tainted, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

/// <summary>Where sessions and the per-subject taint live. SQLite in a real deployment (so a restart cannot launder taint).</summary>
public interface ISessionStore
{
    void Create(SessionRow row);
    SessionRow? Get(string id);
    void Touch(string id, DateTimeOffset at);
    void MarkTainted(string id);
    void TaintSubject(string subject, DateTimeOffset until);
    DateTimeOffset? SubjectTaintedUntil(string subject);

    /// <summary>Deletes sessions idle since before <paramref name="idleBefore"/> and subject taints that lapsed before <paramref name="now"/>.</summary>
    int Purge(DateTimeOffset idleBefore, DateTimeOffset now);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionRow> _rows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _subjects = new(StringComparer.Ordinal);

    public void Create(SessionRow row) => _rows[row.Id] = row;
    public SessionRow? Get(string id) => _rows.GetValueOrDefault(id);

    public void Touch(string id, DateTimeOffset at)
    {
        if (_rows.TryGetValue(id, out var r)) _rows[id] = r with { LastSeenAt = at };
    }

    public void MarkTainted(string id)
    {
        if (_rows.TryGetValue(id, out var r)) _rows[id] = r with { Tainted = true };
    }

    public void TaintSubject(string subject, DateTimeOffset until) =>
        _subjects.AddOrUpdate(subject, until, (_, old) => old > until ? old : until);

    public DateTimeOffset? SubjectTaintedUntil(string subject) => _subjects.TryGetValue(subject, out var u) ? u : null;

    public int Purge(DateTimeOffset idleBefore, DateTimeOffset now)
    {
        var n = 0;
        foreach (var (id, r) in _rows)
            if (r.LastSeenAt < idleBefore && _rows.TryRemove(id, out _)) n++;
        foreach (var (s, u) in _subjects)
            if (u < now) _subjects.TryRemove(s, out _);
        return n;
    }
}

/// <summary>
/// Explicit, optional sessions. MCP spec 2026-07-28 has no protocol sessions, so a session is a Host-minted handle the
/// client passes as an ordinary argument.
/// <para>A session is owned by the subject that started it; anyone else naming the id is told it is unknown.</para>
/// <para>Taint: a session is tainted when it read untrusted content, <b>or</b> when its owner did within
/// <see cref="TaintWindow"/> in <i>any</i> session. So starting a fresh session (or having several) cannot launder it,
/// and a restart cannot either (the store is durable). Taint is one-way: nothing, including the model, clears it
/// before the window lapses.</para>
/// </summary>
public sealed class SessionRegistry(ISessionStore store, TimeProvider? clock = null, TimeSpan? taintWindow = null, TimeSpan? idleTtl = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public SessionRegistry() : this(new InMemorySessionStore()) { }

    public TimeSpan TaintWindow { get; } = taintWindow ?? PolicyConfig.DefaultTaintWindow;
    public TimeSpan IdleTtl { get; } = idleTtl ?? PolicyConfig.DefaultSessionIdleTtl;

    public string Start(string owner = Subjects.Local)
    {
        var now = _clock.GetUtcNow();
        var id = "ses_" + Guid.NewGuid().ToString("N");
        store.Create(new SessionRow(id, owner, IsSubjectTainted(owner), now, now));
        return id;
    }

    public bool Exists(string sessionId) => store.Get(sessionId) is not null;

    /// <summary>Is there such a session <i>and</i> is it this owner's? Foreign ids look exactly like missing ones.</summary>
    public bool Exists(string sessionId, string owner) => store.Get(sessionId) is { } r && r.Owner == owner;

    public string? OwnerOf(string sessionId) => store.Get(sessionId)?.Owner;

    public void Touch(string sessionId) => store.Touch(sessionId, _clock.GetUtcNow());

    public bool IsTainted(string sessionId) =>
        store.Get(sessionId) is { } r && (r.Tainted || IsSubjectTainted(r.Owner));

    public bool IsSubjectTainted(string subject) =>
        TaintWindow > TimeSpan.Zero && store.SubjectTaintedUntil(subject) is { } until && until > _clock.GetUtcNow();

    /// <summary>Marks the session, and its owner for <see cref="TaintWindow"/>. One-way.</summary>
    public void Taint(string sessionId)
    {
        if (store.Get(sessionId) is not { } r) return;
        store.MarkTainted(sessionId);
        TaintSubject(r.Owner);
    }

    /// <summary>The caller read untrusted content outside any session (or through a session-less call).</summary>
    public void TaintSubject(string subject)
    {
        if (TaintWindow > TimeSpan.Zero) store.TaintSubject(subject, _clock.GetUtcNow() + TaintWindow);
    }

    public int PurgeExpired()
    {
        var now = _clock.GetUtcNow();
        return store.Purge(now - IdleTtl, now);
    }
}
