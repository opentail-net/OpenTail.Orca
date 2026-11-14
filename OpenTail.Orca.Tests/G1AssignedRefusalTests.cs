namespace OpenTail.Orca.Tests;

/// <summary>
/// G1 of the reviewer gate (batch 07, part A): a Human-only queued tool may name its AI reviewers when <c>AiMayRefuse</c> lets them refuse it.
/// The named reviewers may refuse the action; they may never approve it, and an unnamed AI reviewer may do neither.
/// </summary>
public class G1AssignedRefusalTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");

    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    /// <summary>A Human-only queued tool that names reviewer "a"; <paramref name="refuse"/> adds AiMayRefuse.</summary>
    private static string HumanOnly(bool refuse) =>
        "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"AiMaxRisk\": \"ExternalEffect\"" +
        (refuse ? ", \"AiMayRefuse\": true" : "") + ", \"AiReviewers\": [\"a\"] } } }";

    private static ActionReview AiDecides(string name, ReviewDecision d) =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai(name) };

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    [Fact]
    public void A_human_only_tool_with_a_list_passes_validation_when_AiMayRefuse_is_set()
    {
        var (host, _, _) = Hosts.Build(HumanOnly(refuse: true));
        Assert.NotNull(host);
    }

    [Fact]
    public void A_human_only_tool_with_a_list_is_refused_without_AiMayRefuse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hosts.Build(HumanOnly(refuse: false)));
        Assert.Contains("AiReviewers only applies to Mode Queue with Reviewer Ai or Either, or with AiMayRefuse", ex.Message);
    }

    [Fact]
    public async Task The_assigned_reviewer_may_refuse_and_an_unassigned_one_may_not_even_refuse()
    {
        var (host, module, audit) = Hosts.Build(HumanOnly(refuse: true));

        var assigned = await Submit(host);
        var rejected = await host.Queue.ReviewAsync(assigned.Id, AiDecides("a", ReviewDecision.Reject));
        Assert.Equal(ActionStatus.Rejected, rejected.Status);

        var unassigned = await Submit(host);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(unassigned.Id, AiDecides("b", ReviewDecision.Reject)));
        Assert.Contains("not assigned to this tool", ex.Message);
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.ActionId == unassigned.Id);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task Neither_the_assigned_nor_an_unassigned_reviewer_may_approve_a_human_only_action()
    {
        var (host, module, _) = Hosts.Build(HumanOnly(refuse: true));
        var mail = await Submit(host);

        foreach (var name in new[] { "a", "b" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, AiDecides(name, ReviewDecision.Approve)));

        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(mail.Id))!.Status);
    }

    [Fact]
    public async Task The_runner_offers_the_refusal_only_to_the_assigned_reviewer()
    {
        var (host, module, _) = Hosts.Build(HumanOnly(refuse: true));
        await Submit(host);
        var shownTo = new List<string>();
        DelegateReviewer Reviewer(string name) => new(name, (action, ct) =>
        {
            shownTo.Add(name);
            return Task.FromResult<ActionReview?>(AiDecides(name, ReviewDecision.Reject));
        });

        var runner = new ReviewerRunner(host);
        var forB = await runner.RunOnceAsync(Reviewer("b"));
        Assert.Equal((0, 0, 1), (forB.Offered, forB.Decided, forB.Skipped));

        var forA = await runner.RunOnceAsync(Reviewer("a"));
        Assert.Equal((1, 1, 0), (forA.Offered, forA.Decided, forA.Skipped));
        Assert.Equal(["a"], shownTo);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public void The_no_list_warning_names_a_refusal_only_tool_until_it_has_a_list()
    {
        var unnamed = PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMayRefuse": true } } }""");
        var warnings = AiReviewRules.UnassignedAiTools(unnamed);
        Assert.Single(warnings);
        Assert.Contains("mail.send", warnings[0]);
        Assert.Contains("may refuse it", warnings[0]);

        // One warning per tool, even when the tool may also be decided by AI.
        var both = PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMayRefuse": true } } }""");
        Assert.Single(AiReviewRules.UnassignedAiTools(both));

        Assert.Empty(AiReviewRules.UnassignedAiTools(PolicyConfig.Parse(HumanOnly(refuse: true))));
    }
}
