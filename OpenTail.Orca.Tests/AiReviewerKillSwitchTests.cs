namespace OpenTail.Orca.Tests;

/// <summary>The overseer's kill switch covers AI reviewers too (section 5.13): a suspended ai: subject decides nothing, and a person still can.</summary>
public class AiReviewerKillSwitchTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";
    private const string Policy = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true } } }""";

    private static ActionReview AiDecides(string name, ReviewDecision d) =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai(name) };

    [Fact]
    public async Task A_suspended_ai_reviewer_decides_nothing_and_a_person_still_can()
    {
        var (host, module, audit) = Hosts.Build(Policy);
        var mail = await host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));
        Assert.True(await host.Levels.SuspendAsync("ai:bot", "ops", "incident"));

        var approve = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, AiDecides("bot", ReviewDecision.Approve)));
        Assert.Contains("suspended", approve.Message);
        var refuse = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, AiDecides("bot", ReviewDecision.Reject)));
        Assert.Contains("suspended", refuse.Message);
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(2, audit.Entries.Count(e => e.Event == "review_refused" && e.ActionId == mail.Id));

        var person = new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, person)).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task The_runner_offers_a_suspended_reviewer_nothing()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));
        Assert.True(await host.Levels.SuspendAsync("ai:bot", "ops", "incident"));
        var shown = 0;
        var bot = new DelegateReviewer("bot", (_, _) => { shown++; return Task.FromResult<ActionReview?>(null); });

        var report = await new ReviewerRunner(host).RunOnceAsync(bot);
        Assert.Equal(0, shown);
        Assert.Equal(0, report.Offered);
    }
}
