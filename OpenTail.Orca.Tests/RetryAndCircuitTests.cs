namespace OpenTail.Orca.Tests;

/// <summary>Failures design, step 2: the Host retries only transient failures of tools idempotent by key, backing off and a few times;
/// a route that keeps failing the same way is paused (report only by default), its calls held, one probe after a cool-down, and held
/// calls released a few at a time.</summary>
public class RetryAndCircuitTests
{
    private sealed class Service : IHostModule
    {
        public int TransientFailuresLeft;
        public bool Down;
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
            registry.Add(new ToolDescriptor("svc.send", "Not idempotent.", ToolRisk.Write, Id),
                AIFunctionFactory.Create(() =>
                {
                    Calls++;
                    if (Down) throw new ToolFailureException(ToolFailureKind.Environment, "Disk full.", "disk_full");
                    return "sent";
                }, "svc.send", "Not idempotent."));
            registry.Add(new ToolDescriptor("svc.bad", "Always a bad request.", ToolRisk.Write, Id),
                AIFunctionFactory.Create(() => { Calls++; throw new ToolFailureException(ToolFailureKind.Contract, "Bad request.", "http_400"); }, "svc.bad", "Always a bad request."));
        }
    }

    private static (OpenTailHost Host, Service Svc, ManualClock Clock) Build(string failures = "")
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var svc = new Service();
        var host = new OpenTailHostBuilder().AddModule(svc).UseClock(clock).UsePolicy(PolicyConfig.Parse($$"""
            { {{failures}} "Tools": { "svc.put": { "Mode": "Execute" }, "svc.send": { "Mode": "Execute" }, "svc.bad": { "Mode": "Execute" } } }
            """)).Build();
        return (host, svc, clock);
    }

    private static Task<ActionRecord> Call(OpenTailHost host, string tool) =>
        host.Queue.SubmitAsync(new ActionRequest(tool, "{}", new ActionRequester(RequesterKind.User, "ops"), null));

    [Fact]
    public async Task A_transient_failure_of_an_idempotent_tool_is_retried_after_a_backoff_until_it_succeeds()
    {
        var (host, svc, clock) = Build();
        svc.TransientFailuresLeft = 2;
        var r = await Call(host, "svc.put");
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.NotNull(r.RetryAt);

        await host.Executor.RetryDueAsync();          // not yet due
        Assert.Equal(1, svc.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.Executor.RetryDueAsync();          // second attempt fails again, scheduled once more
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.Executor.RetryDueAsync();
        var done = await host.Store.GetAsync(r.Id);
        Assert.Equal((ActionStatus.Succeeded, 3, 3), (done!.Status, done.Attempts, svc.Calls));
        Assert.Contains(((IAuditReader)host.Audit).Read(r.Id), e => e.Event == "retry_automatic");
    }

    [Fact]
    public async Task Retries_stop_after_the_limit_and_tools_not_idempotent_are_never_retried_automatically()
    {
        var (host, svc, clock) = Build("\"Failures\": { \"AutoRetries\": 1 },");
        svc.TransientFailuresLeft = 10;
        var r = await Call(host, "svc.put");
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.Executor.RetryDueAsync();
        var after = await host.Store.GetAsync(r.Id);
        Assert.Equal((ActionStatus.Failed, 2), (after!.Status, after.Attempts));
        Assert.Null(after.RetryAt);

        svc.Down = true;
        var send = await Call(host, "svc.send");
        Assert.Null(send.RetryAt);
    }

    [Fact]
    public async Task By_default_a_failing_route_is_only_reported_once_and_calls_go_on()
    {
        var (host, svc, _) = Build();
        svc.Down = true;
        for (var i = 0; i < 7; i++) await Call(host, "svc.send");
        Assert.Equal(7, svc.Calls);
        var reported = ((IAuditReader)host.Audit).Read().Where(e => e.Event == "circuit.would_open").ToList();
        Assert.Contains("svc.send|environment|disk_full", Assert.Single(reported).Detail);
    }

    [Fact]
    public async Task Enforced_a_paused_route_holds_calls_probes_after_the_cooldown_and_releases_held_calls_gradually()
    {
        var (host, svc, clock) = Build("\"Failures\": { \"CircuitMode\": \"Enforce\", \"CircuitThreshold\": 3, \"ReleasePerSweep\": 2 },");
        svc.Down = true;
        for (var i = 0; i < 3; i++) await Call(host, "svc.send");
        Assert.Contains(((IAuditReader)host.Audit).Read(), e => e.Event == "circuit.opened");

        // Paused: calls are held without reaching the tool.
        var held = new List<ActionRecord>();
        for (var i = 0; i < 3; i++) held.Add(await Call(host, "svc.send"));
        Assert.Equal(3, svc.Calls);
        Assert.All(held, h => Assert.Equal(("circuit_open", true), (h.Error!.Code, h.RetryAt is not null)));

        // After the cool-down the next call probes; it succeeds and the route reopens.
        svc.Down = false;
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(ActionStatus.Succeeded, (await Call(host, "svc.send")).Status);
        Assert.Contains(((IAuditReader)host.Audit).Read(), e => e.Event == "circuit.closed");

        // Held calls go a few per sweep, and do not count as attempts.
        await host.Executor.RetryDueAsync();
        var states = await Task.WhenAll(held.Select(h => host.Store.GetAsync(h.Id)));
        Assert.Equal(2, states.Count(s => s!.Status == ActionStatus.Succeeded));
        await host.Executor.RetryDueAsync();
        states = await Task.WhenAll(held.Select(h => host.Store.GetAsync(h.Id)));
        Assert.All(states, s => Assert.Equal((ActionStatus.Succeeded, 1), (s!.Status, s.Attempts)));
    }

    [Fact]
    public async Task A_failed_probe_pauses_the_route_again_for_longer()
    {
        var (host, svc, clock) = Build("\"Failures\": { \"CircuitMode\": \"Enforce\", \"CircuitThreshold\": 2 },");
        svc.Down = true;
        await Call(host, "svc.send");
        await Call(host, "svc.send");
        clock.Advance(TimeSpan.FromMinutes(2));
        await Call(host, "svc.send");   // the probe, which fails
        var reopened = ((IAuditReader)host.Audit).Read().Single(e => e.Event == "circuit.reopened");
        Assert.NotNull(reopened);
        Assert.Equal(TimeSpan.FromMinutes(2), host.Executor.Circuits!.ProbeAt("svc.send") - clock.GetUtcNow());
    }

    [Fact]
    public async Task Bad_requests_never_pause_a_route()
    {
        var (host, svc, _) = Build("\"Failures\": { \"CircuitMode\": \"Enforce\", \"CircuitThreshold\": 2 },");
        for (var i = 0; i < 5; i++) await Call(host, "svc.bad");
        Assert.Equal(5, svc.Calls);
        Assert.DoesNotContain(((IAuditReader)host.Audit).Read(), e => e.Event.StartsWith("circuit.", StringComparison.Ordinal));
    }

    [Fact]
    public void Backoff_doubles_with_jitter_and_honours_retry_after()
    {
        var c = new Circuits(new FailuresConfig(), TimeProvider.System);
        Assert.InRange(c.Backoff(1, null), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.25));
        Assert.InRange(c.Backoff(3, null), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromMinutes(2), c.Backoff(1, TimeSpan.FromMinutes(2)));
        Assert.InRange(c.Backoff(20, null), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6.25));
    }

    [Fact]
    public async Task A_failed_circuit_audit_line_does_not_stop_a_probe_from_recording_its_outcome()
    {
        // The probe that closes a paused route writes circuit.closed. That line is bookkeeping: when it fails, the probe's outcome is still
        // committed as Succeeded, not left Executing.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var svc = new Service();
        var audit = new FailingOnEvent("circuit.closed");
        var host = new OpenTailHostBuilder().AddModule(svc).UseClock(clock).UseAudit(audit).UsePolicy(PolicyConfig.Parse("""
            { "Failures": { "CircuitMode": "Enforce", "CircuitThreshold": 3 }, "Tools": { "svc.send": { "Mode": "Execute" } } }
            """)).Build();
        svc.Down = true;
        for (var i = 0; i < 3; i++) await Call(host, "svc.send");
        svc.Down = false;
        clock.Advance(TimeSpan.FromMinutes(2));

        var probe = await Call(host, "svc.send");

        Assert.Equal(ActionStatus.Succeeded, probe.Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(probe.Id))!.Status);
        Assert.Contains(audit.Inner.Entries, e => e.Event == "bookkeeping_failed" && e.Detail == "circuit: IOException");
    }

    /// <summary>An audit trail that refuses one event, as a failing disk would.</summary>
    private sealed class FailingOnEvent(string failOn) : IAuditSink
    {
        public InMemoryAuditSink Inner { get; } = new();

        public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
            entry.Event == failOn ? throw new IOException("audit trail unavailable") : Inner.AppendAsync(entry, ct);
    }
}
