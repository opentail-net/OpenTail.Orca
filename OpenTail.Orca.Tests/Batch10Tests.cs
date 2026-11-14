namespace OpenTail.Orca.Tests;

/// <summary>
/// Batch 10 of the reviewer gate (P6, D-11): an AI budget is checked inside the decision's own write transaction, against the committed
/// history, so two processes (two stores on one database file) cannot both take the last unit. Also the planner's follow-up: a reviewer that
/// has passed an action to a person cannot pass it again.
/// </summary>
public class Batch10Tests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-14T09:00:00Z");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    /// <summary>One approval an hour across the whole deployment.</summary>
    private const string ApprovalBudget = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiReviewers": ["bot"], "AiBudget": { "Per": "01:00:00", "MaxCount": 1, "Scope": "Global" } } } }""";

    /// <summary>One refusal an hour per requester and tool.</summary>
    private const string RefusalBudget = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiReviewers": ["bot"], "AiMayRefuse": true, "AiRefusalBudget": { "Per": "01:00:00", "MaxCount": 1 } } } }""";

    /// <summary>A review by the bot, stamped with the test's clock. True when the queue let it through; false when an AI limit refused it.</summary>
    private static async Task<bool> TryReview(OpenTailHost host, string id, ReviewDecision decision, ManualClock clock)
    {
        var review = new ActionReview("bot", decision, decision == ReviewDecision.Approve ? null : "not this one", clock.GetUtcNow())
        {
            Subject = Subjects.Ai("bot"),
        };
        try
        {
            await host.Queue.ReviewAsync(id, review);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    // ---- two processes, one remaining unit ----

    [Fact]
    public async Task Two_stores_on_one_database_with_one_approval_left_let_exactly_one_decision_succeed()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var firstModule = new FakeInboxModule();
        var secondModule = new FakeInboxModule();
        var first = Hosts.BuildSqlite(firstModule, ApprovalBudget, db.Path, clock);
        var second = Hosts.BuildSqlite(secondModule, ApprovalBudget, db.Path, clock);
        var a = await Submit(first);
        var b = await Submit(second);

        // Both pre-checks pass: neither process has seen an approval yet. The race is between the two decisions.
        Assert.True((await first.Queue.AiVerdictAsync(a, ReviewDecision.Approve)).Allowed);
        Assert.True((await second.Queue.AiVerdictAsync(b, ReviewDecision.Approve)).Allowed);

        var results = await Task.WhenAll(
            Task.Run(() => TryReview(first, a.Id, ReviewDecision.Approve, clock)),
            Task.Run(() => TryReview(second, b.Id, ReviewDecision.Approve, clock)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, firstModule.SendCalls + secondModule.SendCalls);
    }

    [Fact]
    public async Task Two_stores_on_one_database_with_one_refusal_left_let_exactly_one_refusal_through()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var first = Hosts.BuildSqlite(new FakeInboxModule(), RefusalBudget, db.Path, clock);
        var second = Hosts.BuildSqlite(new FakeInboxModule(), RefusalBudget, db.Path, clock);
        var a = await Submit(first);
        var b = await Submit(second);
        Assert.True((await first.Queue.AiVerdictAsync(a, ReviewDecision.Reject)).Allowed);
        Assert.True((await second.Queue.AiVerdictAsync(b, ReviewDecision.Reject)).Allowed);

        var results = await Task.WhenAll(
            Task.Run(() => TryReview(first, a.Id, ReviewDecision.Reject, clock)),
            Task.Run(() => TryReview(second, b.Id, ReviewDecision.Reject, clock)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task After_both_pre_checks_pass_the_second_approval_is_refused_inside_its_own_transaction_on_one_store()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var (host, module, audit) = Hosts.Build(ApprovalBudget, clock);
        var a = await Submit(host);
        var b = await Submit(host);
        Assert.True((await host.Queue.AiVerdictAsync(a, ReviewDecision.Approve)).Allowed);
        Assert.True((await host.Queue.AiVerdictAsync(b, ReviewDecision.Approve)).Allowed);

        Assert.True(await TryReview(host, a.Id, ReviewDecision.Approve, clock));
        Assert.False(await TryReview(host, b.Id, ReviewDecision.Approve, clock));

        Assert.Equal(1, module.SendCalls);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(b.Id))!.Status);
        Assert.Contains(audit.Entries, e => e.ActionId == b.Id && e.Event == "review_refused" && (e.Detail ?? "").Contains("approved 1 of these", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approvals_made_before_a_restart_still_count_after_it()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var before = Hosts.BuildSqlite(new FakeInboxModule(), ApprovalBudget, db.Path, clock);
        var a = await Submit(before);
        Assert.True(await TryReview(before, a.Id, ReviewDecision.Approve, clock));

        // The Host restarts on the same database. Nothing was kept in memory, so nothing is reset.
        var after = Hosts.BuildSqlite(new FakeInboxModule(), ApprovalBudget, db.Path, clock);
        var b = await Submit(after);

        Assert.False((await after.Queue.AiVerdictAsync(b, ReviewDecision.Approve)).Allowed);
        Assert.False(await TryReview(after, b.Id, ReviewDecision.Approve, clock));
    }

    [Fact]
    public async Task The_window_rolls_so_an_approval_an_hour_old_no_longer_counts()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var host = Hosts.BuildSqlite(new FakeInboxModule(), ApprovalBudget, db.Path, clock);
        var a = await Submit(host);
        Assert.True(await TryReview(host, a.Id, ReviewDecision.Approve, clock));
        var b = await Submit(host);
        Assert.False(await TryReview(host, b.Id, ReviewDecision.Approve, clock));

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.True(await TryReview(host, b.Id, ReviewDecision.Approve, clock));
    }

    [Fact]
    public async Task Two_stores_on_one_database_count_one_refusal_once_the_budget_is_used()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var host = Hosts.BuildSqlite(new FakeInboxModule(), RefusalBudget, db.Path, clock);
        var a = await Submit(host);
        var b = await Submit(host);
        Assert.True(await TryReview(host, a.Id, ReviewDecision.Reject, clock));

        var other = Hosts.BuildSqlite(new FakeInboxModule(), RefusalBudget, db.Path, clock);
        Assert.False((await other.Queue.AiVerdictAsync(b, ReviewDecision.Reject)).Allowed);
        Assert.False(await TryReview(other, b.Id, ReviewDecision.Reject, clock));
    }

    // ---- the planner's follow-up: a second pass by the same reviewer on the same action ----

    [Fact]
    public async Task A_reviewer_that_has_passed_an_action_cannot_pass_it_again_and_the_refusal_is_audited()
    {
        var (host, _, audit) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""");
        var action = await Submit(host);
        var pass = new ActionReview("bot", ReviewDecision.Escalate, "not sure", DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };
        await host.Queue.ReviewAsync(action.Id, pass);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(action.Id, pass));

        Assert.Contains("already passed this action to a person", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await host.Store.GetAsync(action.Id))!.Reviews.Count(v => v.Decision == ReviewDecision.Escalate));
        Assert.Contains(audit.Entries, e => e.ActionId == action.Id && e.Event == "review_refused" && e.Detail == "already passed to a person by this reviewer");
    }
}
