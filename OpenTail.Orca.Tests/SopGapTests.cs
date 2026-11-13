using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Gaps the operating procedures (SOP.md) found, closed: who started the Host, suspending and cancelling by hand in an
/// incident, recording a restore, listing paused routes, telling agents about work, and waking agents for work reopened elsewhere.</summary>
public class SopGapTests
{
    private static ActionRequest Call(string tool, string args = "{}") => new(tool, args, new ActionRequester(RequesterKind.AiSession, "key:bot"), null);

    [Fact]
    public void G4_the_runner_names_who_started_it_or_the_os_account()
    {
        Assert.Equal("rita", DeploymentRunner.StartedBy(["--policy", "p.json", "--by", "rita"]));
        Assert.StartsWith("os:", DeploymentRunner.StartedBy(["--policy", "p.json"]));
    }

    [Fact]
    public async Task G4_policy_loaded_records_who_started_the_Host()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("{}")).StartedBy("rita").Build();
        await host.RunMaintenanceOnceAsync();
        Assert.Equal("rita", ((IAuditReader)host.Audit).Read().Single(e => e.Event == "policy_loaded").Actor);
    }

    [Fact]
    public async Task G7_G8_in_an_incident_an_operator_suspends_an_identity_and_cancels_its_pending_actions()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Hosts.SendQueued, db.Path);
        var a = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"x@y.z","body":"1"}"""));
        var b = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"x@y.z","body":"2"}"""));
        TempDb.ClearPools(db.Path);

        Assert.Equal(64, await PolicyCli.TryRunAsync(["--db", db.Path, "--suspend", "key:bot", "--by", "rita"], "t", _ => { }, new StringWriter()));
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--suspend", "key:bot", "--by", "rita", "--reason", "decoy touched"], "t", _ => { }, new StringWriter()));
        var cancelled = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--cancel-pending", "key:bot", "--by", "rita", "--reason", "incident 42"], "t", _ => { }, cancelled));
        Assert.Contains("Cancelled 2 pending action(s)", cancelled.ToString());
        TempDb.ClearPools(db.Path);

        var again = Hosts.BuildSqlite(new FakeInboxModule(), Hosts.SendQueued, db.Path);
        Assert.Equal(IdentityLevel.Suspended, again.Levels.Effective("key:bot"));
        Assert.Equal(ActionStatus.Denied, (await again.Queue.SubmitAsync(Call("clock.now"))).Status);
        Assert.All(new[] { a.Id, b.Id }, id => Assert.Equal(ActionStatus.Cancelled, again.Store.GetAsync(id).Result!.Status));
        var trail = ((IAuditReader)again.Audit).Read();
        Assert.Contains(trail, e => e.Event == "level_suspended" && e.Actor == "rita" && e.Detail!.Contains("decoy touched"));
        Assert.Equal(2, trail.Count(e => e.Event == "approval_cancelled" && e.Actor == "rita"));
    }

    [Fact]
    public async Task G10_a_restore_is_recorded_in_the_trail_with_the_head_it_restored_to()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Hosts.SendQueued, db.Path);
        await host.Queue.SubmitAsync(Call("mail.send", """{"to":"x@y.z","body":"1"}"""));
        var head = ((SqliteAuditSink)host.Audit).Head()!.ToString();
        TempDb.ClearPools(db.Path);
        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--record-restore", "--by", "rita", "--note", "restored the 02:00 backup after disk failure"], "t", _ => { }, output));
        Assert.Contains(head, output.ToString());
        var entry = new SqliteAuditSink(new SqliteActionStore(db.Path)).Read().Single(e => e.Event == "restore_recorded");
        Assert.Equal("rita", entry.Actor);
        Assert.Contains($"head {head}", entry.Detail);
    }

    [Fact]
    public async Task G3_paused_routes_and_their_held_calls_are_listed()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Failures": { "CircuitMode": "Enforce", "CircuitThreshold": 2 }, "Tools": { "crash.now": { "Mode": "Execute" } } }"""))
            .UseSqlite(db.Path).Build();
        await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "circuit.opened", "act_1", "svc.send", "system", "svc.send|environment|disk_full; probe after 2026-10-20 12:00:00Z"));
        TempDb.ClearPools(db.Path);
        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--circuits"], "t", _ => { }, output));
        Assert.Contains("svc.send  paused since", output.ToString());
        Assert.Contains("a restart clears them", output.ToString());
    }

    [Fact]
    public async Task G13_agents_are_told_about_the_work_tools_when_work_is_enabled()
    {
        var on = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("""{ "Work": { "Enabled": true } }""")).Build();
        var off = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("{}")).Build();
        Assert.Contains("call next_work", on.CreateServerOptions().ServerInstructions);
        Assert.DoesNotContain("next_work", off.CreateServerOptions().ServerInstructions);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task G16_work_reopened_by_the_command_line_wakes_agents_on_the_next_sweep_once()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Work": { "Enabled": true, "MaxRuns": 1 }, "Agents": { "clerk": { "Tools": [ "work.*" ] } } }""")).UseSqlite(db.Path).Build();
        var clerk = new CallerIdentity("key:clerk", "clerk", CallerKind.Agent) { Profile = "clerk" };
        var item = await host.Work.CreateAsync("Reconcile.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(clerk, null);
        await host.Work.FailAsync(item.Id, clerk, "down");
        var woken = new List<string>();
        host.Work.Ready += i => woken.Add(i.Id);

        TempDb.ClearPools(db.Path);
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--redrive", item.Id, "--by", "rita"], "t", _ => { }, new StringWriter()));
        await host.Work.SweepAsync();
        await host.Work.SweepAsync();
        Assert.Equal([item.Id], woken.Where(id => id == item.Id));
    }
}
