using System.Text.Json.Nodes;
using OpenTail.Orca.Notifications;

namespace OpenTail.Orca.Tests;

/// <summary>v5 V3 Tier 1: a Teams channel (Workflows webhook) posts an Adaptive Card with an Open button to the review page; nothing is
/// approved from it, no argument value appears in it, and model-written text cannot inject links or markup.</summary>
public class TeamsNotificationTests
{
    private static readonly Func<string, string?> Env = n => n == "TEAMS_URL" ? "https://prod-01.westeurope.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=x" : null;

    private const string Config = """
        { "PublicBaseUrl": "https://approvals.corp.example",
          "Channels": [ { "Name": "team", "Type": "teams", "UrlEnv": "TEAMS_URL" } ],
          "Routes": [ { "On": ["queued"], "To": ["team"], "IncludePreviewSummary": true } ] }
        """;

    private static async Task<(JsonNode Body, ActionRecord Action)> QueueAndSend(string to)
    {
        using var db = new TempDb();
        var handler = new ScriptedHandler();
        using var notifier = Notifier.Create(NotificationConfig.Parse(Config), db.Path, null, null, Env, handler);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).UseEventSink(notifier).Build();
        notifier.Attach(host.Store, t => host.Catalog.TryGet(t, out var r) ? r.Descriptor.Risk : null);
        var action = await host.Queue.SubmitAsync(new ActionRequest("mail.send", $$"""{"to":"{{to}}","body":"TOPSECRET-BODY"}""",
            new ActionRequester(RequesterKind.User, "key:alice"), host.Sessions.Start()));
        Assert.Equal(1, await notifier.DispatchOnceAsync());
        var sent = Assert.Single(handler.Requests);
        Assert.StartsWith("https://prod-01.westeurope.logic.azure.com/", sent.Url.AbsoluteUri);
        return (JsonNode.Parse(sent.Body)!, action);
    }

    [Fact]
    public async Task A_queued_action_becomes_an_adaptive_card_with_only_an_open_button()
    {
        var (body, action) = await QueueAndSend("jane@corp.example");
        Assert.Equal("message", body["type"]!.GetValue<string>());
        var attachment = body["attachments"]![0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment["contentType"]!.GetValue<string>());
        var card = attachment["content"]!;
        Assert.Equal("AdaptiveCard", card["type"]!.GetValue<string>());
        var action0 = Assert.Single(card["actions"]!.AsArray())!;
        Assert.Equal("Action.OpenUrl", action0["type"]!.GetValue<string>());   // no Action.Submit / Execute: Teams never calls the Host
        Assert.Equal($"https://approvals.corp.example/#{action.Id}", action0["url"]!.GetValue<string>());
        var text = string.Join("\n", card["body"]!.AsArray().Select(b => b!["text"]!.GetValue<string>()));
        Assert.Contains("Approval needed", text);
        Assert.Contains("mail.send", text);
        Assert.Contains("Reaches other people.", text);
        Assert.DoesNotContain("TOPSECRET", body.ToJsonString());               // argument values never travel in a notification
    }

    [Fact]
    public async Task Model_written_text_cannot_inject_a_link_or_markup_into_the_card()
    {
        var (body, _) = await QueueAndSend("[Approve here](https://evil.example)**@corp.example");
        var text = string.Join("\n", body["attachments"]![0]!["content"]!["body"]!.AsArray().Select(b => b!["text"]!.GetValue<string>()));
        Assert.DoesNotContain("](", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("https://evil", text, StringComparison.Ordinal);   // ordinal: culture comparison ignores the zero-width space
        Assert.Contains("evil.example", text.Replace("​", ""));             // still readable, no longer a link
        Assert.Equal("‹a› b 'c' :​// www​.x", TeamsChannel.Escape("[a] b *c* :// www.x"));
    }

    [Fact]
    public void A_teams_channel_is_configured_like_slack_and_refuses_a_signing_secret()
    {
        var errors = NotificationConfig.Parse("""{ "Channels": [ { "Name": "t", "Type": "teams", "UrlEnv": "TEAMS_URL", "SecretEnv": "X" } ] }""").Validate(Env);
        Assert.Contains(errors, e => e.Contains("does not use SecretEnv"));
        Assert.Empty(NotificationConfig.Parse("""{ "Channels": [ { "Name": "t", "Type": "teams", "UrlEnv": "TEAMS_URL" } ], "Routes": [ { "On": ["queued"], "To": ["t"] } ] }""").Validate(Env));
    }

    [Fact]
    public void The_review_page_opens_the_action_named_in_the_link_and_only_after_sign_in()
    {
        var html = typeof(OpenTail.Orca.Admin.AdminServer).Assembly.GetType("OpenTail.Orca.Admin.AdminPage")!
            .GetField("Html")!.GetValue(null) as string;
        Assert.Contains("location.hash", html);
        Assert.Contains("Sign in to open the action from your link.", html);
        Assert.Contains("/^[A-Za-z0-9_-]{1,80}$/", html);                       // the link can only carry an action id
    }
}
