using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenTail.Host.Tests;

/// <summary>Phase 4: signals exist, join up, and never carry argument values. Uses a unique tool name because the instruments are process-wide.</summary>
public class TelemetryTests
{
    private sealed class ProbeModule : IHostModule
    {
        public string Id => "probe";
        public void Register(IToolRegistry registry) => registry.Add(
            new ToolDescriptor("telemetry.probe", "probe", ToolRisk.Write, Id),
            AIFunctionFactory.Create((string secretValue) => "ok:" + secretValue, "telemetry.probe", "probe"));
    }

    [Fact]
    public async Task Activities_and_metrics_are_emitted_without_argument_values()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HostTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (activities) activities.Add(a); },
        };
        ActivitySource.AddActivityListener(activityListener);

        var counts = new List<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        var histos = new List<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, l) => { if (inst.Meter.Name == HostTelemetry.Name) l.EnableMeasurementEvents(inst); };
        meterListener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if ((d.TryGetValue("tool", out var t) ? t as string : null) == "telemetry.probe" || inst.Name.EndsWith("pending_actions"))
                lock (counts) counts.Add((inst.Name, value, d));
        });
        meterListener.SetMeasurementEventCallback<double>((inst, _, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "tool" && (t.Value as string) == "telemetry.probe"))
                lock (histos) histos.Add(inst.Name);
        });
        meterListener.Start();

        var host = new OpenTailHostBuilder().AddModule(new ProbeModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "telemetry.probe": { "Mode": "Queue" } } }""")).Build();
        var pending = await host.Queue.SubmitAsync(new ActionRequest("telemetry.probe",
            "{\"secretValue\":\"TOP-SECRET-ARG\"}", new ActionRequester(RequesterKind.User, "me")));
        await host.Queue.ReviewAsync(pending.Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        meterListener.RecordObservableInstruments();

        List<Activity> mine;
        lock (activities) mine = activities.Where(a => (a.GetTagItem("opentail.tool") as string) == "telemetry.probe").ToList();
        Assert.Contains(mine, a => a.OperationName == "action.submit" && (string?)a.GetTagItem("opentail.mode") == "Queue");
        Assert.Contains(mine, a => a.OperationName == "action.execute" && (string?)a.GetTagItem("opentail.status") == "Succeeded");

        // No tag value anywhere may contain the argument value.
        foreach (var a in mine)
            Assert.DoesNotContain(a.Tags, t => (t.Value ?? "").Contains("TOP-SECRET-ARG"));
        lock (counts)
            foreach (var c in counts)
                Assert.DoesNotContain(c.Tags.Values, v => (v?.ToString() ?? "").Contains("TOP-SECRET-ARG"));

        lock (counts)
        {
            Assert.Contains(counts, c => c.Instrument.EndsWith(".actions") && (string?)c.Tags["status"]! == "Pending");
            Assert.Contains(counts, c => c.Instrument.EndsWith(".actions") && (string?)c.Tags["status"]! == "Succeeded");
        }
        lock (histos)
        {
            Assert.Contains("opentail.host.time_to_decision", histos);
            Assert.Contains("opentail.host.execution_duration", histos);
        }
    }
}
