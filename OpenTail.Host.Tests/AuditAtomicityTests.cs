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
}
