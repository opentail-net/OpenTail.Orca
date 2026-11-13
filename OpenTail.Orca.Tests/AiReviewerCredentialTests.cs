using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>P1 boundary 1 and D-30: an AI reviewer's credential, and what it may reach on the review API.</summary>
public class AiReviewerCredentialTests
{
    private const string AdminToken = "admin-token-0123456789";
    private const string BotSecret = "bot-secret-0123456789abcdef";
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";

    private static string HashOf(string secret) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Policy(string assignedTo) =>
        "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"Reviewer\": \"Ai\", \"AiMaxRisk\": \"ExternalEffect\", \"AiReviewers\": [\"" + assignedTo + "\"] } } }";

    private static AdminOptions Options(IReadOnlyDictionary<string, string>? ai = null) => new()
    {
        Tokens = new Dictionary<string, string> { [AdminToken] = "alice" },
        AiReviewers = ai ?? new Dictionary<string, string> { ["bot"] = HashOf(BotSecret) },
    };

    private static HttpClient Client(RunningAdmin admin, string token)
    {
        var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    [Fact]
    public async Task The_ai_reviewer_lists_and_reads_only_what_is_assigned_to_it()
    {
        var (host, _, _) = Hosts.Build(Policy("other"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var bot = Client(admin, BotSecret);

        Assert.Empty(JsonNode.Parse(await bot.GetStringAsync("/api/actions"))!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bot.GetAsync($"/api/actions/{mail.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_ai_reviewer_decides_an_assigned_action_and_the_decision_is_its_own()
    {
        var (host, module, _) = Hosts.Build(Policy("bot"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var bot = Client(admin, BotSecret);

        var resp = await bot.PostAsync($"/api/actions/{mail.Id}/review", JsonContent.Create(new { decision = "approve" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains((await host.Store.GetAsync(mail.Id))!.Reviews, r => r.Subject == "ai:bot");
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task An_ai_reviewer_cannot_decide_an_action_that_is_not_offered_to_it()
    {
        var (host, module, _) = Hosts.Build(Policy("other"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var bot = Client(admin, BotSecret);

        var resp = await bot.PostAsync($"/api/actions/{mail.Id}/review", JsonContent.Create(new { decision = "reject", reason = "no" }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(mail.Id))!.Status);
    }

    [Fact]
    public async Task An_ai_reviewer_is_refused_every_route_except_the_three_it_may_use()
    {
        var (host, _, _) = Hosts.Build(Policy("bot"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var bot = Client(admin, BotSecret);

        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/explain/mail.send")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/trust/mail.send")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsync($"/api/actions/{mail.Id}/stop", null)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_and_the_stored_hash_is_not_a_secret()
    {
        var (host, _, _) = Hosts.Build(Policy("bot"));
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var wrong = Client(admin, "not-the-bot-secret-0123");
        using var hashAsSecret = Client(admin, HashOf(BotSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hashAsSecret.GetAsync("/api/actions")).StatusCode);
    }

    [Fact]
    public async Task The_admin_token_still_sees_and_reads_everything()
    {
        var (host, _, _) = Hosts.Build(Policy("other"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var ops = Client(admin, AdminToken);

        Assert.Single(JsonNode.Parse(await ops.GetStringAsync("/api/actions"))!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync($"/api/actions/{mail.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_ai_reviewer_needs_a_valid_name_and_a_hash_not_the_secret_at_startup()
    {
        var (host, _, _) = Hosts.Build(Policy("bot"));
        await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(host, Options(new Dictionary<string, string> { ["bot"] = BotSecret })));
        await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(host, Options(new Dictionary<string, string> { ["bad name"] = HashOf(BotSecret) })));
    }

    [Fact]
    public void A_hosted_ai_reviewer_secret_carries_the_review_role_and_no_tool_or_admin_role()
    {
        var table = StaticTokenTable.Build(new HostHttpOptions { AiReviewers = [new AiReviewerEntry("bot", HashOf(BotSecret))] });

        var found = table.Find(BotSecret);
        Assert.NotNull(found);
        Assert.Equal("ai:bot", found!.Subject);
        Assert.Equal(Roles.Review, Assert.Single(found.Roles));
        Assert.Null(table.Find(HashOf(BotSecret)));
    }

    [Fact]
    public async Task An_ai_decision_over_http_keeps_what_it_reported_and_is_labelled_on_the_page()
    {
        var (host, _, audit) = Hosts.Build(Policy("bot"));
        var mail = await Submit(host);
        await using var admin = await AdminServer.StartAsync(host, Options());
        using var bot = Client(admin, BotSecret);

        var resp = await bot.PostAsync($"/api/actions/{mail.Id}/review", JsonContent.Create(new
        {
            decision = "approve", aiModel = "model-x", aiPromptVersion = "prompt-1", aiChecks = new[] { "checked the recipient" },
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(audit.Entries, e => e.Event == "ai_review" && e.ActionId == mail.Id && e.Actor == "ai:bot");

        using var ops = Client(admin, AdminToken);
        var review = JsonNode.Parse(await ops.GetStringAsync($"/api/actions/{mail.Id}"))!["reviews"]!.AsArray().Single()!;
        Assert.True(review["byAi"]!.GetValue<bool>());
        Assert.Equal("model-x", review["aiModel"]!.GetValue<string>());
        Assert.Equal("prompt-1", review["aiPromptVersion"]!.GetValue<string>());
        Assert.Equal("checked the recipient", review["aiChecks"]!.AsArray()[0]!.GetValue<string>());
        Assert.Contains("AI decision by", await ops.GetStringAsync("/"));
    }
}
