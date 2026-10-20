using Microsoft.Data.Sqlite;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>The tamper-evident audit trail: a hash chain over the entries, --verify-audit and checkpoints.</summary>
public class AuditChainTests
{
    private static async Task<SqliteAuditSink> Filled(TempDb db, int n = 5)
    {
        var sink = new SqliteAuditSink(new SqliteActionStore(db.Path));
        for (var i = 0; i < n; i++)
            await sink.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "approved", "act_" + i, "mail.send", "admin:rita", i % 2 == 0 ? null : "note " + i));
        return sink;
    }

    private static void Sql(TempDb db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task An_untouched_chain_verifies()
    {
        using var db = new TempDb();
        var sink = await Filled(db);
        var v = AuditChain.Verify(sink.ReadChain());
        Assert.True(v.Intact);
        Assert.Equal((5, 0), (v.Entries, v.Unchained));
    }

    [Theory]
    [InlineData("UPDATE audit SET detail='approved by someone else' WHERE seq=3", "entry 3 (approved) was changed")]
    [InlineData("DELETE FROM audit WHERE seq=3", "entry 4 (approved) does not follow")]
    [InlineData("DELETE FROM audit WHERE seq=1", "entry 2 (approved) follows an entry that is no longer there")]
    [InlineData("UPDATE audit SET hash=NULL, prev_hash=NULL WHERE seq=4", "entry 4 (approved) has no hash")]
    public async Task Editing_or_removing_an_entry_breaks_the_chain_at_that_point(string tamper, string expected)
    {
        using var db = new TempDb();
        var sink = await Filled(db);
        TempDb.ClearPools(db.Path);
        Sql(db, tamper);
        var v = AuditChain.Verify(sink.ReadChain());
        Assert.False(v.Intact);
        Assert.Contains(expected, v.FirstBreak);
    }

    [Fact]
    public async Task Removing_the_newest_entries_is_caught_only_against_a_checkpoint()
    {
        using var db = new TempDb();
        var sink = await Filled(db);
        var checkpoint = AuditChain.Head(sink.ReadChain())!;
        TempDb.ClearPools(db.Path);
        Sql(db, $"DELETE FROM audit WHERE seq={checkpoint.Seq}");

        Assert.True(AuditChain.Verify(sink.ReadChain()).Intact);           // the rest still chains
        var v = AuditChain.Verify(sink.ReadChain(), [checkpoint]);
        Assert.False(v.Intact);
        Assert.StartsWith("MISSING checkpoint", Assert.Single(v.Checkpoints));
    }

    [Fact]
    public async Task Entries_from_before_the_chain_are_counted_as_unverifiable_not_broken()
    {
        using var db = new TempDb();
        var sink = new SqliteAuditSink(new SqliteActionStore(db.Path));
        TempDb.ClearPools(db.Path);
        // An entry written by a Host older than schema 5: no hash.
        Sql(db, "INSERT INTO audit(seq,at,event) VALUES(1,'2026-01-01T00:00:00.0000000+00:00','old')");
        await sink.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "approved", "act_x", "mail.send", "admin:rita", null));
        var v = AuditChain.Verify(sink.ReadChain());
        Assert.True(v.Intact);
        Assert.Equal((2, 1), (v.Entries, v.Unchained));
    }

    [Fact]
    public async Task The_host_writes_a_chained_trail()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).Build();
        var r = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"b"}""", new ActionRequester(RequesterKind.User, "me")));
        await host.Queue.ReviewAsync(r.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        var chain = ((SqliteAuditSink)host.Audit).ReadChain();
        Assert.True(chain.Count >= 3);
        Assert.True(AuditChain.Verify(chain).Intact);
    }

    [Fact]
    public async Task The_cli_prints_a_checkpoint_and_verifies_against_it()
    {
        using var db = new TempDb();
        await Filled(db);
        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--audit-checkpoint"], "t", _ => { }, output));
        var checkpoint = output.ToString().Trim();

        output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--verify-audit", "--checkpoint", checkpoint], "t", _ => { }, output));
        Assert.Contains("Chain intact.", output.ToString());
        Assert.Contains("ok checkpoint " + checkpoint, output.ToString());

        TempDb.ClearPools(db.Path);
        Sql(db, "UPDATE audit SET actor='admin:mallory' WHERE seq=2");
        output = new StringWriter();
        Assert.Equal(1, await PolicyCli.TryRunAsync(["--db", db.Path, "--verify-audit", "--checkpoint", checkpoint], "t", _ => { }, output));
        Assert.Contains("BROKEN: entry 2", output.ToString());
    }
}
