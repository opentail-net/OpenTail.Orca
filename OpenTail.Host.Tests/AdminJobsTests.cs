using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using OpenTail.Connectivity.Queues;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>The Jobs view: work items, continuations and queue jobs on one list, each stoppable, every change audited.</summary>
public class AdminJobsTests
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReviewerToken = "review-token-0123456789";
    private const string Policy = """{ "Work": { "Enabled": true }, "Agents": { "clerk": { "Tools": [ "inbox.*" ] } } }""";
    private static readonly CallerIdentity Bot = new("key:bot", "bot", CallerKind.Agent);

    private sealed record Setup(OpenTailHost Host, RunningAdmin Admin, TempDb Db, InMemoryWorkQueue Queue) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Admin.DisposeAsync();
            Db.Dispose();
            TempDb.ClearPools(Db.Path);
        }
    }

    private static async Task<Setup> Start(WorkQueueOptions? queueOptions = null)
    {
        var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        var queue = new InMemoryWorkQueue(queueOptions);
        var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [AdminToken] = "ops", [ReviewerToken] = "rev" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [AdminToken] = new HashSet<string> { Roles.Review, Roles.Admin },
                [ReviewerToken] = new HashSet<string> { Roles.Review },
            },
            WorkQueues = [new AdminWorkQueue("deliveries", queue)],
        });
        return new Setup(host, admin, db, queue);
    }

    private static HttpClient Client(RunningAdmin admin, string token)
    {
        var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<JsonNode> Jobs(RunningAdmin admin)
    {
        using var http = Client(admin, AdminToken);
        return JsonNode.Parse(await http.GetStringAsync("/api/admin/jobs"))!;
    }

    private static async Task<HttpStatusCode> Act(RunningAdmin admin, string kind, string id, string action, string? queue = null, string token = AdminToken)
    {
        using var http = Client(admin, token);
        var body = new JsonObject { ["kind"] = kind, ["id"] = id, ["action"] = action, ["queue"] = queue }.ToJsonString();
        return (await http.PostAsync("/api/admin/jobs/act", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode;
    }

    private static JsonNode Row(JsonNode jobs, string list, string id) => jobs[list]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task Work_continuations_and_queue_jobs_share_one_list_and_a_reviewer_cannot_see_it()
    {
        await using var s = await Start();
        var item = await s.Host.Work.CreateAsync("Check ACME's licence.", WorkService.ForProfile("clerk"), "admin:rita");
        var parked = await s.Host.Work.ParkAsync(Bot, null, "Waiting for the licence check.", ["timer:2030-01-01T00:00:00Z"], false, null, null);
        await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{\"secret\":1}", Source: "schedule:nightly"));

        var jobs = await Jobs(s.Admin);

        var work = Row(jobs, "pending", item.Id);
        Assert.Equal("work", work["kind"]!.GetValue<string>());
        Assert.Equal("Check ACME's licence.", work["what"]!.GetValue<string>());
        Assert.Equal("admin:rita", work["source"]!.GetValue<string>());
        Assert.Equal("resume", Row(jobs, "pending", parked.Id)["kind"]!.GetValue<string>());
        var queued = jobs["pending"]!.AsArray().Single(r => r!["kind"]!.GetValue<string>() == "queue")!;
        Assert.Equal("schedule:nightly", queued["source"]!.GetValue<string>());
        Assert.DoesNotContain("secret", jobs.ToJsonString()); // queue bodies are never shown

        using var reviewer = Client(s.Admin, ReviewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync("/api/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await Act(s.Admin, "work", item.Id, "cancel", token: ReviewerToken));
    }

    [Fact]
    public async Task Cancelling_work_and_a_continuation_ends_them_and_audits_who_did_it()
    {
        await using var s = await Start();
        var item = await s.Host.Work.CreateAsync("Post the journal.", WorkService.ForProfile("clerk"), "admin:rita");
        var parked = await s.Host.Work.ParkAsync(Bot, null, "Waiting.", ["timer:2030-01-01T00:00:00Z"], false, null, null);

        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "work", item.Id, "cancel"));
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "resume", parked.Id, "cancel"));
        Assert.Equal(HttpStatusCode.Conflict, await Act(s.Admin, "work", item.Id, "cancel")); // already ended

        Assert.Equal(WorkStatus.Cancelled, s.Host.Work.Store.Get(item.Id)!.Status);
        Assert.Equal(ContinuationStatus.Cancelled, s.Host.Work.Store.GetContinuation(parked.Id)!.Status);
        Assert.Empty((await Jobs(s.Admin))["pending"]!.AsArray());
        var audit = ((IAuditReader)s.Host.Audit).Read();
        Assert.Contains(audit, e => e.Event == "work.cancelled" && e.Actor == "admin:ops");
        Assert.Contains(audit, e => e.Event == "continuation.cancelled" && e.Actor == "admin:ops");
    }

    [Fact]
    public async Task Stopping_the_source_of_delegated_work_cancels_the_parent_that_keeps_creating_it()
    {
        await using var s = await Start();
        var parent = await s.Host.Work.CreateAsync("Onboard ACME.", WorkService.ForProfile("clerk"), "admin:rita");
        var child = await s.Host.Work.CreateAsync("Check the licence.", WorkService.ForProfile("clerk"), "key:bot", parent: parent);

        var row = Row(await Jobs(s.Admin), "pending", child.Id);
        Assert.Equal("work:" + parent.Id, row["source"]!.GetValue<string>());
        Assert.Contains("stop-source", row["actions"]!.AsArray().Select(a => a!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "work", child.Id, "stop-source"));
        Assert.Equal(WorkStatus.Cancelled, s.Host.Work.Store.Get(parent.Id)!.Status);
        Assert.Equal(WorkStatus.Open, s.Host.Work.Store.Get(child.Id)!.Status); // the job itself is cancelled separately
    }

    [Fact]
    public async Task Dead_work_can_be_redriven_or_deleted()
    {
        await using var s = await Start();
        var a = await s.Host.Work.CreateAsync("A.", WorkService.ForProfile("clerk"), "admin:rita");
        var b = await s.Host.Work.CreateAsync("B.", WorkService.ForProfile("clerk"), "admin:rita");
        foreach (var id in new[] { a.Id, b.Id })
            s.Host.Work.Store.Transition(id, WorkStatus.Open, i => i with { Status = WorkStatus.Dead, Summary = "failed three times" });

        var dead = (await Jobs(s.Admin))["dead"]!.AsArray();
        Assert.Equal(2, dead.Count);
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "work", a.Id, "redrive"));
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "work", b.Id, "delete"));

        Assert.Equal(WorkStatus.Open, s.Host.Work.Store.Get(a.Id)!.Status);
        Assert.Equal(WorkStatus.Cancelled, s.Host.Work.Store.Get(b.Id)!.Status);
        Assert.Empty((await Jobs(s.Admin))["dead"]!.AsArray());
    }

    [Fact]
    public async Task Queue_jobs_can_be_cancelled_run_now_redriven_deleted_and_their_source_stopped()
    {
        await using var s = await Start();
        var parent = await s.Host.Work.CreateAsync("Notify the platform.", WorkService.ForProfile("clerk"), "admin:rita");
        var waiting = (await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}"))).Id;
        var delayed = (await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", NotBefore: DateTimeOffset.UtcNow.AddHours(1),
            Source: "work:" + parent.Id))).Id;

        Assert.Contains("run-now", Row(await Jobs(s.Admin), "pending", delayed)["actions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "queue", delayed, "run-now", "deliveries"));
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "queue", delayed, "stop-source", "deliveries"));
        Assert.Equal(WorkStatus.Cancelled, s.Host.Work.Store.Get(parent.Id)!.Status);
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "queue", waiting, "cancel", "deliveries"));

        var left = Assert.Single(await s.Queue.LeaseAsync("deliveries", 10, TimeSpan.FromMinutes(1)));
        Assert.Equal(delayed, left.Id); // ran now; the cancelled one never runs
        await s.Queue.DeadLetterAsync(left, "platform refused it");
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "queue", delayed, "redrive", "deliveries"));
        await s.Queue.DeadLetterAsync(Assert.Single(await s.Queue.LeaseAsync("deliveries", 10, TimeSpan.FromMinutes(1))), "again");
        Assert.Equal(HttpStatusCode.OK, await Act(s.Admin, "queue", delayed, "delete", "deliveries"));
        Assert.Empty(await s.Queue.DeadLettersAsync("deliveries"));

        Assert.Equal(HttpStatusCode.BadRequest, await Act(s.Admin, "queue", delayed, "cancel", "no-such-queue"));
        var audit = ((IAuditReader)s.Host.Audit).Read();
        foreach (var evt in new[] { "job.run_now", "job.stop_source", "job.cancel", "job.redrive", "job.delete" })
            Assert.True(audit.Any(e => e.Event == evt && e.Actor == "admin:ops"), evt + " missing from: " + string.Join(", ", audit.Select(e => e.Event + "/" + e.Actor)));
    }

    private static async Task<JsonNode> Post(RunningAdmin admin, string path, JsonObject body)
    {
        using var http = Client(admin, AdminToken);
        var resp = await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task Resources_show_running_against_the_limit_open_circuits_and_feed_the_health_check()
    {
        await using var s = await Start(new WorkQueueOptions(CircuitFailures: 1, DefaultMaxRunTime: TimeSpan.FromMilliseconds(50),
            ConcurrencyLimits: new Dictionary<string, int> { ["db:*"] = 1 }));
        for (var i = 0; i < 3; i++)
            await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", ConcurrencyKey: "db:reports"));
        await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", ConcurrencyKey: "erp:acme"));
        var leased = await s.Queue.LeaseAsync("deliveries", 10, TimeSpan.FromHours(1));
        await s.Queue.RetryAsync(leased.Single(j => j.ConcurrencyKey == "erp:acme"), TimeSpan.Zero, "refused", resourceFailure: true);
        await Task.Delay(100); // the db job is now past its 50 ms maximum run time

        var jobs = await Jobs(s.Admin);

        var resources = jobs["resources"]!.AsArray().ToDictionary(r => r!["key"]!.GetValue<string>());
        Assert.Equal(1, resources["db:reports"]!["running"]!.GetValue<int>());
        Assert.Equal(1, resources["db:reports"]!["limit"]!.GetValue<int>());
        Assert.Equal(2, resources["db:reports"]!["waiting"]!.GetValue<int>());
        Assert.Equal("open", resources["erp:acme"]!["circuit"]!.GetValue<string>());
        var running = jobs["pending"]!.AsArray().Single(r => r!["state"]!.GetValue<string>() == "running")!;
        Assert.True(running["stuck"]!.GetValue<bool>());
        var queue = jobs["queues"]!.AsArray().Single()!;
        Assert.Equal("needs attention", queue["status"]!.GetValue<string>());
        Assert.Contains(queue["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("erp:acme", StringComparison.Ordinal));
        Assert.Equal("degraded", s.Host.Health.Report().Checks["queue:deliveries"]);
    }

    [Fact]
    public async Task After_an_outage_matching_dead_jobs_can_be_previewed_and_brought_back()
    {
        await using var s = await Start(new WorkQueueOptions(MaxAttempts: 1));
        for (var i = 0; i < 3; i++)
            await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", ConcurrencyKey: "db:reports"));
        await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", ConcurrencyKey: "erp:acme"));
        foreach (var j in await s.Queue.LeaseAsync("deliveries", 10, TimeSpan.FromMinutes(1)))
            await s.Queue.DeadLetterAsync(j, "connection refused");
        var item = await s.Host.Work.CreateAsync("Post the journal.", WorkService.ForProfile("clerk"), "admin:rita");
        s.Host.Work.Store.Transition(item.Id, WorkStatus.Open, i => i with { Status = WorkStatus.Dead, Summary = "failed three times" });

        var preview = await Post(s.Admin, "/api/admin/jobs/redrive-matching",
            new JsonObject { ["queue"] = "deliveries", ["resource"] = "db:*", ["reasonContains"] = "refused", ["dryRun"] = true });
        Assert.Equal(3, preview["count"]!.GetValue<int>());
        Assert.Equal(4, (await s.Queue.DeadLettersAsync("deliveries")).Count); // a preview changes nothing

        var done = await Post(s.Admin, "/api/admin/jobs/redrive-matching",
            new JsonObject { ["queue"] = "deliveries", ["resource"] = "db:*", ["reasonContains"] = "refused", ["dryRun"] = false });
        Assert.Equal(3, done["count"]!.GetValue<int>());
        Assert.Single(await s.Queue.DeadLettersAsync("deliveries"));

        var work = await Post(s.Admin, "/api/admin/jobs/redrive-matching",
            new JsonObject { ["queue"] = "work", ["reasonContains"] = "three", ["dryRun"] = false });
        Assert.Equal(1, work["count"]!.GetValue<int>());
        Assert.Equal(WorkStatus.Open, s.Host.Work.Store.Get(item.Id)!.Status);
        Assert.Contains(((IAuditReader)s.Host.Audit).Read(), e => e.Event == "job.redrive_matching" && e.Actor == "admin:ops");
    }

    [Fact]
    public async Task A_source_the_Host_does_not_own_cannot_be_stopped_from_here()
    {
        await using var s = await Start();
        var id = (await s.Queue.EnqueueAsync(new EnqueueRequest("deliveries", "acme", "{}", Source: "schedule:nightly"))).Id;

        Assert.DoesNotContain("stop-source", Row(await Jobs(s.Admin), "pending", id)["actions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict, await Act(s.Admin, "queue", id, "stop-source", "deliveries"));
    }
}
