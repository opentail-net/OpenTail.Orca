namespace OpenTail.Orca;

/// <summary>A capacity limit (policy <c>Capacity</c>): how many calls a tool, or every tool of a module together, may take per minute
/// across all callers. It protects the system and downstream quotas; it says nothing about trust.</summary>
public sealed class CapacityLimit
{
    public int PerMinute { get; init; }

    /// <summary>Calls allowed at once before pacing starts. Default: <see cref="PerMinute"/>.</summary>
    public int? Burst { get; init; }

    /// <summary>The longest a call is held waiting for room. Beyond it the call is refused as retryable (<c>slow_down</c>) with a retry
    /// time, rather than held open (owner's decision: a short delay up to a cap, then refuse). Default 2 seconds.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Capacity backoff, all callers (adaptive limits, order step 3): a token bucket per tool or module. Under budget a call runs at once;
/// over it, the call waits for the next token if that is within <see cref="CapacityLimit.MaxDelay"/>, else it is refused with
/// <c>retryAfter</c>. The model only ever sees "slow down, retry after N seconds", as from any busy API. In memory, per process.
/// </summary>
public sealed class CapacityLimits(PolicyConfig config, TimeProvider clock)
{
    private sealed class Bucket(double capacity)
    {
        public double Tokens = capacity;
        public DateTimeOffset Last;
        public bool Busy;
    }

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>The limit that applies to a tool: its own entry, else its module's (<c>module.*</c>).</summary>
    public (string Key, CapacityLimit Limit)? LimitFor(string tool)
    {
        if (config.Capacity.TryGetValue(tool, out var own)) return (tool, own);
        var dot = tool.IndexOf('.');
        return dot > 0 && config.Capacity.TryGetValue(tool[..dot] + ".*", out var module) ? (tool[..dot] + ".*", module) : null;
    }

    /// <summary>What to do with one call now: run (Wait zero), wait first, or refuse with a retry time. <c>EpisodeStarted</c> is true for
    /// the first call over budget after a quiet spell (audited once, not per call).</summary>
    public (TimeSpan Wait, TimeSpan? RetryAfter, bool EpisodeStarted) Take(string tool)
    {
        if (LimitFor(tool) is not { } found) return (TimeSpan.Zero, null, false);
        var (key, limit) = found;
        var burst = Math.Max(1, limit.Burst ?? limit.PerMinute);
        var perSecond = limit.PerMinute / 60.0;
        var now = clock.GetUtcNow();
        lock (_buckets)
        {
            if (!_buckets.TryGetValue(key, out var b)) _buckets[key] = b = new Bucket(burst) { Last = now };
            b.Tokens = Math.Min(burst, b.Tokens + (now - b.Last).TotalSeconds * perSecond);
            b.Last = now;
            if (b.Tokens >= 1)
            {
                b.Tokens -= 1;
                if (b.Tokens >= 1) b.Busy = false;
                return (TimeSpan.Zero, null, false);
            }
            var wait = TimeSpan.FromSeconds((1 - b.Tokens) / perSecond);
            var started = !b.Busy;
            b.Busy = true;
            if (wait > limit.MaxDelay) return (TimeSpan.Zero, wait, started);
            b.Tokens -= 1; // the call owns the next token; later callers wait behind it
            return (wait, null, started);
        }
    }
}
