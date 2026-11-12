using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenTail.Host.Admin;
using OpenTail.Modules.Files;
using OpenTail.Modules.OpenApi;

namespace OpenTail.Host.Tests;

/// <summary>Modules as bricks (docs/done/MCP-HOST-V3-PLAN.md W1 to W4): manifest and contract, credentials and guarded HTTP, enabling modules from
/// configuration, uninstalling, policy fragments, adoption, lint and policy tests.</summary>
public class BrickTests
{
    // ---------- test bricks ------------------------------------------------------------------------------------------------

    /// <summary>A brick whose manifest and suggested policy the test controls, with one tool of each risk.</summary>
    public sealed class ProbeFactory : IHostModuleFactory
    {
        public string Type => "probe";
        public static string? Suggested { get; set; }
        public static string[] Hosts { get; set; } = [];
        public static bool ForeignTool { get; set; }

        public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
        {
            Id = id,
            NetworkHosts = Hosts,
            Credentials = [new CredentialRequirement("svc", CredentialKind.Bearer, "service token") { Optional = true }],
            SuggestedPolicyJson = Suggested,
        };

        public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<IHostModule>(new ProbeModule(context, Describe(context.Id, context.Config)));
    }

    public sealed class ProbeModule(ModuleContext ctx, ModuleManifest manifest) : IHostModule
    {
        public string Id => ctx.Id;
        public ModuleManifest Manifest => manifest;
        public ModuleContext Context => ctx;

        public void Register(IToolRegistry registry)
        {
            Add(registry, "read", ToolRisk.Read, untrusted: true);
            Add(registry, "write", ToolRisk.Write);
            Add(registry, "send", ToolRisk.ExternalEffect);
            if (ProbeFactory.ForeignTool) Add(registry, "x", ToolRisk.Read, name: "other.x");
        }

        private void Add(IToolRegistry r, string tool, ToolRisk risk, bool untrusted = false, string? name = null)
        {
            name ??= $"{Id}.{tool}";
            r.Add(new ToolDescriptor(name, tool, risk, Id) { ReadsUntrusted = untrusted },
                AIFunctionFactory.Create((AIFunctionArguments a) => $"{tool} by {a.Context?[HostContract.RequesterContextKey]}", name, tool));
        }
    }

    private static ModulesConfig Modules(string json, string? dir = null) => ModulesConfig.Parse(json, dir);

    private static Task<ModuleLoadResult> Load(string json, ModuleLoadMode mode = ModuleLoadMode.Describe, string? dir = null) =>
        ModuleLoader.LoadAsync(Modules(json, dir), mode, [new ProbeFactory()]);

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "otail-brick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------- W1: manifest and contract ----------------------------------------------------------------------------------

    [Fact]
    public void A_module_assembly_without_a_contract_version_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.ContractOf(typeof(object).Assembly, "m"));
        Assert.Contains("HostContractVersion", ex.Message);
    }

    [Fact]
    public void A_module_built_against_another_contract_is_refused_with_both_versions()
    {
        var ctor = typeof(HostContractVersionAttribute).GetConstructor([typeof(int)])!;
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FutureBrick"), AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(ctor, [HostContract.Version + 1])]);
        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.ContractOf(asm, "future"));
        Assert.Contains($"built against Host contract {HostContract.Version + 1}", ex.Message);
        Assert.Contains($"{HostContract.MinSupportedVersion} to {HostContract.Version}", ex.Message);
    }

    private sealed class Impostor : IHostModule
    {
        public string Id => "imp";
        public void Register(IToolRegistry registry) =>
            registry.Add(new ToolDescriptor("imp.x", "x", ToolRisk.Read, "files"), AIFunctionFactory.Create(() => "x", "imp.x", "x"));
    }

    [Fact]
    public void A_module_cannot_register_tools_under_another_modules_id()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new Impostor()).Build());
        Assert.Contains("under module id 'files'", ex.Message);
    }

    [Fact]
    public async Task Loaded_modules_report_manifest_version_and_contract_and_actions_record_the_version()
    {
        var root = TempDir();
        var host = new OpenTailHostBuilder()
            .AddModule(new FilesModule(new FilesModuleOptions { Root = root }))
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "files.list": { "Mode": "Execute" } } }"""))
            .Build();
        var m = Assert.Single(host.Catalog.Modules);
        Assert.Equal("files", m.Id);
        Assert.Equal(HostContract.Version, m.ContractVersion);
        Assert.Equal("OpenTail.Modules.Files", m.Assembly);
        Assert.False(string.IsNullOrWhiteSpace(m.Version));
        Assert.Contains(m.Manifest.Notes, n => n.Contains("Root", StringComparison.Ordinal));

        var r = await host.Queue.SubmitAsync(new ActionRequest("files.list", "{}", new ActionRequester(RequesterKind.AiSession, "local")));
        Assert.Equal(m.Version, r.ModuleVersion);
    }

    // ---------- W2: credentials and guarded HTTP ---------------------------------------------------------------------------

    [Fact]
    public async Task Guarded_client_reaches_only_allowed_hosts_and_attaches_the_credential_only_to_its_bound_host()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_TOKEN_A", "tok-alpha-plain-value");
        await using var bound = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("ok"));
        await using var other = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("ok"));
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>
        {
            ["svc"] = new() { Env = "OTAIL_TEST_TOKEN_A", Host = bound.Origin },
        }, Path.GetTempPath());
        using var http = new GuardedHttpClientFactory("m", [bound.Origin, other.Origin], new ModuleNetwork(), creds);
        var client = http.Create("svc");

        Assert.Equal("ok", await client.GetStringAsync(new Uri(bound.BaseUri, "a")));
        Assert.Equal("ok", await client.GetStringAsync(new Uri(other.BaseUri, "b")));
        Assert.Equal("Bearer tok-alpha-plain-value", bound.Requests.Single().Authorization);
        Assert.Null(other.Requests.Single().Authorization);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://example.com/"));
        Assert.Contains("may not reach https://example.com", ex.Message);
        Assert.Throws<InvalidOperationException>(() => http.Create("not-mapped"));
    }

    [Fact]
    public async Task A_redirect_to_a_host_that_is_not_allowed_is_refused_and_one_to_another_allowed_host_drops_the_credential()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_TOKEN_B", "tok-bravo-plain-value");
        await using var target = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("landed"));
        await using var origin = await StubServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = ctx.Request.Path == "/out" ? "https://evil.example/steal" : new Uri(target.BaseUri, "in").AbsoluteUri;
            return Task.CompletedTask;
        });
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>
        {
            ["svc"] = new() { Env = "OTAIL_TEST_TOKEN_B", Host = origin.Origin },
        }, Path.GetTempPath());
        using var http = new GuardedHttpClientFactory("m", [origin.Origin, target.Origin], new ModuleNetwork(), creds);
        var client = http.Create("svc");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(new Uri(origin.BaseUri, "out")));
        Assert.Equal("landed", await client.GetStringAsync(new Uri(origin.BaseUri, "go")));
        Assert.Null(target.Requests.Single().Authorization);
        Assert.All(origin.Requests, r => Assert.Equal("Bearer tok-bravo-plain-value", r.Authorization));
    }

    [Fact]
    public async Task When_the_first_address_cannot_be_reached_the_next_one_is_tried()
    {
        await using var s = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("ok"));
        var origin = "http://multi.test:" + s.BaseUri.Port;
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>(), Path.GetTempPath());
        using var http = new GuardedHttpClientFactory("m", [origin], new ModuleNetwork { AllowPrivateNetwork = true }, creds)
        {
            // [::1] first: the stub listens on 127.0.0.1 only, so that one refuses (or IPv6 is off): either way it fails.
            Resolve = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.Loopback }),
        };

        Assert.Equal("ok", await http.Create().GetStringAsync(origin + "/"));
    }

    [Fact]
    public async Task When_no_address_can_be_reached_the_error_says_so()
    {
        await using var s = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("ok"));
        var origin = "http://multi.test:" + s.BaseUri.Port;
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>(), Path.GetTempPath());
        using var http = new GuardedHttpClientFactory("m", [origin], new ModuleNetwork { AllowPrivateNetwork = true }, creds)
        {
            Resolve = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.IPv6Loopback }),
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.Create().GetStringAsync(origin + "/"));
        Assert.Contains("any of its 2 addresses", error.ToString());
    }

    [Fact]
    public async Task Plain_http_is_refused_unless_the_origin_is_listed_with_its_scheme()
    {
        await using var s = await StubServer.StartAsync(ctx => ctx.Response.WriteAsync("ok"));
        var host = s.BaseUri.Host;
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>(), Path.GetTempPath());
        using var http = new GuardedHttpClientFactory("m", [host], new ModuleNetwork(), creds); // bare host means https only
        await Assert.ThrowsAsync<HttpRequestException>(() => http.Create().GetStringAsync(s.BaseUri));
    }

    [Fact]
    public async Task OAuth_tokens_are_fetched_once_for_concurrent_callers_and_again_after_a_401()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_CID", "client-1");
        Environment.SetEnvironmentVariable("OTAIL_TEST_CSECRET", "client-secret-value-xyz");
        var issued = 0;
        await using var idp = await StubServer.StartAsync(async ctx =>
        {
            var n = Interlocked.Increment(ref issued);
            await Task.Delay(50);
            await ctx.Response.WriteAsJsonAsync(new { access_token = "token-" + n, expires_in = 3600 });
        });
        await using var api = await StubServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Headers.Authorization == "Bearer token-1" && ctx.Request.Path == "/expire" ? 401 : 200;
            return ctx.Response.WriteAsync(ctx.Request.Headers.Authorization.ToString());
        });
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource>
        {
            ["svc"] = new() { Host = api.Origin, OAuth2 = new OAuth2ClientCredentials { TokenUrl = new Uri(idp.BaseUri, "token").AbsoluteUri, ClientIdEnv = "OTAIL_TEST_CID", ClientSecretEnv = "OTAIL_TEST_CSECRET" } },
        }, Path.GetTempPath());

        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => creds.GetAsync("svc").AsTask()));
        Assert.All(all, c => Assert.Equal("token-1", c.Secret));
        Assert.Equal(1, issued);
        Assert.Contains("client_secret=client-secret-value-xyz", idp.Requests.Single().Body);

        using var http = new GuardedHttpClientFactory("m", [api.Origin], new ModuleNetwork(), creds);
        var client = http.Create("svc");
        var resp = await client.GetAsync(new Uri(api.BaseUri, "expire"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("Bearer token-2", await client.GetStringAsync(new Uri(api.BaseUri, "ok")));
        Assert.Equal(2, issued);
    }

    [Fact]
    public async Task A_secret_the_host_handed_out_is_scrubbed_exactly_from_errors()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_TOKEN_C", "quietlittlesecret");
        var creds = new ModuleCredentials("m", new Dictionary<string, CredentialSource> { ["svc"] = new() { Env = "OTAIL_TEST_TOKEN_C", Host = "api.example" } }, Path.GetTempPath());
        var c = await creds.GetAsync("svc");
        Assert.Equal("Credential(Bearer, [redacted])", c.ToString());
        Assert.Equal("upstream said: [redacted] is wrong", Redaction.Scrub("upstream said: quietlittlesecret is wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => creds.GetAsync("other").AsTask());
    }

    // ---------- W3: enabling modules from configuration -------------------------------------------------------------------

    [Fact]
    public async Task A_configured_module_does_not_inherit_DefaultReadMode_unless_the_operator_opts_in()
    {
        var root = TempDir();
        var json = $$"""{ "Modules": [ { "Type": "files", "Id": "docs", "Config": { "Root": {{JsonSerializer.Serialize(root)}} } } ] }""";
        var policy = PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }""");

        var off = new OpenTailHostBuilder().UsePolicy(policy).AddModules(await Load(json)).Build();
        Assert.True(off.Catalog.TryGet("docs.read", out var read));
        Assert.Equal(ToolMode.Deny, off.Policy.Resolve(read.Descriptor, false).Mode);
        Assert.DoesNotContain(off.BuildTools(), t => t.ProtocolTool.Name == "docs.read");
        Assert.Contains(off.Explain("docs.read").Notes, n => n.Contains("AllowDefaultRead", StringComparison.Ordinal));

        var on = new OpenTailHostBuilder().UsePolicy(policy).AddModules(await Load(json.Replace("\"Id\": \"docs\"", "\"Id\": \"docs\", \"AllowDefaultRead\": true"))).Build();
        on.Catalog.TryGet("docs.read", out read);
        Assert.Equal(ToolMode.Execute, on.Policy.Resolve(read.Descriptor, false).Mode);
    }

    [Fact]
    public async Task A_module_listed_but_not_present_stops_startup_and_names_the_types_that_are()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load("""{ "Modules": [ { "Type": "no-such-brick", "Id": "sn" } ] }"""));
        Assert.Contains("needs type 'no-such-brick'", ex.Message);
        Assert.Contains("files", ex.Message);
        Assert.Contains("openapi", ex.Message);
    }

    [Fact]
    public async Task Factories_in_referenced_packages_are_found_and_unconfigured_ones_are_reported()
    {
        var loaded = await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""");
        Assert.Contains("files", loaded.Factories.Keys);
        Assert.Contains("openapi", loaded.Factories.Keys);
        Assert.Contains(loaded.Warnings, w => w.Contains("available but not enabled", StringComparison.Ordinal) && w.Contains("'files' (OpenTail.Modules.Files)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tools_of_a_configured_module_must_carry_its_prefix()
    {
        ProbeFactory.ForeignTool = true;
        try
        {
            var loaded = await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""");
            var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModules(loaded).Build());
            Assert.Contains("must be named 'p.<name>'", ex.Message);
        }
        finally { ProbeFactory.ForeignTool = false; }
    }

    [Fact]
    public async Task Network_hosts_can_narrow_the_manifest_but_never_widen_it()
    {
        ProbeFactory.Hosts = ["*.svc.example"];
        try
        {
            await Load("""{ "Modules": [ { "Type": "probe", "Id": "p", "Network": { "Hosts": [ "eu.svc.example" ] } } ] }""");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Load("""{ "Modules": [ { "Type": "probe", "Id": "p", "Network": { "Hosts": [ "eu.svc.example", "attacker.example" ] } } ] }"""));
            Assert.Contains("'attacker.example' is outside", ex.Message);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }"""));
            Assert.Contains("list the ones you allow", ex.Message);
        }
        finally { ProbeFactory.Hosts = []; }
    }

    [Fact]
    public void The_modules_file_refuses_credentials_bound_to_hosts_the_module_may_not_reach()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Modules("""
            { "Modules": [ { "Type": "probe", "Id": "p", "Network": { "Hosts": [ "api.svc.example" ] },
                             "Credentials": { "svc": { "Env": "X", "Host": "other.example" } } } ] }
            """));
        Assert.Contains("not one of the module's Network.Hosts", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => Modules("""{ "Modules": [ { "Type": "probe", "Id": "P.x" } ] }"""));
        Assert.Contains("lower case, no dots", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => Modules("""{ "Modules": [ { "Type": "probe", "Id": "p", "Credentials": { "svc": { "Host": "a.example" } } } ] }"""));
        Assert.Contains("exactly one of Env, File or OAuth2", ex.Message);
    }

    [Fact]
    public async Task Missing_credentials_stop_a_run_but_not_describe_commands()
    {
        var json = """
            { "Modules": [ { "Type": "probe", "Id": "p", "Network": { "Hosts": [ "api.svc.example" ] },
                             "Credentials": { "svc": { "Env": "OTAIL_TEST_UNSET_VARIABLE", "Host": "api.svc.example" } } } ] }
            """;
        Environment.SetEnvironmentVariable("OTAIL_TEST_UNSET_VARIABLE", null);
        var described = await Load(json);
        Assert.Single(described.Modules);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(json, ModuleLoadMode.Run));
        Assert.Contains("OTAIL_TEST_UNSET_VARIABLE is not set", ex.Message);
    }

    [Fact]
    public async Task Tools_receive_the_requester_from_the_stored_action()
    {
        var host = new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "p.write": { "Mode": "Queue" } } }"""))
            .AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
        var r = await host.Queue.SubmitAsync(new ActionRequest("p.write", "{}", new ActionRequester(RequesterKind.AiSession, "key:alice")));
        Assert.Equal(ActionStatus.Pending, r.Status);
        var done = await host.Queue.ReviewAsync(r.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "admin:rita" });
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Contains("write by key:alice", done.ResultJson);
    }

    [Fact]
    public async Task Switching_a_module_off_tolerates_its_policy_expires_its_pending_actions_and_clears_promotions()
    {
        using var db = new TempDb();
        var on = """{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""";
        var policy = """{ "Tools": { "p.write": { "Mode": "Queue", "TrustRamp": { "AfterCleanApprovals": 1 } } } }""";
        var first = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path).AddModules(await Load(on)).Build();
        var pending = await first.Queue.SubmitAsync(new ActionRequest("p.write", "{}", new ActionRequester(RequesterKind.AiSession, "key:alice")));
        first.Policy.Promote("p.write", "admin:rita", DateTimeOffset.UtcNow);
        Assert.True(first.Policy.IsPromoted("p.write"));

        var off = await Load("""{ "Modules": [ { "Type": "gone-from-disk", "Id": "p", "Enabled": false } ] }""");
        Assert.Equal(["p"], off.Retired);
        var second = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path).AddModules(off).Build();
        Assert.Contains(second.StartupWarnings, w => w.Contains("'p.write'", StringComparison.Ordinal));
        await second.RunMaintenanceOnceAsync();

        var after = await second.Store.GetAsync(pending.Id);
        Assert.Equal(ActionStatus.Expired, after!.Status);
        Assert.Equal("module_removed", after.Error!.Code);
        Assert.False(second.Policy.IsPromoted("p.write"));
    }

    [Fact]
    public async Task OpenApi_from_configuration_exposes_only_named_operations_and_reaches_the_api_through_the_guard()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_CRM", "crm-token-value-1");
        await using var api = await StubServer.StartAsync(ctx => ctx.Response.WriteAsJsonAsync(new { id = ctx.Request.Path.ToString() }));
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "crm.json"), $$"""
            { "openapi": "3.0.1", "info": { "title": "crm", "version": "1" }, "servers": [ { "url": "{{api.Origin}}/v1" } ],
              "paths": {
                "/customers/{id}": { "get": { "operationId": "getCustomer", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ] },
                                     "delete": { "operationId": "deleteCustomer", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ] } } } }
            """);
        var json = $$"""
            { "Modules": [ { "Type": "openapi", "Id": "crm",
                "Network": { "Hosts": [ "{{api.Origin}}" ] },
                "Credentials": { "api": { "Env": "OTAIL_TEST_CRM", "Host": "{{api.Origin}}" } },
                "Config": { "Spec": "crm.json", "Operations": [ "getCustomer" ], "AllowInsecureHttp": true } } ] }
            """;
        var host = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse("""{ "Tools": { "crm.getCustomer": { "Mode": "Execute" } } }"""))
            .AddModules(await Load(json, ModuleLoadMode.Run, dir)).Build();
        Assert.Equal(["crm.getCustomer"], host.Catalog.All.Select(t => t.Descriptor.Name));

        var r = await host.Queue.SubmitAsync(new ActionRequest("crm.getCustomer", """{"id":"42"}""", new ActionRequester(RequesterKind.AiSession, "local")));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("/v1/customers/42", r.ResultJson);
        Assert.Equal("Bearer crm-token-value-1", api.Requests.Single().Authorization);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(json.Replace("\"getCustomer\" ]", "\"getCustomr\" ]"), dir: dir));
        Assert.Contains("getCustomr", ex.Message);
    }

    // ---------- W4: fragments, adoption, lint, policy tests ----------------------------------------------------------------

    [Fact]
    public void Policy_fragments_are_included_and_may_only_hold_their_own_modules_tools()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "policy.d"));
        File.WriteAllText(Path.Combine(dir, "policy.json"), """{ "Include": [ "policy.d" ], "Tools": { "a.x": { "Mode": "Queue" } } }""");
        File.WriteAllText(Path.Combine(dir, "policy.d", "p.json"), """{ "Tools": { "p.write": { "Mode": "Queue" } } }""");
        var cfg = PolicyConfig.Load(Path.Combine(dir, "policy.json"));
        Assert.Equal(ToolMode.Queue, cfg.Tools["p.write"].Mode);
        Assert.Single(cfg.IncludedFiles);

        File.WriteAllText(Path.Combine(dir, "policy.d", "p.json"), """{ "TaintWindow": "0s", "Tools": {} }""");
        Assert.Contains("may only contain \"Tools\"", Assert.Throws<InvalidOperationException>(() => PolicyConfig.Load(Path.Combine(dir, "policy.json"))).Message);

        File.WriteAllText(Path.Combine(dir, "policy.d", "p.json"), """{ "Tools": { "a.x": { "Mode": "Execute" } } }""");
        Assert.Contains("may only name 'p.*'", Assert.Throws<InvalidOperationException>(() => PolicyConfig.Load(Path.Combine(dir, "policy.json"))).Message);

        File.WriteAllText(Path.Combine(dir, "policy.d", "a.json"), """{ "Tools": { "a.x": { "Mode": "Execute" } } }""");
        File.Delete(Path.Combine(dir, "policy.d", "p.json"));
        Assert.Contains("already has a policy entry", Assert.Throws<InvalidOperationException>(() => PolicyConfig.Load(Path.Combine(dir, "policy.json"))).Message);

        Assert.Throws<InvalidOperationException>(() => PolicyConfig.Parse("""{ "Include": [ "policy.d" ] }"""));
    }

    [Fact]
    public async Task A_suggested_policy_can_only_narrow_its_own_tools()
    {
        ProbeFactory.Suggested = """
            { "TaintWindow": "0s", "Tools": { "p.read": { "Mode": "Execute" } } }
            """;
        try
        {
            var host = new OpenTailHostBuilder().AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
            var r = PolicyAdoption.Check(host.Catalog, "p", allowExecute: false);
            Assert.Contains(r.Errors, e => e.Contains("may only contain \"Tools\"", StringComparison.Ordinal));

            ProbeFactory.Suggested = """{ "Tools": { "q.send": { "Mode": "Execute" } } }""";
            host = new OpenTailHostBuilder().AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
            Assert.Contains(PolicyAdoption.Check(host.Catalog, "p", true).Errors, e => e.Contains("may only name 'p.*'", StringComparison.Ordinal));

            ProbeFactory.Suggested = """{ "Tools": { "p.ghost": { "Mode": "Queue" }, "p.send": { "Mode": "Execute" }, "p.read": { "Mode": "Execute" } } }""";
            host = new OpenTailHostBuilder().AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
            r = PolicyAdoption.Check(host.Catalog, "p", allowExecute: false);
            Assert.Contains(r.Errors, e => e.Contains("'p.ghost' is not a tool", StringComparison.Ordinal));
            Assert.Contains(r.Errors, e => e.Contains("'p.send' (ExternalEffect): Mode Execute", StringComparison.Ordinal));
            Assert.DoesNotContain(r.Errors, e => e.Contains("'p.read'", StringComparison.Ordinal));

            ProbeFactory.Suggested = """{ "Tools": { "p.send": { "Mode": "Execute" }, "p.read": { "Mode": "Execute" } } }""";
            host = new OpenTailHostBuilder().AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
            r = PolicyAdoption.Check(host.Catalog, "p", allowExecute: true);
            Assert.Empty(r.Errors);
            Assert.Contains(r.Notes, n => n.Contains("--allow-execute", StringComparison.Ordinal));
        }
        finally { ProbeFactory.Suggested = null; }
    }

    [Fact]
    public async Task Adopt_policy_writes_a_fragment_shows_the_diff_and_never_rewrites_the_operators_file()
    {
        var dir = TempDir();
        var root = Path.Combine(dir, "share");
        Directory.CreateDirectory(root);
        var policy = Path.Combine(dir, "policy.json");
        var modules = Path.Combine(dir, "modules.json");
        const string original = "{ // mine\n  \"Tools\": {} }";
        await File.WriteAllTextAsync(policy, original);
        await File.WriteAllTextAsync(modules, """{ "Modules": [ { "Type": "files", "Id": "docs", "Config": { "Root": "share" } } ] }""");

        var output = new StringWriter();
        var exit = await PolicyCli.TryRunAsync(["--policy", policy, "--modules", modules, "--adopt-policy", "docs"], "t", _ => { }, output);
        Assert.Equal(0, exit);
        Assert.Contains("+ docs.write", output.ToString());
        Assert.Contains("add  \"Include\": [\"policy.d\"]", output.ToString());
        Assert.Equal(original, await File.ReadAllTextAsync(policy));
        Assert.Equal(ToolMode.Queue, PolicyConfig.ReadFragment(Path.Combine(dir, "policy.d", "docs.json"))["docs.write"].Mode);

        await File.WriteAllTextAsync(policy, """{ "Include": [ "policy.d" ], "Tools": {} }""");
        output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--modules", modules, "--adopt-policy", "docs"], "t", _ => { }, output));
        Assert.Contains("already holds this suggestion", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--modules", modules, "--list-modules"], "t", _ => { }, output));
        Assert.Contains("docs.write", output.ToString());
        Assert.Contains("Queue", output.ToString());
    }

    [Fact]
    public async Task Lint_warns_when_a_write_tool_can_run_unreviewed_next_to_tools_that_read_outside_content()
    {
        var host = new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "p.write": { "Mode": "Execute" }, "p.read": { "Mode": "Execute" } } }"""))
            .AddModules(await Load("""{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""")).Build();
        var warnings = PolicyLint.Run(host);
        Assert.Contains(warnings, w => w.Contains("'p.write' is Write risk and can execute without review", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("p.send", StringComparison.Ordinal) && w.Contains("denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Policy_test_files_check_expected_decisions_and_fail_on_a_mismatch()
    {
        var dir = TempDir();
        var policy = Path.Combine(dir, "policy.json");
        var modules = Path.Combine(dir, "modules.json");
        var cases = Path.Combine(dir, "cases.json");
        await File.WriteAllTextAsync(policy, """{ "Tools": { "p.send": { "Mode": "Queue", "Rules": [ { "Name": "internal", "Mode": "Execute", "When": { "arg": "/to", "endsWith": "@corp.example" } } ] } } }""");
        await File.WriteAllTextAsync(modules, """{ "Modules": [ { "Type": "probe", "Id": "p" } ] }""");
        await File.WriteAllTextAsync(cases, """
            [ { "Name": "internal runs", "Tool": "p.send", "Args": { "to": "a@corp.example" }, "Expect": "Execute" },
              { "Name": "tainted waits", "Tool": "p.send", "Args": { "to": "a@corp.example" }, "Tainted": true, "Expect": "Queue" },
              { "Name": "wrong on purpose", "Tool": "p.send", "Args": { "to": "x@other.example" }, "Expect": "Execute" } ]
            """);

        // The probe factory lives in this test assembly, which is not an OpenTail.Modules.* package: name it as a factory assembly.
        await File.WriteAllTextAsync(modules, """{ "FactoryAssemblies": [ "OpenTail.Host.Tests" ], "Modules": [ { "Type": "probe", "Id": "p" } ] }""");
        var output = new StringWriter();
        var exit = await PolicyCli.TryRunAsync(["--policy", policy, "--modules", modules, "--test-policy", cases], "t", _ => { }, output);
        Assert.Equal(1, exit);
        Assert.Contains("pass  internal runs", output.ToString());
        Assert.Contains("pass  tainted waits", output.ToString());
        Assert.Contains("FAIL  wrong on purpose: expected Execute, got Queue", output.ToString());
        Assert.Contains("2 passed, 1 failed.", output.ToString());
    }
}
