namespace OpenTail.Orca.Tests;

/// <summary>
/// The rows of the delay table (reviewer-gate plan, section 2.5) that the undo-window and scheduled-approval tests did not pin (batch 10): a
/// stop that ends Cancelled, the window passing within the allowance, an expiry before the run, and a suspended requester's run held.
/// </summary>
public class Batch10DelayPinsTests
{
    private static readonly ActionRequester Bot = new(RequesterKind.AiSession, "key:bot");

    private static (OpenTailHost Host, ManualClock Clock, FakeInboxModule Module) Build(string policy)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-14T09:00:00Z"));
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UseClock(clock).UsePolicy(PolicyConfig.Parse(policy)).Build();
        return (host, clock, module);
    }

    private static ActionRequest Send() =>
        new("mail.send", """{"to":"a@b.c","body":"b"}""", Bot, null);

    private const string Delayed = """{ "TaintWindow": "00:00:00", "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "00:10:00" } } }""";

    [Fact]
    public async Task A_delayed_run_stopped_before_its_time_is_cancelled_and_never_runs()
    {
        var (host, clock, module) = Build(Delayed);
        var r = await host.Queue.SubmitAsync(Send());

        var stopped = await host.Queue.StopAsync(r.Id, Bot.Id, []);
        Assert.Equal(ActionStatus.Cancelled, stopped.Status);

        clock.Advance(TimeSpan.FromMinutes(11));
        await host.RunMaintenanceOnceAsync();
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Cancelled, (await host.Store.GetAsync(r.Id))!.Status);
    }

    [Fact]
    public async Task Within_the_allowance_the_window_passing_is_a_system_approval_and_then_the_run()
    {
        var (host, clock, module) = Build(Delayed);
        var r = await host.Queue.SubmitAsync(Send());

        clock.Advance(TimeSpan.FromMinutes(10));
        await host.RunMaintenanceOnceAsync();

        Assert.Equal(1, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(r.Id))!.Status);
        var audit = (IAuditReader)host.Audit;
        Assert.Contains(audit.Read(r.Id), e => e.Event == "delay_elapsed");
        Assert.Contains((await host.Store.GetAsync(r.Id))!.Reviews, v => v.Reviewer == "system");
    }

    // DEFECT (batch 10, not fixed here): plan 2.5 says an expired delayed run does not run. The code runs it when its time comes, after its
    // ExpireAfter has passed. Left in place, skipped, for the planner's decision.
    [Fact(Skip = "Plan 2.5 says an expired delayed run does not run; the code runs it after ExpireAfter has passed. Defect reported in batch 10.")]
    public async Task An_expired_delayed_run_does_not_run()
    {
        var (host, clock, module) = Build("""{ "TaintWindow": "00:00:00", "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "00:10:00", "ExpireAfter": "00:05:00" } } }""");
        var r = await host.Queue.SubmitAsync(Send());

        clock.Advance(TimeSpan.FromMinutes(11));
        await host.RunMaintenanceOnceAsync();

        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Expired, (await host.Store.GetAsync(r.Id))!.Status);
    }

    [Fact]
    public async Task A_run_whose_requester_is_suspended_is_held_for_a_person_and_does_not_run()
    {
        var (host, clock, module) = Build(Delayed);
        var r = await host.Queue.SubmitAsync(Send());
        Assert.True(await host.Levels.SuspendAsync(Bot.Id, "rita", "incident for the test"));

        clock.Advance(TimeSpan.FromMinutes(10));
        await host.RunMaintenanceOnceAsync();

        Assert.Equal(0, module.SendCalls);
        Assert.NotEqual(ActionStatus.Succeeded, (await host.Store.GetAsync(r.Id))!.Status);
        Assert.Contains(((IAuditReader)host.Audit).Read(r.Id), e => e.Event == "delay_held");
    }
}
