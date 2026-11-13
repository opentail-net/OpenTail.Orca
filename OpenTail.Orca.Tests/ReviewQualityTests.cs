using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Watching the reviewers: a report from the reviews the Host already stores.</summary>
public class ReviewQualityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-15T09:00:00Z");

    private static async Task<(OpenTailHost Host, ManualClock Clock)> Build(string db)
    {
        var clock = new ManualClock(T0);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" }, "ledger.append": { "Mode": "Queue" } } }"""))
            .UseSqlite(db).Build();
        return (host, clock);
    }

    private static Task<ActionRecord> Queue(OpenTailHost host, string tool = "mail.send") =>
        host.Queue.SubmitAsync(new ActionRequest(tool, tool == "mail.send" ? """{"to":"a@b.c","body":"b"}""" : """{"text":"x"}""",
            new ActionRequester(RequesterKind.User, "me")));

    private static ActionReview By(string who, ReviewDecision d, DateTimeOffset at) => new(who, d, d == ReviewDecision.Approve ? null : "no", at) { Subject = "admin:" + who };

    [Fact]
    public async Task Rates_time_to_decide_quick_and_burst_approvals_per_reviewer_and_tool()
    {
        using var db = new TempDb();
        var (host, clock) = await Build(db.Path);

        // rita clears twelve sends within a minute, two seconds after they arrive.
        var sends = new List<ActionRecord>();
        for (var i = 0; i < 12; i++) sends.Add(await Queue(host));
        clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var s in sends) { await host.Queue.ReviewAsync(s.Id, By("rita", ReviewDecision.Approve, clock.GetUtcNow())); clock.Advance(TimeSpan.FromSeconds(3)); }

        // jo reads for ten minutes and rejects one ledger entry.
        var entry = await Queue(host, "ledger.append");
        clock.Advance(TimeSpan.FromMinutes(10));
        await host.Queue.ReviewAsync(entry.Id, By("jo", ReviewDecision.Reject, clock.GetUtcNow()));

        var (reviewers, _) = await ReviewQuality.CollectAsync(host.Store, T0.AddDays(-1));
        var rita = Assert.Single(reviewers, r => r.Reviewer == "admin:rita");
        Assert.Equal(("mail.send", 12, 12, 1.0), (rita.Tool, rita.Decisions, rita.Approved, rita.ApprovalRate));
        Assert.True(rita.QuickApprovals >= 1);
        Assert.Equal(12, rita.BurstApprovals);
        var jo = Assert.Single(reviewers, r => r.Reviewer == "admin:jo");
        Assert.Equal((1, 0, 600.0), (jo.Rejected, jo.QuickApprovals, jo.MedianSecondsToDecide));
    }

    [Fact]
    public async Task A_tool_approved_almost_always_is_suggested_for_less_friction()
    {
        using var db = new TempDb();
        var (host, clock) = await Build(db.Path);
        for (var i = 0; i < ReviewQuality.MinDecisionsForSuggestion; i++)
        {
            var a = await Queue(host);
            clock.Advance(TimeSpan.FromMinutes(5));
            await host.Queue.ReviewAsync(a.Id, By(i % 2 == 0 ? "rita" : "jo", ReviewDecision.Approve, clock.GetUtcNow()));
        }
        var (_, tools) = await ReviewQuality.CollectAsync(host.Store, T0.AddDays(-1));
        Assert.Contains("candidate for a delay", Assert.Single(tools).Suggestion);
    }

    [Fact]
    public async Task The_cli_prints_the_report()
    {
        using var db = new TempDb();
        var (host, _) = await Build(db.Path);
        var a = await Queue(host);
        await host.Queue.ReviewAsync(a.Id, By("rita", ReviewDecision.Approve, DateTimeOffset.UtcNow));
        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--review-report", "--since", "3650d"], "t", _ => { }, output));
        Assert.Contains("admin:rita  mail.send: 1 decided, 100", output.ToString());
    }
}
