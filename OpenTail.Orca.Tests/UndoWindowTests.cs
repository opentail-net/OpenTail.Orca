namespace OpenTail.Orca.Tests;

/// <summary>Execute with an undo window: announced, run after DelayFor unless someone stops it, held after an outage.</summary>
public class UndoWindowTests
{
    private const string Policy = """
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "SessionlessTainted": false,
          "Tools": {
            "mail.send":     { "Mode": "Execute", "DelayFor": "10m", "ReviewerGroup": "support" },
            "ledger.append": { "Mode": "Execute", "Delay": true }
          }
        }
        """;

    private static (OpenTailHost Host, ManualClock Clock, FakeInboxModule Module) Build()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UseClock(clock).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        return (host, clock, module);
    }

    private static ActionRequest Send(string? session = null) =>
        new("mail.send", """{"to":"a@b.c","body":"b"}""", new ActionRequester(RequesterKind.AiSession, "key:bot"), session);

    [Fact]
    public async Task A_delayed_action_is_announced_and_runs_when_the_window_has_passed()
    {
        var (host, clock, module) = Build();
        var r = await host.Queue.SubmitAsync(Send());
        Assert.Equal((ActionStatus.Pending, clock.GetUtcNow().AddMinutes(10)), (r.Status, r.RunAt));
        Assert.Null(r.ExpiresAt);
        Assert.Contains("\"status\":\"scheduled\"", ActionViews.Response(r));

        await host.RunMaintenanceOnceAsync();
        Assert.Equal(0, module.SendCalls);                     // not yet

        clock.Advance(TimeSpan.FromMinutes(10));
        await host.RunMaintenanceOnceAsync();
        Assert.Equal(1, module.SendCalls);
        var done = (await host.Store.GetAsync(r.Id))!;
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Contains(done.Reviews, rv => rv.Reviewer == "system" && rv.Reason!.Contains("Undo window"));
    }

    [Fact]
    public async Task Delay_true_uses_the_default_of_ten_minutes()
    {
        var (host, clock, _) = Build();
        var r = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, "key:bot")));
        Assert.Equal(clock.GetUtcNow() + ToolPolicy.DefaultDelay, r.RunAt);
        Assert.Equal(TimeSpan.FromMinutes(10), ToolPolicy.DefaultDelay);
    }

    [Fact]
    public async Task Anyone_in_the_reviewer_group_or_the_requester_can_stop_it_and_nothing_runs()
    {
        var (host, clock, module) = Build();
        var r = await host.Queue.SubmitAsync(Send());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.StopAsync(r.Id, "admin:outsider", ["finance"]));

        var stopped = await host.Queue.StopAsync(r.Id, "admin:jo", ["support"]);
        Assert.Equal(ActionStatus.Cancelled, stopped.Status);
        clock.Advance(TimeSpan.FromHours(1));
        await host.RunMaintenanceOnceAsync();
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "stopped" && e.Detail == "admin:jo");

        var mine = await host.Queue.SubmitAsync(Send());
        Assert.Equal(ActionStatus.Cancelled, (await host.Queue.StopAsync(mine.Id, "key:bot", [])).Status);
    }

    [Fact]
    public async Task Approving_runs_it_now()
    {
        var (host, _, module) = Build();
        var r = await host.Queue.SubmitAsync(Send());
        var done = await host.Queue.ReviewAsync(r.Id, new ActionReview("jo", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "admin:jo", Groups = ["support"] });
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task After_an_outage_a_stale_action_is_held_for_a_person_instead_of_firing()
    {
        var (host, clock, module) = Build();
        var r = await host.Queue.SubmitAsync(Send());
        clock.Advance(TimeSpan.FromMinutes(25));                // 15 min past its time, more than its 10 min delay
        await host.RunMaintenanceOnceAsync();
        Assert.Equal(0, module.SendCalls);
        var held = (await host.Store.GetAsync(r.Id))!;
        Assert.Equal((ActionStatus.Pending, (DateTimeOffset?)null), (held.Status, held.RunAt));
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "delay_held");
    }

    [Fact]
    public async Task Taint_still_queues_instead_of_delaying()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(new ActionRequest("inbox.read", "{}", new ActionRequester(RequesterKind.AiSession, "key:bot"), s));
        var r = await host.Queue.SubmitAsync(Send(s));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Null(r.RunAt);                                  // an ordinary queued action: a reviewer decides
    }

    [Fact]
    public void A_delay_only_belongs_on_an_execute_tool()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "DelayFor": "5m" } } }""")).Build());
        Assert.Contains("only applies to Mode Execute", ex.Message);
    }

    [Fact]
    public async Task An_execute_rule_can_run_its_calls_after_an_undo_window()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse("""
            { "SessionlessTainted": false, "TaintWindow": "00:00:00",
              "Tools": { "mail.send": { "Mode": "Queue", "Rules": [
                { "Name": "internal", "Mode": "Execute", "DelayFor": "5m", "IgnoreArgs": [ "body" ],
                  "When": { "arg": "/to", "domainIn": [ "corp.example" ] } } ] } } }
            """)).Build();
        var r = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@corp.example","body":"b"}""", new ActionRequester(RequesterKind.AiSession, "key:bot")));
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), r.RunAt);
        var outside = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@else.example","body":"b"}""", new ActionRequester(RequesterKind.AiSession, "key:bot")));
        Assert.Null(outside.RunAt);                            // the rule did not match: an ordinary queued action
    }

    [Fact]
    public async Task Elevated_identities_can_get_a_delay_instead_of_a_queue()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse("""
            { "SessionlessTainted": false, "Tools": { "ledger.append": { "Mode": "Execute" } },
              "Limits": { "Rejections": "Enforce", "ElevatedDelay": "00:15:00" } }
            """)).Build();
        await host.Levels.RejectedAsync("key:bot", "act_x", "ledger.append", default);
        var r = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, "key:bot")));
        Assert.Equal((ActionStatus.Pending, clock.GetUtcNow().AddMinutes(15)), (r.Status, r.RunAt));
    }

    [Fact]
    public void A_rule_delay_belongs_on_an_execute_rule_without_a_budget()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("""
            { "Tools": { "mail.send": { "Mode": "Queue", "Rules": [
                { "Name": "r", "Mode": "Execute", "DelayFor": "5m", "Budget": { "Per": "1h", "MaxCount": 5 }, "IgnoreArgs": [ "to", "body" ], "When": { "arg": "/to", "exists": true } } ] } } }
            """)).Build());
        Assert.Contains("DelayFor and Budget cannot be combined", ex.Message);
    }

    [Fact]
    public async Task A_tool_promoted_by_the_trust_ramp_can_run_after_a_delay_instead_of_at_once()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var trust = new InMemoryTrustStore();
        trust.Promote("mail.send", "rita", clock.GetUtcNow());
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UseTrustStore(trust).UsePolicy(PolicyConfig.Parse("""
            { "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Queue", "TrustRamp": { "AfterCleanApprovals": 5, "DelayFor": "00:05:00" } } } }
            """)).Build();
        var r = await host.Queue.SubmitAsync(Send());
        Assert.Equal((ActionStatus.Pending, clock.GetUtcNow().AddMinutes(5)), (r.Status, r.RunAt));
    }

    [Fact]
    public async Task Outside_working_hours_a_delay_stretches_to_the_next_opening_when_the_tool_opts_in()
    {
        // Friday 17:55 UTC; working hours 08:00-18:00 Monday to Friday. A 10-minute window would end at 18:05, after closing.
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-16T17:55:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse("""
            { "SessionlessTainted": false,
              "WorkingHours": { "Days": [ "Mon", "Tue", "Wed", "Thu", "Fri" ], "From": "08:00", "To": "18:00", "Zone": "UTC" },
              "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "10m", "DelayUntilWorkingHours": true },
                         "ledger.append": { "Mode": "Execute", "DelayFor": "10m" } } }
            """)).Build();
        var mail = await host.Queue.SubmitAsync(Send());
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T08:10:00Z"), mail.RunAt);       // Monday 08:00 plus the window
        var ledger = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, "key:bot")));
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T18:05:00Z"), ledger.RunAt);     // not opted in: the plain window
    }

    [Fact]
    public void Working_hours_must_exist_and_be_valid_when_a_tool_opts_in()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("""
            { "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "10m", "DelayUntilWorkingHours": true } } }
            """)).Build());
        Assert.Contains("needs the policy's WorkingHours", ex.Message);
        var bad = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("""
            { "WorkingHours": { "From": "18:00", "To": "08:00", "Zone": "Nowhere/Land" } }
            """)).Build());
        Assert.Contains("From before To", bad.Message);
        Assert.Contains("not a known time zone", bad.Message);
    }
}
