namespace OpenTail.Orca.Tests;

/// <summary>
/// P1 of the reviewer gate (D-19, D-28): what a person may do to an action whose policy names an AI reviewer, through the queue itself.
/// `Ai`: an AI decides; a person may refuse but not approve. `Either`: an AI or a person may approve.
/// </summary>
public class P1ReviewerTypesTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");

    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    private static string Policy(string reviewer) =>
        "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"Reviewer\": \"" + reviewer + "\", \"AiMaxRisk\": \"ExternalEffect\" } } }";

    private static ActionReview PersonDecides(ReviewDecision d, string name = "rita") =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Admin(name) };

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    [Fact]
    public async Task A_person_cannot_approve_an_ai_action_and_the_attempt_is_audited()
    {
        var (host, module, audit) = Hosts.Build(Policy("Ai"));
        var mail = await Submit(host);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve)));
        Assert.Contains("decided by an AI reviewer", ex.Message);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.ActionId == mail.Id);
    }

    [Fact]
    public async Task A_person_can_refuse_an_ai_action()
    {
        var (host, module, _) = Hosts.Build(Policy("Ai"));
        var mail = await Submit(host);

        var rejected = await host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Reject));
        Assert.Equal(ActionStatus.Rejected, rejected.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task Either_lets_a_person_approve_and_the_action_runs()
    {
        var (host, module, _) = Hosts.Build(Policy("Either"));
        var mail = await Submit(host);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_still_approves_an_ai_action_within_its_limits()
    {
        var (host, module, _) = Hosts.Build(Policy("Ai"));
        var mail = await Submit(host);

        var ai = new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, ai)).Status);
        Assert.Equal(1, module.SendCalls);
    }
}
