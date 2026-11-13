namespace OpenTail.Orca;

/// <summary>How a check stands: <see cref="Ok"/>, <see cref="Degraded"/> (something needs a person, but the Host does its job), or
/// <see cref="Unhealthy"/> (the Host is not doing its job; an orchestrator should restart it).</summary>
public enum HealthState { Ok, Degraded, Unhealthy }

/// <summary>The answer to a health probe: the worst state, and each check's state by name. No counts, times or details: the probe is
/// anonymous.</summary>
public sealed record HealthReport(HealthState State, IReadOnlyDictionary<string, string> Checks);

/// <summary>
/// What <c>/healthz</c> reports (SOP gap G14). The maintenance sweep (recovery, expiry, delayed actions, retries, work, retention) runs
/// every 30 seconds; if it has not finished for longer than <see cref="MaintenanceStaleAfter"/>, the Host is unhealthy. Other parts
/// add checks (notifications add one: delivery failing in the last day is degraded, not unhealthy, because restarting fixes nothing).
/// </summary>
public sealed class HostHealth(TimeProvider clock)
{
    /// <summary>Six missed sweeps: long enough not to flap, short enough that held and delayed work is noticed.</summary>
    public static readonly TimeSpan MaintenanceStaleAfter = TimeSpan.FromMinutes(3);

    private readonly DateTimeOffset _started = clock.GetUtcNow();
    private readonly List<(string Name, Func<HealthState> Check)> _checks = [];
    private DateTimeOffset? _lastSweep;

    public DateTimeOffset? LastSweep => _lastSweep;

    internal void SweepFinished() => _lastSweep = clock.GetUtcNow();

    /// <summary>Adds a named check; it must be cheap and must not throw (a throwing check counts as degraded).</summary>
    public void Add(string name, Func<HealthState> check)
    {
        lock (_checks) _checks.Add((name, check));
    }

    public HealthReport Report()
    {
        var now = clock.GetUtcNow();
        var checks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var worst = HealthState.Ok;

        // Before the first sweep finishes (it runs at start), the Host is starting, not stale.
        var since = _lastSweep ?? _started;
        var maintenance = now - since > MaintenanceStaleAfter ? HealthState.Unhealthy : HealthState.Ok;
        checks["maintenance"] = maintenance == HealthState.Ok ? (_lastSweep is null ? "starting" : "ok") : "stale";
        worst = Max(worst, maintenance);

        List<(string Name, Func<HealthState> Check)> extra;
        lock (_checks) extra = [.. _checks];
        foreach (var (name, check) in extra)
        {
            HealthState s;
            try { s = check(); } catch (Exception) { s = HealthState.Degraded; }
            checks[name] = s switch { HealthState.Ok => "ok", HealthState.Degraded => "degraded", _ => "failing" };
            worst = Max(worst, s);
        }
        return new HealthReport(worst, checks);

        static HealthState Max(HealthState a, HealthState b) => (HealthState)Math.Max((int)a, (int)b);
    }
}
