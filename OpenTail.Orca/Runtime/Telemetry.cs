using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenTail.Orca;

/// <summary>
/// OpenTelemetry-compatible signals (System.Diagnostics only; subscribe with the OpenTelemetry SDK or any listener).
/// Tags never carry argument values or results.
/// </summary>
public static class HostTelemetry
{
    public const string Name = "OpenTail.Orca";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> Actions =
        Meter.CreateCounter<long>("opentail.host.actions", description: "Actions by terminal or queued status.");

    internal static readonly UpDownCounter<long> Pending =
        Meter.CreateUpDownCounter<long>("opentail.host.pending_actions", description: "Actions waiting for a reviewer.");

    internal static readonly Histogram<double> TimeToDecision =
        Meter.CreateHistogram<double>("opentail.host.time_to_decision", unit: "s", description: "Queued to reviewer decision.");

    internal static readonly Histogram<double> ExecutionDuration =
        Meter.CreateHistogram<double>("opentail.host.execution_duration", unit: "ms", description: "Tool execution time.");

    internal static readonly Counter<long> Backoff =
        Meter.CreateCounter<long>("opentail.host.backoff", description: "Calls slowed or refused by a capacity limit, by tool and outcome.");

    internal static readonly Counter<long> ResultRecords =
        Meter.CreateCounter<long>("opentail.host.result.records", description: "Records returned by successful actions (rows, messages, files).");

    internal static readonly Counter<long> ResultBytes =
        Meter.CreateCounter<long>("opentail.host.result.bytes", unit: "By", description: "Size of successful actions' results.");

    internal static readonly Counter<long> Circuits =
        Meter.CreateCounter<long>("opentail.host.circuits", description: "Routes paused, reopened or that would have been paused (report only), by tool and event.");

    internal static readonly Counter<long> Work =
        Meter.CreateCounter<long>("opentail.host.work", description: "Work items and continuations, by event (created, claimed, done, failed, dead, ...) and kind.");

    internal static readonly Counter<long> Tasks =
        Meter.CreateCounter<long>("opentail.host.tasks", description: "Calls answered with an MCP task instead of the pending JSON, by tool and reason.");

    /// <summary>A call answered with a task: why (queued for review, delayed by an undo window, running).</summary>
    internal static void TaskCreated(ActionRecord r) => Tasks.Add(1,
        new KeyValuePair<string, object?>("tool", r.ToolName),
        new KeyValuePair<string, object?>("reason", r.Status != ActionStatus.Pending ? "running" : r.RunAt is null ? "queued" : "delayed"));

    /// <summary>Records and bytes of a successful result, by tool, module and agent profile. Never by identity: per-identity figures live
    /// in the Host's own usage rollups, not in the metrics backend.</summary>
    internal static void CountResult(ActionRecord r)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("tool", r.ToolName),
            new("module", r.ModuleId),
            new("profile", r.Profile ?? "none"),
        ];
        if (r.ResultBytes is { } b) ResultBytes.Add(b, tags);
        if (r.ResultStats?.Records is { } n) ResultRecords.Add(n, tags);
    }

    internal static void Count(ActionRecord r) =>
        Actions.Add(1,
            new KeyValuePair<string, object?>("tool", r.ToolName),
            new KeyValuePair<string, object?>("module", r.ModuleId),
            new KeyValuePair<string, object?>("status", r.Status.ToString()),
            new KeyValuePair<string, object?>("mode", r.PolicyMode.ToString()),
            new KeyValuePair<string, object?>("tainted", r.Tainted));
}
