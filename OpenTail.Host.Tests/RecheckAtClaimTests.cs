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

    private static (OpenTailHost Host, FakeInboxModule Module) Sqlite(TempDb db, string policy, TimeProvider? clock = null)
    {
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var builder = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store));
        if (clock is not null) builder = builder.UseClock(clock);
        return (builder.Build(), module);
    }

    [Fact]
    public async Task A_reviewers_retry_under_a_changed_policy_is_refused_and_nothing_runs()
    {
        using var db = new TempDb();
        var (host, first) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));
        // The action was approved and failed in a way a reviewer may retry.
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Approved, ActionStatus.Failed,
            r => r with { Error = new ActionError("tool_unavailable", "down", true) });

        var (restarted, second) = Sqlite(db, ChangedPolicy);
        var done = await restarted.Queue.ReviewAsync(pending.Id,
            new ActionReview("rita", ReviewDecision.Retry, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") });

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("policy_version_mismatch", done.Error!.Code);
        Assert.Empty(first.Sent);
        Assert.Empty(second.Sent);
        Assert.Contains("execution_denied", Events(restarted, pending.Id));
    }

    [Fact]
    public async Task A_delayed_action_that_comes_due_after_a_policy_change_is_refused_and_nothing_runs()
    {
        const string delayed = """{ "TaintWindow": "00:00:00", "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "10m" } } }""";
        const string delayedChanged = """{ "TaintWindow": "00:00:01", "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "10m" } } }""";
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, first) = Sqlite(db, delayed, clock);
        var scheduled = await host.Queue.SubmitAsync(Send(host));
        Assert.NotNull(scheduled.RunAt);

        var (restarted, second) = Sqlite(db, delayedChanged, clock);
        clock.Advance(TimeSpan.FromMinutes(11));
        await restarted.RunMaintenanceOnceAsync();

        var done = (await restarted.Store.GetAsync(scheduled.Id))!;
        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("policy_version_mismatch", done.Error!.Code);
        Assert.Empty(first.Sent);
        Assert.Empty(second.Sent);
    }

    [Fact]
    public async Task A_reviewers_edit_digest_survives_the_sqlite_store()
    {
        using var db = new TempDb();
        var (host, module) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host, "original"));
        var edited = """{"to":"a@b.c","body":"edited"}""";

        var done = await host.Queue.ReviewAsync(pending.Id, Approve(edited));
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal("edited", Assert.Single(module.Sent).Body);

        var (reopened, _) = Sqlite(db, Policy);
        var stored = (await reopened.Store.GetAsync(pending.Id))!;
        Assert.Equal(ArgumentDigest.Of(edited), stored.Reviews.Single().EditedArgumentsHash);
        Assert.Equal(edited, stored.EffectiveArgumentsJson);
    }

    [Fact]
    public async Task A_new_action_carries_a_binding_digest_that_recomputes_from_its_record_and_survives_sqlite()
    {
        using var db = new TempDb();
        var (host, _) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));

        var schema = host.Catalog.SchemaVersionOf("mail.send")!;
        var expected = new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.send", schema, null, pending.ArgumentsJson, pending.PolicyHash).Digest();
        Assert.Equal(expected, pending.BindingDigest);
        Assert.Equal(ArgumentDigest.Of(pending.ArgumentsJson), pending.ArgumentsHash); // the raw digest is unchanged

        var (reopened, _) = Sqlite(db, Policy);
        Assert.Equal(expected, (await reopened.Store.GetAsync(pending.Id))!.BindingDigest);
    }

    [Fact]
    public async Task An_unknown_tool_or_arguments_that_are_not_json_get_no_binding_digest()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).Build();

        var unknown = await host.Queue.SubmitAsync(new ActionRequest("no.such", "{}", Bot, host.Sessions.Start("key:bot")));
        Assert.Null(unknown.BindingDigest);

        var notJson = await host.Queue.SubmitAsync(new ActionRequest("mail.send", "{not json", Bot, host.Sessions.Start("key:bot")));
        Assert.Null(notJson.BindingDigest);
    }

    [Fact]
    public async Task A_tool_whose_parameter_schema_changed_since_approval_is_refused_and_nothing_runs()
    {
        using var db = new TempDb();
        var (host, first) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));

        // Restart over the same database with a module whose mail.send takes one more parameter: a changed tool contract.
        var changed = new MailWithCopyModule();
        var store = new SqliteActionStore(db.Path);
        var restarted = new OpenTailHostBuilder().AddModule(changed).UsePolicy(PolicyConfig.Parse(Policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store)).Build();

        var done = await restarted.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("action_digest_mismatch", done.Error!.Code);
        Assert.Empty(first.Sent);
        Assert.Empty(changed.Sent);
        Assert.Contains("execution_denied", Events(restarted, pending.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_binding_digest_that_does_not_recompute_or_is_missing_is_refused_and_nothing_runs(bool cleared)
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));
        // The binding is changed, or cleared, after the action was submitted and before its approval.
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Pending,
            r => r with { BindingDigest = cleared ? null : "sha256:" + new string('0', 64) });

        var done = await host.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("action_digest_mismatch", done.Error!.Code);
        Assert.Empty(module.Sent);
        var events = Events(host, pending.Id);
        Assert.Contains("execution_denied", events);
        Assert.DoesNotContain("execution_started", events);
    }

    [Fact]
    public async Task A_reviewers_edit_runs_and_its_review_carries_the_binding_of_the_edited_action()
    {
        using var db = new TempDb();
        var (host, module) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host, "original"));
        var edited = """{"to":"a@b.c","body":"edited"}""";

        var done = await host.Queue.ReviewAsync(pending.Id, Approve(edited));

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal("edited", Assert.Single(module.Sent).Body);
        // Computed here from the binding's definition, not through the Host's helper, so the two cannot agree by sharing a bug.
        var expected = new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.send", host.Catalog.SchemaVersionOf("mail.send")!,
            null, edited, pending.PolicyHash).Digest();
        Assert.Equal(expected, done.Reviews.Single().EditedBindingDigest);
    }

    [Fact]
    public async Task A_changed_requester_on_the_stored_record_is_refused_and_nothing_runs()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var pending = await host.Queue.SubmitAsync(Send(host));
        // Another identity is written onto the action; the binding was made for the requester it was submitted by.
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Pending,
            r => r with { Requester = new ActionRequester(RequesterKind.AiSession, "key:other") });

        var done = await host.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Equal("action_digest_mismatch", done.Error!.Code);
        Assert.Empty(module.Sent);
    }

    /// <summary>The module of <see cref="FakeInboxModule"/> with one more parameter on mail.send (a changed tool contract), for a restart.</summary>
    private sealed class MailWithCopyModule : IHostModule
    {
        public string Id => "fake";
        public List<(string To, string Body)> Sent { get; } = [];

        public void Register(IToolRegistry registry) =>
            registry.Add(
                new ToolDescriptor("mail.send", "Send an email.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string to, string body, string? cc) =>
                {
                    lock (Sent) Sent.Add((to, body));
                    return $"sent to {to}";
                }, "mail.send", "Send an email."),
                new SendPreview());
    }
}
