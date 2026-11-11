namespace OpenTail.Host.Tests;

/// <summary>
/// Phase 2 of docs/design/governance-naming-plan.md: an approved action is re-checked at its claim. The request must not have
/// expired, the policy must be the one it was decided under, and the arguments that would run must be the ones approved. A refused
/// approval runs nothing and is audited with its reason.
/// </summary>
public class RecheckAtClaimTests
{
    private const string Policy = """{ "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    // The same tool and mode, a different TaintWindow: a different policy, and still a valid one (no tool is named that does not exist).
    private const string ChangedPolicy = """{ "TaintWindow": "00:00:01", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    private static readonly ActionRequester Bot = new(RequesterKind.AiSession, "key:bot");

    private static ActionRequest Send(OpenTailHost host, string body = "hi") =>
        new("mail.send", $$"""{"to":"a@b.c","body":"{{body}}"}""", Bot, host.Sessions.Start("key:bot"));

    private static ActionReview Approve(string? edited = null) =>
        new("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita"), EditedArgumentsJson = edited };

    private static List<string> Events(OpenTailHost host, string actionId) =>
        ((IAuditReader)host.Audit).Read(actionId).Select(e => e.Event).ToList();

    [Fact]
    public async Task An_approved_action_runs_and_its_consumption_is_audited_before_it_starts()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));

        var done = await host.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Single(module.Sent);
        Assert.Equal(
            ["policy_decision", "approval_requested", "approval_allowed", "approval_consumed", "execution_started", "execution_succeeded"],
            Events(host, pending.Id));
    }

    [Fact]
    public async Task A_reviewers_edit_runs_and_its_digest_is_kept_with_the_review()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host, "original"));
        var edited = """{"to":"a@b.c","body":"edited"}""";

        var done = await host.Queue.ReviewAsync(pending.Id, Approve(edited));

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal("edited", Assert.Single(module.Sent).Body);
        Assert.Equal(ArgumentDigest.Of(edited), done.Reviews.Single().EditedArgumentsHash);
    }

    [Fact]
    public async Task Arguments_changed_after_submission_are_refused_and_nothing_runs()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host, "original"));
        // The stored arguments change underneath; the digest made at submission no longer matches them.
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Pending,
            r => r with { ArgumentsJson = """{"to":"x@y.z","body":"tampered"}""" });

        var done = await host.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("action_digest_mismatch", done.Error!.Code);
        Assert.Empty(module.Sent);
        var events = Events(host, pending.Id);
        Assert.Contains("execution_denied", events);
        Assert.DoesNotContain("approval_consumed", events);
        Assert.DoesNotContain("execution_started", events);
    }

    [Fact]
    public async Task An_approval_that_was_in_time_but_has_expired_by_its_claim_is_refused()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));
        // Approved in time, and the deadline passes before the claim (a retried or recovered action, for instance). A review after the
        // deadline is refused outright, as it always was; this is the path that was not checked.
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved,
            r => r with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var done = await host.Executor.ExecuteAsync(pending.Id);

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("approval_expired", done.Error!.Code);
        Assert.Empty(module.Sent);
        Assert.Contains("execution_denied", Events(host, pending.Id));
    }

    [Fact]
    public async Task An_approval_under_a_changed_policy_is_refused_and_nothing_runs()
    {
        using var db = new TempDb();
        var first = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(first).UsePolicy(PolicyConfig.Parse(Policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));

        // Restart over the same database under a different policy file.
        var second = new FakeInboxModule();
        var store2 = new SqliteActionStore(db.Path);
        var restarted = new OpenTailHostBuilder().AddModule(second).UsePolicy(PolicyConfig.Parse(ChangedPolicy))
            .UseStore(store2).UseAudit(new SqliteAuditSink(store2)).Build();

        var done = await restarted.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("policy_version_mismatch", done.Error!.Code);
        Assert.Empty(second.Sent);
        Assert.Empty(first.Sent);
    }

    [Fact]
    public async Task The_same_policy_after_a_restart_still_lets_the_approval_run()
    {
        using var db = new TempDb();
        var first = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(first).UsePolicy(PolicyConfig.Parse(Policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));

        var second = new FakeInboxModule();
        var store2 = new SqliteActionStore(db.Path);
        var restarted = new OpenTailHostBuilder().AddModule(second).UsePolicy(PolicyConfig.Parse(Policy))
            .UseStore(store2).UseAudit(new SqliteAuditSink(store2)).Build();

        var done = await restarted.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Single(second.Sent);
    }
}
