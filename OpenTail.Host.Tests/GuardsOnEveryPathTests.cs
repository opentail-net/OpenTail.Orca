namespace OpenTail.Host.Tests;

/// <summary>
/// The guards class test (connectivity plan section 31): a guard holds on every path that runs an action, not only where it was
/// first written. Suspension: approval was guarded, but automatic retries and recovery after a restart ran a suspended
/// identity's actions.
/// </summary>
public class GuardsOnEveryPathTests
{
    private sealed class Service : IHostModule
    {
        public int TransientFailuresLeft;
        public int Calls;
        public string Id => "svc";

        public void Register(IToolRegistry registry)
        {
            registry.Add(new ToolDescriptor("svc.put", "Idempotent write.", ToolRisk.Write, Id) { IdempotentByKey = true },
                AIFunctionFactory.Create(() =>
                {
                    Calls++;
                    if (TransientFailuresLeft-- > 0) throw new ToolTransientException("busy");
                    return "ok";
                }, "svc.put", "Idempotent write."));
        }
    }

    private static readonly ActionRequester Ops = new(RequesterKind.User, "ops");

    private static (OpenTailHost Host, Service Svc, ManualClock Clock) Build(string mode)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var svc = new Service();
        var host = new OpenTailHostBuilder().AddModule(svc).UseClock(clock).UsePolicy(PolicyConfig.Parse($$"""
            { "Tools": { "svc.put": { "Mode": "{{mode}}" } } }
            """)).Build();
        return (host, svc, clock);
    }

    private static Task Suspend(OpenTailHost host) => host.Levels.SuspendAsync("ops", "alice", "incident 42");

    private static async Task AssertHeld(OpenTailHost host, Service svc, string id, int callsBefore)
    {
        var after = (await host.Store.GetAsync(id))!;
        Assert.Equal(ActionStatus.Failed, after.Status);
        Assert.Equal("suspended", after.Error!.Code);
        Assert.Equal(callsBefore, svc.Calls); // the tool never ran
    }

    [Fact]
    public async Task An_automatic_retry_does_not_run_once_the_requester_is_suspended()
    {
        var (host, svc, clock) = Build("Execute");
        svc.TransientFailuresLeft = 1;
        var failed = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Ops, null));
        Assert.NotNull(failed.RetryAt);

        await Suspend(host);
        clock.Advance(TimeSpan.FromMinutes(5));
        await host.Executor.RetryDueAsync();

        await AssertHeld(host, svc, failed.Id, callsBefore: 1);
    }

    [Fact]
    public async Task An_action_approved_before_a_restart_does_not_run_once_the_requester_is_suspended()
    {
        var (host, svc, _) = Build("Queue");
        var pending = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Ops, null));
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved, r => r); // approved, then the crash

        await Suspend(host);
        await host.RunMaintenanceOnceAsync();

        await AssertHeld(host, svc, pending.Id, callsBefore: 0);
    }

    [Fact]
    public async Task An_interrupted_idempotent_action_is_not_rerun_once_the_requester_is_suspended()
    {
        var (host, svc, clock) = Build("Queue");
        var pending = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Ops, null));
        await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        Assert.NotNull(await host.Store.TransitionAsync(pending.Id, ActionStatus.Approved, ActionStatus.Executing,
            r => r with { LeaseUntil = clock.GetUtcNow().AddMinutes(-1) })); // running when the Host stopped

        await Suspend(host);
        await host.RunMaintenanceOnceAsync();

        await AssertHeld(host, svc, pending.Id, callsBefore: 0);
    }

    [Fact]
    public async Task A_reviewer_retry_does_not_run_while_the_requester_is_suspended()
    {
        var (host, svc, _) = Build("Execute");
        svc.TransientFailuresLeft = 1;
        var failed = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Ops, null));

        await Suspend(host);
        try
        {
            await host.Queue.ReviewAsync(failed.Id, new ActionReview("alice", ReviewDecision.Retry, "try again", DateTimeOffset.UtcNow));
        }
        catch (InvalidOperationException)
        {
            // Refused outright is as good as held.
        }

        Assert.Equal(1, svc.Calls);
    }

    [Fact]
    public async Task Cleared_the_held_action_can_be_retried_and_runs()
    {
        var (host, svc, clock) = Build("Execute");
        svc.TransientFailuresLeft = 1;
        var failed = await host.Queue.SubmitAsync(new ActionRequest("svc.put", "{}", Ops, null));
        await Suspend(host);
        clock.Advance(TimeSpan.FromMinutes(5));
        await host.Executor.RetryDueAsync();

        await host.Levels.ClearAsync("ops", "alice", keyRotated: false);
        var retried = await host.Queue.ReviewAsync(failed.Id, new ActionReview("alice", ReviewDecision.Retry, "cleared", DateTimeOffset.UtcNow));

        Assert.Equal(ActionStatus.Succeeded, retried.Status);
        Assert.Equal(2, svc.Calls);
    }
}
