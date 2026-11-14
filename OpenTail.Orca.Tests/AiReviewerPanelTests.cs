using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>The AI reviewers panel (overseer, section 5.13): the record a human needs to decide, and the suspend and clear that only an admin can do.</summary>
public class AiReviewerPanelTests
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReviewOnlyToken = "reviewer-token-0123456789";
    private const string BotSecret = "bot-secret-0123456789abcdef";
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";
    private const string Policy = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"], "AiMaxRisk": "ExternalEffect" } } }""";

    private static string HashOf(string secret) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static AdminOptions Options() => new()
    {
        Tokens = new Dictionary<string, string> { [AdminToken] = "alice", [ReviewOnlyToken] = "rita" },
        TokenRoles = new Dictionary<string, IReadOnlySet<string>>
        {
            [AdminToken] = new HashSet<string> { Roles.Review, Roles.Admin },
            [ReviewOnlyToken] = new HashSet<string> { Roles.Review },
        },
        AiReviewers = new Dictionary<string, string> { ["bot"] = HashOf(BotSecret) },
    };

    private static HttpClient Client(RunningAdmin admin, string token)
    {
        var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    private static async Task<JsonObject> Reviewer(HttpClient admin, string name) =>
        JsonNode.Parse(await admin.GetStringAsync("/api/admin/ai-reviewers"))!["reviewers"]!.AsArray()
            .Select(r => r!.AsObject()).Single(r => r["name"]!.GetValue<string>() == name);

    [Fact]
    public async Task The_panel_shows_each_reviewers_decisions_with_what_came_of_them()
    {
        var (host, _, _) = Hosts.Build(Policy);
        var mail = await Submit(host);
        await using var server = await AdminServer.StartAsync(host, Options());
        using var bot = Client(server, BotSecret);
        using var admin = Client(server, AdminToken);

        var decided = await bot.PostAsync($"/api/actions/{mail.Id}/review", JsonContent.Create(new
        {
            decision = "approve", aiModel = "model-x", aiPromptVersion = "prompt-1", aiChecks = new[] { "the recipient is known" },
        }));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        var panel = await Reviewer(admin, "bot");
        Assert.False(panel["suspended"]!.GetValue<bool>());
        Assert.True(panel["configured"]!.GetValue<bool>());
        Assert.Equal(1, panel["decisions"]!["total"]!.GetValue<int>());
        Assert.Equal(1, panel["decisions"]!["approve"]!.GetValue<int>());
        var recent = panel["recent"]!.AsArray().Single()!;
        Assert.Equal("approve", recent["decision"]!.GetValue<string>());
        Assert.Equal("Succeeded", recent["status"]!.GetValue<string>());
        Assert.Equal("the recipient is known", recent["checks"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_an_admin_sees_the_panel_or_acts_on_it()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var server = await AdminServer.StartAsync(host, Options());
        using var bot = Client(server, BotSecret);
        using var reviewer = Client(server, ReviewOnlyToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/admin/ai-reviewers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync("/api/admin/ai-reviewers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.PostAsync("/api/admin/ai-reviewers/bot/suspend",
            JsonContent.Create(new { reason = "not mine to decide" }))).StatusCode);
        Assert.False(host.Levels.Get("ai:bot") is { Level: IdentityLevel.Suspended });
    }

    [Fact]
    public async Task A_suspension_needs_a_reason_stops_the_reviewer_at_once_and_is_recorded()
    {
        var (host, module, audit) = Hosts.Build(Policy);
        var first = await Submit(host);
        var second = await Submit(host);
        await using var server = await AdminServer.StartAsync(host, Options());
        using var bot = Client(server, BotSecret);
        using var admin = Client(server, AdminToken);

        var noReason = await admin.PostAsync("/api/admin/ai-reviewers/bot/suspend", JsonContent.Create(new { reason = " " }));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Null(host.Levels.Get("ai:bot"));

        var suspended = await admin.PostAsync("/api/admin/ai-reviewers/bot/suspend", JsonContent.Create(new { reason = "odd approvals this morning" }));
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal(IdentityLevel.Suspended, host.Levels.Get("ai:bot")!.Level);
        Assert.Contains(audit.Entries, e => e.Event == "ai_reviewer_suspended" && e.Actor == "admin:alice" && e.Detail!.Contains("odd approvals"));

        // The reviewer's next decision is refused at once, refusals included.
        var refused = await bot.PostAsync($"/api/actions/{first.Id}/review", JsonContent.Create(new { decision = "reject", reason = "no" }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, module.SendCalls);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/ai-reviewers/bot/suspend",
            JsonContent.Create(new { reason = "again" }))).StatusCode);
        Assert.True((await Reviewer(admin, "bot"))["suspended"]!.GetValue<bool>());
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(second.Id))!.Status);   // nothing it would have decided ran
    }

    [Fact]
    public async Task Clearing_restores_the_reviewer_and_records_why()
    {
        var (host, module, audit) = Hosts.Build(Policy);
        var mail = await Submit(host);
        await using var server = await AdminServer.StartAsync(host, Options());
        using var bot = Client(server, BotSecret);
        using var admin = Client(server, AdminToken);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/ai-reviewers/bot/clear", JsonContent.Create(new { reason = "not needed" }))).StatusCode);
        await admin.PostAsync("/api/admin/ai-reviewers/bot/suspend", JsonContent.Create(new { reason = "incident" }));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/ai-reviewers/bot/clear", JsonContent.Create(new { reason = "reviewed, fine" }))).StatusCode);
        Assert.Contains(audit.Entries, e => e.Event == "ai_reviewer_cleared" && e.Detail!.Contains("reviewed, fine"));
        Assert.Equal(HttpStatusCode.OK, (await bot.PostAsync($"/api/actions/{mail.Id}/review", JsonContent.Create(new { decision = "approve" }))).StatusCode);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task The_page_has_the_ai_reviewers_tab()
    {
        var (host, _, _) = Hosts.Build(Policy);
        await using var server = await AdminServer.StartAsync(host, Options());
        using var admin = Client(server, AdminToken);

        Assert.Contains("AI reviewers", await admin.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_list_is_a_window_and_older_decisions_load_a_page_at_a_time()
    {
        var (host, _, _) = Hosts.Build(Policy);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 45; i++)
        {
            // Twenty-five decisions within the last week, and twenty from ten days ago.
            var at = i < 25 ? now.AddMinutes(-i) : now.AddDays(-10).AddMinutes(-i);
            await host.Audit.AppendAsync(new AuditEntry(at, "ai_review", null, "mail.send", "ai:bot", "Approve; model m; prompt p; 0 check(s) reported"));
        }
        await using var server = await AdminServer.StartAsync(host, Options());
        using var admin = Client(server, AdminToken);

        // The default window is seven days: the totals cover all time, the list and its count cover the window.
        var overview = JsonNode.Parse(await admin.GetStringAsync("/api/admin/ai-reviewers"))!;
        var bot = overview["reviewers"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["name"]!.GetValue<string>() == "bot");
        Assert.Equal(45, bot["decisions"]!["total"]!.GetValue<int>());
        Assert.Equal(25, bot["inWindow"]!.GetValue<int>());
        Assert.Equal(20, bot["recent"]!.AsArray().Count);
        Assert.True(bot["hasMore"]!.GetValue<bool>());

        // Load older: the rest of the window, then nothing more inside it.
        var lastShown = bot["recent"]!.AsArray()[19]!["seq"]!.GetValue<long>();
        var rest = JsonNode.Parse(await admin.GetStringAsync($"/api/admin/ai-reviewers/bot/decisions?days=7&before={lastShown}"))!;
        Assert.Equal(5, rest["items"]!.AsArray().Count);
        Assert.False(rest["hasMore"]!.GetValue<bool>());

        // All time, a page at a time: every decision once, and the last page says there is nothing more.
        var seqs = new List<long>();
        long? before = null;
        var more = true;
        while (more)
        {
            var url = "/api/admin/ai-reviewers/bot/decisions?days=0&limit=20" + (before is { } b ? $"&before={b}" : "");
            var page = JsonNode.Parse(await admin.GetStringAsync(url))!;
            var items = page["items"]!.AsArray();
            seqs.AddRange(items.Select(i => i!["seq"]!.GetValue<long>()));
            before = seqs[^1];
            more = page["hasMore"]!.GetValue<bool>();
        }
        Assert.Equal(45, seqs.Count);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/ai-reviewers?days=2000")).StatusCode);
    }
}
