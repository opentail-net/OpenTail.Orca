using System.Diagnostics.Metrics;
using System.Text.Json;

namespace OpenTail.Host.Tests;

/// <summary>The metrics groundwork for adaptive limits: result stats, daily usage rollups that outlive retention, telemetry.</summary>
public class UsageTests
{
    /// <summary>A module whose tools declare counts: a list of customers (with a repeated id) and an outbound send.</summary>
    private sealed class CountingModule : IHostModule
    {
        public string Id => "usage";

        public void Register(IToolRegistry registry)
        {
            registry.Add(new ToolDescriptor("usage.customers", "List customers.", ToolRisk.Read, Id)
                    { Counts = new ResultCounts { Records = "/rows", EntityKeys = "/rows/*/id" } },
                AIFunctionFactory.Create(() => new { rows = new[] { new { id = "c1" }, new { id = "c2" }, new { id = "c1" } } }, "usage.customers"));
            registry.Add(new ToolDescriptor("usage.send", "Send.", ToolRisk.Write, Id) { Counts = new ResultCounts { RecipientArgs = ["/to", "/cc"] } },
                AIFunctionFactory.Create((string[] to, string[]? cc) => new { sent = true }, "usage.send"));
            registry.Add(new ToolDescriptor("usage.plain", "No counts declared.", ToolRisk.Read, Id),
                AIFunctionFactory.Create(() => "hello", "usage.plain"));
        }
    }

    private const string Policy = """
        { "DefaultReadMode": "Execute", "Tools": { "usage.send": { "Mode": "Execute" } },
          "Agents": { "crm": { "Tools": [ "usage.customers", "usage.plain" ] } } }
        """;

    private static ActionRequest Call(string tool, string args = "{}", string? profile = null, string who = "key:bot") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, who)) { Profile = profile };

    // ---- measuring ----

    [Fact]
    public void Records_come_from_an_array_or_a_number_and_recipients_from_the_arguments()
    {
        var (stats, _) = ResultMeasure.Measure(new ResultCounts { Records = "/rows" }, "{}", """{"rows":[1,2,3]}""", null);
        Assert.Equal(new ResultStats(Records: 3), stats);
        (stats, _) = ResultMeasure.Measure(new ResultCounts { Records = "/count" }, "{}", """{"count":42}""", null);
        Assert.Equal(42, stats!.Records);
        (stats, _) = ResultMeasure.Measure(new ResultCounts { RecipientArgs = ["/to", "/cc"] }, """{"to":["a@x","b@x"],"cc":"c@x"}""", "{}", null);
        Assert.Equal(3, stats!.Recipients);
    }

    [Fact]
    public void Entity_keys_are_salted_hashes_counted_once_each()
    {
        var salt = new byte[32];
        var (stats, hashes) = ResultMeasure.Measure(new ResultCounts { EntityKeys = "/rows/*/id" }, "{}",
            """{"rows":[{"id":"c1"},{"id":"c2"},{"id":"c1"},{"id":7}]}""", salt);
        Assert.Equal(3, stats!.Entities);
        Assert.Equal(3, hashes.Count);
        Assert.DoesNotContain(hashes, h => h.Contains("c1"));
        var (_, other) = ResultMeasure.Measure(new ResultCounts { EntityKeys = "/rows/*/id" }, "{}", """{"rows":[{"id":"c1"}]}""", [1, 2, 3]);
        Assert.DoesNotContain(other[0], hashes); // another deployment's salt gives another hash
    }

    [Fact]
    public void A_result_that_is_not_JSON_or_has_no_counts_declared_gives_no_stats()
    {
        Assert.Null(ResultMeasure.Measure(null, "{}", "{}", null).Stats);
        Assert.Null(ResultMeasure.Measure(new ResultCounts { Records = "/rows" }, "{}", "not json", null).Stats);
        Assert.Null(ResultMeasure.Measure(new ResultCounts { Records = "/missing" }, "{}", "{}", null).Stats);
    }

    // ---- on the action and in the rollup ----

    [Fact]
    public async Task A_successful_action_carries_its_size_and_counts_but_no_entity_keys()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new CountingModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path).Build();

        var r = await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));

        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal(new ResultStats(Records: 3, Entities: 2), r.ResultStats);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(r.ResultJson!), r.ResultBytes);
        Assert.Equal("crm", r.Profile);
        var stored = JsonSerializer.Serialize(await host.Store.GetAsync(r.Id));
        Assert.DoesNotContain("entityKeys", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_daily_rollup_counts_outcomes_records_recipients_bytes_and_distinct_entities_per_identity()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new CountingModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path).Build();

        var a = await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));
        await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));          // the same two customers again
        await host.Queue.SubmitAsync(Call("usage.send", """{"to":["a@x.example","b@x.example"],"cc":["c@x.example"]}""", profile: "crm")); // outside the profile
        await host.Queue.SubmitAsync(Call("usage.send", """{"to":["a@x.example","b@x.example"],"cc":["c@x.example"]}""", who: "key:other"));

        var usage = ((IUsageStore)host.Store).ReadUsage(DateOnly.MinValue, DateOnly.MaxValue);
        var customers = Assert.Single(usage, u => u.Subject == "key:bot" && u.Tool == "usage.customers");
        Assert.Equal((2L, 2L, 6L, 2L, "crm"), (customers.Calls, customers.Succeeded, customers.Records, customers.Entities, customers.Profile));
        Assert.Equal(2 * a.ResultBytes, customers.Bytes);
        var refused = Assert.Single(usage, u => u.Subject == "key:bot" && u.Tool == "usage.send");
        Assert.Equal((1L, 1L, 0L), (refused.Calls, refused.Denied, refused.Recipients));
        var sent = Assert.Single(usage, u => u.Subject == "key:other" && u.Tool == "usage.send");
        Assert.Equal((1L, 3L), (sent.Succeeded, sent.Recipients));
    }

    [Fact]
    public async Task A_tool_without_declared_counts_still_counts_calls_and_bytes()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new CountingModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path).Build();
        var r = await host.Queue.SubmitAsync(Call("usage.plain"));
        Assert.Null(r.ResultStats);
        var row = Assert.Single(((IUsageStore)host.Store).ReadUsage(DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Equal((1L, r.ResultBytes!.Value, 0L), (row.Calls, row.Bytes, row.Records));
    }

    [Fact]
    public async Task Rejections_are_counted()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).Build();
        var r = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));
        await host.Queue.ReviewAsync(r.Id, new ActionReview("rita", ReviewDecision.Reject, "no", DateTimeOffset.UtcNow));
        var row = Assert.Single(((IUsageStore)host.Store).ReadUsage(DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Equal((1L, 1L), (row.Calls, row.Rejected));
    }

    [Fact]
    public async Task Rollups_outlive_action_retention_and_are_kept_for_13_months()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new CountingModule())
            .UsePolicy(PolicyConfig.Parse(Policy.Replace("\"DefaultReadMode\"", "\"RetentionDays\": 1, \"DefaultReadMode\""))).UseSqlite(db.Path).Build();
        await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));
        var store = (SqliteActionStore)host.Store;
        var usage = (IUsageStore)host.Store;

        // A year on, the action is gone (retention) but its day's rollup is kept.
        var yearOn = DateTimeOffset.UtcNow.AddMonths(12);
        Assert.Equal(1, store.PurgeTerminalBefore(yearOn).Removed);
        usage.PurgeUsage(yearOn);
        Assert.Single(usage.ReadUsage(DateOnly.MinValue, DateOnly.MaxValue));

        usage.PurgeUsage(DateTimeOffset.UtcNow.AddMonths(13).AddDays(2));
        Assert.Empty(usage.ReadUsage(DateOnly.MinValue, DateOnly.MaxValue));
    }

    [Fact]
    public void The_entity_salt_is_created_once_and_kept()
    {
        using var db = new TempDb();
        var first = new SqliteActionStore(db.Path).EntitySalt;
        TempDb.ClearPools(db.Path);
        Assert.Equal(first, new SqliteActionStore(db.Path).EntitySalt);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public async Task Records_and_bytes_go_to_telemetry_by_tool_and_profile_never_by_identity()
    {
        var seen = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) => { if (inst.Meter.Name == HostTelemetry.Name && inst.Name.StartsWith("opentail.host.result.")) l.EnableMeasurementEvents(inst); };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (d.TryGetValue("tool", out var t) && t as string == "usage.customers") lock (seen) seen.Add((inst.Name, value, d));
        });
        listener.Start();

        var host = new OpenTailHostBuilder().AddModule(new CountingModule()).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));

        lock (seen)
        {
            Assert.Contains(seen, s => s.Name == "opentail.host.result.records" && s.Value == 3 && (string?)s.Tags["profile"] == "crm");
            Assert.Contains(seen, s => s.Name == "opentail.host.result.bytes" && s.Value > 0);
            Assert.All(seen, s => Assert.DoesNotContain(s.Tags.Values, v => v as string == "key:bot"));
        }
    }

    [Fact]
    public async Task The_usage_command_prints_the_rollups()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new CountingModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path).Build();
        await host.Queue.SubmitAsync(Call("usage.customers", profile: "crm"));

        var output = new StringWriter();
        Assert.Equal(0, await OpenTail.Host.Admin.PolicyCli.TryRunAsync(["--db", db.Path, "--usage", "--since", "2d"], "t", _ => { }, output));
        Assert.Contains("key:bot (crm)  usage.customers: 1 call(s), 1 ok, 0 failed, 0 denied, 0 rejected; 3 record(s), 2 distinct entities", output.ToString());
    }
}
