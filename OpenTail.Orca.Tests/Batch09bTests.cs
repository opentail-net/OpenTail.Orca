namespace OpenTail.Orca.Tests;

/// <summary>
/// Batch 09b of the reviewer gate: a pass to a person (<see cref="ReviewDecision.Escalate"/>) is recorded without changing the action, and it counts
/// as no approval, no refusal and no budget, and it does not void an approval. Only an AI that is offered the action may pass it.
/// </summary>
public class Batch09bTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    private static ActionReview Escalated(string ai) =>
        new(ai, ReviewDecision.Escalate, "the arguments are unusual", DateTimeOffset.UtcNow) { Subject = Subjects.Ai(ai) };

    private static ActionReview PersonApproves(string name) =>
        new(name, ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin(name) };

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    // ---- the authority rules ignore a pass ----

    [Fact]
    public void A_pass_neither_counts_as_an_approval_nor_voids_one()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "RequiredApprovals": 2 } } }""");
        var stored = Submit(host).Result;
        var people = new PeopleMap(null);

        var once = stored with { Reviews = [PersonApproves("rita"), Escalated("bot")] };
        Assert.Equal(1, ReviewAuthority.Approvals(once, people));
        Assert.Equal((2, false), ReviewAuthority.Tally(once, PersonApproves("lee"), people));

        // A pass after the approval does not void it, and a pass before it is not an approval.
        var reordered = stored with { Reviews = [Escalated("bot"), PersonApproves("rita"), Escalated("bot")] };
        Assert.Equal(1, ReviewAuthority.Approvals(reordered, people));
        Assert.Equal((2, false), ReviewAuthority.Tally(reordered, PersonApproves("lee"), people));
    }

    [Fact]
    public void A_pass_is_neither_an_ai_approval_nor_an_ai_refusal()
    {
        Assert.False(AiReviewRules.IsAiApproval(Escalated("bot")));
        Assert.False(AiReviewRules.IsAiRefusal(Escalated("bot")));
    }

    // ---- the queue: who may pass, and what it costs ----

    [Fact]
    public async Task A_pass_is_recorded_without_changing_the_action_and_is_audited_as_the_kind_of_outcome()
    {
        var (host, module, audit) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""");
        var action = await Submit(host);

        var after = await host.Queue.ReviewAsync(action.Id, Escalated("bot"));

        Assert.Equal(ActionStatus.Pending, after.Status);
        Assert.Equal(ReviewDecision.Escalate, after.Reviews.Single().Decision);
        Assert.Equal(0, module.SendCalls);
        var entry = audit.Entries.Single(e => e.ActionId == action.Id && e.Event == "ai_review");
        Assert.Equal("Escalate; model unknown; prompt unknown; 0 check(s) reported", entry.Detail);
    }

    [Fact]
    public async Task A_pass_counts_toward_no_budget()
    {
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiReviewers": ["bot"], "AiBudget": { "Per": "01:00:00", "MaxCount": 1 } } } }""");
        var action = await Submit(host);
        await host.Queue.ReviewAsync(action.Id, Escalated("bot"));
        await host.Queue.ReviewAsync(action.Id, Escalated("bot") with { Reason = "still unsure" });

        // The budget allows one AI approval an hour; two passes have used none of it.
        var approved = await host.Queue.ReviewAsync(action.Id, new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow)
        { Subject = Subjects.Ai("bot") });

        Assert.Equal(ActionStatus.Succeeded, approved.Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task A_person_cannot_pass_an_action_to_a_person()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""");
        var action = await Submit(host);
        var byPerson = new ActionReview("rita", ReviewDecision.Escalate, "not for me", DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(action.Id, byPerson));
    }

    [Fact]
    public async Task An_ai_that_is_not_offered_the_tool_cannot_pass_it_and_the_refusal_is_audited()
    {
        var (host, _, audit) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""");
        var action = await Submit(host);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(action.Id, Escalated("other")));

        Assert.Contains("not assigned to this tool", ex.Message, StringComparison.Ordinal);
        Assert.Contains(audit.Entries, e => e.ActionId == action.Id && e.Event == "review_refused");
    }

    [Fact]
    public async Task A_human_only_tool_is_not_one_an_ai_may_pass_when_ai_may_not_refuse()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""");
        var action = await Submit(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(action.Id, Escalated("bot")));
    }

    [Fact]
    public async Task The_runner_does_not_count_a_pass_as_a_decision()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiReviewers": ["bot"] } } }""");
        await Submit(host);
        var passing = new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(
            new ActionReview("bot", ReviewDecision.Escalate, "not sure", DateTimeOffset.UtcNow)));

        var report = await new ReviewerRunner(host).RunOnceAsync(passing);

        Assert.Equal((1, 0, 0), (report.Offered, report.Decided, report.Skipped));
    }
}
