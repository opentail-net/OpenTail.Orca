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

    // ---- D-20 and D-36: named AI reviewers and their assignments ----

    private static string Assigned(string reviewer, string names) =>
        "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"Reviewer\": \"" + reviewer + "\", \"AiMaxRisk\": \"ExternalEffect\", \"AiReviewers\": [" + names + "] } } }";

    private static ActionReview AiDecides(string name, ReviewDecision d = ReviewDecision.Approve) =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai(name) };

    [Fact]
    public async Task An_ai_reviewer_not_assigned_to_the_tool_cannot_decide_it_even_to_reject()
    {
        var (host, module, audit) = Hosts.Build(Assigned("Ai", "\"bot\""));
        var mail = await Submit(host);

        foreach (var d in new[] { ReviewDecision.Approve, ReviewDecision.Reject })
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, AiDecides("other", d)));
            Assert.Contains("not assigned to this tool", ex.Message);
        }
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(2, audit.Entries.Count(e => e.Event == "review_refused" && e.ActionId == mail.Id));
    }

    [Fact]
    public async Task An_assigned_ai_reviewer_decides_its_tool()
    {
        var (host, module, _) = Hosts.Build(Assigned("Ai", "\"bot\", \"second\""));
        var mail = await Submit(host);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, AiDecides("second"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task The_runner_never_shows_an_unassigned_action_to_a_reviewer()
    {
        var (host, module, _) = Hosts.Build(Assigned("Ai", "\"bot\""));
        await Submit(host);
        var shown = 0;
        var other = new DelegateReviewer("other", (action, ct) => { shown++; return Task.FromResult<ActionReview?>(null); });

        var report = await new ReviewerRunner(host).RunOnceAsync(other);
        Assert.Equal(0, shown);
        Assert.Equal((0, 1), (report.Offered, report.Skipped));
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task The_runner_offers_an_assigned_reviewer_its_action_and_stamps_its_own_name()
    {
        var (host, module, _) = Hosts.Build(Assigned("Ai", "\"bot\""));
        await Submit(host);
        var bot = new DelegateReviewer("bot", (action, ct) => Task.FromResult<ActionReview?>(
            new ActionReview("someone-else", ReviewDecision.Approve, null, DateTimeOffset.UtcNow)));

        var report = await new ReviewerRunner(host).RunOnceAsync(bot);
        Assert.Equal((1, 1), (report.Offered, report.Decided));
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Without_an_assignment_list_any_ai_reviewer_may_decide_the_tool()
    {
        var (host, module, _) = Hosts.Build(Policy("Ai"));
        var mail = await Submit(host);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, AiDecides("anyone"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public void A_policy_without_assignments_keeps_its_canonical_text_and_an_assigned_one_does_not()
    {
        var plain = PolicyConfig.Parse(Policy("Ai"));
        Assert.DoesNotContain("AiReviewers", plain.ToCanonicalJson());

        var assigned = PolicyConfig.Parse(Assigned("Ai", "\"bot\""));
        Assert.Contains("\"AiReviewers\"", assigned.ToCanonicalJson());
        Assert.NotEqual(plain.Hash(), assigned.Hash());
    }

    [Theory]
    [InlineData("", "needs at least one reviewer name")]
    [InlineData("\"\"", "is not a valid AI reviewer name")]
    [InlineData("\"bad name\"", "is not a valid AI reviewer name")]
    [InlineData("\"ai:bot\"", "is not a valid AI reviewer name")]
    public void Assignments_name_reviewers_that_can_be_named(string names, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hosts.Build(Assigned("Ai", names)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Assignments_belong_to_a_queued_tool_an_ai_may_decide()
    {
        var human = "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"AiReviewers\": [\"bot\"] } } }";
        var ex = Assert.Throws<InvalidOperationException>(() => Hosts.Build(human));
        Assert.Contains("AiReviewers only applies to Mode Queue with Reviewer Ai or Either", ex.Message);
    }
}
