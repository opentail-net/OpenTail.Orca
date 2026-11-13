using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace OpenTail.Orca.Tests;

/// <summary>
/// W7: a module enabled with <c>"Isolation": { "Mode": "Process" }</c> runs in a worker process (here a deployment-shaped test program).
/// The Host carries out its HTTP and keeps HTTP credentials; the worker sees none of the Host's environment; hangs, crashes, noise on
/// stdout and oversized answers are contained; and tools, previews, taint and errors behave exactly as in process.
/// </summary>
public class IsolationTests
{
    private static string WorkerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = dir.FullName.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenTail.Orca.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "OpenTail.Orca.Tests.Worker", "bin", config, "net10.0", "OpenTail.Orca.Tests.Worker.dll");
    }

    private static async Task<(OpenTailHost Host, IsolatedModule Module)> HostFor(string isolation = "", string network = "", string credentials = "",
        string policy = """{ "DefaultReadMode": "Execute", "TaintWindow": "00:10:00", "Tools": { "iso.send": { "Mode": "Execute" } } }""")
    {
        var modules = ModulesConfig.Parse($$"""
            { "FactoryAssemblies": [ "OpenTail.Orca.Tests.Worker" ],
              "Modules": [ { "Type": "isolation-probe", "Id": "iso", "AllowDefaultRead": true {{network}} {{credentials}},
                "Isolation": { "Mode": "Process", "WorkerCommand": [ "dotnet", {{JsonSerializer.Serialize(WorkerDll())}} ] {{isolation}} } } ] }
            """);
        var loaded = await ModuleLoader.LoadAsync(modules, ModuleLoadMode.Run);
        var module = (IsolatedModule)loaded.Modules.Single().Module;
        return (new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).AddModules(loaded).Build(), module);
    }

    private static async Task<ActionRecord> Call(OpenTailHost host, string tool, string args = "{}", string? session = null) =>
        await host.Queue.SubmitAsync(Req.Call(tool, args, session ?? host.Sessions.Start()));

    [Fact]
    public async Task The_module_runs_in_another_process_and_the_catalog_shows_the_real_module()
    {
        var (host, module) = await HostFor();
        await using var _ = module;
        var r = await Call(host, "iso.whoami");
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        var pid = JsonDocument.Parse(r.ResultJson!).RootElement.GetProperty("pid").GetInt32();
        Assert.NotEqual(Environment.ProcessId, pid);
        Assert.Equal(module.ProcessId, pid);
        Assert.Contains("\"module\":\"iso\"", r.ResultJson);

        var loaded = host.Catalog.Modules.Single();
        Assert.Equal("OpenTail.Orca.Tests.Worker", loaded.Assembly);
        Assert.Equal(HostContract.Version, loaded.ContractVersion);
        Assert.True(loaded.Options.Isolated);
        Assert.Equal("Isolation probe", loaded.Manifest.DisplayName);
        Assert.Contains(OpenTail.Orca.Admin.ModulesCli.Describe(host), l => l.Contains("runs in its own process"));
    }

    [Fact]
    public async Task The_worker_sees_none_of_the_hosts_environment_and_a_credential_variable_cannot_be_passed()
    {
        Environment.SetEnvironmentVariable("OTAIL_ISO_HOST_SECRET", "host-only-value");
        Environment.SetEnvironmentVariable("OTAIL_ISO_SHARED", "shared-value");
        var (host, module) = await HostFor(isolation: """, "Environment": [ "OTAIL_ISO_SHARED" ]""");
        await using var _ = module;
        Assert.Contains("(not set)", (await Call(host, "iso.env", """{"name":"OTAIL_ISO_HOST_SECRET"}""")).ResultJson);
        Assert.Contains("shared-value", (await Call(host, "iso.env", """{"name":"OTAIL_ISO_SHARED"}""")).ResultJson);

        var ex = Assert.ThrowsAny<Exception>(() => ModulesConfig.Parse("""
            { "Modules": [ { "Type": "isolation-probe", "Id": "iso", "Credentials": { "db": { "Env": "OTAIL_ISO_DB", "Kind": "Secret" } },
              "Isolation": { "Mode": "Process", "Environment": [ "OTAIL_ISO_DB" ] } } ] }
            """));
        Assert.Contains("may not pass 'OTAIL_ISO_DB'", ex.Message);
    }

    [Fact]
    public async Task Http_goes_through_the_host_which_attaches_the_token_that_the_worker_never_gets()
    {
        Environment.SetEnvironmentVariable("OTAIL_ISO_TOKEN", "iso-bearer-token-1");
        Environment.SetEnvironmentVariable("OTAIL_ISO_DB", "iso-db-password");
        await using var api = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync(ctx.Request.Headers.Authorization == "Bearer iso-bearer-token-1" ? "authorised" : "anonymous"));
        var (host, module) = await HostFor(
            network: $$""", "Network": { "Hosts": [ "{{api.Origin}}" ] }""",
            credentials: $$""", "Credentials": { "api": { "Env": "OTAIL_ISO_TOKEN", "Host": "{{api.Origin}}" }, "db": { "Env": "OTAIL_ISO_DB", "Kind": "Secret" } }""");
        await using var _ = module;

        var ok = await Call(host, "iso.fetch", JsonSerializer.Serialize(new { url = new Uri(api.BaseUri, "x").AbsoluteUri }));
        Assert.Equal(ActionStatus.Succeeded, ok.Status);
        Assert.Contains("authorised", ok.ResultJson);

        var token = await Call(host, "iso.secret", """{"name":"api"}""");
        Assert.Equal(ActionStatus.Failed, token.Status);
        Assert.Contains("stays in the Host", token.Error!.Message);

        var db = await Call(host, "iso.secret", """{"name":"db"}""");
        Assert.Equal(ActionStatus.Succeeded, db.Status);
        Assert.Equal("15", db.ResultJson);

        var elsewhere = await Call(host, "iso.fetch", """{"url":"https://example.com/"}""");
        Assert.Equal(ActionStatus.Failed, elsewhere.Status);
        Assert.Contains("may not reach", elsewhere.Error!.Message);
    }

    [Fact]
    public async Task A_hanging_call_times_out_the_worker_is_stopped_and_the_next_call_gets_a_new_one()
    {
        var (host, module) = await HostFor(isolation: """, "CallTimeout": "00:00:02" """);
        await using var _ = module;
        var before = module.ProcessId;

        var hung = await Call(host, "iso.hang");
        Assert.Equal(ActionStatus.Failed, hung.Status);
        Assert.True(hung.Error!.Retryable);
        Assert.Contains("did not answer within 2 s", hung.Error.Message);

        var after = await Call(host, "iso.whoami");
        Assert.Equal(ActionStatus.Succeeded, after.Status);
        Assert.NotEqual(before, module.ProcessId);
    }

    [Fact]
    public async Task A_crash_fails_the_call_as_retryable_and_the_worker_comes_back()
    {
        var (host, module) = await HostFor();
        await using var _ = module;
        var crashed = await Call(host, "iso.crash");
        Assert.Equal(ActionStatus.Failed, crashed.Status);
        Assert.True(crashed.Error!.Retryable);
        Assert.Contains("process stopped", crashed.Error.Message);
        Assert.Equal(ActionStatus.Succeeded, (await Call(host, "iso.whoami")).Status);
    }

    [Fact]
    public async Task Noise_on_stdout_does_not_break_the_protocol_and_an_oversized_answer_is_refused()
    {
        var (host, module) = await HostFor(isolation: """, "MaxMessageBytes": 200000""");
        await using var _ = module;
        var noisy = await Call(host, "iso.noisy");
        Assert.Equal(ActionStatus.Succeeded, noisy.Status);
        Assert.Contains("still fine", noisy.ResultJson);

        var big = await Call(host, "iso.big", """{"bytes":500000}""");
        Assert.Equal(ActionStatus.Failed, big.Status);
        Assert.True(big.Error!.Retryable);
        Assert.Equal(ActionStatus.Succeeded, (await Call(host, "iso.whoami")).Status);
    }

    [Fact]
    public async Task Previews_errors_and_taint_behave_as_in_process()
    {
        var (host, module) = await HostFor();
        await using var _ = module;

        var sent = await Call(host, "iso.send", """{"to":"a@corp.example"}""");
        Assert.Equal(ActionStatus.Succeeded, sent.Status);
        Assert.Contains("sent to a@corp.example", sent.ResultJson);

        var transient = await Call(host, "iso.fail", """{"kind":"transient"}""");
        Assert.True(transient.Error!.Retryable);
        Assert.Contains("Try again after 7 s", transient.Error.Message);
        var broken = await Call(host, "iso.fail", """{"kind":"other"}""");
        Assert.False(broken.Error!.Retryable);
        Assert.Contains("broken", broken.Error.Message);

        // A ReadsUntrusted tool taints across the process boundary: the next ExternalEffect call is held although policy says Execute.
        var s = host.Sessions.Start();
        await Call(host, "iso.fetch", """{"url":"https://example.com/"}""", s); // fails (no host allowed) but still marks the session
        Assert.True(host.Sessions.IsTainted(s));
        var held = await Call(host, "iso.send", """{"to":"b@corp.example"}""", s);
        Assert.Equal(ActionStatus.Pending, held.Status);
        Assert.Contains("from process", held.PreviewJson);                      // the preview ran in the worker

        var refused = await Call(host, "iso.send", """{"to":"x@elsewhere.example"}""", s);
        Assert.Equal(ActionStatus.Denied, refused.Status);                       // the worker's preview refused it before review
        Assert.Equal("invalid_arguments", refused.Error!.Code);
    }

    [Fact]
    public async Task Describing_builds_an_isolated_module_in_process_without_starting_a_worker()
    {
        var loaded = await ModuleLoader.LoadAsync(ModulesConfig.Parse($$"""
            { "FactoryAssemblies": [ "OpenTail.Orca.Tests.Worker" ],
              "Modules": [ { "Type": "isolation-probe", "Id": "iso", "Isolation": { "Mode": "Process", "WorkerCommand": [ "no-such-program" ] } } ] }
            """), ModuleLoadMode.Describe);
        Assert.IsNotType<IsolatedModule>(loaded.Modules.Single().Module);
        Assert.True(loaded.Modules.Single().Options.Isolated);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ModuleLoader.LoadAsync(ModulesConfig.Parse("""
            { "FactoryAssemblies": [ "OpenTail.Orca.Tests.Worker" ],
              "Modules": [ { "Type": "isolation-probe", "Id": "iso", "Isolation": { "Mode": "Process", "WorkerCommand": [ "no-such-program-otail" ] } } ] }
            """), ModuleLoadMode.Run));
        Assert.Contains("iso", ex.Message);
    }
}
