using System.Text.Json.Nodes;
using OpenTail.Host.Admin;
using OpenTail.Host.Http;

namespace OpenTail.Host.Tests;

/// <summary>v5 V3 Tier 1b: reviewers sign in to the page with the company's identity provider (OpenID Connect, code with PKCE). The page
/// learns where to sign in from a public endpoint that holds no secret; its CSP widens only to that provider.</summary>
public class PageSignInTests
{
    private const string Authority = "https://login.microsoftonline.com/contoso-tenant/v2.0";

    [Fact]
    public async Task The_http_host_tells_the_page_where_to_sign_in_and_lets_it_reach_only_that_provider()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseHttpSignIn().UsePolicy(PolicyConfig.Deny).Build();
        await using var server = await HostHttp.StartAsync(host, new HostHttpOptions
        {
            Admin = new AdminOptions(),
            Jwt = new JwtSignInOptions { Audience = "api://opentail-host", Authority = Authority, PageClientId = "page-client-id" },
        });
        using var http = new HttpClient { BaseAddress = server.BaseUri };

        var cfg = JsonNode.Parse(await http.GetStringAsync("/sign-in.json"))!;              // no sign-in needed to read it
        Assert.Equal(Authority, cfg["authority"]!.GetValue<string>());
        Assert.Equal("page-client-id", cfg["clientId"]!.GetValue<string>());
        Assert.Equal("openid api://opentail-host/opentail.review", cfg["scope"]!.GetValue<string>());
        Assert.DoesNotContain("secret", cfg.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var page = await http.GetAsync("/");
        var csp = string.Join(" ", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("connect-src 'self' https://login.microsoftonline.com;", csp);
        Assert.Contains("form-action 'none'", csp);
        Assert.Contains("script-src 'nonce-", csp);
    }

    [Fact]
    public async Task Without_sign_in_settings_the_page_keeps_the_token_box_and_reaches_only_itself()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Deny).Build();
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions { Tokens = new Dictionary<string, string> { ["t-0123456789abcdef"] = "rita" } });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        Assert.Equal("{}", await http.GetStringAsync("/sign-in.json"));
        var csp = string.Join(" ", (await http.GetAsync("/")).Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("connect-src 'self';", csp);
    }

    [Fact]
    public void The_page_uses_pkce_checks_the_state_and_never_keeps_a_client_secret()
    {
        var html = typeof(AdminServer).Assembly.GetType("OpenTail.Host.Admin.AdminPage")!.GetField("Html")!.GetValue(null) as string;
        Assert.Contains("code_challenge_method: 'S256'", html);
        Assert.Contains("crypto.subtle.digest('SHA-256'", html);
        Assert.Contains("q.get('state') !== saved.state", html);                         // a sign-in that did not start here is refused
        Assert.Contains("grant_type: 'authorization_code'", html);
        Assert.DoesNotContain("client_secret", html);
        Assert.Contains("sessionStorage", html);                                          // the token lives only in this tab
        Assert.DoesNotContain("localStorage", html);
        Assert.Contains("r.status === 401", html);                                        // an expired sign-in asks again
    }
}
