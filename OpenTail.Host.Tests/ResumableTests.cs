using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace OpenTail.Host.Tests;

/// <summary>V5: the AI's turn ends before the decision arrives; the Host makes sure it finds out at its next opportunity.</summary>
public class ResumableTests
{
    private const string Policy = """{ "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" }, "inbox.read": { "Mode": "Queue" } } }""";

    private static ActionReview Decide(ReviewDecision d, string? reason = null) => new("rita", d, reason, DateTimeOffset.UtcNow);

    private static async Task<string> QueueSend(McpPair pair, string? session = null, string to = "x@y.z")
    {
        var args = new List<(string, object?)> { ("to", to), ("body", "b") };
        if (session is not null) args.Add(("sessionId", session));
        return (await pair.CallAsync("mail.send", [.. args]))["actionId"]!.ToString();
    }

    // ---- notices ----

    [Fact]
    public async Task A_decision_made_while_the_caller_was_away_rides_on_its_next_call_exactly_once()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var id = await QueueSend(pair);

        await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Reject, "Unknown recipient."));

        var next = await pair.CallAsync("clock.now");   // any call at all, in any later conversation
        var notice = Assert.Single(next["notices"]!.AsArray());
        Assert.Equal(id, notice!["actionId"]!.ToString());
        Assert.Equal("rejected", notice["status"]!.ToString());
        Assert.Equal("Unknown recipient.", notice["reviewerReason"]!.ToString());
        Assert.Contains("Do not submit the same action again", notice["next"]!.ToString());

        Assert.Null((await pair.CallAsync("clock.now"))["notices"]);   // told once, then quiet
    }

    [Fact]
    public async Task Notices_say_what_to_do_for_each_outcome_and_never_carry_a_result()
    {
        var (host, module, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var ok = await QueueSend(pair, to: "ok@y.z");
        var changes = await QueueSend(pair, to: "chg@y.z");
        await host.Queue.ReviewAsync(ok, Decide(ReviewDecision.Approve));
        await host.Queue.ReviewAsync(changes, Decide(ReviewDecision.RequestChanges, "Add a greeting."));

        var updates = await pair.CallAsync("get_updates");
        var notices = updates["notices"]!.AsArray().ToDictionary(n => n!["actionId"]!.ToString(), n => (JsonObject)n!);
        Assert.Equal("succeeded", notices[ok]["status"]!.ToString());
        Assert.Contains("get_action_status", notices[ok]["next"]!.ToString());
        Assert.Null(notices[ok]["result"]);
        Assert.DoesNotContain("sent to", updates.ToJsonString());
        Assert.Equal("Add a greeting.", notices[changes]["reviewerReason"]!.ToString());
        Assert.Contains("resubmit_action", notices[changes]["next"]!.ToString());
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Things_that_ran_at_once_or_were_cancelled_by_the_caller_produce_no_notice()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        await pair.CallAsync("clock.now");                                    // ran immediately: the caller saw the result
        var id = await QueueSend(pair);
        await pair.CallAsync("cancel_action", ("actionId", id));              // the caller's own doing
        var updates = await pair.CallAsync("get_updates");
        Assert.Empty(updates["notices"]!.AsArray());
    }

    [Fact]
    public async Task Reading_the_status_yourself_counts_as_being_told()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var id = await QueueSend(pair);
        await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Approve));

        Assert.Equal("succeeded", (await pair.CallAsync("get_action_status", ("actionId", id)))["status"]!.ToString());
        Assert.Null((await pair.CallAsync("clock.now"))["notices"]);          // no repeat
    }

    [Fact]
    public async Task At_most_five_notices_ride_a_response_and_the_rest_wait_for_the_next()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var ids = new List<string>();
        for (var i = 0; i < 7; i++) ids.Add(await QueueSend(pair, to: $"u{i}@y.z"));   // queue them all first: a later call would deliver earlier news
        foreach (var id in ids) await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Reject, "no"));

        Assert.Equal(5, (await pair.CallAsync("clock.now"))["notices"]!.AsArray().Count);
        Assert.Equal(2, (await pair.CallAsync("clock.now"))["notices"]!.AsArray().Count);
        Assert.Null((await pair.CallAsync("clock.now"))["notices"]);
    }

    [Fact]
    public async Task The_originating_sessions_news_comes_first()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var other = host.Sessions.Start();
        var mine = host.Sessions.Start();
        var first = await QueueSend(pair, other, "a@y.z");     // older, another conversation
        var second = await QueueSend(pair, mine, "b@y.z");
        await host.Queue.ReviewAsync(first, Decide(ReviewDecision.Reject, "no"));
        await host.Queue.ReviewAsync(second, Decide(ReviewDecision.Reject, "no"));

        var r = await pair.CallAsync("get_updates", ("sessionId", mine));
        Assert.Equal(second, r["notices"]![0]!["actionId"]!.ToString());
    }

    [Fact]
    public async Task Two_concurrent_responses_never_hand_over_the_same_notice()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var queued = new List<string>();
        for (var i = 0; i < 4; i++) queued.Add(await QueueSend(pair, to: $"u{i}@y.z"));
        foreach (var id in queued) await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Reject, "no"));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => pair.CallAsync("clock.now")));
        var ids = results.Where(r => r["notices"] is not null).SelectMany(r => r["notices"]!.AsArray().Select(n => n!["actionId"]!.ToString())).ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public async Task Notices_belong_to_the_caller_and_survive_a_restart()
    {
        using var db = new TempDb();
        string id;
        {
            var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
            id = (await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"x"}""", new ActionRequester(RequesterKind.AiSession, "local"), host.Sessions.Start()))).Id;
            await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Reject, "not today"));
        }
        SqliteConnection.ClearAllPools();

        var restarted = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        Assert.Equal(1, restarted.Notices.Pending("local"));
        Assert.Equal(0, restarted.Notices.Pending("key:someone-else"));       // not theirs to hear
        var taken = await restarted.Notices.TakeAsync("local", null);
        Assert.Equal("not today", Assert.Single(taken)["reviewerReason"]!.ToString());
        Assert.Equal(0, restarted.Notices.Pending("local"));
    }

    // ---- get_updates ----

    [Fact]
    public async Task get_updates_lists_what_is_still_waiting_as_well()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var waiting = await QueueSend(pair);
        var updates = await pair.CallAsync("get_updates");
        Assert.Equal(waiting, Assert.Single(updates["stillWaiting"]!.AsArray())!["actionId"]!.ToString());
        Assert.Empty(updates["notices"]!.AsArray());
    }

    // ---- wait_for_action ----

    [Fact]
    public async Task wait_for_action_returns_as_soon_as_a_reviewer_decides()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var id = await QueueSend(pair);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var wait = pair.CallAsync("wait_for_action", ("actionId", id), ("timeoutSeconds", 20));
        await Task.Delay(600);
        await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Approve));
        var r = await wait;
        watch.Stop();

        Assert.Equal("succeeded", r["status"]!.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Null(r["stillWaiting"]);
    }

    [Fact]
    public async Task wait_for_action_gives_up_politely_when_nobody_decides()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var id = await QueueSend(pair);

        var r = await pair.CallAsync("wait_for_action", ("actionId", id), ("timeoutSeconds", 1));
        Assert.Equal("pending_approval", r["status"]!.ToString());
        Assert.True(r["stillWaiting"]!.GetValue<bool>());
        Assert.InRange(r["waitedSeconds"]!.GetValue<double>(), 0.9, 4);
    }

    [Fact]
    public async Task wait_for_action_only_waits_for_your_own_actions_and_caps_the_wait()
    {
        var who = new CallerIdentity("key:alice", "Alice");
        var current = who;
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseCallerResolver(_ => current).Build();
        await using var pair = await McpPair.StartAsync(host);
        var id = await QueueSend(pair);

        current = new CallerIdentity("key:bob", "Bob");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = await pair.CallAsync("wait_for_action", ("actionId", id), ("timeoutSeconds", 25));
        Assert.Equal("not_found", r["status"]!.ToString());                    // answered at once, no 25-second wait on someone else's action
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_for_an_untrusted_read_result_taints_the_reader()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var id = (await pair.CallAsync("inbox.read"))["actionId"]!.ToString();
        await host.Queue.ReviewAsync(id, Decide(ReviewDecision.Approve));
        var reader = host.Sessions.Start();
        Assert.False(host.Sessions.IsTainted(reader));
        await pair.CallAsync("wait_for_action", ("actionId", id), ("timeoutSeconds", 1), ("sessionId", reader));
        Assert.True(host.Sessions.IsTainted(reader));
    }

    // ---- list_my_actions ----

    [Fact]
    public async Task list_my_actions_filters_by_time_and_limits()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, _, _) = Hosts.Build(Policy, clock);
        await using var pair = await McpPair.StartAsync(host);
        await QueueSend(pair, to: "old@y.z");
        clock.Advance(TimeSpan.FromHours(2));
        var recent = await QueueSend(pair, to: "new@y.z");

        var since = await pair.CallAsync("list_my_actions", ("since", "2026-10-15T10:00:00Z"));
        Assert.Equal(recent, Assert.Single(since["actions"]!.AsArray())!["actionId"]!.ToString());
        Assert.Single((await pair.CallAsync("list_my_actions", ("limit", 1)))["actions"]!.AsArray());
        Assert.Contains("not a time", (await pair.CallAsync("list_my_actions", ("since", "yesterday-ish")))["error"]!.ToString());
    }

    // ---- guidance ----

    [Fact]
    public async Task The_server_tells_the_model_how_to_handle_waiting_and_news()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var pair = await McpPair.StartAsync(host);
        var text = pair.Client.ServerInstructions ?? "";
        Assert.Contains("get_updates", text);
        Assert.Contains("do not poll in a loop", text);
        Assert.Contains("notices", text);
        var names = (await pair.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("get_updates", names);
        Assert.Contains("wait_for_action", names);
    }

    // ---- retention ----

    [Fact]
    public async Task Finished_actions_are_deleted_after_the_retention_period_but_the_audit_trail_stays()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(-100));
        var host = Hosts.BuildSqlite(new FakeInboxModule(), """{ "RetentionDays": 90, "Tools": { "mail.send": { "Mode": "Queue" } } }""", db.Path, clock);
        var a = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"secret-recipient@x.y","body":"b"}""", new ActionRequester(RequesterKind.User, "me"), host.Sessions.Start()));
        await host.Queue.ReviewAsync(a.Id, Decide(ReviewDecision.Reject, "no"));
        clock.Advance(TimeSpan.FromDays(100));
        var pending = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"keep@x.y","body":"b"}""", new ActionRequester(RequesterKind.User, "me"), host.Sessions.Start()));

        await host.RunMaintenanceOnceAsync();

        Assert.Null(await host.Store.GetAsync(a.Id));                     // finished and old: gone (the argument values with it)
        Assert.NotNull(await host.Store.GetAsync(pending.Id));            // recent and still waiting: kept
        var audit = ((IAuditReader)host.Audit).Read(a.Id);
        Assert.Contains(audit, e => e.Event == "rejected");               // the story survives
        Assert.DoesNotContain(audit, e => (e.Detail ?? "").Contains("secret-recipient"));
        Assert.Contains(((IAuditReader)host.Audit).Read(), e => e.Event == "retention_purge");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Retention_off_by_default_and_negative_is_an_error()
    {
        Assert.Equal(0, PolicyConfig.Parse("{}").RetentionDays);
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("""{ "RetentionDays": -1 }"""));
        Assert.Contains("RetentionDays", Assert.Throws<InvalidOperationException>(() => b.Build()).Message);
    }
}
