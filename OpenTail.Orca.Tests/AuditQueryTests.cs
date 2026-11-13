using Microsoft.Data.Sqlite;

namespace OpenTail.Orca.Tests;

/// <summary>The audit query (filters, a sequence cursor and counts) gives the same answers in memory and in the database.</summary>
public class AuditQueryTests
{
    private static readonly DateTimeOffset Day = DateTimeOffset.Parse("2026-11-13T09:00:00Z");

    private static async Task Fill(IAuditSink sink)
    {
        // Ten reviews of the same tool: bot approves the even ones, bot2 rejects the odd ones. One review of bot is ten days old.
        for (var i = 0; i < 10; i++)
            await sink.AppendAsync(new AuditEntry(Day.AddMinutes(i), "ai_review", "act_" + i, "mail.send", i % 2 == 0 ? "ai:bot" : "ai:bot2",
                (i % 2 == 0 ? "Approve" : "Reject") + "; model m; prompt p; 0 check(s) reported"));
        await sink.AppendAsync(new AuditEntry(Day.AddDays(-10), "ai_review", "act_old", "mail.send", "ai:bot", "Approve; model m; prompt p; 0 check(s) reported"));
        await sink.AppendAsync(new AuditEntry(Day, "review_refused", "act_0", "mail.send", "ai:bot", "not assigned to this tool"));
        await sink.AppendAsync(new AuditEntry(Day, "approval_allowed", "act_0", "mail.send", "key:alice-agent", "ai:bot"));
    }

    private static void Check(IAuditQuery q)
    {
        var bot = new AuditQuery { Event = "ai_review", Actor = "ai:bot" };
        Assert.Equal(6, q.Count(bot));
        Assert.Equal(6, q.Count(bot with { DetailStartsWith = "Approve;" }));
        Assert.Equal(5, q.Count(new AuditQuery { Event = "ai_review", Actor = "ai:bot2", DetailStartsWith = "Reject;" }));
        Assert.Equal(5, q.Count(bot with { Since = Day.AddDays(-1) }));     // the ten-day-old review is outside the window
        Assert.Equal(["ai:bot", "ai:bot2"], q.Actors("ai_review").ToArray());

        // Pages of four, newest first, each starting before the last sequence number of the one before: nothing repeated, nothing skipped.
        var seqs = new List<long>();
        long? before = null;
        for (var page = q.Page(bot with { Limit = 4 }); page.Count > 0; page = q.Page(bot with { Limit = 4, Before = before }))
        {
            Assert.All(page, row => Assert.Equal("ai:bot", row.Entry.Actor));
            seqs.AddRange(page.Select(row => row.Seq));
            before = page[^1].Seq;
        }
        Assert.Equal(6, seqs.Count);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.True(seqs.SequenceEqual(seqs.OrderByDescending(s => s)));

        Assert.Equal(13, q.Page(new AuditQuery { Limit = 100 }).Count);     // the whole trail, every event
    }

    [Fact]
    public async Task The_in_memory_trail_filters_pages_and_counts()
    {
        var sink = new InMemoryAuditSink();
        await Fill(sink);
        Check(sink);
    }

    [Fact]
    public async Task The_database_trail_filters_pages_and_counts()
    {
        using var db = new TempDb();
        var store = new SqliteActionStore(db.Path);
        var sink = new SqliteAuditSink(store);
        await Fill(sink);
        Check(sink);
    }

    [Fact]
    public void The_database_has_the_index_the_query_uses()
    {
        using var db = new TempDb();
        var store = new SqliteActionStore(db.Path);
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_audit_event_actor'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }
}
