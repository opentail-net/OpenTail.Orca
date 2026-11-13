using System.Diagnostics;

namespace OpenTail.Orca.Tests;

/// <summary>Capacity backoff, all callers: a short wait when a tool is busy, then a retryable refusal with a retry time.</summary>
public class CapacityTests
{
    private static OpenTailHost Host(string capacity, TimeProvider? clock = null)
    {
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse($$"""
            { "DefaultReadMode": "Execute", "Tools": { "ledger.append": { "Mode": "Execute" } }, "Capacity": {{capacity}} }
            """));
        if (clock is not null) b.UseClock(clock);
        return b.Build();
    }

    private static Task<ActionRecord> Append(OpenTailHost host, string who = "key:a") =>
        host.Queue.SubmitAsync(new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, who)));

    [Fact]
    public async Task Within_the_burst_calls_run_at_once_and_beyond_it_they_are_refused_with_a_retry_time()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = Host("""{ "ledger.append": { "PerMinute": 6, "Burst": 2, "MaxDelay": "00:00:00" } }""", clock);
        Assert.Equal(ActionStatus.Succeeded, (await Append(host)).Status);
        Assert.Equal(ActionStatus.Succeeded, (await Append(host, "key:b")).Status);   // all callers share the limit

        var refused = await Append(host);
        Assert.Equal((ActionStatus.Failed, "slow_down", true), (refused.Status, refused.Error!.Code, refused.Error.Retryable));
        Assert.Contains("Retry after 10 s", refused.Error.Message);                     // 6 a minute: one every 10 s
        Assert.Single(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "backoff");

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ActionStatus.Succeeded, (await Append(host)).Status);
    }

    [Fact]
    public async Task A_short_wait_is_absorbed_instead_of_refused()
    {
        var host = Host("""{ "ledger.append": { "PerMinute": 600, "Burst": 1, "MaxDelay": "00:00:02" } }""");
        Assert.Equal(ActionStatus.Succeeded, (await Append(host)).Status);
        var watch = Stopwatch.StartNew();
        Assert.Equal(ActionStatus.Succeeded, (await Append(host)).Status);              // waits about 0.1 s for the next token
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task A_module_limit_covers_all_its_tools()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse("""
            { "DefaultReadMode": "Execute", "Capacity": { "clock.*": { "PerMinute": 1, "MaxDelay": "00:00:00" } } }
            """)).Build();
        var who = new ActionRequester(RequesterKind.AiSession, "key:a");
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(new ActionRequest("clock.now", "{}", who))).Status);
        Assert.Equal("slow_down", (await host.Queue.SubmitAsync(new ActionRequest("clock.now", "{}", who))).Error!.Code);
    }

    [Theory]
    [InlineData("""{ "no.such": { "PerMinute": 5 } }""", "matches no loaded tool")]
    [InlineData("""{ "ledger.append": { "PerMinute": 0 } }""", "PerMinute must be positive")]
    [InlineData("""{ "ledger.append": { "PerMinute": 5, "MaxDelay": "00:01:00" } }""", "MaxDelay must be between")]
    public void Bad_capacity_settings_stop_startup(string capacity, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Host(capacity));
        Assert.Contains(expected, ex.Message);
    }
}
