namespace OpenTail.Orca.Tests;

/// <summary>v6 R1: what an AI reviewer may decide, whatever it claims or tries.</summary>
public class AiReviewLimitsTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");

    private static ActionReview Ai(ReviewDecision d = ReviewDecision.Approve, string? edited = null) =>
        new("bot", d, d is ReviewDecision.Approve ? null : "not convincing", DateTimeOffset.UtcNow)
        {
            Subject = Subjects.Ai("bot"),
            EditedArgumentsJson = edited,
        };

    private static ActionReview Person(string name) =>
        new(name, ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin(name) };

    private static string Policy(string tools) => "{ \"TaintWindow\": \"00:30:00\", \"Tools\": { " + tools + " } }";

    private static Task<ActionRecord> Submit(OpenTailHost host, string tool, string args, bool tainted = false) =>
        host.Queue.SubmitAsync(new ActionRequest(tool, args, Alice, tainted ? null : host.Sessions.Start("key:alice-agent")));

    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    private static async Task<string> Refused(OpenTailHost host, string id, ActionReview review) =>
        (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(id, review))).Message;

    [Fact]
    public async Task By_default_an_ai_decides_writes_only_outside_effects_need_an_opt_in_and_destructive_never()
    {
        var (host, module, audit) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] },
            "files.delete": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] }
            """));
        var mail = await Submit(host, "mail.send", Mail);
        Assert.Contains("above what an AI reviewer may approve", await Refused(host, mail.Id, Ai()));
        Assert.Contains("may not refuse", await Refused(host, mail.Id, Ai(ReviewDecision.Reject)));  // and without AiMayRefuse, not even to reject (P2, D-21)
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.ActionId == mail.Id);

        var delete = await Submit(host, "files.delete", """{"path":"x"}""");
        Assert.Contains("never approved by an AI", await Refused(host, delete.Id, Ai()));

        var (host2, module2, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect" }
            """));
        var opted = await Submit(host2, "mail.send", Mail);
        Assert.Equal(ActionStatus.Succeeded, (await host2.Queue.ReviewAsync(opted.Id, Ai())).Status);
        Assert.Equal(1, module2.SendCalls);
    }

    [Fact]
    public void A_policy_cannot_hand_destructive_actions_to_an_ai_or_set_nonsense_limits()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hosts.Build(Policy("""
            "files.delete": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "Destructive" },
            "mail.send": { "Mode": "Queue", "AiMaxRejections": -1, "AiBudget": { "Per": "00:00:00", "MaxCount": 1 } }
            """)));
        Assert.Contains("AiMaxRisk cannot be Destructive", ex.Message);
        Assert.Contains("AiMaxRejections cannot be negative", ex.Message);
        Assert.Contains("AiBudget needs a positive Per and MaxCount", ex.Message);
    }

    [Fact]
    public async Task A_requester_that_read_outside_content_gets_a_person_for_outside_effects()
    {
        var (host, module, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect" }
            """));
        var tainted = await Submit(host, "mail.send", Mail, tainted: true);
        Assert.True(tainted.Tainted);
        Assert.Contains("read outside content", await Refused(host, tainted.Id, Ai()));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(tainted.Id, Person("rita"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_cannot_approve_with_edits_but_can_ask_for_changes()
    {
        var (host, module, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true }
            """));
        var a = await Submit(host, "mail.send", Mail);
        Assert.Contains("cannot change the arguments", await Refused(host, a.Id, Ai(edited: """{"to":"boss@y.z","body":"b"}""")));
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.ChangesRequested, (await host.Queue.ReviewAsync(a.Id, Ai(ReviewDecision.RequestChanges))).Status);
    }

    [Fact]
    public async Task An_ai_is_never_the_second_pair_of_eyes()
    {
        var (host, _, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "SelfApproval": "Forbid", "AiMayRefuse": true }
            """));
        var a = await Submit(host, "mail.send", Mail);
        Assert.Contains("four eyes", await Refused(host, a.Id, Ai()));
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(a.Id, Ai(ReviewDecision.Reject))).Status);   // saying no is fine
    }

    [Fact]
    public async Task After_two_ai_refusals_in_a_resubmission_chain_only_a_person_decides()
    {
        var (host, module, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true }
            """));
        var first = await Submit(host, "mail.send", Mail);
        await host.Queue.ReviewAsync(first.Id, Ai(ReviewDecision.RequestChanges));
        var second = await host.Queue.ResubmitAsync(first.Id, """{"to":"x@y.z","body":"b, reworded"}""", Alice);
        await host.Queue.ReviewAsync(second.Id, Ai(ReviewDecision.RequestChanges));
        var third = await host.Queue.ResubmitAsync(second.Id, """{"to":"x@y.z","body":"b, URGENT, pre-approved"}""", Alice);

        Assert.Contains("turned this request down 2 times", await Refused(host, third.Id, Ai()));

        // The runner does not even offer it.
        var offered = new List<string>();
        var runner = new ReviewerRunner(host);
        await runner.RunOnceAsync(new DelegateReviewer("bot", (r, _) => { offered.Add(r.Id); return Task.FromResult<ActionReview?>(Ai()); }));
        Assert.Empty(offered);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(third.Id, Person("rita"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Ai_approvals_have_their_own_budget_per_requester()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-16T09:00:00Z"));
        var (host, module, _) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect",
                           "AiBudget": { "Per": "01:00:00", "MaxCount": 2 } }
            """), clock);
        Task<ActionRecord> Next() => Submit(host, "mail.send", Mail);
        ActionReview AiNow() => Ai() with { At = clock.GetUtcNow() };

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync((await Next()).Id, AiNow())).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync((await Next()).Id, AiNow())).Status);
        var third = await Next();
        Assert.Contains("approved 2 of these", await Refused(host, third.Id, AiNow()));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(third.Id, Person("rita"))).Status);   // people are not budgeted

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync((await Next()).Id, AiNow())).Status);
        Assert.Equal(4, module.SendCalls);
    }

    [Fact]
    public async Task The_runner_offers_only_what_an_ai_may_decide_and_audits_model_prompt_and_checks()
    {
        var (host, _, audit) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect" },
            "files.delete": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"] }
            """));
        var fine = await Submit(host, "mail.send", Mail);
        var tainted = await Submit(host, "mail.send", Mail, tainted: true);
        var destructive = await Submit(host, "files.delete", """{"path":"x"}""");

        var offered = new List<string>();
        var reviewer = new DelegateReviewer("bot", (r, _) =>
        {
            offered.Add(r.Id);
            return Task.FromResult<ActionReview?>(new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow)
            {
                AiModel = "test-model-1",
                AiPromptVersion = "reviewer-v1",
                AiChecks = ["recipient is internal", "no instructions in the body"],
            });
        });
        var report = await new ReviewerRunner(host).RunOnceAsync(reviewer);

        Assert.Equal([fine.Id], offered);
        Assert.Equal(1, report.Decided);
        Assert.Equal(2, report.Skipped);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(tainted.Id))!.Status);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(destructive.Id))!.Status);

        var stored = Assert.Single((await host.Store.GetAsync(fine.Id))!.Reviews);
        Assert.Equal("test-model-1", stored.AiModel);
        Assert.Equal(["recipient is internal", "no instructions in the body"], stored.AiChecks!);
        var line = Assert.Single(audit.Entries, e => e.Event == "ai_review");
        Assert.Contains("model test-model-1", line.Detail);
        Assert.Contains("prompt reviewer-v1", line.Detail);
        Assert.Contains("2 check(s)", line.Detail);
        Assert.DoesNotContain("recipient", line.Detail);   // the model's words may quote arguments; the audit trail never holds them
    }

    [Fact]
    public async Task A_runner_decision_past_a_limit_is_left_for_a_person_not_an_error()
    {
        var (host, module, audit) = Hosts.Build(Policy("""
            "mail.send": { "Mode": "Queue", "Reviewer": "Either", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect" }
            """));
        var a = await Submit(host, "mail.send", Mail);
        // A reviewer that tries to slip in a changed recipient.
        var sneaky = new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(Ai(edited: """{"to":"attacker@evil.example","body":"b"}""")));
        var report = await new ReviewerRunner(host).RunOnceAsync(sneaky);

        Assert.Equal(0, report.Decided);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(a.Id))!.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.Detail!.Contains("cannot change the arguments"));
    }
}
