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
    private const string DefaultRoute = "\"queued\", \"decided\"";
    private const string PersonalReply = "I post here what your AI prepares for your approval. Approve, change or reject it from the card.";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("teams-bot-test-signing-key-0123456789abcdef"));

    private sealed class FakeConnector
    {
        public readonly List<(string Method, string Path, JsonNode Body, string? Auth)> Calls = [];
        private int _next;
        public bool FailUpdates { get; set; }
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
            if (FailUpdates && ctx.Request.Method == "PUT") { ctx.Response.StatusCode = StatusCodes.Status500InternalServerError; return; }
            lock (Calls) Calls.Add((ctx.Request.Method, ctx.Request.Path.Value!, JsonNode.Parse(body)!, ctx.Request.Headers.Authorization));
            await ctx.Response.WriteAsJsonAsync(new { id = "activity-" + Interlocked.Increment(ref _next) });
        }

        public JsonNode LastCard() => Calls.Last().Body["attachments"]![0]!["content"]!;
    }

    /// <summary>A module with two tools that the tests need: one with no preview, and one whose preview refuses some text.</summary>
    private sealed class OrdersModule : IHostModule
    {
        public string Id => "orders";
        public List<string> Placed { get; } = [];
        public List<string> Noted { get; } = [];

        public void Register(IToolRegistry registry)
        {
            registry.Add(
                new ToolDescriptor("notes.write", "Write a note.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string text) => { lock (Noted) Noted.Add(text); return "noted"; }, "notes.write", "Write a note."));
            registry.Add(
                new ToolDescriptor("orders.place", "Place an order.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string body) => { lock (Placed) Placed.Add(body); return "placed"; }, "orders.place", "Place an order."),
                new OrderPreview());
        }
    }

    private sealed class OrderPreview : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var body = JsonNode.Parse(argumentsJson)?["body"]?.GetValue<string>() ?? "";
            if (body.Contains("forbidden", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the order text is forbidden");
            return Task.FromResult(new PreviewResult($"Place an order: {body}"));
        }
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
        string mailPolicy = """{ "Mode": "Queue" }""", string route = DefaultRoute, string target = "requester",
        string aliceTeamsId = Alice, string extraTools = "", IHostModule? extra = null, string reviewerKey = Alice)
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_BOT_SECRET", "bot-client-secret-value");
        _connector = await FakeConnector.StartAsync();
        _db = new TempDb();
        _bot = new TeamsBot(new TeamsBotOptions
        {
            AppId = AppId, TenantId = Tenant, AppPasswordEnv = "OTAIL_TEST_BOT_SECRET", TokenEndpoint = _connector.Server.Origin + "/token",
            ChatMaxRisk = chatMaxRisk, DigestThreshold = digest,
            // The JSON loader builds this dictionary with the default comparer, so the harness does too.
            Reviewers = aliceMayDecide ? new() { [reviewerKey] = new TeamsReviewer { Name = "alice" } } : new(),
            ConfigureValidation = p => { p.IssuerSigningKey = Key; p.ValidAlgorithms = [SecurityAlgorithms.HmacSha256]; },
        }, _db.Path, "https://approvals.example");
        _notifier = Notifier.Create(NotificationConfig.Parse($$"""
            { "Channels": [ { "Name": "bot", "Type": "teams-bot", "Target": "{{target}}" } ],
              "Routes": [ { "On": [ {{route}} ], "To": [ "bot" ] } ] }
            """), _db.Path, botChannel: _bot.Channel);
        _mail = new FakeInboxModule();
        var builder = new OpenTailHostBuilder().AddModule(_mail).UseHttpSignIn().UseSqlite(_db.Path).UseEventSink(_notifier)
            .UsePolicy(PolicyConfig.Parse($$"""
                { "People": { "alice": [ "key:alice-agent", "teams:{{aliceTeamsId}}" ] }, "SessionlessTainted": false, "Tools": { "mail.send": {{mailPolicy}}{{extraTools}} } }
                """));
        if (extra is not null) builder.AddModule(extra);
        _host = builder.Build();
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

    private JsonObject Activity(string type, string user, string conversation = "a:conv-alice", string tenant = Tenant, JsonObject? value = null,
        string? name = null, string conversationType = "personal", string? text = null)
    {
        var a = new JsonObject
        {
            ["type"] = type, ["serviceUrl"] = _connector.Server.Origin,
            ["from"] = new JsonObject { ["id"] = "29:" + user, ["aadObjectId"] = user },
            ["conversation"] = new JsonObject { ["id"] = conversation, ["conversationType"] = conversationType, ["tenantId"] = tenant },
            ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = tenant } },
        };
        if (name is not null) a["name"] = name;
        if (value is not null) a["value"] = value;
        if (text is not null) a["text"] = text;
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

    private async Task<ActionRecord> QueueTool(string tool, string argumentsJson)
    {
        var r = await _host.Queue.SubmitAsync(new ActionRequest(tool, argumentsJson, new ActionRequester(RequesterKind.AiSession, "key:alice-agent")));
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
        Assert.Equal(["approve", "edit", "request_changes", "reject"], card["actions"]!.AsArray().Where(a => a!["verb"] is not null).Select(a => a!["verb"]!.GetValue<string>()).ToArray());
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

    // ---- A1, A2: the risk order, and a tool with no registered risk ----

    [Fact]
    public async Task Destructive_actions_show_only_open_under_the_default_chat_limit()
    {
        await StartAsync(extraTools: ", \"files.delete\": { \"Mode\": \"Queue\" }");
        await InstallForAlice();
        await QueueTool("files.delete", "{\"path\":\"old.txt\"}");
        var card = _connector.LastCard();
        Assert.DoesNotContain("\"verb\":\"approve\"", card.ToJsonString());
        Assert.DoesNotContain("\"verb\":\"reject\"", card.ToJsonString());
        Assert.Contains("Decide this on the review page", card.ToJsonString());
    }

    [Fact]
    public async Task Destructive_actions_are_decidable_from_chat_only_when_the_operator_allows_destructive()
    {
        await StartAsync(chatMaxRisk: ToolRisk.Destructive, extraTools: ", \"files.delete\": { \"Mode\": \"Queue\" }");
        await InstallForAlice();
        await QueueTool("files.delete", "{\"path\":\"old.txt\"}");
        Assert.Contains("\"verb\":\"approve\"", _connector.LastCard().ToJsonString());
    }

    [Fact]
    public async Task A_tool_with_no_registered_risk_is_not_decidable_from_chat()
    {
        await StartAsync();
        var ghost = new ActionRecord
        {
            Id = "act-ghost", ToolName = "ghost.tool", ModuleId = "gone", ArgumentsJson = "{\"text\":\"x\"}", ArgumentsHash = "h",
            Requester = new ActionRequester(RequesterKind.AiSession, "key:alice-agent"), Status = ActionStatus.Pending,
            PolicyMode = ToolMode.Queue, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var card = _bot.FullCard(ghost, "user:" + Alice).ToJsonString();
        Assert.DoesNotContain("\"verb\":\"approve\"", card);
        Assert.Contains("Decide this on the review page", card);
    }

    // ---- A3: a decision or a stop on the page refreshes the card, with the route the guide documents ----

    [Fact]
    public async Task With_the_documented_route_a_stop_on_the_page_refreshes_the_card()
    {
        await StartAsync(route: "\"queued\", \"reminder\", \"approved\", \"rejected\", \"changes_requested\", \"succeeded\", \"failed\", \"expired\", \"cancelled\"",
            mailPolicy: """{ "Mode": "Execute", "DelayFor": "10m" }""");
        await InstallForAlice();
        var pending = await QueueMail();
        await _host.Queue.StopAsync(pending.Id, TeamsBotOptions.SubjectOf(Alice), [], default);
        await _notifier.DispatchOnceAsync();
        var update = _connector.Calls.Last(c => c.Method == "PUT");
        Assert.Contains("Stopped", update.Body.ToJsonString());
        Assert.Empty(_mail.Sent);
    }

    // ---- A4: object ids are case-insensitive everywhere the bot uses them ----

    [Fact]
    public async Task A_mixed_case_teams_id_in_people_still_delivers_the_card()
    {
        await StartAsync(aliceTeamsId: Alice.ToUpperInvariant());
        await InstallForAlice();
        await QueueMail();
        var post = _connector.Calls.Single(c => c.Method == "POST" && c.Path.Contains("a:conv-alice"));
        Assert.Contains("Approval needed", post.Body.ToJsonString());
    }

    [Fact]
    public async Task A_mixed_case_reviewer_key_in_the_config_still_lets_that_reviewer_decide()
    {
        await StartAsync(reviewerKey: Alice.ToUpperInvariant());
        await InstallForAlice();
        var pending = await QueueMail();
        var response = await Click(_connector.LastCard(), "approve");
        Assert.Contains("Approve by alice", response["value"]!.ToJsonString());
        Assert.Equal(ActionStatus.Succeeded, (await _host.Store.GetAsync(pending.Id))!.Status);
    }

    [Fact]
    public async Task A_mixed_case_teams_id_in_people_still_blocks_self_approval()
    {
        await StartAsync(aliceTeamsId: Alice.ToUpperInvariant(), mailPolicy: """{ "Mode": "Queue", "SelfApproval": "Forbid" }""");
        await InstallForAlice();
        await QueueMail();
        var response = await Click(_connector.LastCard(), "approve");
        Assert.Contains("cannot approve your own request", response.ToJsonString());
        Assert.Empty(_mail.Sent);
    }

    // ---- A5: back passes the same signature check as every other verb ----

    [Fact]
    public async Task A_back_click_without_a_valid_signature_is_refused()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail();
        var forged = await Post(Activity("invoke", Alice, name: "adaptiveCard/action", value: new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "Action.Execute", ["verb"] = "back", ["data"] = new JsonObject() },
        }));
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        Assert.Contains("not valid here", await forged.Content.ReadAsStringAsync());
    }

    // ---- A6: edits that name a field not in the draft are refused; a tool with no preview says so ----

    [Fact]
    public async Task An_edit_naming_a_field_that_is_not_in_the_draft_is_refused()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail();
        var response = await Click(_connector.LastCard(), "edit", inputs: d => d["edit.nope"] = "injected");
        Assert.Contains("not in this draft", response.ToJsonString());
        Assert.Empty(_mail.Sent);
        Assert.Equal(ActionStatus.Pending, (await _host.Store.GetAsync(pending.Id))!.Status);
    }

    [Fact]
    public async Task Changes_to_a_tool_without_a_preview_say_so_on_the_card()
    {
        await StartAsync(extra: new OrdersModule(), extraTools: ", \"notes.write\": { \"Mode\": \"Queue\" }");
        await InstallForAlice();
        await QueueTool("notes.write", "{\"text\":\"hello\"}");
        Assert.Contains("not checked by the tool", _connector.LastCard().ToJsonString());
    }

    // ---- A7: a failed card refresh is recorded, not swallowed ----

    [Fact]
    public async Task A_failed_card_refresh_is_recorded_in_the_audit_trail()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail();
        _connector.FailUpdates = true;
        await _host.Queue.ReviewAsync(pending.Id, new ActionReview("rita", ReviewDecision.Reject, "Not now", DateTimeOffset.UtcNow));
        await _notifier.DispatchOnceAsync();
        var audit = ((SqliteAuditSink)_host.Audit).ReadChain();
        Assert.Contains(audit, e => e.Event == "teams_refresh_failed" && e.ActionId == pending.Id);
    }

    // ---- A10: paths the inventory lists as untested ----

    [Fact]
    public async Task A_conversation_target_posts_the_card_to_that_channel()
    {
        await StartAsync(target: "conversation:channel:19:chan");
        Assert.Equal(HttpStatusCode.OK, (await Post(Activity("message", Alice, conversation: "19:chan;messageid=1", conversationType: "channel"))).StatusCode);
        await QueueMail();
        var post = _connector.Calls.Single(c => c.Method == "POST");
        Assert.Contains("19:chan", post.Path);
        Assert.Contains("Approval needed", post.Body.ToJsonString());
    }

    [Fact]
    public async Task Run_now_on_a_delayed_action_runs_it_at_once()
    {
        await StartAsync(mailPolicy: """{ "Mode": "Execute", "DelayFor": "10m" }""");
        await InstallForAlice();
        var pending = await QueueMail();
        var card = _connector.LastCard();
        Assert.Contains("Going out soon", card.ToJsonString());
        await Click(card, "approve");
        Assert.Equal(ActionStatus.Succeeded, (await _host.Store.GetAsync(pending.Id))!.Status);
        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task An_edit_refused_by_a_preview_is_refused_and_a_valid_edit_runs()
    {
        var orders = new OrdersModule();
        await StartAsync(extra: orders, extraTools: ", \"orders.place\": { \"Mode\": \"Queue\" }");
        await InstallForAlice();
        var pending = await QueueTool("orders.place", "{\"body\":\"ok\"}");
        var refused = await Click(_connector.LastCard(), "edit", inputs: d => d["edit.body"] = "this is forbidden");
        Assert.Contains("not valid", refused.ToJsonString());
        Assert.Empty(orders.Placed);
        Assert.Equal(ActionStatus.Pending, (await _host.Store.GetAsync(pending.Id))!.Status);

        await Click(_connector.LastCard(), "edit", inputs: d => d["edit.body"] = "fine");
        Assert.Equal(["fine"], orders.Placed);
    }

    [Fact]
    public async Task A_missing_bot_secret_refuses_to_start()
    {
        Environment.SetEnvironmentVariable("OTAIL_TEST_UNSET_SECRET", null);
        using var db = new TempDb();
        var refused = Assert.Throws<InvalidOperationException>(() => new TeamsBot(
            new TeamsBotOptions { AppId = AppId, TenantId = Tenant, AppPasswordEnv = "OTAIL_TEST_UNSET_SECRET" }, db.Path, null));
        Assert.Contains("not available", refused.Message);
    }

    [Fact]
    public void TeamsStore_keeps_conversations_cards_and_digests_across_a_restart()
    {
        using var db = new TempDb();
        var store = new TeamsStore(db.Path);
        store.Remember(new TeamsStore.Conversation("user:x", "https://svc.example/", "a:conv-x", "personal"));
        store.Card("act-1", "user:x", "activity-1");
        store.Digest("user:x", "digest-1");

        var reopened = new TeamsStore(db.Path);                                  // a restart
        Assert.Equal("a:conv-x", reopened.Find("user:x")!.ConversationId);
        Assert.Null(reopened.Find("user:nobody"));
        Assert.Equal("activity-1", Assert.Single(reopened.CardsOf("act-1")).ActivityId);
        Assert.Equal("digest-1", reopened.Digest("user:x"));
        reopened.Digest("user:x", null);
        Assert.Null(reopened.Digest("user:x"));
    }

    // ---- B1: request changes from chat ----

    [Fact]
    public async Task Request_changes_from_chat_needs_a_reason_and_sends_the_action_back()
    {
        await StartAsync();
        await InstallForAlice();
        var pending = await QueueMail();
        var card = _connector.LastCard();
        var noReason = await Click(card, "request_changes");
        Assert.Contains("Type a reason first", noReason.ToJsonString());

        var response = await Click(card, "request_changes", inputs: d => d["reason"] = "Add the invoice number");
        Assert.Contains("Changes requested", response["value"]!.ToJsonString());
        Assert.Equal(ActionStatus.ChangesRequested, (await _host.Store.GetAsync(pending.Id))!.Status);
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Request_changes_is_offered_only_where_reject_is_allowed()
    {
        await StartAsync(chatMaxRisk: ToolRisk.Write);
        await InstallForAlice();
        await QueueMail();
        Assert.DoesNotContain("\"verb\":\"request_changes\"", _connector.LastCard().ToJsonString());
    }

    // ---- B2: typed commands, for listed reviewers only ----

    [Fact]
    public async Task Waiting_lists_what_this_reviewer_may_decide_oldest_first()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail("one");
        await QueueMail("two");
        await Post(Activity("message", Alice, text: "waiting"));
        Assert.Contains("2 actions are waiting for you", _connector.LastCard().ToJsonString());
    }

    [Fact]
    public async Task Waiting_includes_an_action_that_never_had_a_card_in_this_chat()
    {
        await StartAsync();
        await QueueMail("early");                                                  // no chat yet, so no card can be posted
        await InstallForAlice();
        await Post(Activity("message", Alice, text: "waiting"));
        Assert.Contains("1 action is waiting for you", _connector.LastCard().ToJsonString());
    }

    [Fact]
    public async Task Waiting_with_nothing_waiting_says_so()
    {
        await StartAsync();
        await InstallForAlice();
        await Post(Activity("message", Alice, text: "waiting"));
        Assert.Contains("Nothing is waiting for you.", _connector.Calls.Last().Body.ToJsonString());
    }

    [Fact]
    public async Task Commands_are_exact_words_ignoring_case_and_free_text_never_becomes_an_action()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail();

        await Post(Activity("message", Alice, text: "  HELP  "));
        Assert.Contains("Commands", _connector.Calls.Last().Body.ToJsonString());

        await Post(Activity("message", Alice, text: "approve"));
        Assert.Contains("Type help", _connector.Calls.Last().Body.ToJsonString());
        Assert.Empty(_mail.Sent);

        await Post(Activity("message", Alice, text: "Waiting please"));
        Assert.DoesNotContain("are waiting for you", _connector.Calls.Last().Body.ToJsonString());
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task A_person_who_is_not_a_reviewer_gets_the_fixed_reply_only()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail();
        await Post(Activity("message", Carol, conversation: "a:conv-carol", text: "waiting"));
        Assert.Equal(PersonalReply, _connector.Calls.Last().Body["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Waiting_cards_are_signed_for_the_chat_they_were_sent_to()
    {
        await StartAsync();
        await InstallForAlice();
        await QueueMail();
        await Post(Activity("message", Alice, text: "waiting"));
        var waiting = _connector.LastCard();
        var elsewhere = await Click(waiting, "open", user: Carol, conversation: "a:conv-carol");
        Assert.Contains("not valid here", elsewhere.ToJsonString());
    }
}
