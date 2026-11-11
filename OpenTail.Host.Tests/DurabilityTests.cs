using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Tests;

/// <summary>Phase 3 exit tests: restart, crash windows, and concurrency across separate store instances on one file.</summary>
public class DurabilityTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");
    private static readonly ActionReview Approve = new("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow);

    private static ActionRequest Send(string? key = null) =>
        new("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}", Me, null, key);

    private static ActionRecord Stale(string tool, string args, ActionStatus status, DateTimeOffset leaseEnded, string? key = null) => new()
    {
        Id = "act_" + Guid.NewGuid().ToString("N"),
        ToolName = tool,
        ModuleId = "fake",
        ArgumentsJson = args,
        ArgumentsHash = "h" + Guid.NewGuid().ToString("N")[..8],
        Requester = Me,
        Status = status,
        PolicyMode = ToolMode.Execute,
        CreatedAt = leaseEnded.AddMinutes(-10),
        UpdatedAt = leaseEnded.AddMinutes(-10),
        Attempts = 1,
        LeaseUntil = leaseEnded,
        IdempotencyKey = key,
    };

    [Fact]
    public async Task Pending_survives_a_restart_and_can_then_be_approved()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();

        string id;
        {
            var a = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
            id = (await a.Queue.SubmitAsync(Send())).Id;
        }

        var b = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var found = await b.Store.GetAsync(id);
        Assert.Equal(ActionStatus.Pending, found!.Status);
        Assert.Equal(0, module.SendCalls);

        var done = await b.Queue.ReviewAsync(id, Approve);
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Approved_but_never_started_runs_exactly_once_after_restart()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();

        string id;
        {
            var a = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
            id = (await a.Queue.SubmitAsync(Send())).Id;
            // Crash window: approval committed, process dies before executing.
            await a.Store.TransitionAsync(id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        }
        Assert.Equal(0, module.SendCalls);

        var b = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var first = await b.RunMaintenanceOnceAsync();
        var second = await b.RunMaintenanceOnceAsync();

        Assert.Equal(1, first.StartedApproved);
        Assert.Equal(0, second.StartedApproved);
        Assert.Equal(1, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await b.Store.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task Interrupted_non_idempotent_action_is_not_rerun_and_waits_for_a_reviewer()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var host = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var stale = await host.Store.CreateAsync(Stale("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}",
            ActionStatus.Executing, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var report = await host.RunMaintenanceOnceAsync();

        Assert.Equal(1, report.Interrupted);
        Assert.Equal(0, report.RetriedIdempotent);
        Assert.Equal(0, module.SendCalls); // never silently re-run an external effect
        var after = (await host.Store.GetAsync(stale.Id))!;
        Assert.Equal(ActionStatus.Failed, after.Status);
        Assert.Equal("interrupted", after.Error!.Code);
        Assert.True(after.Error.Retryable);

        // A reviewer decides to retry.
        var retried = await host.Queue.ReviewAsync(stale.Id,
            new ActionReview("alice", ReviewDecision.Retry, "Checked the mail server; it was not sent.", DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Succeeded, retried.Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Interrupted_idempotent_action_is_retried_once_with_the_same_key()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var host = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var stale = await host.Store.CreateAsync(Stale("ledger.append", "{\"text\":\"entry\"}",
            ActionStatus.Executing, DateTimeOffset.UtcNow.AddMinutes(-5), key: "client-key-1"));

        var first = await host.RunMaintenanceOnceAsync();
        var second = await host.RunMaintenanceOnceAsync();

        Assert.Equal(1, first.RetriedIdempotent);
        Assert.Equal(0, second.RetriedIdempotent + second.Interrupted);
        Assert.Equal(("client-key-1", "entry"), Assert.Single(module.Ledger));
        var after = (await host.Store.GetAsync(stale.Id))!;
        Assert.Equal(ActionStatus.Succeeded, after.Status);
        Assert.Equal(2, after.Attempts);
        Assert.Contains(after.Reviews, r => r.Reviewer == "system" && r.Decision == ReviewDecision.Retry);
    }

    [Fact]
    public async Task A_live_lease_is_left_alone()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var host = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var live = await host.Store.CreateAsync(Stale("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}",
            ActionStatus.Executing, DateTimeOffset.UtcNow.AddMinutes(5)));

        var report = await host.RunMaintenanceOnceAsync();

        Assert.Equal(0, report.Interrupted);
        Assert.Equal(ActionStatus.Executing, (await host.Store.GetAsync(live.Id))!.Status);
    }

    [Fact]
    public async Task Two_hosts_on_one_file_approving_at_once_execute_once()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var a = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var b = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var id = (await a.Queue.SubmitAsync(Send())).Id;

        var attempts = Enumerable.Range(0, 12).Select(async i =>
        {
            var queue = i % 2 == 0 ? a.Queue : b.Queue;
            try { return await queue.ReviewAsync(id, Approve); }
            catch (InvalidOperationException) { return null; }
        });
        await Task.WhenAll(attempts);

        Assert.Equal(1, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await a.Store.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task Idempotency_key_survives_a_restart()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var first = await Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path).Queue.SubmitAsync(Send("k-1"));
        var again = await Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path).Queue.SubmitAsync(Send("k-1"));
        Assert.Equal(first.Id, again.Id);
        var rows = await Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path).Store.QueryAsync(new ActionQuery());
        Assert.Single(rows);
    }

    [Fact]
    public async Task Audit_is_durable_ordered_and_free_of_argument_values()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var audit = new SqliteAuditSink(store);
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Hosts.SendQueued))
            .UseStore(store).UseAudit(audit).Build();

        var pending = await host.Queue.SubmitAsync(Send());
        await host.Queue.ReviewAsync(pending.Id, Approve);

        var events = audit.Read(pending.Id).Select(e => e.Event).ToList();
        Assert.Equal(["policy_decision", "approval_requested", "approval_allowed", "execution_started", "execution_succeeded"], events);
        Assert.DoesNotContain(audit.Read(), e => (e.Detail ?? "").Contains("a@b.c"));
    }

    [Fact]
    public void A_database_from_a_newer_host_is_refused()
    {
        using var db = new TempDb();
        _ = new SqliteActionStore(db.Path);
        using (var c = new SqliteConnection($"Data Source={db.Path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }
        TempDb.ClearPools(db.Path);
        var ex = Assert.Throws<InvalidOperationException>(() => new SqliteActionStore(db.Path));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public async Task Round_trip_preserves_every_field_including_reviews_and_policy_snapshot()
    {
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var host = Hosts.BuildSqlite(module, Hosts.SendQueued, db.Path);
        var pending = await host.Queue.SubmitAsync(Send("rt"));
        await host.Queue.ReviewAsync(pending.Id, new ActionReview("bob", ReviewDecision.RequestChanges, "why?", DateTimeOffset.UtcNow));

        var loaded = (await host.Store.GetAsync(pending.Id))!;
        Assert.Equal(ActionStatus.ChangesRequested, loaded.Status);
        Assert.Equal("why?", Assert.Single(loaded.Reviews).Reason);
        Assert.Equal(ToolMode.Queue, loaded.PolicySnapshot!.Mode);
        Assert.Equal(TimeSpan.FromHours(1), loaded.PolicySnapshot.ExpireAfter);
        Assert.Equal("rt", loaded.IdempotencyKey);
        Assert.Equal(HostContract.ActionSchemaVersion, loaded.SchemaVersion);
    }
}
