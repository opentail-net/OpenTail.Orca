namespace OpenTail.Orca;

/// <summary>
/// What the Host does about failures (docs/design/failures-and-inspection.md, step 2), in the policy file under <c>Failures</c>. Every
/// default is provisional (the design's proposals) and the owner's to change.
/// </summary>
public sealed class FailuresConfig
{
    /// <summary>Automatic retries of a transient failure, only for tools idempotent by key. Zero turns automatic retries off.</summary>
    public int AutoRetries { get; init; } = 3;

    /// <summary>The first wait before an automatic retry; it doubles each time, with jitter, up to <see cref="RetryMax"/>. A
    /// <c>Retry-After</c> from the tool is honoured when longer.</summary>
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary><c>ReportOnly</c> (default): a route that would be paused is audited (<c>circuit.would_open</c>) and calls go on.
    /// <c>Enforce</c>: calls on a paused route are held and released once a probe call succeeds. <c>Off</c>.</summary>
    public LimitMode CircuitMode { get; init; } = LimitMode.ReportOnly;

    /// <summary>Failures of one kind on one tool within <see cref="CircuitWindow"/> that pause it.</summary>
    public int CircuitThreshold { get; init; } = 5;
    public TimeSpan CircuitWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a paused route waits before one probe call; it doubles after each failed probe, up to the maximum.</summary>
    public TimeSpan CircuitCooldown { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CircuitMaxCooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Held calls released per maintenance sweep once a route reopens: a few at a time, not all at once.</summary>
    public int ReleasePerSweep { get; init; } = 5;
}

/// <summary>What a circuit change means for the audit trail.</summary>
public enum CircuitEvent { None, Opened, WouldOpen, Reopened, Closed }

/// <summary>The state of one route (a tool), as the gate reports it.</summary>
public sealed record CircuitState(string Tool, ToolFailureKind Kind, string Fingerprint, DateTimeOffset ProbeAt, TimeSpan Cooldown);

/// <summary>
/// Pauses a route (a tool) when it keeps failing the same way, instead of letting every call fail slowly: one incident per fingerprint
/// (tool, kind, code), held calls, one probe after a cool-down, and a gradual release. Only kinds that say the route is broken count:
/// transient, environment and auth. A bad request or a bug is about one call, not the route. In memory, per process (several Host
/// instances each keep their own circuits), like the capacity limits.
/// </summary>
public sealed class Circuits(FailuresConfig config, TimeProvider clock)
{
    private enum Phase { Closed, Open, HalfOpen }

    private sealed class Route
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public Phase Phase = Phase.Closed;
        public ToolFailureKind Kind;
        public string Fingerprint = "";
        public DateTimeOffset ProbeAt;
        public TimeSpan Cooldown;
        public bool ReportedOnly;
    }

    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public LimitMode Mode => config.CircuitMode;

    public static bool Counts(ToolFailureKind kind) => kind is ToolFailureKind.Transient or ToolFailureKind.Environment or ToolFailureKind.Auth;

    /// <summary>May a call on this tool run now? On an open route the first call after the cool-down is let through as the probe;
    /// everyone else is held until it comes back.</summary>
    public (bool Allowed, CircuitState? Paused) Gate(string tool)
    {
        if (config.CircuitMode != LimitMode.Enforce) return (true, null);
        lock (_gate)
        {
            if (!_routes.TryGetValue(tool, out var r) || r.Phase == Phase.Closed) return (true, null);
            if (r.Phase == Phase.Open && clock.GetUtcNow() >= r.ProbeAt)
            {
                r.Phase = Phase.HalfOpen;
                return (true, null);
            }
            return (false, State(tool, r));
        }
    }

    public bool IsClosed(string tool)
    {
        lock (_gate) return !_routes.TryGetValue(tool, out var r) || r.Phase == Phase.Closed || config.CircuitMode != LimitMode.Enforce;
    }

    /// <summary>When the held calls of a paused route are tried again (the probe time); null when it is not paused.</summary>
    public DateTimeOffset? ProbeAt(string tool)
    {
        lock (_gate) return _routes.TryGetValue(tool, out var r) && r.Phase != Phase.Closed ? r.ProbeAt : null;
    }

    public CircuitEvent Succeeded(string tool)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(tool, out var r)) return CircuitEvent.None;
            r.Failures.Clear();
            r.ReportedOnly = false;
            if (r.Phase == Phase.Closed) return CircuitEvent.None;
            r.Phase = Phase.Closed;
            return CircuitEvent.Closed;
        }
    }

    public (CircuitEvent Event, CircuitState? State) Failed(string tool, ToolFailureKind kind, string code)
    {
        if (config.CircuitMode == LimitMode.Off || !Counts(kind)) return (CircuitEvent.None, null);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_routes.TryGetValue(tool, out var r)) _routes[tool] = r = new Route();
            if (r.Phase == Phase.HalfOpen)
            {
                // The probe failed: pause again, for longer.
                r.Cooldown = r.Cooldown * 2 > config.CircuitMaxCooldown ? config.CircuitMaxCooldown : r.Cooldown * 2;
                r.Phase = Phase.Open;
                r.ProbeAt = now + r.Cooldown;
                return (CircuitEvent.Reopened, State(tool, r));
            }
            if (r.Phase == Phase.Open) return (CircuitEvent.None, null);
            r.Failures.Enqueue(now);
            while (r.Failures.Count > 0 && now - r.Failures.Peek() > config.CircuitWindow) r.Failures.Dequeue();
            if (r.Failures.Count < config.CircuitThreshold) return (CircuitEvent.None, null);
            r.Failures.Clear();
            r.Kind = kind;
            r.Fingerprint = $"{tool}|{FailureKinds.Name(kind)}|{code}";
            r.Cooldown = config.CircuitCooldown;
            r.ProbeAt = now + r.Cooldown;
            if (config.CircuitMode == LimitMode.Enforce)
            {
                r.Phase = Phase.Open;
                return (CircuitEvent.Opened, State(tool, r));
            }
            // Report only: say once per episode what would have happened, and carry on.
            if (r.ReportedOnly) return (CircuitEvent.None, null);
            r.ReportedOnly = true;
            return (CircuitEvent.WouldOpen, State(tool, r));
        }
    }

    private static CircuitState State(string tool, Route r) => new(tool, r.Kind, r.Fingerprint, r.ProbeAt, r.Cooldown);

    /// <summary>The wait before automatic retry number <paramref name="attempt"/> (1 for the first): doubling from the base, with up to
    /// a quarter of jitter, at most the maximum, and never shorter than what the tool asked for.</summary>
    public TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var wait = TimeSpan.FromTicks(Math.Min(config.RetryMax.Ticks, config.RetryBase.Ticks * (1L << Math.Clamp(attempt - 1, 0, 20))));
        wait += TimeSpan.FromTicks((long)(wait.Ticks * 0.25 * Random.Shared.NextDouble()));
        return retryAfter is { } asked && asked > wait ? asked : wait;
    }
}
