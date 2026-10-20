using System.Text.Json.Nodes;

namespace OpenTail.Host.Tests;

/// <summary>Sandbox mode: the sandbox answers, no module code runs, and policy, taint and queueing stay the real thing.</summary>
public class SandboxTests
{
    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    private static (OpenTailHost Host, FakeInboxModule Module, RecordingSandbox Sandbox, List<string> Reads) Build(string policyJson)
    {
        var module = new FakeInboxModule();
        var reads = new List<string>();
        var sandbox = new RecordingSandbox((call, _) =>
        {
            lock (reads) reads.Add(call.Tool.Name);
            return Task.FromResult("{\"from\":\"world\",\"text\":\"An email from the scenario world.\"}");
        });
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(policyJson)).UseSandbox(sandbox).Build();
        return (host, module, sandbox, reads);
    }

    [Fact]
    public async Task Reads_are_answered_by_the_sandbox_and_still_taint()
    {
        var (host, _, _, reads) = Build(Hosts.SendExecute);
        await using var pair = await McpPair.StartAsync(host);
        var session = S(await pair.CallAsync("start_session"), "sessionId");

        var read = await pair.CallAsync("inbox.read", ("sessionId", session));

        Assert.Equal("succeeded", S(read, "status"));
        Assert.Contains("scenario world", read.ToJsonString());
        Assert.DoesNotContain("x@evil.example", read.ToJsonString()); // the module's own answer never appears
        Assert.Equal(["inbox.read"], reads);
        Assert.True(host.Sessions.IsTainted(session));
    }

    [Fact]
    public async Task External_effects_are_recorded_and_never_run_even_when_policy_executes_them()
    {
        var (host, module, sandbox, _) = Build(Hosts.SendExecute);
        await using var pair = await McpPair.StartAsync(host);
        var clean = S(await pair.CallAsync("start_session"), "sessionId");

        var sent = await pair.CallAsync("mail.send", ("to", "a@b.example"), ("body", "hi"), ("sessionId", clean));

        Assert.Equal("succeeded", S(sent, "status"));
        Assert.Equal(0, module.SendCalls);
        var call = Assert.Single(sandbox.Recorded);
        Assert.Equal("mail.send", call.ToolName);
        Assert.Equal(ToolRisk.ExternalEffect, call.Risk);
        Assert.Contains("a@b.example", call.ArgumentsJson);
    }

    [Fact]
    public async Task Queueing_is_unchanged_and_approval_records_instead_of_running()
    {
        var (host, module, sandbox, _) = Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);

        var queued = await pair.CallAsync("mail.send", ("to", "a@b.example"), ("body", "hi"));
        Assert.Equal("pending_approval", S(queued, "status"));
        Assert.Empty(sandbox.Recorded);

        var done = await host.Queue.ReviewAsync(S(queued, "actionId"),
            new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Single(sandbox.Recorded);
    }

    [Fact]
    public async Task Destructive_and_write_tools_are_recorded()
    {
        var policy = """
            { "DefaultReadMode": "Execute", "Tools": { "files.delete": { "Mode": "Execute" }, "crash.now": { "Mode": "Execute" } } }
            """;
        var (host, _, sandbox, _) = Build(policy);
        await using var pair = await McpPair.StartAsync(host);
        var clean = S(await pair.CallAsync("start_session"), "sessionId");

        var deleted = await pair.CallAsync("files.delete", ("path", "x"), ("sessionId", clean));
        var crashed = await pair.CallAsync("crash.now", ("x", "y"), ("sessionId", clean));

        Assert.Equal("succeeded", S(deleted, "status"));
        Assert.Equal("succeeded", S(crashed, "status")); // the throwing module function was never called
        Assert.Equal(["files.delete", "crash.now"], sandbox.Recorded.Select(r => r.ToolName));
    }

    [Fact]
    public async Task Shadow_reads_run_the_real_module_but_writes_are_still_recorded()
    {
        var module = new FakeInboxModule();
        var sandbox = new RecordingSandbox(RecordingSandbox.ReadLive);
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Hosts.SendExecute)).UseSandbox(sandbox).Build();
        await using var pair = await McpPair.StartAsync(host);
        var clean = S(await pair.CallAsync("start_session"), "sessionId");

        var read = await pair.CallAsync("clock.now", ("sessionId", clean));
        var sent = await pair.CallAsync("mail.send", ("to", "a@b.example"), ("body", "hi"), ("sessionId", clean));

        Assert.Contains("2026-10-15", read.ToJsonString());
        Assert.Equal("succeeded", S(sent, "status"));
        Assert.Equal(0, module.SendCalls);
        Assert.Single(sandbox.Recorded);
    }

    [Fact]
    public void The_host_says_it_is_a_sandbox()
    {
        var (host, _, _, _) = Build(Hosts.SendQueued);
        Assert.True(host.IsSandbox);
        Assert.Contains(host.StartupWarnings, w => w.StartsWith("Sandbox mode", StringComparison.Ordinal));
        Assert.False(new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Hosts.SendQueued)).Build().IsSandbox);
    }
}
