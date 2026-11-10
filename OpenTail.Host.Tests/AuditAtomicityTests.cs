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

    private static ActionRequest Mail() => new("mail.send", """{"to":"a@b.c","body":"hi"}""", Me, null);

    private static ActionReview Decide(ReviewDecision decision) => new("alice", decision, "reason", DateTimeOffset.UtcNow);

    private static OpenTailHost Build(TempDb db, string policy, TimeProvider? clock = null)
    {
        var store = new SqliteActionStore(db.Path);
        var builder = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
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
}
