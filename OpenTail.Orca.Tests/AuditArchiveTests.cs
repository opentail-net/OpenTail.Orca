using Microsoft.Data.Sqlite;

namespace OpenTail.Orca.Tests;

/// <summary>
/// The audit trail's live window and its archive (reviewer gate plan, D-41): old entries leave the live database only after they are written to an
/// archive file and verified, the live trail verifies from the cut, a broken chain is never archived, and archives expire on their own terms.
/// </summary>
public class AuditArchiveTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T09:00:00Z");
    private static readonly TimeSpan Live = TimeSpan.FromDays(28);

    /// <summary>A database, a sink over it, and a directory for its archive files, cleaned up afterwards.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TempDb Db = new();
        public readonly SqliteActionStore Store;
        public readonly SqliteAuditSink Sink;
        public readonly AuditArchiver Archiver;
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "otail-archive-" + Guid.NewGuid().ToString("N"));

        public Rig()
        {
            Store = new SqliteActionStore(Db.Path);
            Sink = new SqliteAuditSink(Store);
            Archiver = new AuditArchiver(Store);
        }

        public void Dispose()
        {
            Db.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
    }

    /// <summary>One hundred entries from forty days ago (seq 1 to 100).</summary>
    private static async Task FillOld(SqliteAuditSink sink)
    {
        for (var i = 0; i < 100; i++)
            await sink.AppendAsync(new AuditEntry(Now.AddDays(-40).AddMinutes(i), "ai_review", "act_" + i, "mail.send", "ai:bot",
                "Approve; model m; prompt p; 0 check(s) reported"));
    }

    /// <summary>Ten entries from the last few minutes (seq 101 to 110, after the old ones).</summary>
    private static async Task FillRecent(SqliteAuditSink sink)
    {
        for (var i = 0; i < 10; i++)
            await sink.AppendAsync(new AuditEntry(Now.AddMinutes(-i), "approval_allowed", "act_r" + i, "mail.send", "key:alice-agent", "ai:bot"));
    }

    [Fact]
    public async Task Old_entries_move_to_one_verified_archive_and_the_live_trail_still_verifies()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);

        var cut = rig.Archiver.Cut(Now, rig.Dir, Live);

        Assert.NotNull(cut);
        Assert.Equal(1, cut.FirstSeq);
        Assert.Equal(100, cut.LastSeq);
        Assert.True(File.Exists(cut.ArchiveFile));
        Assert.Empty(AuditArchive.Verify(cut.ArchiveFile));

        // The live trail: the ten recent entries, then the entry that records the cut. The old ones are gone from it.
        var live = rig.Sink.ReadChain();
        Assert.Equal(11, live.Count);
        Assert.Equal(101, live[0].Seq);
        Assert.Equal("audit_archived", live[^1].Event);
        var verified = AuditChain.Verify(live, [cut.Anchor], cut.Anchor);
        Assert.Null(verified.FirstBreak);
        Assert.True(verified.Intact);
        Assert.Contains(verified.Checkpoints, c => c.StartsWith("ok checkpoint", StringComparison.Ordinal));
        Assert.Equal(cut.LastHash, rig.Archiver.Latest()!.LastHash);

        // Without the cut, a trail that starts after entry 1 is a break: nothing is hidden.
        Assert.NotNull(AuditChain.Verify(live).FirstBreak);
    }

    [Fact]
    public async Task With_nothing_old_enough_nothing_is_archived()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        Assert.NotNull(rig.Archiver.Cut(Now, rig.Dir, Live));

        Assert.Null(rig.Archiver.Cut(Now, rig.Dir, Live));
        Assert.Single(rig.Archiver.Cuts());
    }

    [Fact]
    public async Task A_broken_chain_is_never_archived()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        // Someone with the database file changes an old entry.
        using (var c = rig.Store.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE audit SET detail='tampered' WHERE seq=50";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => rig.Archiver.Cut(Now, rig.Dir, Live));
        Assert.Contains("nothing was archived", ex.Message);
        Assert.Empty(rig.Archiver.Cuts());
        Assert.Equal(110, rig.Sink.ReadChain().Count);
        Assert.False(Directory.Exists(rig.Dir));
    }

    [Fact]
    public async Task A_changed_archive_file_is_found_by_its_verifier()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        var cut = rig.Archiver.Cut(Now, rig.Dir, Live)!;

        File.WriteAllText(cut.ArchiveFile, File.ReadAllText(cut.ArchiveFile).Replace("model m", "model x", StringComparison.Ordinal));

        Assert.NotEmpty(AuditArchive.Verify(cut.ArchiveFile));
    }

    [Fact]
    public async Task A_second_cut_continues_from_the_first()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        var first = rig.Archiver.Cut(Now, rig.Dir, Live)!;

        // Six weeks on, the recent entries and the record of the first cut are old too.
        var second = rig.Archiver.Cut(Now.AddDays(42), rig.Dir, Live)!;

        Assert.Equal(101, second.FirstSeq);
        Assert.Equal(111, second.LastSeq);
        Assert.Equal(2, rig.Archiver.Cuts().Count);
        Assert.Empty(AuditArchive.Verify(first.ArchiveFile));
        Assert.Empty(AuditArchive.Verify(second.ArchiveFile));
        var live = rig.Sink.ReadChain();
        Assert.Single(live);
        Assert.True(AuditChain.Verify(live, null, second.Anchor).Intact);
    }

    [Fact]
    public async Task Expired_archives_are_removed_and_the_removal_is_recorded_in_the_trail()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        var cut = rig.Archiver.Cut(Now, rig.Dir, Live)!;

        Assert.Equal(0, rig.Archiver.ExpireArchives(Now.AddYears(1), TimeSpan.FromDays(2555)));
        Assert.True(File.Exists(cut.ArchiveFile));

        Assert.Equal(1, rig.Archiver.ExpireArchives(Now.AddDays(2600), TimeSpan.FromDays(2555)));
        Assert.False(File.Exists(cut.ArchiveFile));
        Assert.True(rig.Archiver.Cuts().Single().Removed);
        var live = rig.Sink.ReadChain();
        Assert.Contains(live, e => e.Event == "archive_expired");
        Assert.True(AuditChain.Verify(live, null, cut.Anchor).Intact);
    }

    [Fact]
    public async Task The_hosts_maintenance_sweep_archives_the_old_trail_by_default()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), """{ "Tools": { "mail.send": { "Mode": "Queue" } } }""", db.Path);
        for (var i = 0; i < 20; i++)
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow.AddDays(-60).AddMinutes(i), "ai_review", "act_" + i, "mail.send", "ai:bot",
                "Approve; model m; prompt p; 0 check(s) reported"));
        var archive = db.Path + ".archive";   // the default: beside the database
        try
        {
            await host.RunMaintenanceOnceAsync();

            var archiver = new AuditArchiver(new SqliteActionStore(db.Path));
            var cut = Assert.Single(archiver.Cuts());
            Assert.True(File.Exists(cut.ArchiveFile));
            Assert.Equal(Path.GetFullPath(archive), Path.GetDirectoryName(Path.GetFullPath(cut.ArchiveFile)));
            Assert.Empty(AuditArchive.Verify(cut.ArchiveFile));
            Assert.True(AuditChain.Verify(new SqliteAuditSink(new SqliteActionStore(db.Path)).ReadChain(), null, cut.Anchor).Intact);
        }
        finally
        {
            if (Directory.Exists(archive)) Directory.Delete(archive, recursive: true);
        }
    }

    [Fact]
    public async Task A_crash_after_the_record_leaves_a_file_the_next_pass_removes()
    {
        using var rig = new Rig();
        await FillOld(rig.Sink);
        await FillRecent(rig.Sink);
        var cut = rig.Archiver.Cut(Now, rig.Dir, Live)!;
        Assert.Equal(1, rig.Archiver.ExpireArchives(Now.AddDays(2600), TimeSpan.FromDays(2555)));

        // The process stops after the record is written and before the file is deleted: the file is back, and the trail says it is removed.
        File.WriteAllText(cut.ArchiveFile, "left behind by a crash");
        Assert.Equal(0, rig.Archiver.ExpireArchives(Now.AddDays(2601), TimeSpan.FromDays(2555)));
        Assert.False(File.Exists(cut.ArchiveFile));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"format":"opentail-audit-archive/1"}""")]
    [InlineData("""{"format":"opentail-audit-archive/1","entries":null}""")]
    [InlineData("""{"format":"opentail-audit-archive/1","firstSeq":1,"lastSeq":1,"count":1,"entries":[{"seq":1,"at":null,"event":"x","hash":"h","prevHash":null}]}""")]
    [InlineData("""{"format":"something-else"}""")]
    public void A_malformed_archive_is_reported_not_thrown(string text)
    {
        var file = Path.Combine(Path.GetTempPath(), "otail-malformed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, text);
            Assert.NotEmpty(AuditArchive.Verify(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_archive_settings_are_bounded_and_left_out_of_the_policy_text_when_unset()
    {
        Assert.DoesNotContain("AuditLiveDays", PolicyConfig.Parse("""{ "RetentionDays": 0 }""").ToCanonicalJson());
        Assert.DoesNotContain(PolicyConfig.Parse("""{ "AuditLiveDays": 28, "AuditArchiveDays": 2555 }""").Validate([]),
            e => e.Contains("Audit", StringComparison.Ordinal));
        Assert.Contains(PolicyConfig.Parse("""{ "AuditLiveDays": 28, "AuditArchiveDays": 7 }""").Validate([]),
            e => e.Contains("AuditArchiveDays must be at least AuditLiveDays", StringComparison.Ordinal));
    }
}
