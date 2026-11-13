using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>Agent profiles: the tools each agent identity may call, bound to its credential (roles and permissions design, step 1).</summary>
public class AgentProfileTests
{
    private const string Policy = """
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:00:00",
          "Tools": { "mail.send": { "Mode": "Queue" }, "ledger.append": { "Mode": "Execute" } },
          "Agents": {
            "reader":   { "Tools": [ "inbox.read", "clock.now" ] },
            "mailer":   { "Tools": [ "mail.*", "inbox.read" ] },
            "everything": { "Tools": [ "*" ] }
          }
        }
        """;

    private static OpenTailHost Host(string policy = Policy) =>
        new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy)).Build();

    private static ToolDescriptor Tool(OpenTailHost host, string name) => host.Catalog.TryGet(name, out var r) ? r.Descriptor : throw new KeyNotFoundException(name);

    private static ActionRequest Call(string tool, string args, string? profile, string subject = "key:bot") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, subject)) { Profile = profile };

    [Fact]
    public void A_profile_admits_exact_names_module_patterns_and_star_and_no_profile_admits_everything()
    {
        var host = Host();
        Assert.True(host.Policy.Admits(Tool(host, "inbox.read"), "reader"));
        Assert.False(host.Policy.Admits(Tool(host, "mail.send"), "reader"));
        Assert.True(host.Policy.Admits(Tool(host, "mail.send"), "mailer"));
        Assert.False(host.Policy.Admits(Tool(host, "ledger.append"), "mailer"));
        Assert.True(host.Policy.Admits(Tool(host, "ledger.append"), "everything"));
        Assert.True(host.Policy.Admits(Tool(host, "files.delete"), null));
    }

    [Fact]
    public void A_profile_name_the_policy_does_not_define_admits_nothing()
    {
        var host = Host();
        Assert.False(host.Policy.Admits(Tool(host, "clock.now"), "typo"));
    }

    [Fact]
    public void A_profile_never_loosens_policy()
    {
        // files.delete has no policy entry and is not Read: denied for every caller, whatever the profile says.
        var host = Host();
        Assert.False(host.Policy.IsAdvertised(Tool(host, "files.delete"), "everything"));
        Assert.True(host.Policy.IsAdvertised(Tool(host, "clock.now"), "reader"));
        Assert.False(host.Policy.IsAdvertised(Tool(host, "mail.send"), "reader"));
    }

    [Theory]
    [InlineData("""{ "Agents": { "a": { "Tools": [ "no.such" ] } } }""", "unknown tool 'no.such'")]
    [InlineData("""{ "Agents": { "a": { "Tools": [ "nope.*" ] } } }""", "starts with 'nope.'")]
    [InlineData("""{ "Agents": { "a": { "Tools": [] } } }""", "names no tools")]
    [InlineData("""{ "Agents": { "a b": { "Tools": [ "*" ] } } }""", "1-64 letters")]
    public void Profiles_that_name_nothing_real_stop_startup(string policy, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Host(policy));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_profile_entry_for_a_switched_off_module_is_a_warning_not_an_error()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).RetireModule("gone")
            .UsePolicy(PolicyConfig.Parse("""{ "Agents": { "a": { "Tools": [ "gone.read", "clock.now" ] } } }""")).Build();
        Assert.Contains(host.StartupWarnings, w => w.Contains("'gone.read'") && w.Contains("switched off"));
    }

    [Fact]
    public async Task A_call_outside_the_profile_is_refused_and_audited_and_nothing_runs()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).Build();

        var r = await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"x"}""", "reader"));

        Assert.Equal(ActionStatus.Denied, r.Status);
        Assert.Equal("not_in_profile", r.Error!.Code);
        Assert.Empty(module.Ledger);
        var audit = ((InMemoryAuditSink)host.Audit).Read(r.Id);
        Assert.Contains(audit, e => e.Event == "denied" && e.Detail!.Contains("not_in_profile") && e.Detail.Contains("reader"));
    }

    [Fact]
    public async Task A_call_inside_the_profile_follows_policy_as_before()
    {
        var host = Host();
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}""", "mailer"))).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("clock.now", "{}", "reader"))).Status);
    }

    [Fact]
    public async Task A_caller_whose_profile_is_missing_from_the_policy_is_refused_everything()
    {
        var host = Host();
        var r = await host.Queue.SubmitAsync(Call("clock.now", "{}", "deleted-profile"));
        Assert.Equal("not_in_profile", r.Error!.Code);
    }

    [Fact]
    public async Task Resubmitting_cannot_reach_a_tool_outside_the_profile()
    {
        var host = Host();
        // Submitted with no profile (say, before profiles were configured), then sent back by a reviewer.
        var first = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}""", null));
        await host.Queue.ReviewAsync(first.Id, new ActionReview("rita", ReviewDecision.RequestChanges, "fix", DateTimeOffset.UtcNow));

        var again = await host.Queue.ResubmitAsync(first.Id, null, first.Requester, profile: "reader");
        Assert.Equal("not_in_profile", again.Error!.Code);
    }

    // ---- over MCP, through the caller resolver ----

    private static OpenTailHost HostFor(CallerIdentity caller) =>
        new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseCallerResolver(_ => caller).Build();

    [Fact]
    public async Task Tools_list_shows_only_the_profile_and_the_built_ins()
    {
        var host = HostFor(new CallerIdentity("key:bot", "bot", CallerKind.Agent) { Profile = "reader" });
        await using var pair = await McpPair.StartAsync(host);
        var client = pair.Client;

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("inbox.read", names);
        Assert.Contains("clock.now", names);
        Assert.DoesNotContain("mail.send", names);
        Assert.DoesNotContain("ledger.append", names);
        Assert.Contains("get_action_status", names);
    }

    [Fact]
    public async Task A_caller_without_a_profile_sees_every_tool_policy_allows()
    {
        var host = HostFor(new CallerIdentity("key:old", "old", CallerKind.Agent));
        await using var pair = await McpPair.StartAsync(host);
        var client = pair.Client;

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("mail.send", names);
        Assert.Contains("ledger.append", names);
    }

    [Fact]
    public async Task Calling_a_hidden_tool_by_name_over_MCP_is_refused()
    {
        var host = HostFor(new CallerIdentity("key:bot", "bot", CallerKind.Agent) { Profile = "reader" });
        await using var pair = await McpPair.StartAsync(host);
        var client = pair.Client;

        var json = await pair.CallAsync("mail.send", ("to", "a@b.c"), ("body", "b"));
        Assert.Equal("denied", json["status"]!.ToString());
        Assert.Contains("not_in_profile", json.ToJsonString());
    }

    // ---- API keys ----

    [Fact]
    public void An_api_key_naming_an_undefined_profile_is_an_error_and_a_key_without_one_a_warning()
    {
        var options = new HostHttpOptions
        {
            ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k1"), Profile: "reader"), new ApiKeyEntry("b", ApiKeys.Hash("k2"), Profile: "typo"), new ApiKeyEntry("c", ApiKeys.Hash("k3"))],
        };
        var (errors, warnings) = options.CheckProfiles(PolicyConfig.Parse(Policy));
        Assert.Equal(["API key 'b' names agent profile 'typo', which the policy's \"Agents\" does not define."], errors);
        Assert.Single(warnings);
        Assert.Contains("API key 'c' has no agent profile", warnings[0]);
    }

    [Fact]
    public void An_api_key_profile_name_is_validated()
    {
        var errors = new HostHttpOptions { ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"), Profile: "two words")] }.Validate();
        Assert.Contains(errors, e => e.Contains("Profile 'two words'"));
    }

    [Fact]
    public async Task The_server_refuses_to_start_when_a_key_names_an_undefined_profile()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseHttpSignIn().Build();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            HostHttp.StartAsync(host, new HostHttpOptions { ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"), Profile: "typo")] }));
        Assert.Contains("'typo'", ex.Message);
    }

    [Fact]
    public async Task An_api_key_with_a_profile_sees_and_calls_only_its_tools_over_http()
    {
        var key = ApiKeys.Generate().Key;
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseHttpSignIn().Build();
        await using var server = await HostHttp.StartAsync(host, new HostHttpOptions { ApiKeys = [new ApiKeyEntry("bot", ApiKeys.Hash(key), Profile: "reader")] });
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.BaseUri, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }));

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("clock.now", names);
        Assert.DoesNotContain("mail.send", names);

        var r = await client.CallToolAsync("mail.send", new Dictionary<string, object?> { ["to"] = "a@b.c", ["body"] = "b" });
        Assert.Contains("not_in_profile", r.Content.OfType<TextContentBlock>().First().Text);
        var stored = await host.Store.QueryAsync(new ActionQuery { Limit = 10 });
        Assert.Contains(stored, a => a.ToolName == "mail.send" && a.Requester.Id == "key:bot" && a.Status == ActionStatus.Denied);
    }

    // ---- --explain ----

    [Fact]
    public async Task Explain_with_a_profile_says_when_the_profile_refuses_the_tool()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var policy = Path.Combine(dir, "policy.json");
        await File.WriteAllTextAsync(policy, Policy);
        var add = (OpenTailHostBuilder b) => { b.AddModule(new FakeInboxModule()); };

        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--explain", "mail.send", "{}", "--profile", "reader"], "t", add, output));
        Assert.Contains("mail.send -> Deny  (not in agent profile 'reader')", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--explain", "mail.send", "{}", "--profile", "mailer"], "t", add, output));
        Assert.Contains("mail.send -> Queue", output.ToString());

        Assert.Equal(2, await PolicyCli.TryRunAsync(["--policy", policy, "--explain", "mail.send", "{}", "--profile", "typo"], "t", add, new StringWriter()));
    }

    // ---- administration tools (step 2) ----

    // ledger.append marked as administration by the operator; its policy meets the administration rules.
    private const string AdminPolicy = """
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:00:00",
          "Tools": {
            "mail.send": { "Mode": "Queue" },
            "ledger.append": { "Mode": "Queue", "Administers": true, "Reviewer": "Human", "ReviewerGroup": "admins",
                               "SelfApproval": "Forbid", "AllowEdits": false }
          },
          "Agents": {
            "everything": { "Tools": [ "*" ] },
            "ledger-all": { "Tools": [ "ledger.*" ] },
            "admin":      { "Tools": [ "ledger.append", "clock.now" ] }
          }
        }
        """;

    [Fact]
    public void No_wildcard_grants_an_administration_tool_only_its_exact_name()
    {
        var host = Host(AdminPolicy);
        var ledger = Tool(host, "ledger.append");
        Assert.True(host.Policy.IsAdministration(ledger));
        Assert.False(host.Policy.Admits(ledger, "everything"));
        Assert.False(host.Policy.Admits(ledger, "ledger-all"));
        Assert.True(host.Policy.Admits(ledger, "admin"));
    }

    [Fact]
    public void Without_a_profile_only_the_local_operator_reaches_an_administration_tool()
    {
        var host = Host(AdminPolicy);
        Assert.False(host.Policy.Admits(Tool(host, "ledger.append"), null));
        Assert.True(host.Policy.Admits(Tool(host, "ledger.append"), null, localOperator: true));
        Assert.True(host.Policy.Admits(Tool(host, "mail.send"), null));
    }

    [Fact]
    public async Task A_signed_in_caller_without_a_profile_neither_sees_nor_calls_an_administration_tool()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(AdminPolicy))
            .UseCallerResolver(_ => new CallerIdentity("key:old", "old", CallerKind.Agent)).Build();
        await using var pair = await McpPair.StartAsync(host);
        var names = (await pair.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("mail.send", names);
        Assert.DoesNotContain("ledger.append", names);
        Assert.Contains("not_in_profile", (await pair.CallAsync("ledger.append", ("text", "x"))).ToJsonString());
    }

    [Fact]
    public async Task The_local_operator_still_sees_an_administration_tool()
    {
        await using var pair = await McpPair.StartAsync(Host(AdminPolicy));
        Assert.Contains("ledger.append", (await pair.Client.ListToolsAsync()).Select(t => t.Name));
    }

    [Fact]
    public void A_profile_with_an_administration_tool_and_a_business_tool_with_effects_stops_a_live_host()
    {
        var policy = AdminPolicy.Replace("""[ "ledger.append", "clock.now" ]""", """[ "ledger.append", "mail.send" ]""");
        var ex = Assert.Throws<InvalidOperationException>(() => Host(policy));
        Assert.Contains("Agent profile 'admin' holds administration tool(s) (ledger.append) and business tools with effects (mail.send)", ex.Message);

        // A sandbox may combine them (the workbench's world).
        new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy))
            .UseSandbox(new RecordingSandbox(RecordingSandbox.ReadLive)).Build();
    }

    [Fact]
    public void Profiles_holding_administration_tools_are_listed_at_startup()
    {
        Assert.Contains(Host(AdminPolicy).StartupWarnings, w => w == "Agent profile 'admin' holds administration tool(s): ledger.append.");
    }

    [Theory]
    [InlineData("""{ "Mode": "Queue", "Administers": true""", """{ "Mode": "Execute", "Administers": true""")]
    [InlineData("\"Administers\": true, \"Reviewer\": \"Human\"", "\"Administers\": true, \"Reviewer\": \"Either\"")]
    public void An_administration_tool_cannot_be_executed_or_left_to_an_AI(string from, string to)
    {
        var policy = AdminPolicy.Replace(from, to);
        Assert.NotEqual(AdminPolicy, policy);
        var ex = Assert.Throws<InvalidOperationException>(() => Host(policy));
        Assert.Contains("Administration tool 'ledger.append'", ex.Message);
    }

    [Fact]
    public void An_administration_tool_is_never_promoted_to_execute()
    {
        var trust = new InMemoryTrustStore();
        trust.Promote("ledger.append", "rita", DateTimeOffset.UtcNow);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(AdminPolicy)).UseTrustStore(trust).Build();
        Assert.Equal(ToolMode.Queue, host.Policy.Resolve(Tool(host, "ledger.append"), tainted: false).Mode);
    }

    // ---- the shipped samples ----

    private static string Repo(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenTail.Orca.slnx"))) root = root.Parent;
        return Path.Combine([root!.FullName, .. parts]);
    }

    private const string CrmSpec = """
        { "openapi": "3.0.3", "info": { "title": "CRM", "version": "1" }, "servers": [ { "url": "https://crm.example.test" } ],
          "paths": { "/customers": { "post": { "operationId": "createCustomer", "responses": { "200": { "description": "ok" } } } },
                     "/customers/{id}": { "put": { "operationId": "updateCustomer",
                        "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
                        "responses": { "200": { "description": "ok" } } } } } }
        """;

    [Fact]
    public void Every_sample_policy_loads_with_its_agent_profiles()
    {
        var email = new OpenTailHostBuilder().AddModule(new OpenTail.Modules.Email.EmailModule(new StubInbox(), new StubSender()))
            .UsePolicy(PolicyConfig.Load(Repo("OpenTail.Orca.Sample.EmailOnly", "policy.email.json"))).Build();
        Assert.True(email.Policy.Admits(Tool(email, "email.send"), "email-assistant"));

        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = new OpenTail.Modules.Files.FilesModule(new OpenTail.Modules.Files.FilesModuleOptions { Root = dir });
        var crm = OpenTail.Modules.OpenApi.OpenApiModule.Create(CrmSpec, new OpenTail.Modules.OpenApi.OpenApiModuleOptions { Namespace = "crm", HttpClient = new HttpClient() });
        var filesApi = new OpenTailHostBuilder().AddModule(files).AddModule(crm)
            .UsePolicy(PolicyConfig.Load(Repo("OpenTail.Orca.Sample.FilesOpenApi", "policy.files-api.json"))).Build();
        Assert.False(filesApi.Policy.Admits(Tool(filesApi, "files.write"), "files-reader"));
        Assert.True(filesApi.Policy.Admits(Tool(filesApi, "crm.updateCustomer"), "files-and-crm"));

        var team = new OpenTailHostBuilder().AddModule(new OpenTail.Modules.Files.FilesModule(new OpenTail.Modules.Files.FilesModuleOptions { Root = dir }))
            .UsePolicy(PolicyConfig.Load(Repo("OpenTail.Orca.Sample.TeamHttp", "policy.team.json"))).Build();
        Assert.True(team.Policy.Admits(Tool(team, "files.write"), "team-agent"));

        // SQL Server: the read-only policy without a write login, the writes policy with one. No connection is opened to build.
        static OpenTail.Modules.Sql.SqlModule Sql(bool writes) => new(new OpenTail.Modules.Sql.SqlModuleOptions
        {
            Dialect = OpenTail.Modules.Sql.SqlDialect.SqlServer,
            ConnectionFactory = () => new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"),
            WriteConnectionFactory = writes ? () => new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:") : null,
            AllowWrites = writes,
        });
        var readOnly = new OpenTailHostBuilder().AddModule(Sql(false))
            .UsePolicy(PolicyConfig.Load(Repo("OpenTail.Orca.Sample.SqlServer", "policy.sqlserver.json"))).Build();
        Assert.True(readOnly.Policy.Admits(Tool(readOnly, "sql.query"), "sql-reader"));
        var writes = new OpenTailHostBuilder().AddModule(Sql(true))
            .UsePolicy(PolicyConfig.Load(Repo("OpenTail.Orca.Sample.SqlServer", "policy.sqlserver-writes.json"))).Build();
        Assert.False(writes.Policy.Admits(Tool(writes, "sql.delete"), "sql-reader"));
        Assert.Equal(2, writes.Policy.Config.Tools["sql.delete"].RequiredApprovals);

        // The server's starting policy has no modules and only commented-out profiles; it must still load.
        Assert.Empty(PolicyConfig.Load(Repo("OpenTail.Orca.Server", "config", "policy.json")).Agents);
    }
}
