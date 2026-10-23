using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Audit D4: changes to who could do what leave entries (API keys at each start, profiles and people with each new policy
/// version), and access can be rebuilt as of a date.</summary>
public class AccessChangeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T09:00:00Z");

    [Fact]
    public async Task Keys_added_rotated_reprofiled_and_removed_are_audited_once_each()
    {
        var snapshots = new InMemoryAccessSnapshotStore();
        var audit = new InMemoryAuditSink();
        async Task<List<string>> Start(params KeyAccess[] keys)
        {
            var before = audit.Entries.Count;
            await AccessChanges.RecordKeysAsync(snapshots, audit, keys, T0, "ops");
            return [.. audit.Entries.Skip(before).Select(e => $"{e.Event} {e.Detail}")];
        }

        var first = await Start(KeyAccess.Of("bot", "sha256:aaaaaaaaaaaaaaaaaaaaaaaa", "clerk"), KeyAccess.Of("ci", "sha256:bbbbbbbbbbbbbbbbbbbb", null));
        Assert.Equal(["key_added key:bot (sha256:aaaaaaaaaaaa…) profile clerk", "key_added key:ci (sha256:bbbbbbbbbbbb…) profile none"], first);
        Assert.Empty(await Start(KeyAccess.Of("bot", "sha256:aaaaaaaaaaaaaaaaaaaaaaaa", "clerk"), KeyAccess.Of("ci", "sha256:bbbbbbbbbbbbbbbbbbbb", null)));

        var changed = await Start(KeyAccess.Of("bot", "sha256:cccccccccccccccccccccc", "finance"));
        Assert.Equal([
            "key_rotated key:bot (sha256:aaaaaaaaaaaa… -> sha256:cccccccccccc…)",
            "key_profile_changed key:bot: profile clerk -> finance",
            "key_removed key:ci (sha256:bbbbbbbbbbbb…)"], changed);
        Assert.DoesNotContain(audit.Entries, e => e.Detail!.Contains("aaaaaaaaaaaaaaaaaaaaaaaa"));   // only a prefix of the hash
    }

    private const string PolicyA = """{ "Agents": { "clerk": { "Tools": [ "inbox.*" ] }, "old": { "Tools": [ "clock.*" ] } }, "People": { "rita": [ "admin:rita" ] } }""";
    private const string PolicyB = """{ "Agents": { "clerk": { "Tools": [ "inbox.*", "mail.*" ] }, "new": { "Tools": [ "clock.*" ] } }, "People": { "rita": [ "admin:rita", "key:rita-bot" ] } }""";

    [Fact]
    public async Task A_new_policy_version_audits_what_changed_in_profiles_and_people_and_a_restart_audits_nothing_new()
    {
        using var db = new TempDb();
        var a = Hosts.BuildSqlite(new FakeInboxModule(), PolicyA, db.Path);
        await a.RunMaintenanceOnceAsync();
        TempDb.ClearPools(db.Path);
        var b = Hosts.BuildSqlite(new FakeInboxModule(), PolicyB, db.Path);
        await b.RunMaintenanceOnceAsync();
        TempDb.ClearPools(db.Path);
        var again = Hosts.BuildSqlite(new FakeInboxModule(), PolicyB, db.Path);
        await again.RunMaintenanceOnceAsync();

        var changes = ((IAuditReader)again.Audit).Read().Where(e => e.Event is "profile_added" or "profile_changed" or "profile_removed" or "people_changed")
            .Select(e => $"{e.Event} {e.Detail}").ToList();
        Assert.Equal([
            "profile_changed clerk: tools +mail.*",
            "profile_added new: tools clock.*",
            "profile_removed old",
            "people_changed rita: subjects +key:rita-bot"], changes);
    }

    [Fact]
    public async Task The_access_report_rebuilds_keys_profiles_and_levels_as_of_a_date()
    {
        using var db = new TempDb();
        var store = new SqliteActionStore(db.Path);
        var audit = new SqliteAuditSink(store);
        var versions = new SqlitePolicyVersionStore(store);
        var snapshots = new SqliteAccessSnapshotStore(store);
        var a = new PolicyResolver(PolicyConfig.Parse(PolicyA));
        var b = new PolicyResolver(PolicyConfig.Parse(PolicyB));

        await PolicyLoad.RecordAsync(a, versions, audit, T0, "ops");
        await AccessChanges.RecordKeysAsync(snapshots, audit, [KeyAccess.Of("bot", "sha256:aaaaaaaaaaaaaaaaaaaa", "clerk")], T0, "ops");
        await audit.AppendAsync(new AuditEntry(T0.AddDays(1), "level_changed", null, "mail.send", "key:bot", "Normal -> Elevated: decoy read"));
        await PolicyLoad.RecordAsync(b, versions, audit, T0.AddDays(5), "ops");
        await AccessChanges.RecordKeysAsync(snapshots, audit, [KeyAccess.Of("bot", "sha256:aaaaaaaaaaaaaaaaaaaa", "new")], T0.AddDays(5), "ops");
        await audit.AppendAsync(new AuditEntry(T0.AddDays(6), "level_cleared", null, null, "rita", "key:bot: Elevated -> Normal"));

        var early = AccessChanges.Report(audit.Read(), versions, [], T0.AddDays(2));
        Assert.Contains($"Policy in force: {a.PolicyHash}", early);
        Assert.Contains("  key:bot  profile clerk: tools inbox.*", early);
        Assert.Contains("  key:bot: Normal -> Elevated: decoy read", early);

        var late = AccessChanges.Report(audit.Read(), versions, [], T0.AddDays(7));
        Assert.Contains($"Policy in force: {b.PolicyHash}", late);
        Assert.Contains("  key:bot  profile new: tools clock.*", late);
        Assert.DoesNotContain(late, l => l.Contains("Elevated"));

        TempDb.ClearPools(db.Path);
        var cli = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--access-report", "--at", T0.AddDays(2).ToString("O")], "host", _ => { }, cli));
        Assert.Contains("profile clerk", cli.ToString());
    }

    [Fact]
    public async Task A_manual_suspension_is_in_the_access_report_until_it_is_cleared()
    {
        var clock = new ManualClock(T0);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse(Hosts.SendQueued)).Build();
        var trail = (IAuditReader)host.Audit;

        await host.Levels.SuspendAsync("user:ops", "rita", "incident 42");
        clock.Advance(TimeSpan.FromDays(1));
        var whileSuspended = AccessChanges.Report(trail.Read(), new InMemoryPolicyVersionStore(), [], clock.GetUtcNow());
        await host.Levels.ClearAsync("user:ops", "rita", keyRotated: false);
        clock.Advance(TimeSpan.FromDays(1));
        var afterClearing = AccessChanges.Report(trail.Read(), new InMemoryPolicyVersionStore(), [], clock.GetUtcNow());

        Assert.Contains(whileSuspended, l => l == "  user:ops: Normal -> Suspended: incident 42 (by rita)");
        Assert.DoesNotContain(afterClearing, l => l.Contains("user:ops"));
    }
}
