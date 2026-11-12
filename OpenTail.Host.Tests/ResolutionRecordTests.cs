using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Tests;

/// <summary>
/// Governance A.3 (reduced scope): every action a person or the Host resolves records how. The outcome, the policy and binding it held, the
/// reason, the time and who resolved it. It is written in the same write as the status change, and it survives a reopen of the database.
/// </summary>
public class ResolutionRecordTests
{
    private const string Policy = """{ "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    // The same tool and mode, a different TaintWindow: a different policy, and still a valid one.
    private const string ChangedPolicy = """{ "TaintWindow": "00:00:01", "Tools": { "mail.send": { "Mode": "Queue" } } }""";
    private const string Expiring = """{ "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue", "ExpireAfter": "00:01:00" } } }""";
    private const string Delayed = """{ "TaintWindow": "00:00:00", "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "10m" } } }""";

    private static readonly ActionRequester Bot = new(RequesterKind.AiSession, "key:bot");

    private static ActionRequest Send(OpenTailHost host) =>
        new("mail.send", """{"to":"a@b.c","body":"hi"}""", Bot, host.Sessions.Start("key:bot"));

    private static ActionReview Approve() =>
        new("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };

    private static ActionReview Reject() =>
        new("rita", ReviewDecision.Reject, "Not now.", DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };

    private static OpenTailHost InMemory(FakeInboxModule module, string policy, TimeProvider? clock = null)
    {
        var builder = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(policy));
        if (clock is not null) builder.UseClock(clock);
        return builder.Build();
    }

    private static (OpenTailHost Host, FakeInboxModule Module) Sqlite(TempDb db, string policy)
    {
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store)).Build();
        return (host, module);
    }

    /// <summary>Makes the trail refuse one event, inside the transaction that would write it (as <c>AuditAtomicityTests</c> does).</summary>
    private static void RefuseTrailEvent(TempDb db, string evt)
    {
        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE TRIGGER refuse_entry BEFORE INSERT ON audit WHEN NEW.event = '{evt}' " +
                          "BEGIN SELECT RAISE(ABORT, 'audit trail unavailable'); END;";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_reviewer_approval_is_resolved_as_allowed_with_its_policy_binding_and_approver()
    {
        var module = new FakeInboxModule();
        var host = InMemory(module, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));
        var before = DateTimeOffset.UtcNow;

        var done = await host.Queue.ReviewAsync(pending.Id, Approve());

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.NotNull(done.Resolution);
        var resolution = done.Resolution!;
        Assert.Equal((ResolutionOutcomes.Allowed, Subjects.Admin("rita"), (string?)null), (resolution.Outcome, resolution.Resolver, resolution.ReasonCode));
        Assert.Equal(pending.PolicyHash, resolution.PolicyVersion);
        Assert.Equal(pending.BindingDigest, resolution.BindingDigest);
        Assert.InRange(resolution.At, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_reviewer_rejection_is_resolved_as_denied_with_the_reason_kept_in_the_review()
    {
        var module = new FakeInboxModule();
        var host = InMemory(module, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));

        var done = await host.Queue.ReviewAsync(pending.Id, Reject());

        Assert.Equal(ActionStatus.Rejected, done.Status);
        Assert.NotNull(done.Resolution);
        var resolution = done.Resolution!;
        Assert.Equal((ResolutionOutcomes.Denied, Subjects.Admin("rita"), (string?)null), (resolution.Outcome, resolution.Resolver, resolution.ReasonCode));
        Assert.Equal(pending.PolicyHash, resolution.PolicyVersion);
        Assert.Equal(pending.BindingDigest, resolution.BindingDigest);
        Assert.Equal("Not now.", done.Reviews.Single().Reason);
        Assert.Empty(module.Sent);
    }

    [Fact]
    public async Task An_approval_that_runs_out_of_time_is_resolved_as_expired_by_the_system()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = InMemory(module, Expiring, clock);
        var pending = await host.Queue.SubmitAsync(Send(host));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await host.Queue.ExpireDueAsync());

        var done = (await host.Store.GetAsync(pending.Id))!;
        Assert.Equal(ActionStatus.Expired, done.Status);
        Assert.NotNull(done.Resolution);
        var resolution = done.Resolution!;
        Assert.Equal((ResolutionOutcomes.Expired, "system", ActionExecutor.ReasonApprovalExpired),
            (resolution.Outcome, resolution.Resolver, resolution.ReasonCode));
        Assert.Equal(clock.GetUtcNow(), resolution.At);
        Assert.Empty(module.Sent);
    }

    [Fact]
    public async Task An_approval_given_after_the_deadline_expires_the_action_as_the_system()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = InMemory(module, Expiring, clock);
        var pending = await host.Queue.SubmitAsync(Send(host));
        clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(pending.Id, Approve()));

        var done = (await host.Store.GetAsync(pending.Id))!;
        Assert.Equal(ActionStatus.Expired, done.Status);
        Assert.NotNull(done.Resolution);
        Assert.Equal((ResolutionOutcomes.Expired, "system", ActionExecutor.ReasonApprovalExpired),
            (done.Resolution!.Outcome, done.Resolution.Resolver, done.Resolution.ReasonCode));
        Assert.Empty(module.Sent);
    }

    [Fact]
    public async Task A_cancellation_by_the_requester_is_resolved_as_cancelled()
    {
        var module = new FakeInboxModule();
        var host = InMemory(module, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));

        var done = await host.Queue.CancelAsync(pending.Id, Bot);

        Assert.Equal(ActionStatus.Cancelled, done.Status);
        Assert.NotNull(done.Resolution);
        Assert.Equal((ResolutionOutcomes.Cancelled, "key:bot", (string?)null),
            (done.Resolution!.Outcome, done.Resolution.Resolver, done.Resolution.ReasonCode));
        Assert.Empty(module.Sent);
    }

    [Fact]
    public async Task A_delayed_action_stopped_in_its_undo_window_is_resolved_as_cancelled_by_the_person_who_stopped_it()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = InMemory(module, Delayed, clock);
        var scheduled = await host.Queue.SubmitAsync(Send(host));

        var done = await host.Queue.StopAsync(scheduled.Id, "rita", []);

        Assert.Equal(ActionStatus.Cancelled, done.Status);
        Assert.NotNull(done.Resolution);
        Assert.Equal((ResolutionOutcomes.Cancelled, "rita", "stopped"), (done.Resolution!.Outcome, done.Resolution.Resolver, done.Resolution.ReasonCode));
        Assert.Empty(module.Sent);
    }

    [Fact]
    public async Task A_delayed_action_that_runs_after_its_undo_window_is_resolved_as_allowed_by_the_system()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = InMemory(module, Delayed, clock);
        var scheduled = await host.Queue.SubmitAsync(Send(host));
        clock.Advance(TimeSpan.FromMinutes(11));

        await host.RunMaintenanceOnceAsync();

        var done = (await host.Store.GetAsync(scheduled.Id))!;
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Single(module.Sent);
        Assert.NotNull(done.Resolution);
        Assert.Equal((ResolutionOutcomes.Allowed, "system", (string?)null),
            (done.Resolution!.Outcome, done.Resolution.Resolver, done.Resolution.ReasonCode));
        Assert.Equal(scheduled.BindingDigest, done.Resolution.BindingDigest);
    }

    [Fact]
    public async Task A_refused_claim_is_resolved_as_execution_denied_and_the_resolution_survives_a_reopen()
    {
        using var db = new TempDb();
        var (host, first) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));

        // Restart under a changed policy. The approval was given under the old one, so it does not hold at the claim.
        var (restarted, second) = Sqlite(db, ChangedPolicy);
        var done = await restarted.Queue.ReviewAsync(pending.Id, Approve());
        Assert.Equal(ActionStatus.Failed, done.Status);
        Assert.Empty(first.Sent);
        Assert.Empty(second.Sent);

        // Reopen the same database: the resolution is read back from it.
        var (reopened, _) = Sqlite(db, ChangedPolicy);
        var stored = (await reopened.Store.GetAsync(pending.Id))!;
        Assert.NotNull(stored.Resolution);
        var resolution = stored.Resolution!;
        Assert.Equal((ResolutionOutcomes.ExecutionDenied, "system", ActionExecutor.ReasonPolicyVersionMismatch),
            (resolution.Outcome, resolution.Resolver, resolution.ReasonCode));
        // The policy it was decided under is the old one; the refusal names the mismatch.
        Assert.Equal(pending.PolicyHash, resolution.PolicyVersion);
        Assert.Equal(pending.BindingDigest, resolution.BindingDigest);
    }

    [Fact]
    public async Task A_resolution_is_not_stored_without_the_entry_that_records_its_change()
    {
        using var db = new TempDb();
        var (host, _) = Sqlite(db, Policy);
        var pending = await host.Queue.SubmitAsync(Send(host));
        RefuseTrailEvent(db, "approval_denied");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.ReviewAsync(pending.Id, Reject()));

        var stored = (await host.Store.GetAsync(pending.Id))!;
        Assert.Equal(ActionStatus.Pending, stored.Status);
        Assert.Null(stored.Resolution);
    }
}
