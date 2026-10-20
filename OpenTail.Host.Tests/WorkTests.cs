using System.Text.Json.Nodes;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Work items and continuations (docs/design/work-and-continuations.md, steps 1 and 2): park and resume with the outcomes,
/// taint and labels carried across the wait, an inbox with leases and a cap on runs, delegation that only narrows.</summary>
public class WorkTests
{
    private static readonly CallerIdentity Bot = new("key:bot", "bot", CallerKind.Agent) { Profile = "clerk" };
    private static readonly CallerIdentity Helper = new("key:helper", "helper", CallerKind.Agent) { Profile = "researcher" };

    private const string Policy = """
        { "DefaultReadMode": "Execute",
          "Work": { "Enabled": true },
          "Agents": {
            "clerk":      { "Tools": [ "inbox.*", "mail.*", "clock.*", "work.*" ], "MayDelegateTo": [ "researcher" ] },
            "researcher": { "Tools": [ "inbox.*", "clock.*", "work.*" ] } },
          "Tools": { "mail.send": { "Mode": "Queue" }, "work.delegate": { "Mode": "Execute" } } }
        """;

    private static (OpenTailHost Host, ManualClock Clock) Build(string? db = null, string policy = Policy)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-20T09:00:00Z"));
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy)).UseClock(clock);
        if (db is not null) b.UseSqlite(db);
        return (b.Build(), clock);
    }

    private static ActionRequest Call(string tool, string args, CallerIdentity who, string? session) =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, who.Subject), session) { Profile = who.Profile };

    private static ActionReview Approve() => new("rita", ReviewDecision.Approve, "Fine.", DateTimeOffset.UtcNow) { Subject = "admin:rita" };

    [Fact]
    public async Task Parking_on_an_approval_resumes_with_the_note_and_the_outcome_once_it_is_decided()
    {
        var (host, _) = Build();
        var s = host.Sessions.Start(Bot.Subject);
        var send = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.example","body":"hi"}""", Bot, s));
        var parked = await host.Work.ParkAsync(Bot, s, "Sent the bank-detail mail; if approved, update the vendor.", [send.Id], false, null, null);

        await host.Work.SweepAsync();
        Assert.Null(await host.Work.ClaimNextAsync(Bot, null));   // still waiting for the reviewer

        await host.Queue.ReviewAsync(send.Id, Approve());
        await host.Work.SweepAsync();
        var resume = await host.Work.ClaimNextAsync(Bot, host.Sessions.Start(Bot.Subject));
        Assert.NotNull(resume);
        Assert.Equal((WorkKind.Resume, parked.Id), (resume!.Kind, resume.ContinuationId));
        Assert.Equal("Sent the bank-detail mail; if approved, update the vendor.", resume.Note);
        var outcome = JsonNode.Parse(resume.OutcomesJson!)!.AsArray().Single()!;
        Assert.Equal((send.Id, "succeeded", "Fine."), (outcome["wait"]!.ToString(), outcome["state"]!.ToString(), outcome["reason"]!.ToString()));
        Assert.Equal(ContinuationStatus.Woken, host.Work.Store.GetContinuation(parked.Id)!.Status);
    }

    [Fact]
    public async Task A_continuation_whose_time_runs_out_wakes_with_what_is_known()
    {
        var (host, clock) = Build();
        var send = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.example","body":"hi"}""", Bot, null));
        await host.Work.ParkAsync(Bot, null, "Waiting on the licence check.", [send.Id, "timer:2026-10-21T09:00:00Z"], false, clock.GetUtcNow().AddHours(2), null);
        clock.Advance(TimeSpan.FromHours(3));
        await host.Work.SweepAsync();
        var resume = await host.Work.ClaimNextAsync(Bot, null);
        Assert.Contains("time ran out", resume!.Goal);
        var states = JsonNode.Parse(resume.OutcomesJson!)!.AsArray().Select(o => o!["state"]!.ToString()).ToList();
        Assert.Equal(["pending_approval", "waiting"], states);
    }

    [Fact]
    public async Task Waiting_does_not_clean_a_session_taint_and_labels_come_back_with_the_resume()
    {
        var (host, clock) = Build(policy: Policy.Replace("\"mail.send\": { \"Mode\": \"Queue\" }", "\"mail.send\": { \"Mode\": \"Queue\" }, \"inbox.read\": { \"Mode\": \"Execute\", \"Labels\": [ \"customer\" ] }"));
        var s = host.Sessions.Start(Bot.Subject);
        await host.Queue.SubmitAsync(Call("inbox.read", "{}", Bot, s));
        Assert.True(host.Sessions.IsTainted(s));
        await host.Work.ParkAsync(Bot, s, "note", ["timer:2026-10-20T10:00:00Z"], false, null, null);

        clock.Advance(TimeSpan.FromDays(2));   // far past the taint window
        Assert.False(host.Sessions.IsSubjectTainted(Bot.Subject));
        await host.Work.SweepAsync();
        var fresh = host.Sessions.Start(Bot.Subject);
        var resume = await host.Work.ClaimNextAsync(Bot, fresh);
        Assert.True(resume!.Tainted);
        Assert.True(host.Sessions.IsTainted(fresh));
        Assert.Contains(host.Labels.Held(fresh, Bot.Subject), h => h.Label == "customer" && h.Sources.Contains("inbox.read"));
    }

    [Fact]
    public async Task A_lost_lease_counts_as_a_failed_run_and_an_item_that_keeps_failing_is_dead()
    {
        var (host, clock) = Build();
        var item = await host.Work.CreateAsync("Reconcile October.", WorkService.ForProfile("clerk"), "admin:rita");
        for (var run = 1; run <= 3; run++)
        {
            var claimed = await host.Work.ClaimNextAsync(Bot, null);
            Assert.Equal(item.Id, claimed!.Id);
            clock.Advance(TimeSpan.FromMinutes(11));
            await host.Work.SweepAsync();
        }
        var dead = host.Work.Store.Get(item.Id)!;
        Assert.Equal((WorkStatus.Dead, 3), (dead.Status, dead.Runs));
        Assert.Null(await host.Work.ClaimNextAsync(Bot, null));
        Assert.Contains(((IAuditReader)host.Audit).Read(), e => e.Event == "work.dead");
    }

    [Fact]
    public async Task Failing_reopens_until_the_runs_are_used_and_handing_back_does_not_count()
    {
        var (host, _) = Build();
        var item = await host.Work.CreateAsync("Reconcile October.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(Bot, null);
        var back = await host.Work.HandBackAsync(item.Id, Bot, "Needs the finance profile.");
        Assert.Equal((WorkStatus.Open, 0), (back.Status, back.Runs));

        await host.Work.ClaimNextAsync(Bot, null);
        Assert.Equal(WorkStatus.Open, (await host.Work.FailAsync(item.Id, Bot, "ERP down.")).Status);
        await host.Work.ClaimNextAsync(Bot, null);
        await host.Work.FailAsync(item.Id, Bot, "ERP down.");
        await host.Work.ClaimNextAsync(Bot, null);
        Assert.Equal(WorkStatus.Dead, (await host.Work.FailAsync(item.Id, Bot, "ERP down.")).Status);
    }

    [Fact]
    public async Task Only_the_profile_or_identity_the_item_is_for_may_claim_or_read_it()
    {
        var (host, _) = Build();
        var item = await host.Work.CreateAsync("Research ACME.", WorkService.ForProfile("researcher"), "admin:rita");
        Assert.Null(await host.Work.ClaimNextAsync(Bot, null));
        Assert.Null(host.Work.Read(item.Id, Bot, null, out _));
        Assert.Equal(item.Id, (await host.Work.ClaimNextAsync(Helper, null))!.Id);
        await Assert.ThrowsAsync<WorkException>(() => host.Work.CompleteAsync(item.Id, Bot, "done"));
    }

    [Fact]
    public async Task Delegation_goes_through_policy_and_only_to_profiles_the_caller_may_delegate_to()
    {
        var (host, _) = Build();
        var task = await host.Work.CreateAsync("Onboard ACME.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(Bot, null);

        var ok = await host.Queue.SubmitAsync(Call("work.delegate", $$"""{"goal":"Check ACME's licence.","profile":"researcher","fromWork":"{{task.Id}}"}""", Bot, null));
        Assert.Equal(ActionStatus.Succeeded, ok.Status);
        var childId = JsonNode.Parse(ok.ResultJson!)!["workId"]!.ToString();
        var child = host.Work.Store.Get(childId)!;
        Assert.Equal((WorkService.ForProfile("researcher"), task.Thread, 1, Bot.Subject), (child.For, child.Thread, child.Depth, child.From));
        Assert.Equal(["admin:rita", "key:bot"], child.Chain);

        // The researcher profile lists nobody to delegate to.
        await host.Work.ClaimNextAsync(Helper, null);
        var refused = await host.Queue.SubmitAsync(Call("work.delegate", """{"goal":"x","profile":"clerk"}""", Helper, null));
        Assert.Equal(ActionStatus.Failed, refused.Status);
        Assert.Contains("may not delegate", refused.Error!.Message);
    }

    [Fact]
    public async Task Delegation_depth_is_capped()
    {
        var (host, _) = Build(policy: Policy.Replace("\"Work\": { \"Enabled\": true }", "\"Work\": { \"Enabled\": true, \"MaxDelegationDepth\": 1 }")
            .Replace("\"researcher\": { \"Tools\": [ \"inbox.*\", \"clock.*\", \"work.*\" ] }", "\"researcher\": { \"Tools\": [ \"inbox.*\", \"clock.*\", \"work.*\" ], \"MayDelegateTo\": [ \"clerk\" ] }"));
        var root = await host.Work.CreateAsync("Onboard ACME.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(Bot, null);
        var first = await host.Queue.SubmitAsync(Call("work.delegate", $$"""{"goal":"a","profile":"researcher","fromWork":"{{root.Id}}"}""", Bot, null));
        Assert.Equal(ActionStatus.Succeeded, first.Status);
        var child = await host.Work.ClaimNextAsync(Helper, null);
        var second = await host.Queue.SubmitAsync(Call("work.delegate", $$"""{"goal":"b","profile":"clerk","fromWork":"{{child!.Id}}"}""", Helper, null));
        Assert.Equal(ActionStatus.Failed, second.Status);
        Assert.Contains("limited to 1", second.Error!.Message);
    }

    [Fact]
    public async Task Items_expire_and_a_parent_waiting_on_a_child_wakes_when_it_ends()
    {
        var (host, clock) = Build();
        var child = await host.Work.CreateAsync("Research.", WorkService.ForProfile("researcher"), Bot.Subject, ttl: TimeSpan.FromHours(1));
        await host.Work.ParkAsync(Bot, null, "Waiting on research.", [child.Id], false, null, null);
        clock.Advance(TimeSpan.FromHours(2));
        await host.Work.SweepAsync();   // the child expires
        await host.Work.SweepAsync();   // and the parent wakes with it
        Assert.Equal(WorkStatus.Expired, host.Work.Store.Get(child.Id)!.Status);
        var resume = await host.Work.ClaimNextAsync(Bot, null);
        Assert.Equal("stalled", JsonNode.Parse(resume!.OutcomesJson!)!.AsArray().Single()!["state"]!.ToString());
    }

    [Fact]
    public async Task Notes_are_size_capped_and_the_thread_note_carries_between_runs()
    {
        var (host, _) = Build();
        await Assert.ThrowsAsync<WorkException>(() => host.Work.ParkAsync(Bot, null, new string('x', 9000), ["timer:2026-10-21T00:00:00Z"], false, null, null));
        var item = await host.Work.CreateAsync("Reconcile.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(Bot, null);
        host.Work.UpdateThreadNote(item.Id, Bot, null, "Tried the ERP export; next, the bank file.");
        await host.Work.HandBackAsync(item.Id, Bot, "Out of time.");
        await host.Work.ClaimNextAsync(Bot, null);
        Assert.Equal("Tried the ERP export; next, the bank file.", host.Work.Read(item.Id, Bot, null, out var note) is not null ? note!.Note : null);
    }

    [Fact]
    public async Task Work_survives_the_database_and_an_operator_can_requeue_a_dead_item()
    {
        using var db = new TempDb();
        var (host, _) = Build(db.Path);
        var item = await host.Work.CreateAsync("Reconcile.", WorkService.ForProfile("clerk"), "admin:rita");
        for (var i = 0; i < 3; i++) { await host.Work.ClaimNextAsync(Bot, null); await host.Work.FailAsync(item.Id, Bot, "down"); }
        Assert.Equal(WorkStatus.Dead, host.Work.Store.Get(item.Id)!.Status);
        TempDb.ClearPools(db.Path);

        var list = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--work", "--state", "dead"], "host", _ => { }, list));
        Assert.Contains(item.Id, list.ToString());
        var requeued = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--requeue", item.Id, "--by", "rita"], "host", _ => { }, requeued));
        Assert.Contains("is now open", requeued.ToString());
        TempDb.ClearPools(db.Path);
        var (again, _) = Build(db.Path);
        Assert.Equal((WorkStatus.Open, 0), (again.Work.Store.Get(item.Id)!.Status, again.Work.Store.Get(item.Id)!.Runs));
    }

    [Fact]
    public async Task The_work_tools_are_offered_only_when_work_is_enabled()
    {
        var (on, _) = Build();
        await using (var pair = await McpPair.StartAsync(on))
            Assert.Contains("next_work", (await pair.Client.ListToolsAsync()).Select(t => t.Name));
        var (off, _) = Build(policy: Policy.Replace("\"Work\": { \"Enabled\": true },", ""));
        await using (var pair = await McpPair.StartAsync(off))
            Assert.DoesNotContain("next_work", (await pair.Client.ListToolsAsync()).Select(t => t.Name));
    }
}
