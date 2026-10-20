using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenTail.Host.Admin;
using OpenTail.Host.Http;
using OpenTail.Host.Http.Teams;
using OpenTail.Host.Notifications;

namespace OpenTail.Host.Tests;

/// <summary>
/// v5 V3 Tier 2: the Teams bot against a fake Bot Connector, with Bot Framework tokens signed by a test key. The person whose AI prepared
/// a mail gets a card in their own chat and approves it, or changes the text and approves; forged, replayed, foreign and over-risk clicks
/// are refused; many waiting actions become one digest; a decision made elsewhere updates the card.
/// </summary>
public class TeamsBotTests : IAsyncLifetime
{
    private const string AppId = "bot-app-id";
    private const string Tenant = "tenant-1";
    private const string Alice = "11111111-aaaa-4aaa-8aaa-111111111111";
    private const string Carol = "33333333-cccc-4ccc-8ccc-333333333333";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("teams-bot-test-signing-key-0123456789abcdef"));

    private sealed class FakeConnector
    {
        public readonly List<(string Method, string Path, JsonNode Body, string? Auth)> Calls = [];
        private int _next;
        public StubServer Server { get; private set; } = null!;

        public static async Task<FakeConnector> StartAsync()
        {
            var f = new FakeConnector();
            f.Server = await StubServer.StartAsync(f.HandleAsync);
            return f;
        }

        private async Task HandleAsync(HttpContext ctx)
        {
            var body = (string)ctx.Items["body"]!;
            if (ctx.Request.Path == "/token") { await ctx.Response.WriteAsJsonAsync(new { access_token = "bot-access-token", expires_in = 3600 }); return; }
            lock (Calls) Calls.Add((ctx.Request.Method, ctx.Request.Path.Value!, JsonNode.Parse(body)!, ctx.Request.Headers.Authorization));
            await ctx.Response.WriteAsJsonAsync(new { id = "activity-" + Interlocked.Increment(ref _next) });
        }

        public JsonNode LastCard() => Calls.Last().Body["attachments"]![0]!["content"]!;
    }

    private FakeConnector _connector = null!;
    private TempDb _db = null!;
    private OpenTailHost _host = null!;
    private FakeInboxModule _mail = null!;
    private TeamsBot _bot = null!;
    private Notifier _notifier = null!;
    private RunningHostHttp _server = null!;
    private HttpClient _http = null!;

    private async Task StartAsync(ToolRisk chatMaxRisk = ToolRisk.ExternalEffect, int digest = 3, bool aliceMayDecide = true,
        string mailPolicy = """{ "Mode": "Queue" }""")
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_BOT_SECRET", "bot-client-secret-value");
        _connector = await FakeConnector.StartAsync();
        _db = new TempDb();
        _bot = new TeamsBot(new TeamsBotOptions
        {
            AppId = AppId, TenantId = Tenant, AppPasswordEnv = "OTAIL_TEST_BOT_SECRET", TokenEndpoint = _connector.Server.Origin + "/token",
            ChatMaxRisk = chatMaxRisk, DigestThreshold = digest,
            Reviewers = aliceMayDecide ? new(StringComparer.OrdinalIgnoreCase) { [Alice] = new TeamsReviewer { Name = "alice" } } : new(StringComparer.OrdinalIgnoreCase),
            ConfigureValidation = p => { p.IssuerSigningKey = Key; p.ValidAlgorithms = [SecurityAlgorithms.HmacSha256]; },
        }, _db.Path, "https://approvals.example");
        _notifier = Notifier.Create(NotificationConfig.Parse("""
            { "Channels": [ { "Name": "bot", "Type": "teams-bot", "Target": "requester" } ],
              "Routes": [ { "On": [ "queued", "decided" ], "To": [ "bot" ] } ] }
            """), _db.Path, botChannel: _bot.Channel);
        _mail = new FakeInboxModule();
        _host = new OpenTailHostBuilder().AddModule(_mail).UseHttpSignIn().UseSqlite(_db.Path).UseEventSink(_notifier)
            .UsePolicy(PolicyConfig.Parse($$"""
                { "People": { "alice": [ "key:alice-agent", "teams:{{Alice}}" ] }, "SessionlessTainted": false, "Tools": { "mail.send": {{mailPolicy}} } }
                """)).Build();
        _notifier.Attach(_host.Store, t => _host.Catalog.TryGet(t, out var r) ? r.Descriptor.Risk : null);
        _server = await HostHttp.StartAsync(_host, new HostHttpOptions { TeamsBot = _bot, Admin = new AdminOptions() });
        _http = new HttpClient { BaseAddress = _server.BaseUri };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null) await _server.DisposeAsync();
        _notifier?.Dispose();
        if (_connector is not null) await _connector.Server.DisposeAsync();
        _db?.Dispose();
    }

    private string BotToken(string? audience = null, string? serviceUrl = null) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "https://api.botframework.com", Audience = audience ?? AppId, Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = new Dictionary<string, object> { ["serviceurl"] = serviceUrl ?? _connector.Server.Origin },
        SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
    });

    private JsonObject Activity(string type, string user, string conversation = "a:conv-alice", string tenant = Tenant, JsonObject? value = null, string? name = null)
    {
        var a = new JsonObject
        {
            ["type"] = type, ["serviceUrl"] = _connector.Server.Origin,
            ["from"] = new JsonObject { ["id"] = "29:" + user, ["aadObjectId"] = user },
            ["conversation"] = new JsonObject { ["id"] = conversation, ["conversationType"] = "personal", ["tenantId"] = tenant },
            ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = tenant } },
        };
        if (name is not null) a["name"] = name;
        if (value is not null) a["value"] = value;
        return a;
    }

    private async Task<HttpResponseMessage> Post(JsonObject activity, string? token = "valid")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/teams/messages") { Content = JsonContent.Create(activity) };
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (token == "valid" ? BotToken() : token));
        return await _http.SendAsync(req);
    }

    private async Task<JsonNode> Click(JsonNode card, string verb, string user = Alice, string conversation = "a:conv-alice", Action<JsonObject>? inputs = null)
    {
        var action = card["actions"]!.AsArray().First(a => a!["verb"]?.GetValue<string>() == verb)!;
        var data = action["data"]!.DeepClone().AsObject();
        inputs?.Invoke(data);
        var resp = await Post(Activity("invoke", user, conversation, name: "adaptiveCard/action",
            value: new JsonObject { ["action"] = new JsonObject { ["type"] = "Action.Execute", ["verb"] = verb, ["data"] = data } }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    private async Task<ActionRecord> QueueMail(string body = "Thanks, the invoice is paid.")
    {
        var r = await _host.Queue.SubmitAsync(new ActionRequest("mail.send", System.Text.Json.JsonSerializer.Serialize(new { to = "jane@corp.example", body }),
            new ActionRequester(RequesterKind.AiSession, "key:alice-agent")));
        await _notifier.DispatchOnceAsync();
        return r;
    }

    private async Task InstallForAlice() => Assert.Equal(HttpStatusCode.OK, (await Post(Activity("installationUpdate", Alice))).StatusCode);

    [Fact]
    public async Task The_mail_an_AI_prepared_arrives_in_its_owners_chat_and_is_approved_from_the_card()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail();
        Assert.Equal(ActionStatus.Pending, pending.Status);

        var post = _connector.Calls.Single(c => c.Method == "POST");
        Assert.Equal("/v3/conversations/a:conv-alice/activities", post.Path);
        Assert.Equal("Bearer bot-access-token", post.Auth);
        var card = _connector.LastCard();
        var text = card.ToJsonString();
        Assert.Contains("Approval needed", text);
        Assert.Contains("Reaches other people.", text);
        Assert.Equal(["approve", "edit", "reject"], card["actions"]!.AsArray().Where(a => a!["verb"] is not null).Select(a => a!["verb"]!.GetValue<string>()).ToArray());
        Assert.Contains(card["actions"]!.AsArray(), a => a!["type"]!.GetValue<string>() == "Action.OpenUrl" && a["url"]!.GetValue<string>() == $"https://approvals.example/#{pending.Id}");
        Assert.Contains(card["body"]!.AsArray(), b => b!["id"]?.GetValue<string>() == "edit.body");

        var response = await Click(card, "approve");
        Assert.Contains("Approve by alice", response["value"]!.ToJsonString());
        var done = (await _host.Store.GetAsync(pending.Id))!;
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal($"teams:{Alice}", done.Reviews.Single().Subject);
        Assert.Equal(("jane@corp.example", "Thanks, the invoice is paid."), Assert.Single(_mail.Sent));
    }

    [Fact]
    public async Task Approve_with_changes_sends_the_text_the_reviewer_corrected()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail("Thanks, teh invoice is paid.");
        await Click(_connector.LastCard(), "edit", inputs: d => d["edit.body"] = "Thanks, the invoice is paid. Best, Alice");
        Assert.Equal(("jane@corp.example", "Thanks, the invoice is paid. Best, Alice"), Assert.Single(_mail.Sent));
        var done = (await _host.Store.GetAsync(pending.Id))!;
        Assert.NotNull(done.Reviews.Single().EditedArgumentsJson);
        Assert.Contains("teh invoice", done.ArgumentsJson);                    // what the AI submitted is kept as it was
    }

    [Fact]
    public async Task Forged_foreign_replayed_and_unlisted_clicks_are_refused()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail();
        var card = _connector.LastCard();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Activity("message", Alice), token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Activity("message", Alice), token: BotToken(audience: "another-bot"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Activity("message", Alice), token: BotToken(serviceUrl: "https://evil.example/"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Activity("message", Alice, tenant: "other-tenant"))).StatusCode);

        var elsewhere = await Click(card, "approve", user: Carol, conversation: "a:conv-carol");    // the card's signature is for Alice's chat
        Assert.Contains("not valid here", elsewhere.ToJsonString());
        var sameChatOtherUser = await Click(card, "approve", user: Carol);                         // a personal chat card is Alice's alone
        Assert.Contains("not valid here", sameChatOtherUser.ToJsonString());

        var noReason = await Click(card, "reject");
        Assert.Contains("Type a reason first", noReason.ToJsonString());
        Assert.Empty(_mail.Sent);

        await Click(card, "reject", inputs: d => d["reason"] = "Wrong customer");
        var again = await Click(card, "approve");                                                  // a second click on a decided card
        Assert.DoesNotContain("Approve by alice", again.ToJsonString());
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Someone_who_is_not_listed_as_a_reviewer_cannot_decide_even_in_their_own_chat()
    {
        await StartAsync(aliceMayDecide: false);
        await InstallForAlice();
        await QueueMail();
        var response = await Click(_connector.LastCard(), "approve");
        Assert.Contains("not set up to decide from Teams", response.ToJsonString());
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Risks_above_what_chat_may_decide_show_only_open()
    {
        await StartAsync(chatMaxRisk: ToolRisk.Write);
        await InstallForAlice();
        await QueueMail();
        var card = _connector.LastCard();
        Assert.DoesNotContain(card["actions"]!.AsArray(), a => a!["verb"] is not null);
        Assert.Contains("Decide this on the review page", card.ToJsonString());
    }

    [Fact]
    public async Task Many_waiting_actions_become_one_digest_that_opens_each_one()
    {
        await StartAsync(digest: 2);
        await InstallForAlice();
        await QueueMail("one");
        await QueueMail("two");
        var third = await QueueMail("three");
        var digestPost = _connector.Calls.Last();
        Assert.Equal("POST", digestPost.Method);
        var digest = _connector.LastCard();
        Assert.Contains("actions are waiting for you", digest.ToJsonString());

        var opened = await Click(digest, "open");
        Assert.Contains("Approval needed", opened["value"]!.ToJsonString());
        var full = opened["value"]!;
        Assert.Contains(full["actions"]!.AsArray(), a => a!["verb"]?.GetValue<string>() == "back");

        await QueueMail("four");                                                                   // the digest is updated, not posted again
        Assert.Equal("PUT", _connector.Calls.Last().Method);
        Assert.NotNull(third);
    }

    [Fact]
    public async Task A_decision_on_the_review_page_updates_the_card_in_teams()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail();
        await _host.Queue.ReviewAsync(pending.Id, new ActionReview("rita", ReviewDecision.Reject, "Not now", DateTimeOffset.UtcNow));
        await _notifier.DispatchOnceAsync();
        var update = _connector.Calls.Last(c => c.Method == "PUT");
        Assert.EndsWith("/activities/activity-1", update.Path);
        Assert.Contains("Reject by rita", update.Body.ToJsonString());
        Assert.DoesNotContain("\"verb\":\"approve\"", update.Body.ToJsonString());
    }

    [Fact]
    public async Task A_delayed_action_can_be_stopped_from_the_card()
    {
        await StartAsync(mailPolicy: """{ "Mode": "Execute", "DelayFor": "10m" }""");
        await InstallForAlice();
        var pending = await QueueMail();
        Assert.NotNull(pending.RunAt);
        var card = _connector.LastCard();
        Assert.Contains("Going out soon", card.ToJsonString());
        Assert.Equal(["stop", "approve"], card["actions"]!.AsArray().Where(a => a!["verb"] is not null).Select(a => a!["verb"]!.GetValue<string>()).ToArray());

        var response = await Click(card, "stop");
        Assert.Contains("Stopped", response["value"]!.ToJsonString());
        Assert.Equal(ActionStatus.Cancelled, (await _host.Store.GetAsync(pending.Id))!.Status);
        Assert.Empty(_mail.Sent);
    }
}
