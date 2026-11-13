using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>V2 of the v2 plan: the Host over Streamable HTTP with sign-in, over a real listening socket.</summary>
public class HttpTests : IAsyncLifetime
{
    private const string Issuer = "https://idp.test";
    private const string Audience = "https://host.test/mcp";
    private const string ReviewerToken = "reviewer-token-0123456789";
    private const string ReadOnlyReviewerToken = "junior-token-0123456789";

    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("test-signing-key-test-signing-key-0123456789"));

    private readonly string _alice = ApiKeys.Generate().Key;
    private readonly string _bob = ApiKeys.Generate().Key;
    private FakeInboxModule _module = null!;
    private OpenTailHost _host = null!;
    private RunningHostHttp _server = null!;
    private HttpClient _http = null!;

    private static HostHttpOptions Options(string alice, string bob, Action<List<string>>? _ = null, int rate = 0, int maxBody = 1024 * 1024) => new()
    {
        ApiKeys = [new ApiKeyEntry("alice", ApiKeys.Hash(alice)), new ApiKeyEntry("bob", ApiKeys.Hash(bob))],
        Admin = new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [ReviewerToken] = "rita", [ReadOnlyReviewerToken] = "jo" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [ReviewerToken] = new HashSet<string> { Roles.Review, Roles.Admin, "group:finance" },
                [ReadOnlyReviewerToken] = new HashSet<string> { Roles.Review },
            },
        },
        Jwt = new JwtSignInOptions
        {
            Audience = Audience,
            AuthorizationServer = Issuer,
            Configure = o =>
            {
                o.TokenValidationParameters.ValidIssuer = Issuer;
                o.TokenValidationParameters.ValidateIssuer = true;
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
            },
        },
        RateLimitPerMinute = rate,
        MaxBodyBytes = maxBody,
    };

    public async ValueTask InitializeAsync()
    {
        _module = new FakeInboxModule();
        _host = new OpenTailHostBuilder().AddModule(_module).UseHttpSignIn()
            .UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" } } }"""))
            .Build();
        _server = await HostHttp.StartAsync(_host, Options(_alice, _bob));
        _http = new HttpClient { BaseAddress = _server.BaseUri };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    // ---- helpers ----

    private static string Jwt(string sub = "user-1", string aud = Audience, string scope = "mcp:tools", DateTime? expires = null,
        SecurityKey? key = null, string[]? roles = null, bool includeSub = true)
    {
        var claims = new Dictionary<string, object> { ["scope"] = scope };
        if (includeSub) claims["sub"] = sub;
        if (roles is not null) claims["roles"] = roles;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = aud,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    private async Task<McpClient> ClientAsync(string token) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_server.BaseUri, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

    private static async Task<JsonObject> Call(McpClient c, string tool, params (string, object?)[] args)
    {
        var r = await c.CallToolAsync(tool, args.ToDictionary(a => a.Item1, a => a.Item2));
        return (JsonObject)JsonNode.Parse(r.Content.OfType<TextContentBlock>().First().Text)!;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, string? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (method == HttpMethod.Post && path == "/mcp")
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        tweak?.Invoke(req);
        return _http.SendAsync(req);
    }

    private const string InitializeBody = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    // ---- API keys ----

    [Fact]
    public async Task An_api_key_signs_in_and_its_calls_are_attributed_to_the_key()
    {
        await using var client = await ClientAsync(_alice);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("mail.send", names);
        Assert.Contains("start_session", names);

        var made = await Call(client, "mail.send", ("to", "x@y.z"), ("body", "b"));
        Assert.Equal("pending_approval", made["status"]!.ToString());
        var record = await _host.Store.GetAsync(made["actionId"]!.ToString());
        Assert.Equal("key:alice", record!.Requester.Id);
    }

    [Fact]
    public async Task No_credential_or_a_wrong_one_is_a_401_with_a_challenge()
    {
        var anonymous = await Send(HttpMethod.Post, "/mcp", body: InitializeBody);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("Bearer", string.Join(",", anonymous.Headers.WwwAuthenticate.Select(h => h.Scheme)));

        var wrong = await Send(HttpMethod.Post, "/mcp", token: "otk_not-a-real-key", body: InitializeBody);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task The_challenge_points_clients_at_the_protected_resource_metadata()
    {
        var anonymous = await Send(HttpMethod.Post, "/mcp", body: InitializeBody);
        Assert.Contains("resource_metadata", string.Join(" ", anonymous.Headers.WwwAuthenticate.Select(h => h.Parameter)));

        var meta = await Send(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        var doc = JsonNode.Parse(await meta.Content.ReadAsStringAsync())!;
        Assert.EndsWith("/mcp", doc["resource"]!.ToString());
        Assert.Equal(Issuer, doc["authorization_servers"]![0]!.ToString());
        Assert.Equal("mcp:tools", doc["scopes_supported"]![0]!.ToString());
    }

    // ---- credentials are separate: agents cannot review, reviewers cannot call tools ----

    [Fact]
    public async Task A_reviewer_token_cannot_call_tools_and_an_api_key_cannot_review()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/mcp", ReviewerToken, InitializeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/actions", _alice)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/actions", ReviewerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/actions")).StatusCode);
    }

    [Fact]
    public async Task An_agent_cannot_approve_its_own_action()
    {
        await using var client = await ClientAsync(_alice);
        var made = await Call(client, "mail.send", ("to", "x@y.z"), ("body", "b"));
        var id = made["actionId"]!.ToString();

        var attempt = await Send(HttpMethod.Post, $"/api/actions/{id}/review", _alice, """{"decision":"approve"}""");
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Equal(0, _module.SendCalls);
    }

    [Fact]
    public async Task Promoting_a_tool_needs_the_admin_role_not_just_the_review_role()
    {
        var junior = await Send(HttpMethod.Post, "/api/trust/mail.send/promote", ReadOnlyReviewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, junior.StatusCode);
    }

    // ---- end to end: agent asks, reviewer decides, agent sees it; another agent sees nothing ----

    [Fact]
    public async Task Two_agents_are_isolated_and_the_reviewer_identity_comes_from_the_server()
    {
        await using var alice = await ClientAsync(_alice);
        await using var bob = await ClientAsync(_bob);

        var made = await Call(alice, "mail.send", ("to", "x@y.z"), ("body", "b"));
        var id = made["actionId"]!.ToString();

        Assert.Equal("not_found", (await Call(bob, "get_action_status", ("actionId", id)))["status"]!.ToString());
        Assert.Empty((await Call(bob, "list_my_actions"))["actions"]!.AsArray());

        // The body tries to claim to be someone else; the review records who the server authenticated.
        var review = await Send(HttpMethod.Post, $"/api/actions/{id}/review", ReviewerToken,
            """{"decision":"approve","reviewer":"ceo","Subject":"admin:ceo"}""");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        Assert.Equal("succeeded", (await Call(alice, "get_action_status", ("actionId", id)))["status"]!.ToString());
        Assert.Equal(1, _module.SendCalls);
        var stored = await _host.Store.GetAsync(id);
        var rv = Assert.Single(stored!.Reviews);
        Assert.Equal("rita", rv.Reviewer);
        Assert.Equal("admin:rita", rv.Subject);
        Assert.Contains("finance", rv.Groups);
    }

    [Fact]
    public async Task A_session_is_not_shared_between_signed_in_callers()
    {
        await using var alice = await ClientAsync(_alice);
        await using var bob = await ClientAsync(_bob);
        var session = (await Call(alice, "start_session"))["sessionId"]!.ToString();

        var hijack = await Call(bob, "mail.send", ("to", "x@y.z"), ("body", "b"), ("sessionId", session));
        Assert.Equal("invalid_request", hijack["error"]!["code"]!.ToString());
    }

    // ---- JWT ----

    [Fact]
    public async Task A_valid_jwt_signs_in_with_a_namespaced_subject()
    {
        await using var client = await ClientAsync(Jwt("user-1"));
        var made = await Call(client, "mail.send", ("to", "x@y.z"), ("body", "b"));
        var record = await _host.Store.GetAsync(made["actionId"]!.ToString());
        Assert.Equal($"jwt:{Issuer}|user-1", record!.Requester.Id);
    }

    [Fact]
    public async Task Bad_jwts_are_refused()
    {
        var wrongAudience = Jwt(aud: "https://someone-else");
        var expired = Jwt(expires: DateTime.UtcNow.AddMinutes(-5));
        var forged = Jwt(key: new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-different-key-a-different-key-0123456789")));
        var noSubject = Jwt(includeSub: false);
        foreach (var token in new[] { wrongAudience, expired, forged, noSubject })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/mcp", token, InitializeBody)).StatusCode);
    }

    [Fact]
    public async Task A_jwt_without_the_tool_scope_is_forbidden_and_the_review_scope_unlocks_the_admin_api()
    {
        var noScope = Jwt(scope: "profile");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/mcp", noScope, InitializeBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/actions", Jwt())).StatusCode); // tools scope only

        var reviewer = Jwt("rev-1", scope: "opentail.review", roles: ["group:ops"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/actions", reviewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/mcp", reviewer, InitializeBody)).StatusCode); // review scope alone is not tools
    }

    // ---- browser and host defences, limits ----

    [Fact]
    public async Task A_browser_origin_is_refused_unless_allowed_and_a_foreign_host_header_is_a_400()
    {
        var evil = await Send(HttpMethod.Get, "/healthz", tweak: r => r.Headers.Add("Origin", "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, evil.StatusCode);

        var rebinding = await Send(HttpMethod.Get, "/", tweak: r => r.Headers.Host = "attacker.example");
        Assert.Equal(HttpStatusCode.BadRequest, rebinding.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/mcp", body: InitializeBody, tweak: r => r.Headers.Host = "attacker.example")).StatusCode);
        // A health probe reaches the container by its own address; it is answered, and says nothing.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/healthz", tweak: r => r.Headers.Host = "10.0.0.7:8080")).StatusCode);

        var health = await Send(HttpMethod.Get, "/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        // States only, no counts or times: the probe is anonymous.
        var body = System.Text.Json.Nodes.JsonNode.Parse(await health.Content.ReadAsStringAsync())!;
        Assert.Contains(body["status"]!.ToString(), new[] { "ok", "degraded" });
        Assert.NotNull(body["checks"]!["maintenance"]);
        var ownPage = await Send(HttpMethod.Get, "/healthz", tweak: r => r.Headers.Add("Origin", _server.BaseUri.GetLeftPart(UriPartial.Authority)));
        Assert.Equal(HttpStatusCode.OK, ownPage.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_the_page_is_served_without_a_credential()
    {
        var page = await Send(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("nonce-", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join(",", page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Requests_are_rate_limited_per_subject()
    {
        await using var limited = await HostHttp.StartAsync(
            new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseHttpSignIn().UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }""")).Build(),
            Options(_alice, _bob, rate: 5));
        using var http = new HttpClient { BaseAddress = limited.BaseUri };
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/actions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReviewerToken);
            codes.Add((await http.SendAsync(req)).StatusCode);
        }
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task An_oversized_body_is_refused()
    {
        await using var small = await HostHttp.StartAsync(
            new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseHttpSignIn().UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }""")).Build(),
            Options(_alice, _bob, maxBody: 2048));
        using var http = new HttpClient { BaseAddress = small.BaseUri };
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(new string('x', 10_000), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _alice);
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(req)).StatusCode);
    }

    // ---- refusing to start open ----

    [Fact]
    public void Configurations_that_would_start_open_are_refused()
    {
        string First(HostHttpOptions o) => string.Join(" ", o.Validate());

        Assert.Contains("needs sign-in", First(new HostHttpOptions { Urls = ["https://0.0.0.0:8443"], PublicBaseUrl = "https://host.test" }));
        Assert.Contains("plain http", First(new HostHttpOptions
        {
            Urls = ["http://0.0.0.0:8080"], PublicBaseUrl = "https://host.test",
            ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"))],
        }));
        Assert.Contains("PublicBaseUrl", First(new HostHttpOptions { Urls = ["https://0.0.0.0:8443"], ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"))] }));
        Assert.Contains("never carry", First(new HostHttpOptions { ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"), [Roles.Review])] }));
        Assert.Contains("sha256", First(new HostHttpOptions { ApiKeys = [new ApiKeyEntry("a", "plaintext-key")] }));
        Assert.Contains("Duplicate", First(new HostHttpOptions { ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k")), new ApiKeyEntry("a", ApiKeys.Hash("j"))] }));
        Assert.Contains("cannot also call tools", First(new HostHttpOptions
        {
            Admin = new AdminOptions
            {
                Tokens = new Dictionary<string, string> { ["reviewer-token-0123456789"] = "x" },
                TokenRoles = new Dictionary<string, IReadOnlySet<string>> { ["reviewer-token-0123456789"] = new HashSet<string> { Roles.Review, Roles.Tools } },
            },
        }));

        // Loopback behind a tunnel (the public address says so) is not "only this machine".
        Assert.Contains("tunnel or proxy", First(new HostHttpOptions { Urls = ["http://127.0.0.1:5600"], PublicBaseUrl = "https://abc-5600.euw.devtunnels.ms" }));

        // Fine: a proxy terminates TLS, or loopback.
        Assert.Empty(new HostHttpOptions { Urls = ["http://127.0.0.1:5600"], PublicBaseUrl = "https://abc-5600.euw.devtunnels.ms", ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"))] }.Validate());
        Assert.Empty(new HostHttpOptions { Urls = ["http://127.0.0.1:5600"], PublicBaseUrl = "http://localhost:5600" }.Validate());
        Assert.Empty(new HostHttpOptions
        {
            Urls = ["http://0.0.0.0:8080"], PublicBaseUrl = "https://host.test", TrustProxy = true,
            ApiKeys = [new ApiKeyEntry("a", ApiKeys.Hash("k"))],
        }.Validate());
        Assert.Empty(new HostHttpOptions().Validate());
    }

    [Fact]
    public async Task A_host_built_without_http_sign_in_refuses_to_serve()
    {
        var plain = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HostHttp.StartAsync(plain, new HostHttpOptions()));
        Assert.Contains("UseHttpSignIn", ex.Message);
    }

    // ---- keys ----

    [Fact]
    public void Generated_keys_are_long_random_and_only_their_hash_is_stored()
    {
        var (key, hash) = ApiKeys.Generate();
        var (key2, _) = ApiKeys.Generate();
        Assert.StartsWith("otk_", key);
        Assert.True(key.Length >= 40);
        Assert.NotEqual(key, key2);
        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);
        Assert.DoesNotContain(key, hash);
        Assert.Equal(hash, ApiKeys.Hash(key));
    }

    // ---- protocol revisions: the current one, and older clients still served ----

    [Fact]
    public async Task A_current_client_negotiates_the_2026_07_28_revision()
    {
        await using var client = await ClientAsync(_alice);
        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        Assert.Contains("start_session", (await client.ListToolsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task A_2025_11_25_client_still_initializes_lists_and_calls_tools()
    {
        static string Rpc(int id, string method, string @params) => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{@params}}}""";
        async Task<JsonNode> Post(string body, bool legacyHeader)
        {
            var res = await Send(HttpMethod.Post, "/mcp", _alice, body, r => { if (legacyHeader) r.Headers.Add("MCP-Protocol-Version", "2025-11-25"); });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var text = await res.Content.ReadAsStringAsync();
            // Streamable HTTP may answer as JSON or as one server-sent event.
            var json = text.TrimStart().StartsWith('{') ? text : text.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];
            return JsonNode.Parse(json)!;
        }

        var init = await Post(Rpc(1, "initialize", """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"old","version":"1"}}"""), legacyHeader: false);
        Assert.Equal("2025-11-25", init["result"]!["protocolVersion"]!.ToString());

        var list = await Post(Rpc(2, "tools/list", "{}"), legacyHeader: true);
        Assert.Contains(list["result"]!["tools"]!.AsArray(), t => t!["name"]!.ToString() == "mail.send");

        var call = await Post(Rpc(3, "tools/call", """{"name":"mail.send","arguments":{"to":"x@y.z","body":"b"}}"""), legacyHeader: true);
        var made = JsonNode.Parse(call["result"]!["content"]![0]!["text"]!.ToString())!;
        Assert.Equal("pending_approval", made["status"]!.ToString());
    }

    [Fact]
    public async Task The_x_api_key_header_works_too()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-API-Key", _alice);
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(req)).StatusCode);
    }
}
