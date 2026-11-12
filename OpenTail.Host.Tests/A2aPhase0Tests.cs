using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenTail.Host.Admin;
using OpenTail.Host.Http;

namespace OpenTail.Host.Tests;

/// <summary>A2A phase 0 (a2a.md, build order): the <c>a2a:call</c> scope, the A2A config and its startup checks, partner ids, and a
/// conditional rule on a top-level <c>partner</c> argument. No A2A endpoint is served in phase 0, so nothing here calls one.</summary>
public class A2aPhase0Tests
{
    private const string ReviewerToken = "reviewer-token-0123456789";
    private const string PartnerId = "5f1c2a9e-8b7d-4c3e-9a10-2b4d6e8f0a12";
    private const string OtherPartnerId = "0b9e4d2a-1c3f-4a5b-8d7e-6f5a4b3c2d1e";
    private const string InitializeBody = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private const string EnforcedPolicy = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce",
          "Tools": { "mail.send": { "Mode": "Queue" } },
          "Agents": { "partner-profile": { "Tools": [ "mail.send" ] }, "reply-assistant": { "Tools": [ "mail.send" ] } } }
        """;

    private const string ReportOnlyPolicy = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "ReportOnly",
          "Tools": { "mail.send": { "Mode": "Queue" } },
          "Agents": { "partner-profile": { "Tools": [ "mail.send" ] } } }
        """;

    private static readonly IReadOnlyList<string> A2aOnly = ["a2a:call"];

    private static string TokenOf(string keyName) => keyName + "-key-0123456789";

    private static ApiKeyEntry Key(string name, IReadOnlyList<string>? scopes = null, string? profile = "partner-profile", string? partner = PartnerId) =>
        new(name, ApiKeys.Hash(TokenOf(name)), scopes, profile, partner);

    private static A2aOptions Enabled() => new() { Enabled = true, Partners = [new A2aPartner(PartnerId, "Acme")] };

    private static PolicyConfig Enforced => PolicyConfig.Parse(EnforcedPolicy);

    private static OpenTailHost HostWith(IActionStore? store = null)
    {
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseHttpSignIn().UsePolicy(Enforced);
        if (store is not null) b.UseStore(store);
        return b.Build();
    }

    private static async Task<HttpResponseMessage> Call(HttpClient http, HttpMethod method, string path, string token, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        }
        return await http.SendAsync(req);
    }

    // ---- A.1: the a2a:call scope ----

    [Fact]
    public void An_a2a_call_key_passes_the_existing_key_validation()
    {
        Assert.Empty(new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)] }.Validate());
    }

    [Fact]
    public async Task An_a2a_call_key_authenticates_but_cannot_call_tools_or_review()
    {
        // A reviewer token makes the review API exist (it is mapped only when admin tokens or JWT sign-in are configured).
        var options = new HostHttpOptions
        {
            ApiKeys = [Key("partner", A2aOnly), Key("caller", ["mcp:tools"], "reply-assistant", partner: null)],
            Admin = new AdminOptions
            {
                Tokens = new Dictionary<string, string> { [ReviewerToken] = "rita" },
                TokenRoles = new Dictionary<string, IReadOnlySet<string>> { [ReviewerToken] = new HashSet<string> { Roles.Review } },
            },
        };
        await using var server = await HostHttp.StartAsync(HostWith(), options);
        using var http = new HttpClient { BaseAddress = server.BaseUri };

        // 403, not 401: the key signs in, but it holds neither the tools role nor the review role.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(http, HttpMethod.Post, "/mcp", TokenOf("partner"), InitializeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(http, HttpMethod.Get, "/api/actions", TokenOf("partner"))).StatusCode);

        // Controls: the review route answers its reviewer, and the MCP route answers a key that carries mcp:tools.
        Assert.Equal(HttpStatusCode.OK, (await Call(http, HttpMethod.Get, "/api/actions", ReviewerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(http, HttpMethod.Post, "/mcp", TokenOf("caller"), InitializeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(http, HttpMethod.Get, "/api/actions", TokenOf("caller"))).StatusCode);
    }

    // ---- A.2 to A.3: the A2A section and its startup checks ----

    [Fact]
    public void Absent_or_disabled_a2a_runs_no_checks()
    {
        var badKey = new[] { Key("x", ["a2a:call", "mcp:tools"], profile: null, partner: null) };
        Assert.Empty(new HostHttpOptions { ApiKeys = badKey }.CheckA2A(PolicyConfig.Parse(ReportOnlyPolicy), null));
        Assert.Empty(new HostHttpOptions { ApiKeys = badKey, A2A = new A2aOptions { Enabled = false } }.CheckA2A(PolicyConfig.Parse(ReportOnlyPolicy), null));
    }

    [Fact]
    public void A_complete_enabled_configuration_passes_with_full_durability()
    {
        Assert.Empty(new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)], A2A = Enabled() }.CheckA2A(Enforced, SqliteSynchronous.Full));
    }

    [Fact]
    public void Enabled_a2a_requires_dataflow_enforce()
    {
        var errors = new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)], A2A = Enabled() }.CheckA2A(PolicyConfig.Parse(ReportOnlyPolicy), SqliteSynchronous.Full);
        var error = Assert.Single(errors);
        Assert.Contains("DataFlowMode", error);
        Assert.Contains("Enforce", error);
    }

    [Fact]
    public void Enabled_a2a_requires_full_durability_as_the_store_resolved_it()
    {
        var options = new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)], A2A = Enabled() };
        var normal = Assert.Single(options.CheckA2A(Enforced, SqliteSynchronous.Normal));
        Assert.Contains("OPENTAIL_DB_SYNCHRONOUS=full", normal);
        Assert.Contains("normal", normal);

        var store = Assert.Single(options.CheckA2A(Enforced, null));
        Assert.Contains("not SQLite", store);
    }

    [Fact]
    public void A_partner_key_carries_a2a_call_alone()
    {
        var error = Assert.Single(new HostHttpOptions { ApiKeys = [Key("acme-1", ["a2a:call", "mcp:tools"])], A2A = Enabled() }
            .CheckA2A(Enforced, SqliteSynchronous.Full));
        Assert.Contains("alone", error);
    }

    [Fact]
    public void A_partner_key_must_name_a_profile()
    {
        var error = Assert.Single(new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly, profile: null)], A2A = Enabled() }
            .CheckA2A(Enforced, SqliteSynchronous.Full));
        Assert.Contains("Profile", error);
    }

    [Fact]
    public void A_partner_key_must_name_a_listed_partner()
    {
        var missing = Assert.Single(new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly, partner: null)], A2A = Enabled() }
            .CheckA2A(Enforced, SqliteSynchronous.Full));
        Assert.Contains("Partners", missing);

        var unknown = Assert.Single(new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly, partner: OtherPartnerId)], A2A = Enabled() }
            .CheckA2A(Enforced, SqliteSynchronous.Full));
        Assert.Contains("Partners", unknown);
    }

    [Fact]
    public void Several_keys_may_name_one_partner()
    {
        var keys = new[] { Key("acme-1", A2aOnly), Key("acme-2", A2aOnly) };
        Assert.Empty(new HostHttpOptions { ApiKeys = keys, A2A = Enabled() }.CheckA2A(Enforced, SqliteSynchronous.Full));
    }

    [Fact]
    public void A_key_without_a2a_call_is_not_checked_by_the_a2a_rules()
    {
        Assert.Empty(new HostHttpOptions { ApiKeys = [Key("tools-1", ["mcp:tools"], profile: "reply-assistant", partner: null)], A2A = Enabled() }
            .CheckA2A(Enforced, SqliteSynchronous.Full));
    }

    [Fact]
    public void Partner_ids_must_be_uuids_and_unique_and_partners_need_a_name()
    {
        var options = new HostHttpOptions
        {
            ApiKeys = [Key("acme-1", A2aOnly)],
            A2A = new A2aOptions
            {
                Enabled = true,
                Partners = [new A2aPartner("acme", "Acme"), new A2aPartner(PartnerId, "Acme"), new A2aPartner(PartnerId.ToUpperInvariant(), "Again"), new A2aPartner(OtherPartnerId, " ")],
            },
        };
        var errors = options.CheckA2A(Enforced, SqliteSynchronous.Full);
        Assert.Contains(errors, e => e.Contains("is not a UUID"));
        Assert.Contains(errors, e => e.Contains("more than once"));
        Assert.Contains(errors, e => e.Contains("needs a DisplayName"));
    }

    [Fact]
    public async Task Enabled_a2a_refuses_to_start_on_a_store_that_is_not_sqlite()
    {
        var options = new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)], A2A = Enabled() };
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => HostHttp.StartAsync(HostWith(), options));
        Assert.Contains("not SQLite", refused.Message);
    }

    [Fact]
    public async Task Enabled_a2a_starts_when_the_store_runs_with_full_durability_and_refuses_normal()
    {
        using var normalDb = new TempDb();
        using var fullDb = new TempDb();
        var options = new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly)], A2A = Enabled() };

        // The durability the store resolved is the one checked: a normal store is refused whatever the environment says.
        var normal = new SqliteActionStore(normalDb.Path, synchronous: SqliteSynchronous.Normal);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => HostHttp.StartAsync(HostWith(normal), options));
        Assert.Contains("OPENTAIL_DB_SYNCHRONOUS=full", refused.Message);

        var full = new SqliteActionStore(fullDb.Path, synchronous: SqliteSynchronous.Full);
        await using var server = await HostHttp.StartAsync(HostWith(full), options);
        Assert.NotNull(server.BaseUri);
    }

    [Fact]
    public async Task Disabled_a2a_starts_exactly_as_before_even_with_a_bad_partner_key()
    {
        var options = new HostHttpOptions { ApiKeys = [Key("acme-1", A2aOnly, profile: null, partner: null)], A2A = new A2aOptions { Enabled = false } };
        await using var server = await HostHttp.StartAsync(HostWith(), options);
        Assert.NotNull(server.BaseUri);
    }

    // ---- A.4: partner identity ----

    [Fact]
    public void New_partners_are_fresh_uuids_with_a_trimmed_name_and_pass_the_checks()
    {
        var acme = HttpDeploymentRunner.NewPartner("  Acme Corp ");
        Assert.True(Guid.TryParseExact(acme.Id, "D", out _));
        Assert.Equal("Acme Corp", acme.DisplayName);
        Assert.NotEqual(acme.Id, HttpDeploymentRunner.NewPartner("Acme Corp").Id);

        var options = new HostHttpOptions
        {
            ApiKeys = [Key("acme-1", A2aOnly, partner: acme.Id)],
            A2A = new A2aOptions { Enabled = true, Partners = [acme] },
        };
        Assert.Empty(options.CheckA2A(Enforced, SqliteSynchronous.Full));
    }

    // ---- A.5: a conditional rule on a top-level "partner" argument ----

    [Fact]
    public async Task A_rule_on_the_partner_argument_executes_one_partner_and_queues_another()
    {
        var policy = $$"""
            { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00",
              "Tools": { "mail.send": { "Mode": "Queue", "Rules": [
                { "Name": "acme-only", "Mode": "Execute", "IgnoreArgs": ["body", "to"],
                  "When": { "arg": "/partner", "equals": "{{PartnerId}}" } } ] } } }
            """;
        var (host, module, _) = Hosts.Build(policy);
        var session = host.Sessions.Start();
        var requester = new ActionRequester(RequesterKind.User, "me");
        ActionRequest Mail(string partner) =>
            new("mail.send", $$"""{"to":"alice@corp.example","body":"hi","partner":"{{partner}}"}""", requester, session);

        var acme = await host.Queue.SubmitAsync(Mail(PartnerId));
        Assert.Equal(ActionStatus.Succeeded, acme.Status);
        Assert.Equal("acme-only", acme.PolicyRule);
        Assert.Equal(1, module.SendCalls);

        var other = await host.Queue.SubmitAsync(Mail(OtherPartnerId));
        Assert.Equal(ActionStatus.Pending, other.Status);
        Assert.Null(other.PolicyRule);
        Assert.Equal(1, module.SendCalls);
    }
}
