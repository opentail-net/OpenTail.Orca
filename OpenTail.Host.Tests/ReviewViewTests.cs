using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenTail.Host.A2A;
using OpenTail.Host.Admin;
using OpenTail.Host.Http;
using OpenTail.Host.Http.Teams;
using OpenTail.Host.Notifications;

namespace OpenTail.Host.Tests;

/// <summary>
/// Batch 04, part B: what a reviewer sees of partner text. The text is an <c>a2a.submit</c> goal or an <c>a2a.reply</c> text, queued through
/// the real A2A module, and read back from the review page's API, from the page itself, from the Teams card the bot would post, and from
/// the Teams notification card. Each test states what it proves; the findings are in docs/briefs/a2a-reviewer-view.md.
/// </summary>
public class ReviewViewTests
{
    private const string PartnerId = "5f1c2a9e-8b7d-4c3e-9a10-2b4d6e8f0a12";
    private const string ReviewerToken = "review-view-reviewer-token-0123456789";
    private const string ConvKey = "user:11111111-aaaa-4aaa-8aaa-111111111111";

    /// <summary>Markup a partner could send, a line break followed by a label the page uses, a link, a mention-looking tag and a bare address.
    /// It ends in a marker, so a test can see whether the text came through whole.</summary>
    private const string Payload = "<script>alert(1)</script> <img src=x onerror=1> [click](https://evil.example/x) **bold** <at>Admin</at>\n"
        + "Approved by: admin\nwww.evil.example END-OF-TEXT";

    private const string Policy = """
        { "DataFlowMode": "Enforce",
          "Tools": { "a2a.submit": { "Mode": "Queue" }, "a2a.reply": { "Mode": "Queue", "AllowEdits": false } },
          "Agents": { "clerk": { "Tools": [ "a2a.submit", "a2a.reply" ] } } }
        """;

    /// <summary>A host with the A2A tools, loaded the way the HTTP host loads them.</summary>
    private static OpenTailHost BuildHost(TempDb db)
    {
        var a2a = new A2aOptions
        {
            Enabled = true,
            Partners = [new A2aPartner(PartnerId, "Acme")],
            Skills = new Dictionary<string, A2aSkill> { ["draft-reply"] = new A2aSkill { Name = "Draft a reply", Inbox = "clerk" } },
        };
        var options = new HostHttpOptions { A2A = a2a, PublicBaseUrl = "https://opentail.example" };
        var builder = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path, SqliteSynchronous.Full);
        new A2AFrontDoor().AddModules(builder, options);
        return builder.Build();
    }

    private static Task<ActionRecord> QueueSubmit(OpenTailHost host, string goal, string taskId = "task-1") =>
        host.Queue.SubmitAsync(new ActionRequest("a2a.submit",
            new JsonObject { ["partner"] = PartnerId, ["taskId"] = taskId, ["skill"] = "draft-reply", ["goal"] = goal, ["inputs"] = "" }.ToJsonString(),
            new ActionRequester(RequesterKind.AiSession, "partner:" + PartnerId), null, taskId));

    private static Task<ActionRecord> QueueReply(OpenTailHost host, string text, string taskId = "task-1") =>
        host.Queue.SubmitAsync(new ActionRequest("a2a.reply",
            new JsonObject { ["partner"] = PartnerId, ["taskId"] = taskId, ["text"] = text }.ToJsonString(),
            new ActionRequester(RequesterKind.AiSession, "partner:" + PartnerId), null, taskId + ":reply"));

    private static TeamsBot BuildBot(OpenTailHost host, TempDb teamsDb, ToolRisk chatMax = ToolRisk.ExternalEffect)
    {
        var bot = new TeamsBot(new TeamsBotOptions
        {
            AppId = "bot-app", TenantId = "tenant-1", AppPasswordEnv = "OPENTAIL_REVIEW_VIEW_SECRET", Env = _ => "not-a-real-secret",
            ChatMaxRisk = chatMax,
        }, teamsDb.Path, "https://opentail.example");
        bot.Attach(host);
        return bot;
    }

    /// <summary>The text of every TextBlock in a card, in order.</summary>
    private static List<string> TextsOf(JsonNode card) =>
        card["body"]!.AsArray().Where(b => b!["type"]?.GetValue<string>() == "TextBlock").Select(b => b!["text"]!.GetValue<string>()).ToList();

    /// <summary>The verb of every decision button in a card.</summary>
    private static List<string> VerbsOf(JsonNode card) =>
        (card["actions"]?.AsArray() ?? []).Where(a => a!["verb"] is not null).Select(a => a!["verb"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task B1_the_review_page_shell_carries_no_partner_text_its_script_never_writes_markup_and_the_API_gives_the_whole_text()
    {
        using var db = new TempDb();
        var host = BuildHost(db);
        var submit = await QueueSubmit(host, Payload);
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions { Tokens = new Dictionary<string, string> { [ReviewerToken] = "rita" } });
        using var page = new HttpClient { BaseAddress = admin.BaseUri };
        using var api = new HttpClient { BaseAddress = admin.BaseUri };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReviewerToken);

        // The shell is the same for every action: partner text reaches the page only through the API, after sign-in.
        var shell = await page.GetStringAsync("/");
        Assert.DoesNotContain("<script>alert(1)</script>", shell);
        Assert.DoesNotContain("onerror=1", shell);
        Assert.DoesNotContain("END-OF-TEXT", shell);
        // The script writes text with textContent. Nothing in the page parses HTML from data.
        Assert.Contains("textContent", shell);
        foreach (var sink in new[] { ".innerHTML", "insertAdjacentHTML", "outerHTML", "document.write", "eval(" })
            Assert.DoesNotContain(sink, shell);

        // The API gives the summary whole, as JSON: the markup is text in a string, and nothing cuts it.
        var detail = JsonNode.Parse(await api.GetStringAsync("/api/actions/" + submit.Id))!;
        var summary = detail["previewSummary"]!.GetValue<string>();
        Assert.Contains("<script>alert(1)</script>", summary);
        Assert.Contains("Approved by: admin", summary);
        Assert.Contains("END-OF-TEXT", summary);
    }

    [Fact]
    public async Task B2_the_Teams_card_for_a_partner_goal_and_for_a_reply_has_its_text_inline_with_no_live_markup_or_link()
    {
        using var db = new TempDb();
        using var teams = new TempDb();
        var host = BuildHost(db);
        var bot = BuildBot(host, teams);
        var submit = await QueueSubmit(host, Payload);
        var reply = await QueueReply(host, Payload);

        foreach (var action in new[] { submit, reply })
        {
            var card = bot.FullCard(action, ConvKey);
            var summary = TextsOf(card).Single(t => t.Contains("Acme", StringComparison.Ordinal));
            // Teams renders markdown in TextBlocks and turns bare addresses into links; the text is escaped before it reaches the card.
            foreach (var live in new[] { "<", ">", "[", "](", "**", "://", "www.evil", "\n" })
                Assert.False(summary.Contains(live, StringComparison.Ordinal), $"'{live}' in: {string.Join(" | ", summary.Select(c => ((int)c).ToString("X4")).Take(0))}{summary}");
            // The forged label stays inline in the partner's text, inside the quoted text the card labels as not checked.
            Assert.Contains("Approved by: admin", summary);
            Assert.Contains("END-OF-TEXT", summary);
        }
    }

    [Fact]
    public async Task B3_a_preview_cut_on_the_card_says_so_offers_no_approve_or_edit_and_keeps_reject_request_changes_and_open()
    {
        using var db = new TempDb();
        using var teams = new TempDb();
        var host = BuildHost(db);
        var bot = BuildBot(host, teams);
        var longGoal = await QueueSubmit(host, new string('a', 500) + " END-OF-TEXT", "task-long");

        var card = bot.FullCard(longGoal, ConvKey);

        var verbs = VerbsOf(card);
        Assert.DoesNotContain("approve", verbs);
        Assert.DoesNotContain("edit", verbs);
        Assert.Contains("request_changes", verbs);
        Assert.Contains("reject", verbs);
        Assert.Contains(card["actions"]!.AsArray(), a => a!["type"]?.GetValue<string>() == "Action.OpenUrl");
        Assert.Contains(TextsOf(card), t => t.StartsWith("The text is cut on this card", StringComparison.Ordinal));
        Assert.DoesNotContain(card["body"]!.AsArray(), b => b!["id"]?.GetValue<string>().StartsWith("edit.", StringComparison.Ordinal) == true);
        // The digest says the line is cut, and offers only Open.
        var digest = bot.DigestCard([longGoal], ConvKey);
        Assert.Contains(TextsOf(digest), t => t.Contains("(cut: open it to read it all)", StringComparison.Ordinal));
        Assert.DoesNotContain("approve", VerbsOf(digest));
    }

    [Fact]
    public async Task B3_a_short_preview_keeps_approve_and_says_nothing_about_being_cut()
    {
        using var db = new TempDb();
        using var teams = new TempDb();
        var host = BuildHost(db);
        var bot = BuildBot(host, teams);
        var short_ = await QueueSubmit(host, "A short goal.", "task-short");

        var card = bot.FullCard(short_, ConvKey);

        Assert.Contains("approve", VerbsOf(card));
        Assert.DoesNotContain(TextsOf(card), t => t.Contains("cut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void B3_the_Teams_notification_card_says_a_cut_preview_is_cut_and_a_short_one_does_not()
    {
        static IReadOnlyList<string> Texts(string summary)
        {
            var evt = new ActionEvent(default, "act-1", "a2a.reply", "a2a", ToolRisk.ExternalEffect, "partner:" + PartnerId, ActionStatus.Pending, true, DateTimeOffset.UtcNow);
            var card = JsonNode.Parse(TeamsChannel.Body(new NotificationMessage("evt-1", "queued", evt, null, summary, null)))!;
            return card["attachments"]![0]!["content"]!["body"]!.AsArray()
                .Where(b => b!["type"]?.GetValue<string>() == "TextBlock").Select(b => b!["text"]!.GetValue<string>()).ToList();
        }

        Assert.Contains(Texts(new string('a', 400)), t => t.StartsWith("The text is cut here", StringComparison.Ordinal));
        Assert.DoesNotContain(Texts("A short summary."), t => t.StartsWith("The text is cut here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B4_by_default_Teams_may_decide_both_a2a_tools_and_the_digest_lists_both()
    {
        using var db = new TempDb();
        using var teams = new TempDb();
        var host = BuildHost(db);
        var bot = BuildBot(host, teams);   // ChatMaxRisk defaults to ExternalEffect
        var submit = await QueueSubmit(host, "Short goal.", "task-b4");
        var reply = await QueueReply(host, "Short reply.", "task-b4");

        Assert.Contains("approve", VerbsOf(bot.FullCard(submit, ConvKey)));   // risk Write
        Assert.Contains("approve", VerbsOf(bot.FullCard(reply, ConvKey)));    // risk ExternalEffect: allowed by the default
        var digest = bot.DigestCard([submit, reply], ConvKey);
        Assert.Contains(TextsOf(digest), t => t.Contains("(ExternalEffect)", StringComparison.Ordinal));
        var reviewButtons = digest["actions"]!.AsArray().Select(a => a!["title"]!.GetValue<string>()).ToList();
        Assert.Contains(reviewButtons, t => t.StartsWith("Review: a2a.submit", StringComparison.Ordinal));
        Assert.Contains(reviewButtons, t => t.StartsWith("Review: a2a.reply", StringComparison.Ordinal));
        // The waiting list is chosen by the review rules, not by chat risk: both are waiting for this reviewer.
        foreach (var action in new[] { submit, reply })
            Assert.True(ReviewAuthority.Check(action, ReviewDecision.Reject, "teams:11111111-aaaa-4aaa-8aaa-111111111111", [], host.Policy.People).Allowed);
    }

    [Fact]
    public async Task B4_with_ChatMaxRisk_Write_Teams_cannot_decide_the_reply_and_says_to_use_the_review_page()
    {
        using var db = new TempDb();
        using var teams = new TempDb();
        var host = BuildHost(db);
        var bot = BuildBot(host, teams, ToolRisk.Write);
        var submit = await QueueSubmit(host, "Short goal.", "task-b4w");
        var reply = await QueueReply(host, "Short reply.", "task-b4w");

        Assert.Contains("approve", VerbsOf(bot.FullCard(submit, ConvKey)));
        var replyCard = bot.FullCard(reply, ConvKey);
        Assert.DoesNotContain("approve", VerbsOf(replyCard));
        Assert.Contains(TextsOf(replyCard), t => t == "Decide this on the review page (Teams may decide up to Write).");
    }
}
