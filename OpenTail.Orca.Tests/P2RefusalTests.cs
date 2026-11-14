namespace OpenTail.Orca.Tests;

/// <summary>
/// P2 of the reviewer gate (D-21, D-26, D-27, F20): an AI reviewer may refuse where its policy says AiMayRefuse, across the Human-only and
/// reviewer-group boundaries, and ignoring the risk ceiling and taint. It never approves across those lines, and a refusal costs the requester nothing.
/// </summary>
public class P2RefusalTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    private static Task<ActionRecord> Submit(OpenTailHost host, string tool = "mail.send", string args = Mail, bool tainted = false) =>
        host.Queue.SubmitAsync(new ActionRequest(tool, args, Alice, tainted ? null : host.Sessions.Start("key:alice-agent")));

    private static ActionReview Bot(ReviewDecision d) =>
        new("bot", d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };

    private static ActionReview Person(ReviewDecision d) =>
        new("rita", d, d is ReviewDecision.Approve ? null : "no", DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };

    private static async Task<string> Refused(OpenTailHost host, string id, ActionReview review) =>
        (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(id, review))).Message;

    [Fact]
    public async Task An_ai_refuses_a_human_only_action_only_where_aimayrefuse_says_so_and_never_approves_it()
    {
        var (host, module, audit) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect" } } }""");
        var off = await Submit(host);
        Assert.Contains("may not refuse", await Refused(host, off.Id, Bot(ReviewDecision.Reject)));
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.ActionId == off.Id);

        var (host2, module2, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }""");
        var on = await Submit(host2);
        Assert.Contains("needs a human reviewer", await Refused(host2, on.Id, Bot(ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Rejected, (await host2.Queue.ReviewAsync(on.Id, Bot(ReviewDecision.Reject))).Status);
        Assert.Equal(0, module2.SendCalls);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_may_request_changes_under_the_same_rule()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }""");
        var mail = await Submit(host);
        Assert.Equal(ActionStatus.ChangesRequested, (await host.Queue.ReviewAsync(mail.Id, Bot(ReviewDecision.RequestChanges))).Status);
    }

    [Fact]
    public async Task An_ai_refuses_a_destructive_action_but_never_approves_it()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "files.delete": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMayRefuse": true } } }""");
        var delete = await Submit(host, "files.delete", """{"path":"x"}""");
        Assert.Contains("never approved", await Refused(host, delete.Id, Bot(ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(delete.Id, Bot(ReviewDecision.Reject))).Status);
    }

    [Fact]
    public async Task An_ai_refuses_a_tainted_external_action_but_never_approves_it()
    {
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true } } }""");
        var tainted = await Submit(host, tainted: true);
        Assert.Contains("read outside content", await Refused(host, tainted.Id, Bot(ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(tainted.Id, Bot(ReviewDecision.Reject))).Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_refuses_a_reviewer_group_action_but_never_approves_it()
    {
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "ReviewerGroup": "finance" } } }""");
        var mail = await Submit(host);
        Assert.Contains("cannot stand in", await Refused(host, mail.Id, Bot(ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(mail.Id, Bot(ReviewDecision.Reject))).Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_refusal_costs_the_requester_nothing_and_a_persons_refusal_still_does()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true } } }""");
        var byAi = await Submit(host);
        await host.Queue.ReviewAsync(byAi.Id, Bot(ReviewDecision.Reject));
        Assert.Null(host.Levels.Get("key:alice-agent"));

        var byPerson = await Submit(host);
        await host.Queue.ReviewAsync(byPerson.Id, Person(ReviewDecision.Reject));
        Assert.NotNull(host.Levels.Get("key:alice-agent"));   // the control: a person's refusal is recorded against the requester, as before
    }

    [Fact]
    public async Task The_runner_offers_a_refusal_capable_ai_its_human_only_action_and_takes_only_its_refusal()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }""");
        var mail = await Submit(host);
        var eager = new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(Bot(ReviewDecision.Approve)));
        var refusing = new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(Bot(ReviewDecision.Reject)));

        var first = await new ReviewerRunner(host).RunOnceAsync(eager);
        Assert.Equal((1, 0), (first.Offered, first.Decided));   // offered; its approval is refused by the authority and left for a person
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(mail.Id))!.Status);

        var second = await new ReviewerRunner(host).RunOnceAsync(refusing);
        Assert.Equal((1, 1), (second.Offered, second.Decided));
        Assert.Equal(ActionStatus.Rejected, (await host.Store.GetAsync(mail.Id))!.Status);
    }

    [Fact]
    public async Task Without_aimayrefuse_the_runner_does_not_show_an_ai_a_human_only_action()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect" } } }""");
        await Submit(host);
        var shown = 0;
        var reviewer = new DelegateReviewer("bot", (_, _) => { shown++; return Task.FromResult<ActionReview?>(null); });

        var report = await new ReviewerRunner(host).RunOnceAsync(reviewer);
        Assert.Equal(0, shown);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public async Task An_ai_refusal_budget_is_counted_per_requester_and_tool()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"], "AiRefusalBudget": { "Per": "01:00:00", "MaxCount": 1 } } } }""");
        var first = await Submit(host);
        var second = await Submit(host);
        await host.Queue.ReviewAsync(first.Id, Bot(ReviewDecision.Reject));
        Assert.Contains("may not refuse more in this window", await Refused(host, second.Id, Bot(ReviewDecision.Reject)));

        // Another requester is counted on its own.
        var otherRequester = new ActionRequester(RequesterKind.AiSession, "key:bob-agent");
        var other = await host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, otherRequester, host.Sessions.Start("key:bob-agent")));
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(other.Id, Bot(ReviewDecision.Reject))).Status);
    }

    [Fact]
    public async Task Two_concurrent_ai_refusals_against_one_remaining_unit_yield_exactly_one_refusal()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"], "AiRefusalBudget": { "Per": "01:00:00", "MaxCount": 1 } } } }""");
        var a = await Submit(host);
        var b = await Submit(host);

        var results = await Task.WhenAll(Attempt(host, a.Id), Attempt(host, b.Id));
        Assert.Equal(1, results.Count(ok => ok));
    }

    private static async Task<bool> Attempt(OpenTailHost host, string id)
    {
        try
        {
            await host.Queue.ReviewAsync(id, Bot(ReviewDecision.Reject));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void Aimayrefuse_belongs_to_a_queued_tool()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Execute", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }"""));
        Assert.Contains("AiMayRefuse only applies to Mode Queue", ex.Message);
    }

    [Fact]
    public void A_policy_without_aimayrefuse_keeps_its_canonical_text()
    {
        Assert.DoesNotContain("AiMayRefuse", PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""").ToCanonicalJson());
        Assert.Contains("\"AiMayRefuse\": true", PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }""").ToCanonicalJson());
    }
}
