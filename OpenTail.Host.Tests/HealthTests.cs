namespace OpenTail.Host.Tests;

/// <summary>SOP gap G14: <c>/healthz</c> says whether the Host is doing its job: a stalled maintenance sweep is unhealthy (an
/// orchestrator restarts it), failing notifications are degraded (a restart would not fix them).</summary>
public class HealthTests
{
    [Fact]
    public async Task Starting_then_ok_then_stale_when_the_sweep_stops()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse("{}")).UseClock(clock).Build();
        Assert.Equal((HealthState.Ok, "starting"), (host.Health.Report().State, host.Health.Report().Checks["maintenance"]));

        await host.RunMaintenanceOnceAsync();
        Assert.Equal((HealthState.Ok, "ok"), (host.Health.Report().State, host.Health.Report().Checks["maintenance"]));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal((HealthState.Unhealthy, "stale"), (host.Health.Report().State, host.Health.Report().Checks["maintenance"]));
        await host.RunMaintenanceOnceAsync();
        Assert.Equal(HealthState.Ok, host.Health.Report().State);
    }

    [Fact]
    public void A_start_that_never_completes_a_sweep_turns_unhealthy()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var health = new HostHealth(clock);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HealthState.Unhealthy, health.Report().State);
    }

    [Fact]
    public void Other_checks_degrade_without_making_the_Host_unhealthy_and_a_throwing_check_is_degraded()
    {
        var health = new HostHealth(TimeProvider.System);
        health.SweepFinished();
        health.Add("notifications", () => HealthState.Degraded);
        health.Add("broken", () => throw new InvalidOperationException());
        var report = health.Report();
        Assert.Equal(HealthState.Degraded, report.State);
        Assert.Equal(("degraded", "degraded", "ok"), (report.Checks["notifications"], report.Checks["broken"], report.Checks["maintenance"]));
    }
}
