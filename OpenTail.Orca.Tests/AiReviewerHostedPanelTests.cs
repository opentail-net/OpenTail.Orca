using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>On the hosted server, the overseer's panel lists every configured AI reviewer, including one that has not decided anything yet.</summary>
public class AiReviewerHostedPanelTests
{
    private const string AdminToken = "admin-token-0123456789";

    [Fact]
    public async Task A_configured_hosted_reviewer_that_has_not_decided_appears_in_the_panel()
    {
        var host = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse("""{ "Tools": {} }""")).UseHttpSignIn().Build();
        var options = new HostHttpOptions
        {
            Admin = new AdminOptions { Tokens = new Dictionary<string, string> { [AdminToken] = "alice" } },
            AiReviewers = [new AiReviewerEntry("silent", ApiKeys.Hash("silent-secret-0123456789"))],
        };
        await using var server = await HostHttp.StartAsync(host, options);
        using var admin = new HttpClient { BaseAddress = server.BaseUri };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        var overview = JsonNode.Parse(await admin.GetStringAsync("/api/admin/ai-reviewers"))!;
        var silent = overview["reviewers"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["name"]!.GetValue<string>() == "silent");
        Assert.True(silent["configured"]!.GetValue<bool>());
        Assert.Equal(0, silent["decisions"]!["total"]!.GetValue<int>());
    }
}
