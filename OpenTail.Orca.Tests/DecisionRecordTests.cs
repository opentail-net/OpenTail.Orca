using Microsoft.Data.Sqlite;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Audit D3: when retention deletes an action, a decision record is kept (how it was decided, never what it carried),
/// bound into the audit chain by its batch's digest, and --verify-audit checks it.</summary>
public class DecisionRecordTests
{
    private const string Policy = """{ "RetentionDays": 1, "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    private static async Task<(OpenTailHost Host, string ActionId)> PurgedAsync(TempDb db)
    {
        // Created long ago by the clock, so the first maintenance sweep (which purges against real time) removes it.
        var clock = new ManualClock(DateTimeOffset.Parse("2020-01-01T09:00:00Z"));
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path, clock);
        var s = host.Sessions.Start("key:bot");
        var send = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"ceo@corp.example","body":"secret plan"}""",
            new ActionRequester(RequesterKind.AiSession, "key:bot"), s));
        await host.Queue.ReviewAsync(send.Id, new ActionReview("rita", ReviewDecision.Reject, "Not now.", clock.GetUtcNow()) { Subject = "admin:rita" });
        await host.RunMaintenanceOnceAsync();
        return (host, send.Id);
    }

    private static void Sql(TempDb db, string sql)
    {
        TempDb.ClearPools(db.Path);
        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_purged_action_leaves_a_decision_record_without_its_arguments()
    {
        using var db = new TempDb();
        var (host, id) = await PurgedAsync(db);
        Assert.Null(await host.Store.GetAsync(id));
        var (record, json, _) = ((SqliteActionStore)host.Store).ReadDecisionRecords().Single();
        Assert.Equal((id, "mail.send", "Rejected", "key:bot"), (record.ActionId, record.Tool, record.Status, record.Requester));
        Assert.Equal("Not now.", record.Reviews.Single().Reason);
        Assert.Equal("admin:rita", record.Reviews.Single().Subject);
        // The resolution is kept too: the answer to "how was it decided" outlives the arguments.
        Assert.Equal((ResolutionOutcomes.Denied, "admin:rita"), (record.Resolution!.Outcome, record.Resolution.Resolver));
        Assert.Equal(host.Policy.PolicyHash, record.Resolution.PolicyVersion);
        Assert.Equal(host.Policy.PolicyHash, record.PolicyHash);
        Assert.Contains(record.DecisionTrace, t => t.Check == "queue");
        Assert.DoesNotContain("secret plan", json);
        Assert.DoesNotContain("ceo@corp.example", json);

        var purge = ((IAuditReader)host.Audit).Read().Single(e => e.Event == "retention_purge");
        Assert.Contains($"batch {record.Batch}, digest sha256:", purge.Detail);
    }

    [Fact]
    public async Task The_records_verify_against_the_chain_and_an_edit_or_removal_is_caught()
    {
        using var db = new TempDb();
        await PurgedAsync(db);
        TempDb.ClearPools(db.Path);

        var ok = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--verify-audit"], "host", _ => { }, ok));
        Assert.Contains("1 decision record(s) in 1 purge batch(es): intact", ok.ToString());

        Sql(db, "UPDATE decision_records SET json=replace(json, 'Not now.', 'Approved.'), hash=hash");
        var edited = new StringWriter();
        Assert.Equal(1, await PolicyCli.TryRunAsync(["--db", db.Path, "--verify-audit"], "host", _ => { }, edited));
        Assert.Contains("was changed", edited.ToString());

        Sql(db, "DELETE FROM decision_records");
        var removed = new StringWriter();
        Assert.Equal(1, await PolicyCli.TryRunAsync(["--db", db.Path, "--verify-audit"], "host", _ => { }, removed));
        Assert.Contains("its decision records are gone", removed.ToString());
    }

    [Theory]
    [InlineData("""{ "RetentionDays": 30, "AuditRetentionDays": 10 }""", false)]
    [InlineData("""{ "AuditRetentionDays": 10 }""", false)]
    [InlineData("""{ "RetentionDays": 30, "AuditRetentionDays": 400 }""", true)]
    [InlineData("""{ "RetentionDays": 30 }""", true)]
    public void Audit_retention_is_never_shorter_than_action_retention(string json, bool valid) =>
        Assert.Equal(valid, PolicyConfig.Parse(json).Validate([]).Count == 0);
}
