using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Tests;

/// <summary>V1 of the v2 plan: identity, taint that cannot be laundered, ownership, atomic limits, events, migration.</summary>
public class FoundationTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    private static string Policy(string extra = "", string tools = "") =>
        "{ \"DefaultReadMode\": \"Execute\"" + (extra == "" ? "" : ", " + extra) + ", \"Tools\": { " +
        "\"mail.send\": { \"Mode\": \"Queue\" }" + (tools == "" ? "" : ", " + tools) + " } }";

    private static ActionRequest Send(ActionRequester who, string? session = null, string? key = null, string to = "a@b.c") =>
        new("mail.send", "{\"to\":\"" + to + "\",\"body\":\"hi\"}", who, session, key);

    // ---- sessions and taint ----

    [Fact]
    public void A_session_and_its_taint_survive_a_restart()
    {
        using var db = new TempDb();
        string session;
        {
            var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy(), db.Path);
            session = host.Sessions.Start();
            host.Sessions.Taint(session);
        }
        TempDb.ClearPools(db.Path);

        var restarted = Hosts.BuildSqlite(new FakeInboxModule(), Policy(), db.Path);
        Assert.True(restarted.Sessions.Exists(session));
        Assert.True(restarted.Sessions.IsTainted(session));
        // A brand-new session of the same owner is tainted too: restarting did not launder anything.
        Assert.True(restarted.Sessions.IsTainted(restarted.Sessions.Start()));
    }

    [Fact]
    public void Tainting_one_session_taints_the_owners_other_sessions_for_the_window_only()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, _, _) = Hosts.Build(Policy("\"TaintWindow\": \"00:30:00\""), clock);
        var a = host.Sessions.Start();
        var b = host.Sessions.Start();
        Assert.False(host.Sessions.IsTainted(a));

        host.Sessions.Taint(b);
        Assert.True(host.Sessions.IsTainted(a));                   // existing clean session
        Assert.True(host.Sessions.IsTainted(host.Sessions.Start())); // fresh session: start_session cannot launder

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(host.Sessions.IsTainted(a));                  // the window lapsed
        Assert.True(host.Sessions.IsTainted(b));                   // the session that read it stays tainted for good
    }

    [Fact]
    public void A_zero_window_keeps_taint_per_session()
    {
        var (host, _, _) = Hosts.Build(Policy("\"TaintWindow\": \"00:00:00\""));
        var a = host.Sessions.Start();
        var b = host.Sessions.Start();
        host.Sessions.Taint(b);
        Assert.False(host.Sessions.IsTainted(a));
    }

    [Fact]
    public async Task A_queued_untrusted_read_taints_at_submit_not_when_it_runs()
    {
        var (host, _, _) = Hosts.Build(Policy(tools: "\"inbox.read\": { \"Mode\": \"Queue\" }"));
        var s = host.Sessions.Start();
        var read = await host.Queue.SubmitAsync(new ActionRequest("inbox.read", "{}", new ActionRequester(RequesterKind.AiSession, "local"), s));
        Assert.Equal(ActionStatus.Pending, read.Status); // has not run, and nobody has seen its result yet
        Assert.True(host.Sessions.IsTainted(s));
    }

    [Fact]
    public async Task A_denied_untrusted_read_taints_nothing()
    {
        var (host, _, _) = Hosts.Build(Policy(tools: "\"inbox.read\": { \"Mode\": \"Deny\" }"));
        var s = host.Sessions.Start();
        await host.Queue.SubmitAsync(new ActionRequest("inbox.read", "{}", new ActionRequester(RequesterKind.AiSession, "local"), s));
        Assert.False(host.Sessions.IsTainted(s));
    }

    [Fact]
    public async Task Reading_an_untrusted_result_through_get_action_status_taints_the_reader()
    {
        var (host, _, _) = Hosts.Build(Policy("\"TaintWindow\": \"00:00:00\"", "\"inbox.read\": { \"Mode\": \"Queue\" }"));
        await using var pair = await McpPair.StartAsync(host);
        var s = host.Sessions.Start();
        var queued = await pair.CallAsync("inbox.read", ("sessionId", s));
        Assert.Equal("pending_approval", queued["status"]!.ToString());
        var id = queued["actionId"]!.ToString();
        await host.Queue.ReviewAsync(id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        var other = host.Sessions.Start(); // a clean session that has not seen the result
        var reader = host.Sessions.Start();
        Assert.False(host.Sessions.IsTainted(reader));
        // The submit already tainted `s`; `reader` reads the result through the status tool and must be tainted too.
        var status = await pair.CallAsync("get_action_status", ("actionId", id), ("sessionId", reader));
        Assert.Equal("succeeded", status["status"]!.ToString());
        Assert.True(host.Sessions.IsTainted(reader));
        Assert.False(host.Sessions.IsTainted(other));
    }

    [Fact]
    public async Task Idle_sessions_are_purged()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, _, _) = Hosts.Build(Policy("\"SessionIdleTtl\": \"1.00:00:00\""), clock);
        var kept = host.Sessions.Start();
        var gone = host.Sessions.Start();
        clock.Advance(TimeSpan.FromHours(20));
        host.Sessions.Touch(kept);
        clock.Advance(TimeSpan.FromHours(10));
        await host.RunMaintenanceOnceAsync();
        Assert.True(host.Sessions.Exists(kept));
        Assert.False(host.Sessions.Exists(gone));
    }

    // ---- ownership ----

    private sealed class WhoAmI { public CallerIdentity Current = CallerIdentity.Local; }

    private static (OpenTailHost Host, FakeInboxModule Module, WhoAmI Who) TwoUserHost()
    {
        var who = new WhoAmI();
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse(Policy("\"TaintWindow\": \"00:00:00\"")))
            .UseCallerResolver(_ => who.Current).Build();
        return (host, module, who);
    }

    [Fact]
    public async Task One_caller_cannot_see_cancel_or_resubmit_anothers_actions()
    {
        var (host, _, who) = TwoUserHost();
        await using var pair = await McpPair.StartAsync(host);
        var alice = new CallerIdentity(Subjects.Key("alice"), "Alice");
        var bob = new CallerIdentity(Subjects.Key("bob"), "Bob");

        who.Current = alice;
        var session = (await pair.CallAsync("start_session"))["sessionId"]!.ToString();
        var made = await pair.CallAsync("mail.send", ("to", "x@y.z"), ("body", "b"), ("sessionId", session));
        var id = made["actionId"]!.ToString();
        Assert.Equal("pending_approval", made["status"]!.ToString());

        who.Current = bob;
        Assert.Equal("not_found", (await pair.CallAsync("get_action_status", ("actionId", id)))["status"]!.ToString());
        Assert.Equal("error", (await pair.CallAsync("cancel_action", ("actionId", id)))["status"]!.ToString());
        Assert.Empty((await pair.CallAsync("list_my_actions"))["actions"]!.AsArray());
        // Naming Alice's session does not make Bob her: it looks exactly like an unknown id.
        var hijack = await pair.CallAsync("mail.send", ("to", "x@y.z"), ("body", "b"), ("sessionId", session));
        Assert.Equal("invalid_request", hijack["error"]!["code"]!.ToString());
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(id))!.Status);

        who.Current = alice;
        Assert.Equal("pending_approval", (await pair.CallAsync("get_action_status", ("actionId", id)))["status"]!.ToString());
        Assert.Single((await pair.CallAsync("list_my_actions"))["actions"]!.AsArray());
    }

    [Fact]
    public async Task The_same_idempotency_key_from_two_requesters_is_two_actions()
    {
        var (host, module, _) = Hosts.Build(Policy().Replace("\"Queue\"", "\"Execute\""));
        var s1 = host.Sessions.Start("key:alice");
        var s2 = host.Sessions.Start("key:bob");
        var alice = new ActionRequester(RequesterKind.AiSession, "key:alice");
        var bob = new ActionRequester(RequesterKind.AiSession, "key:bob");

        var a = await host.Queue.SubmitAsync(Send(alice, s1, "k1"));
        var b = await host.Queue.SubmitAsync(Send(bob, s2, "k1"));
        var again = await host.Queue.SubmitAsync(Send(alice, s1, "k1"));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, again.Id);
        Assert.Equal(2, module.SendCalls);
        Assert.Equal("key:bob", (await host.Store.GetAsync(b.Id))!.Requester.Id); // Bob never got Alice's record
    }

    // ---- atomic limits ----

    [Fact]
    public async Task The_pending_cap_holds_across_two_hosts_on_one_database()
    {
        using var db = new TempDb();
        var policy = Policy("\"MaxPendingPerRequester\": 3");
        var h1 = Hosts.BuildSqlite(new FakeInboxModule(), policy, db.Path);
        var h2 = Hosts.BuildSqlite(new FakeInboxModule(), policy, db.Path);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => (i % 2 == 0 ? h1 : h2).Queue.SubmitAsync(Send(Me, to: $"u{i}@b.c")))));

        Assert.Equal(3, results.Count(r => r.Status == ActionStatus.Pending));
        Assert.Equal(13, results.Count(r => r.Error?.Code == "queue_full"));
        Assert.Equal(3, await h1.Store.CountAsync(new ActionQuery { Status = ActionStatus.Pending, RequesterId = "me" }));
    }

    [Fact]
    public async Task Admission_counts_inside_the_insert_for_both_stores()
    {
        using var db = new TempDb();
        foreach (var store in new IActionStore[] { new InMemoryActionStore(), new SqliteActionStore(db.Path) })
        {
            ActionRecord Rec() => new()
            {
                Id = "act_" + Guid.NewGuid().ToString("N"), ToolName = "t", ModuleId = "m", ArgumentsJson = "{}", ArgumentsHash = "h",
                Requester = Me, Status = ActionStatus.Pending, PolicyMode = ToolMode.Queue,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var cap = new[] { new Admission(new ActionQuery { Status = ActionStatus.Pending, RequesterId = "me" }, null, 2) };
            Assert.NotNull(await store.TryCreateAsync(Rec(), cap));
            Assert.NotNull(await store.TryCreateAsync(Rec(), cap));
            Assert.Null(await store.TryCreateAsync(Rec(), cap));
            Assert.Equal(2, await store.CountAsync(new ActionQuery { RequesterId = "me" }));
        }
    }

    // ---- events ----

    private sealed class Collect : IActionEventSink
    {
        public List<ActionEvent> Seen { get; } = [];
        public Task PublishAsync(ActionEvent evt, CancellationToken ct = default) { lock (Seen) Seen.Add(evt); return Task.CompletedTask; }
    }

    private sealed class Boom : IActionEventSink
    {
        public Task PublishAsync(ActionEvent evt, CancellationToken ct = default) => throw new InvalidOperationException("sink down: password=hunter2");
    }

    [Fact]
    public async Task Events_follow_the_lifecycle_and_carry_no_argument_values()
    {
        var sink = new Collect();
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy())).UseEventSink(sink).Build();

        var pending = await host.Queue.SubmitAsync(Send(Me, to: "secret-recipient@x.y"));
        await host.Queue.ReviewAsync(pending.Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        var kinds = sink.Seen.Select(e => e.Kind).ToList();
        Assert.Equal([ActionEventKind.Queued, ActionEventKind.Approved, ActionEventKind.Succeeded], kinds);
        Assert.All(sink.Seen, e => Assert.Equal(pending.Id, e.ActionId));
        Assert.Equal(ToolRisk.ExternalEffect, sink.Seen[0].Risk);
        Assert.DoesNotContain(sink.Seen, e => System.Text.Json.JsonSerializer.Serialize(e).Contains("secret-recipient"));
    }

    [Fact]
    public async Task A_failing_sink_never_breaks_the_action_and_is_audited_without_its_message()
    {
        var audit = new InMemoryAuditSink();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy()))
            .UseAudit(audit).UseEventSink(new Boom()).Build();

        var pending = await host.Queue.SubmitAsync(Send(Me));
        var done = await host.Queue.ReviewAsync(pending.Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Contains(audit.Entries, e => e.Event == "event_sink_error" && e.Detail == nameof(InvalidOperationException));
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("hunter2"));
    }

    // ---- durability of operator decisions ----

    [Fact]
    public async Task A_trust_promotion_is_shared_by_every_host_on_the_database()
    {
        using var db = new TempDb();
        var policy = "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"TrustRamp\": { \"AfterCleanApprovals\": 1 } } } }";
        var h1 = Hosts.BuildSqlite(new FakeInboxModule(), policy, db.Path);
        await h1.Queue.ReviewAsync((await h1.Queue.SubmitAsync(Send(Me))).Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.True(await h1.Trust.PromoteAsync("mail.send", "op"));

        var h2 = Hosts.BuildSqlite(new FakeInboxModule(), policy, db.Path);
        Assert.True(h2.Policy.IsPromoted("mail.send"));
        var clean = h2.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await h2.Queue.SubmitAsync(Send(new ActionRequester(RequesterKind.AiSession, "local"), clean))).Status);
    }

    [Fact]
    public void Migrating_a_v1_database_keeps_a_backup_and_adds_the_new_tables()
    {
        var path = new TempDb().Path;
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE actions (id TEXT PRIMARY KEY, tool TEXT NOT NULL, module TEXT NOT NULL, status TEXT NOT NULL, requester_id TEXT NOT NULL,
                    session_id TEXT NULL, idem_key TEXT NULL, args_hash TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, json TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_actions_idem ON actions(tool, idem_key, args_hash) WHERE idem_key IS NOT NULL;
                CREATE TABLE audit (seq INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, event TEXT NOT NULL, action_id TEXT NULL, tool TEXT NULL, actor TEXT NULL, detail TEXT NULL);
                INSERT INTO actions VALUES ('act_old','t','m','Succeeded','me',NULL,NULL,'h','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','{}');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        TempDb.ClearPools(path);

        var store = new SqliteActionStore(path);
        Assert.NotNull(store.MigrationBackupPath);
        Assert.True(File.Exists(store.MigrationBackupPath));

        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT (SELECT user_version FROM pragma_user_version), (SELECT COUNT(*) FROM sqlite_master WHERE name IN ('sessions','subject_taint','trust_promotions','ux_actions_idem2','notices','usage_daily','usage_entities','settings','policy_proposals')), (SELECT COUNT(*) FROM sqlite_master WHERE name='ux_actions_idem')";
        using var r = q.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(12, r.GetInt64(0));
        Assert.Equal(9, r.GetInt64(1));
        Assert.Equal(0, r.GetInt64(2)); // the global idempotency index is gone

        // The backup is the old schema, untouched.
        using var old = new SqliteConnection($"Data Source={store.MigrationBackupPath};Mode=ReadOnly");
        old.Open();
        using var oq = old.CreateCommand();
        oq.CommandText = "SELECT (SELECT user_version FROM pragma_user_version), (SELECT COUNT(*) FROM actions)";
        using var or = oq.ExecuteReader();
        Assert.True(or.Read());
        Assert.Equal(1, or.GetInt64(0));
        Assert.Equal(1, or.GetInt64(1));

        old.Close();
        TempDb.ClearPools(store.MigrationBackupPath!);
        TempDb.ClearPools(path);
        foreach (var f in new[] { store.MigrationBackupPath!, store.MigrationBackupPath + "-wal", store.MigrationBackupPath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    [Fact]
    public void A_database_from_a_newer_host_is_refused()
    {
        var path = new TempDb().Path;
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }
        TempDb.ClearPools(path);
        Assert.Contains("newer", Assert.Throws<InvalidOperationException>(() => new SqliteActionStore(path)).Message);
    }

    // ---- durations in policy files ----

    [Theory]
    [InlineData("24:00:00", 24 * 60)]
    [InlineData("01:00:00", 60)]
    [InlineData("1.00:00:00", 24 * 60)]
    [InlineData("90m", 90)]
    [InlineData("8h", 8 * 60)]
    [InlineData("2d", 2 * 24 * 60)]
    [InlineData("30s", 0.5)]
    [InlineData("100:30:00", 100 * 60 + 30)]
    public void Durations_are_written_the_way_people_write_them(string text, double minutes)
    {
        var policy = PolicyConfig.Parse("{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"ExpireAfter\": \"" + text + "\" } } }");
        Assert.Equal(minutes, policy.Tools["mail.send"].ExpireAfter!.Value.TotalMinutes, 3);
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("25:61:00")]
    [InlineData("-5m")]
    [InlineData("")]
    public void A_duration_that_is_not_one_is_an_error_not_a_silent_default(string text)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            PolicyConfig.Parse("{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"ExpireAfter\": \"" + text + "\" } } }"));
    }

    [Fact]
    public void The_sample_policy_files_load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenTail.Host.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in Directory.EnumerateFiles(root!.FullName, "policy.*.json", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            Assert.NotNull(PolicyConfig.Load(file));
    }
}
