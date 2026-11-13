using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenTail.Orca;

/// <summary>One metric series: an instrument and one combination of tag values, totalled since the recorder started.</summary>
public sealed record MetricSeries(string Instrument, string Kind, string? Unit, IReadOnlyDictionary<string, string> Tags,
    double Value, long Count, double Min, double Max, IReadOnlyList<long>? Buckets);

/// <summary>One finished span of the <see cref="HostTelemetry.Name"/> source. Tags are the ones the Host sets: never argument values or results.</summary>
public sealed record SpanRecord(string TraceId, string SpanId, string? ParentSpanId, string Name, DateTimeOffset Start, double DurationMs,
    string Status, IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Keeps the Host's own metrics and recent spans in memory so the review page can show them without an OpenTelemetry collector
/// (plan v7, A2). Bounded: at most <see cref="MaxSeries"/> series (new tag combinations beyond that are counted in
/// <see cref="DroppedSeries"/> and not kept) and the last <see cref="MaxSpans"/> spans. Holds what happened since it started; it is
/// not a metrics backend.
/// </summary>
public sealed class HostTelemetryRecorder : IDisposable
{
    /// <summary>Histogram bucket upper bounds, shared by every histogram (seconds for decision time, milliseconds for execution).</summary>
    public static readonly double[] BucketBounds = [1, 5, 10, 50, 100, 500, 1_000, 5_000, 10_000, 60_000, 300_000];

    private readonly MeterListener _meters = new();
    private readonly ActivityListener _spans;
    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SpanRecord> _recent = new();
    private long _droppedSeries;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    public int MaxSeries { get; }
    public int MaxSpans { get; }
    public long DroppedSeries => Interlocked.Read(ref _droppedSeries);
    public DateTimeOffset Started => _started;

    public HostTelemetryRecorder(int maxSpans = 500, int maxSeries = 2_000)
    {
        MaxSpans = maxSpans;
        MaxSeries = maxSeries;
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HostTelemetry.Name) listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meters.Start();

        _spans = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HostTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Keep,
        };
        ActivitySource.AddActivityListener(_spans);
    }

    public IReadOnlyList<MetricSeries> Metrics() => _series.Values
        .Select(s => s.Snapshot())
        .OrderBy(s => s.Instrument, StringComparer.Ordinal).ThenBy(s => string.Join(",", s.Tags.Select(t => t.Key + "=" + t.Value)), StringComparer.Ordinal)
        .ToList();

    /// <summary>The newest spans first.</summary>
    public IReadOnlyList<SpanRecord> Spans(int limit = 200) => _recent.Reverse().Take(Math.Clamp(limit, 1, MaxSpans)).ToList();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tags) sorted[t.Key] = Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var key = instrument.Name + "|" + string.Join("|", sorted.Select(t => t.Key + "=" + t.Value));
        if (!_series.TryGetValue(key, out var series))
        {
            if (_series.Count >= MaxSeries) { Interlocked.Increment(ref _droppedSeries); return; }
            series = _series.GetOrAdd(key, _ => new Series(instrument, sorted));
        }
        series.Add(value);
    }

    private void Keep(Activity a)
    {
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in a.TagObjects) tags[t.Key] = Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _recent.Enqueue(new SpanRecord(a.TraceId.ToHexString(), a.SpanId.ToHexString(),
            a.ParentSpanId == default ? null : a.ParentSpanId.ToHexString(), a.DisplayName,
            new DateTimeOffset(a.StartTimeUtc, TimeSpan.Zero), a.Duration.TotalMilliseconds,
            a.Status.ToString(), tags));
        while (_recent.Count > MaxSpans) _recent.TryDequeue(out _);
    }

    public void Dispose()
    {
        _meters.Dispose();
        _spans.Dispose();
    }

    private sealed class Series(Instrument instrument, IReadOnlyDictionary<string, string> tags)
    {
        private readonly Lock _gate = new();
        private readonly bool _histogram = instrument.GetType().IsGenericType && instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>);
        // The last bucket counts values above the largest bound.
        private readonly long[]? _buckets = instrument.GetType().IsGenericType && instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>)
            ? new long[BucketBounds.Length + 1] : null;
        private double _sum, _min = double.MaxValue, _max = double.MinValue;
        private long _count;
        private long[]? Buckets => _buckets;

        public void Add(double v)
        {
            lock (_gate)
            {
                _sum += v; _count++;
                if (v < _min) _min = v;
                if (v > _max) _max = v;
                if (Buckets is { } b)
                {
                    var i = 0;
                    while (i < BucketBounds.Length && v > BucketBounds[i]) i++;
                    b[i]++;
                }
            }
        }

        public MetricSeries Snapshot()
        {
            lock (_gate)
            {
                var kind = _histogram ? "histogram" : instrument.GetType().Name.StartsWith("UpDownCounter", StringComparison.Ordinal) ? "updown" : "counter";
                return new MetricSeries(instrument.Name, kind, instrument.Unit, tags, _sum, _count,
                    _count == 0 ? 0 : _min, _count == 0 ? 0 : _max, Buckets is { } b ? [.. b] : null);
            }
        }
    }
}
