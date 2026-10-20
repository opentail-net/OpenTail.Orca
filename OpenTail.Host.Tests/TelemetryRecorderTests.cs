using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Plan v7 A2: the in-process recorder behind the page's Telemetry view. Unique tool names, because the source and meter are
/// process-wide and other tests emit at the same time.</summary>
public class TelemetryRecorderTests
{
    private sealed class ProbeModule(string tool) : IHostModule
    {
        public string Id => tool.Split('.')[0];
        public void Register(IToolRegistry registry) => registry.Add(
            new ToolDescriptor(tool, "probe", ToolRisk.Write, Id),
            AIFunctionFactory.Create((string secretValue) => "ok:" + secretValue, tool, "probe"));
    }

    private static OpenTailHost Host(string tool) => new OpenTailHostBuilder().AddModule(new ProbeModule(tool))
        .UsePolicy(PolicyConfig.Parse($$"""{ "Tools": { "{{tool}}": { "Mode": "Queue" } } }""")).Build();

    private static async Task<ActionRecord> QueueAndApprove(OpenTailHost host, string tool)
    {
        var pending = await host.Queue.SubmitAsync(new ActionRequest(tool, "{\"secretValue\":\"TOP-SECRET-ARG\"}",
            new ActionRequester(RequesterKind.User, "me")));
        return await host.Queue.ReviewAsync(pending.Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Counters_and_histograms_are_totalled_per_tag_set_and_spans_carry_the_action_id_but_no_argument()
    {
        using var recorder = new HostTelemetryRecorder(maxSpans: 50_000); // other tests emit spans at the same time
        var host = Host("recprobe.one");
        var done = await QueueAndApprove(host, "recprobe.one");
        await QueueAndApprove(host, "recprobe.one");

        var mine = recorder.Metrics().Where(s => s.Tags.GetValueOrDefault("tool") == "recprobe.one").ToList();
        var succeeded = mine.Single(s => s.Instrument == "opentail.host.actions" && s.Tags["status"] == "Succeeded");
        Assert.Equal("counter", succeeded.Kind);
        Assert.Equal(2, succeeded.Value);
        var exec = mine.Single(s => s.Instrument == "opentail.host.execution_duration");
        Assert.Equal("histogram", exec.Kind);
        Assert.Equal(2, exec.Count);
        Assert.Equal(2, exec.Buckets!.Sum());

        var spans = recorder.Spans(50_000).Where(s => s.Tags.GetValueOrDefault("opentail.action_id") == done.Id).ToList();
        Assert.Contains(spans, s => s.Name == "action.submit");
        Assert.Contains(spans, s => s.Name == "action.review" && s.Tags["opentail.decision"] == "Approve");
        Assert.Contains(spans, s => s.Name == "action.execute");
        Assert.DoesNotContain(recorder.Spans(50_000), s => s.Tags.Values.Any(v => v.Contains("TOP-SECRET-ARG", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Memory_is_bounded_in_spans_and_series()
    {
        using var recorder = new HostTelemetryRecorder(maxSpans: 5, maxSeries: 1);
        var host = Host("recprobe.two");
        for (var i = 0; i < 4; i++) await QueueAndApprove(host, "recprobe.two");

        Assert.True(recorder.Spans(50_000).Count <= 5);
        Assert.Single(recorder.Metrics());
        Assert.True(recorder.DroppedSeries > 0);
    }

    [Fact]
    public async Task The_page_serves_metrics_traces_and_the_audit_tail_to_admins_only()
    {
        const string adminToken = "admin-token-0123456789", reviewerToken = "review-token-0123456789";
        using var recorder = new HostTelemetryRecorder(maxSpans: 50_000); // other tests emit spans at the same time
        var host = Host("recprobe.three");
        var done = await QueueAndApprove(host, "recprobe.three");
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [adminToken] = "ops", [reviewerToken] = "rev" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [adminToken] = new HashSet<string> { Roles.Review, Roles.Admin },
                [reviewerToken] = new HashSet<string> { Roles.Review },
            },
            Telemetry = recorder,
        });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        async Task<(HttpStatusCode, JsonNode?)> Get(string path, string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }

        foreach (var p in new[] { "/api/admin/telemetry/metrics", "/api/admin/telemetry/traces", "/api/admin/audit" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Get(p, reviewerToken)).Item1);

        var (_, metrics) = await Get("/api/admin/telemetry/metrics", adminToken);
        Assert.True(metrics!["enabled"]!.GetValue<bool>());
        Assert.Contains(metrics["series"]!.AsArray(), s => s!["tags"]!["tool"]?.GetValue<string>() == "recprobe.three");

        var (_, traces) = await Get("/api/admin/telemetry/traces?limit=50000", adminToken);
        var trace = traces!["traces"]!.AsArray().First(t => t!["actionId"]?.GetValue<string>() == done.Id)!;
        Assert.NotEmpty(trace["spans"]!.AsArray());

        var (_, audit) = await Get("/api/admin/audit?limit=50", adminToken);
        var events = audit!["entries"]!.AsArray().Where(e => e!["actionId"]?.GetValue<string>() == done.Id).Select(e => e!["event"]!.GetValue<string>()).ToList();
        Assert.Contains("approved", events);
        Assert.Contains("execution_succeeded", events);
        Assert.Equal("execution_succeeded", events[0]); // newest first
    }

    [Fact]
    public async Task With_the_recorder_off_the_views_say_so()
    {
        const string adminToken = "admin-token-0123456789";
        var host = Host("recprobe.four");
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [adminToken] = "ops" },
            RecordTelemetry = false,
        });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var body = JsonNode.Parse(await http.GetStringAsync("/api/admin/telemetry/metrics"))!;
        Assert.False(body["enabled"]!.GetValue<bool>());
    }
}
