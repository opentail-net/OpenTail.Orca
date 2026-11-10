using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Tests;

/// <summary>An action's outcome and the audit entry that records it are committed together, in one transaction, when the trail is the
/// store's own database. An outcome whose entry cannot be written is not committed either.</summary>
public sealed class AuditAtomicityTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    [Fact]
    public async Task An_outcome_is_not_committed_without_its_audit_entry()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }"""))
            .UseStore(store).UseAudit(new SqliteAuditSink(store)).Build();
        var pending = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"hi"}""", Me, host.Sessions.Start()));
        var approved = await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);

        // The trail refuses the outcome's entry, as a full disk would. The entry is written in the outcome's own transaction, so the outcome
        // must not be committed either.
        using (var c = new SqliteConnection($"Data Source={db.Path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER refuse_outcome BEFORE INSERT ON audit WHEN NEW.event = 'execution_succeeded' " +
                              "BEGIN SELECT RAISE(ABORT, 'audit trail unavailable'); END;";
            cmd.ExecuteNonQuery();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => host.Executor.ExecuteAsync(approved!.Id));

        Assert.Equal(1, module.SendCalls); // the external effect happened; the outcome is unknown to the Host, not recorded as Succeeded
        var id = approved!.Id;
        Assert.Equal(ActionStatus.Executing, (await host.Store.GetAsync(id))!.Status);
        Assert.DoesNotContain(new SqliteAuditSink(store).Read(id), e => e.Event == "execution_succeeded");
    }

    private const string QueueMail = """{ "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    private static ActionRequest Mail(string? session = null) => new("mail.send", """{"to":"a@b.c","body":"hi"}""", Me, session);

    private static ActionReview Decide(ReviewDecision decision) => new("alice", decision, "reason", DateTimeOffset.UtcNow);

    private static OpenTailHost Build(TempDb db, string policy, TimeProvider? clock = null, IHostModule? module = null)
    {
        var store = new SqliteActionStore(db.Path);
        var builder = new OpenTailHostBuilder().AddModule(module ?? new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse(policy))
            .UseStore(store).UseAudit(new SqliteAuditSink(store));
        if (clock is not null) builder.UseClock(clock);
        return builder.Build();
    }

    /// <summary>Makes the trail refuse one event, inside the transaction that would write it.</summary>
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
    public async Task A_queued_action_is_not_stored_without_its_queued_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        RefuseTrailEvent(db, "queued");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.SubmitAsync(Mail()));

        Assert.Empty(await host.Store.QueryAsync(new ActionQuery { ToolName = "mail.send" }));
    }

    [Fact]
    public async Task An_approval_is_not_recorded_without_its_approved_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        var queued = await host.Queue.SubmitAsync(Mail());
        RefuseTrailEvent(db, "approved");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.ReviewAsync(queued.Id, Decide(ReviewDecision.Approve)));

        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task A_rejection_is_not_recorded_without_its_rejected_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        var queued = await host.Queue.SubmitAsync(Mail());
        RefuseTrailEvent(db, "rejected");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.ReviewAsync(queued.Id, Decide(ReviewDecision.Reject)));

        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task A_cancellation_is_not_recorded_without_its_cancelled_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        var queued = await host.Queue.SubmitAsync(Mail());
        RefuseTrailEvent(db, "cancelled");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.CancelAsync(queued.Id, Me));

        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task An_expiry_is_not_recorded_without_its_expired_entry()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = Build(db, """{ "Tools": { "mail.send": { "Mode": "Queue", "ExpireAfter": "00:01:00" } } }""", clock);
        var queued = await host.Queue.SubmitAsync(Mail());
        clock.Advance(TimeSpan.FromMinutes(2));
        RefuseTrailEvent(db, "expired");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.ExpireDueAsync());

        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task A_denial_is_not_stored_without_its_denied_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        RefuseTrailEvent(db, "denied");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.SubmitAsync(new ActionRequest("no.such.tool", "{}", Me, null)));

        Assert.Empty(await host.Store.QueryAsync(new ActionQuery { ToolName = "no.such.tool" }));
    }

    [Fact]
    public async Task An_interrupted_action_is_not_recorded_as_failed_without_its_entry()
    {
        // The process stopped while the call was running: its lease has lapsed, and recovery records the interruption.
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = Build(db, QueueMail, clock);
        var queued = await host.Queue.SubmitAsync(Mail());
        await host.Store.TransitionAsync(queued.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        await host.Store.TransitionAsync(queued.Id, ActionStatus.Approved, ActionStatus.Executing,
            r => r with { LeaseUntil = clock.GetUtcNow() - TimeSpan.FromMinutes(1) });
        RefuseTrailEvent(db, "interrupted");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Executor.RecoverAsync());

        Assert.Equal(ActionStatus.Executing, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task An_automatic_retry_is_not_recorded_without_its_entry()
    {
        // A transient failure of an idempotent tool is scheduled for a retry; the retry is recorded when the sweep runs it.
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = Build(db, """{ "Tools": { "svc.put": { "Mode": "Execute" } } }""", clock, new FlakyModule());
        var failed = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Me, null));
        Assert.Equal(ActionStatus.Failed, failed.Status);
        clock.Advance(TimeSpan.FromMinutes(5));
        RefuseTrailEvent(db, "retry_automatic");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Executor.RetryDueAsync());

        Assert.Equal(ActionStatus.Failed, (await host.Store.GetAsync(failed.Id))!.Status);
    }

    [Fact]
    public async Task A_reviewer_retry_is_not_recorded_without_its_entry()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = Build(db, """{ "Tools": { "mail.send": { "Mode": "Execute", "RateLimit": { "PerHour": 1 } } } }""", clock);
        var session = host.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Mail(session))).Status);
        var limited = await host.Queue.SubmitAsync(Mail(session));
        Assert.Equal("rate_limited", limited.Error!.Code);
        clock.Advance(TimeSpan.FromHours(2));
        RefuseTrailEvent(db, "retry_approved");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.ReviewAsync(limited.Id, Decide(ReviewDecision.Retry)));

        Assert.Equal(ActionStatus.Failed, (await host.Store.GetAsync(limited.Id))!.Status);
    }

    [Fact]
    public async Task A_late_delayed_action_is_not_held_without_its_entry()
    {
        // Past the time it was due by more than its undo window: the sweep holds it for a person, clearing its run time.
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = Build(db, """{ "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "00:10:00" } } }""", clock);
        var delayed = await host.Queue.SubmitAsync(Mail(host.Sessions.Start()));
        Assert.NotNull(delayed.RunAt);
        clock.Advance(TimeSpan.FromMinutes(25));
        RefuseTrailEvent(db, "delay_held");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.RunDueDelayedAsync());

        Assert.NotNull((await host.Store.GetAsync(delayed.Id))!.RunAt);
    }

    [Fact]
    public async Task A_claim_is_not_recorded_without_its_started_entry()
    {
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        var queued = await host.Queue.SubmitAsync(Mail());
        await host.Store.TransitionAsync(queued.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        RefuseTrailEvent(db, "execution_started");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Executor.ExecuteAsync(queued.Id));

        Assert.Equal(ActionStatus.Approved, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    [Fact]
    public async Task A_call_created_as_running_is_not_stored_without_its_started_entry()
    {
        using var db = new TempDb();
        var host = Build(db, """{ "Tools": { "mail.send": { "Mode": "Execute" } } }""");
        RefuseTrailEvent(db, "execution_started");

        await Assert.ThrowsAnyAsync<Exception>(() => host.Queue.SubmitAsync(Mail(host.Sessions.Start())));

        Assert.Empty(await host.Store.QueryAsync(new ActionQuery { ToolName = "mail.send" }));
    }

    [Fact]
    public async Task A_change_made_through_the_store_is_not_applied_without_its_entry()
    {
        // The operator's cancel-pending command uses this call directly, with the trail in the same database.
        using var db = new TempDb();
        var host = Build(db, QueueMail);
        var queued = await host.Queue.SubmitAsync(Mail());
        var store = new SqliteActionStore(db.Path);
        RefuseTrailEvent(db, "cancelled");

        await Assert.ThrowsAnyAsync<Exception>(() => store.TransitionAuditedAsync(queued.Id, ActionStatus.Pending, ActionStatus.Cancelled,
            r => r, d => new AuditEntry(d.UpdatedAt, "cancelled", d.Id, d.ToolName, "operator", null)));

        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(queued.Id))!.Status);
    }

    /// <summary>A tool that fails once with a transient error, and succeeds after that.</summary>
    private sealed class FlakyModule : IHostModule
    {
        private int _calls;

        public string Id => "svc";

        public void Register(IToolRegistry registry) =>
            registry.Add(new ToolDescriptor("svc.put", "Idempotent write.", ToolRisk.Write, Id) { IdempotentByKey = true },
                AIFunctionFactory.Create(() =>
                {
                    if (_calls++ == 0) throw new ToolTransientException("busy");
                    return "ok";
                }, "svc.put", "Idempotent write."));
    }
}
