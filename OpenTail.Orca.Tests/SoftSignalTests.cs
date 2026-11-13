namespace OpenTail.Orca.Tests;

/// <summary>Soft signals: breadth and repeated high-impact attempts after outside content raise to Elevated, never Suspended.</summary>
public class SoftSignalTests
{
    private static OpenTailHost Host(string extra = "", string mode = "Enforce") =>
        new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse($$"""
            {
              "DefaultReadMode": "Execute", "TaintWindow": "00:30:00", "SessionlessTainted": false,
              "Tools": { "mail.send": { "Mode": "Queue" }, "ledger.append": { "Mode": "Execute" } },
              "Limits": { "SoftSignals": "{{mode}}" {{extra}} }
            }
            """)).Build();

    private static ActionRequest Call(string tool, string args = "{}") => new(tool, args, new ActionRequester(RequesterKind.AiSession, "key:bot"));

    [Fact]
    public async Task Many_different_tools_in_a_short_time_elevate()
    {
        var host = Host(""", "BreadthTools": 3""");
        await host.Queue.SubmitAsync(Call("clock.now"));
        await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"x"}"""));
        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:bot"));
        await host.Queue.SubmitAsync(Call("inbox.read"));
        Assert.Equal(IdentityLevel.Elevated, host.Levels.Effective("key:bot"));
    }

    [Fact]
    public async Task Repeated_high_impact_attempts_after_outside_content_elevate()
    {
        var host = Host();
        await host.Queue.SubmitAsync(Call("inbox.read"));                       // taints the caller
        for (var i = 0; i < 2; i++) await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));
        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:bot"));
        await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));
        var row = host.Levels.Get("key:bot")!;
        Assert.Equal(IdentityLevel.Elevated, row.Level);
        Assert.Contains("after reading outside content", row.Reason);
    }

    [Fact]
    public async Task Soft_signals_report_only_by_default_and_never_suspend()
    {
        Assert.Equal(LimitMode.ReportOnly, PolicyConfig.Parse("{}").Limits.SoftSignals);
        var host = Host(""", "BreadthTools": 2""", mode: "ReportOnly");
        await host.Queue.SubmitAsync(Call("clock.now"));
        await host.Queue.SubmitAsync(Call("inbox.read"));
        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:bot"));
        Assert.Equal((IdentityLevel.Elevated, false), (host.Levels.Get("key:bot")!.Level, host.Levels.Get("key:bot")!.Enforced));
    }
}
