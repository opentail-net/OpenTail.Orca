using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace OpenTail.Host;

/// <summary>The adaptive-limits ladder, as far as it is built: Normal, Elevated (everything above Read is queued) and Suspended (every
/// call denied). Throttled comes with per-identity budgets.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityLevel>))]
public enum IdentityLevel
{
    Normal = 0,
    Elevated = 2,
    Suspended = 3,
}

/// <summary>Off: the signal is ignored. ReportOnly: the level is recorded and audited as what <i>would</i> happen, with no effect.
/// Enforce: the level takes effect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LimitMode>))]
public enum LimitMode
{
    Off = 0,
    ReportOnly = 1,
    Enforce = 2,
}

/// <summary>Policy <c>Limits</c>: which high-precision signals move an identity up the ladder. Both report only by default, so operators
/// see what would happen before anything is enforced.</summary>
public sealed class LimitsConfig
{
    /// <summary>A call to a tool outside the caller's agent profile (a healthy agent never sees those tools): Suspended.</summary>
    public LimitMode OutOfProfile { get; init; } = LimitMode.ReportOnly;

    /// <summary>A reviewer rejecting one of the identity's actions: Elevated.</summary>
    public LimitMode Rejections { get; init; } = LimitMode.ReportOnly;

    /// <summary>Decoy records (policy <c>Decoys</c>): reading one Elevated, sending one out Suspended (and, enforced, that call is refused).</summary>
    public LimitMode Decoys { get; init; } = LimitMode.ReportOnly;

    /// <summary>Softer signals, which raise to Elevated and never suspend: breadth (many tools in a short time) and repeated high-impact
    /// attempts after reading outside content. Report only by default (owner's decision for soft signals).</summary>
    public LimitMode SoftSignals { get; init; } = LimitMode.ReportOnly;

    /// <summary>Distinct tools within <see cref="SignalWindow"/> that count as enumeration. Default 15.</summary>
    public int BreadthTools { get; init; } = 15;

    /// <summary>High-impact attempts by a tainted caller within <see cref="SignalWindow"/>. Default 3.</summary>
    public int TaintedHighImpactAttempts { get; init; } = 3;

    public TimeSpan SignalWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>For Elevated identities: run their Execute calls above Read after this undo window instead of queueing them, for operators
    /// who prefer less friction. Null (the default): Elevated queues.</summary>
    public TimeSpan? ElevatedDelay { get; init; }
}

/// <param name="Enforced">False for a report-only level: recorded, with no effect.</param>
public sealed record IdentityLevelRow(string Subject, IdentityLevel Level, string Reason, DateTimeOffset Since, bool Enforced);

/// <summary>Where identity levels live. Raising only tightens; only <see cref="Clear"/> (a person) loosens.</summary>
public interface IIdentityLevelStore
{
    IdentityLevelRow? Get(string subject);
    IReadOnlyList<IdentityLevelRow> List();

    /// <summary>Records <paramref name="row"/> unless the subject already has an equal or higher level that is at least as enforced.
    /// Returns true when something changed.</summary>
    bool Raise(IdentityLevelRow row);

    bool Clear(string subject);
}

public sealed class InMemoryIdentityLevelStore : IIdentityLevelStore
{
    private readonly ConcurrentDictionary<string, IdentityLevelRow> _rows = new(StringComparer.Ordinal);

    public IdentityLevelRow? Get(string subject) => _rows.TryGetValue(subject, out var r) ? r : null;
    public IReadOnlyList<IdentityLevelRow> List() => _rows.Values.OrderBy(r => r.Subject, StringComparer.Ordinal).ToList();

    public bool Raise(IdentityLevelRow row)
    {
        lock (_rows)
        {
            if (_rows.TryGetValue(row.Subject, out var old) && !IdentityLevels.Tightens(old, row)) return false;
            _rows[row.Subject] = row;
            return true;
        }
    }

    public bool Clear(string subject) => _rows.TryRemove(subject, out _);
}

/// <summary>Moves identities up the ladder on high-precision signals and answers what level a caller is at. Automatic changes only
/// tighten; clearing is a person's act, audited. The local operator is never levelled.</summary>
public sealed class IdentityLevels(IIdentityLevelStore store, LimitsConfig config, IAuditSink audit, TimeProvider clock)
{
    // Recent calls per identity, for the soft signals: in memory, per process; a restart forgets them, which only errs toward leniency.
    private readonly ConcurrentDictionary<string, List<(DateTimeOffset At, string Tool, bool TaintedHighImpact)>> _recent = new(StringComparer.Ordinal);

    public LimitsConfig Config => config;

    /// <summary>Notes a submitted call and raises the identity to Elevated when a soft signal fires (report only unless enforced).</summary>
    public async Task ObserveAsync(string subject, string tool, bool taintedHighImpact, CancellationToken ct)
    {
        if (config.SoftSignals == LimitMode.Off || subject == Subjects.Local) return;
        var now = clock.GetUtcNow();
        int tools, attempts;
        var calls = _recent.GetOrAdd(subject, _ => []);
        lock (calls)
        {
            calls.RemoveAll(c => now - c.At > config.SignalWindow);
            calls.Add((now, tool, taintedHighImpact));
            tools = calls.Select(c => c.Tool).Distinct(StringComparer.Ordinal).Count();
            attempts = calls.Count(c => c.TaintedHighImpact);
        }
        var minutes = config.SignalWindow.TotalMinutes;
        if (tools >= config.BreadthTools)
            await RaiseAsync(subject, IdentityLevel.Elevated, config.SoftSignals, $"{tools} different tools within {minutes:0} minutes", tool, ct);
        else if (taintedHighImpact && attempts >= config.TaintedHighImpactAttempts)
            await RaiseAsync(subject, IdentityLevel.Elevated, config.SoftSignals, $"{attempts} high-impact attempts within {minutes:0} minutes after reading outside content", tool, ct);
    }

    internal static bool Tightens(IdentityLevelRow old, IdentityLevelRow next) =>
        next.Level > old.Level || (next.Level == old.Level && next.Enforced && !old.Enforced);

    /// <summary>The level that applies to calls now: enforced levels only.</summary>
    public IdentityLevel Effective(string subject) =>
        subject != Subjects.Local && store.Get(subject) is { Enforced: true } r ? r.Level : IdentityLevel.Normal;

    public IdentityLevelRow? Get(string subject) => store.Get(subject);
    public IReadOnlyList<IdentityLevelRow> List() => store.List();

    public Task OutOfProfileAsync(string subject, string tool, CancellationToken ct) =>
        RaiseAsync(subject, IdentityLevel.Suspended, config.OutOfProfile, $"called {tool}, outside its agent profile", tool, ct);

    public Task RejectedAsync(string subject, string actionId, string tool, CancellationToken ct) =>
        RaiseAsync(subject, IdentityLevel.Elevated, config.Rejections, $"a reviewer rejected {actionId}", tool, ct);

    /// <summary>A decoy record came back in a result: no real task needs one, so the identity is reading where it should not.</summary>
    public Task DecoyReadAsync(string subject, string tool, CancellationToken ct) =>
        RaiseAsync(subject, IdentityLevel.Elevated, config.Decoys, $"read a decoy record through {tool}", tool, ct);

    /// <summary>A decoy record's marker in an outbound call's arguments: data is being sent out. Returns whether the call must be refused.</summary>
    public async Task<bool> DecoySentAsync(string subject, string tool, CancellationToken ct)
    {
        await RaiseAsync(subject, IdentityLevel.Suspended, config.Decoys, $"tried to send a decoy record through {tool}", tool, ct);
        return config.Decoys == LimitMode.Enforce && subject != Subjects.Local;
    }

    /// <summary>True when the text contains one of the policy's decoy markers (case is ignored).</summary>
    public static bool ContainsDecoy(IReadOnlyList<string> decoys, string? text) =>
        text is not null && decoys.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase));

    private async Task RaiseAsync(string subject, IdentityLevel level, LimitMode mode, string reason, string tool, CancellationToken ct)
    {
        if (mode == LimitMode.Off || subject == Subjects.Local) return;
        var from = store.Get(subject);
        var row = new IdentityLevelRow(subject, level, reason, clock.GetUtcNow(), mode == LimitMode.Enforce);
        if (!store.Raise(row)) return;
        var evt = row.Enforced ? "level_changed" : "level_would_change";
        await audit.AppendAsync(new AuditEntry(row.Since, evt, null, tool, subject, $"{from?.Level ?? IdentityLevel.Normal} -> {level}: {reason}"), ct);
    }

    /// <summary>A credential failure the Host's rules assessed as suspicious (failures design, step 4): a soft signal, so it raises the
    /// identity to Elevated only when soft signals are enforced, and is audited as what would happen otherwise.</summary>
    public Task SuspiciousCredentialAsync(string subject, string tool, string reason, CancellationToken ct = default) =>
        RaiseAsync(subject, IdentityLevel.Elevated, config.SoftSignals, "suspicious credential failure: " + reason, tool, ct);

    /// <summary>A person suspends an identity (an incident): enforced whatever the configured modes, audited with who did it. Clearing it
    /// follows the usual rule (a suspended key must be rotated first).</summary>
    public async Task<bool> SuspendAsync(string subject, string by, string reason, CancellationToken ct = default)
    {
        if (subject == Subjects.Local) throw new InvalidOperationException("The local operator is never levelled.");
        var from = store.Get(subject);
        var row = new IdentityLevelRow(subject, IdentityLevel.Suspended, "suspended by " + by + ": " + reason, clock.GetUtcNow(), true);
        if (!store.Raise(row)) return false;
        await audit.AppendAsync(new AuditEntry(row.Since, "level_suspended", null, null, by, $"{subject}: {from?.Level ?? IdentityLevel.Normal} -> Suspended: {reason}"), ct);
        return true;
    }

    /// <summary>A person returns an identity to Normal. A suspended API key must have been rotated first (owner's decision): the caller
    /// states it, and the statement is audited.</summary>
    public async Task<bool> ClearAsync(string subject, string by, bool keyRotated, CancellationToken ct = default)
    {
        if (store.Get(subject) is not { } row) return false;
        if (row is { Level: IdentityLevel.Suspended, Enforced: true } && subject.StartsWith("key:", StringComparison.Ordinal))
        {
            if (!keyRotated)
                throw new InvalidOperationException($"{subject} is suspended: rotate its API key first (a new key and hash in the HTTP config), then clear it with --key-rotated.");
            // The Host records key rotations itself (key_rotated at the HTTP start): the statement is checked, not taken on trust.
            if (audit is IAuditReader reader
                && !reader.Read().Any(e => e.Event == "key_rotated" && e.At >= row.Since && e.Detail?.StartsWith(subject + " ", StringComparison.Ordinal) == true))
                throw new InvalidOperationException($"{subject} is suspended and no key rotation has been recorded since {row.Since:u}: put the new key's hash in the HTTP config and restart the Host, then clear it.");
        }
        if (!store.Clear(subject)) return false;
        await audit.AppendAsync(new AuditEntry(clock.GetUtcNow(), "level_cleared", null, null, by,
            $"{subject}: {row.Level}{(row.Enforced ? "" : " (report only)")} -> Normal{(keyRotated ? ", key rotated" : "")}"), ct);
        return true;
    }
}
