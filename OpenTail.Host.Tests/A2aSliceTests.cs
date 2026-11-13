using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.Data.Sqlite;
using OpenTail.Host.A2A;
using OpenTail.Host.Http;

namespace OpenTail.Host.Tests;

/// <summary>
/// The A2A thin slice, first half (batch 03b-1): the endpoint and its sign-in, the version gate, the task table, SendMessage for new tasks,
/// GetTask and CancelTask. Each test drives the endpoint with the SDK's own client over a real socket, as docs/briefs/a2a-sdk-facts.md does.
/// </summary>
public class A2aSliceTests
{
    private const string PartnerA = "5f1c2a9e-8b7d-4c3e-9a10-2b4d6e8f0a12";
    private const string PartnerB = "0b9e4d2a-1c3f-4a5b-8d7e-6f5a4b3c2d1e";
    private const string TokenA = "acme-partner-token-0123456789";
    private const string TokenB = "beta-partner-token-0123456789";
    private const string TokenMcp = "mcp-only-token-0123456789";

    private const string Policy = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce", "Work": { "Enabled": true },
          "Tools": { "a2a.submit": { "Mode": "Queue" }, "a2a.reply": { "Mode": "Queue", "AllowEdits": false } },
          "Agents": { "clerk": { "Tools": [ "a2a.submit", "a2a.reply", "work.*" ] }, "reply-assistant": { "Tools": [ "clock.now" ] } } }
        """;

    /// <summary>The same deployment without A2A: the profiles name no tool the Host does not load.</summary>
    private const string PolicyWithoutA2a = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce",
          "Agents": { "clerk": { "Tools": [ "clock.now" ] }, "reply-assistant": { "Tools": [ "clock.now" ] } } }
        """;

    private static ApiKeyEntry[] DefaultKeys =>
    [
        new("acme-agent", ApiKeys.Hash(TokenA), ["a2a:call"], "clerk", PartnerA),
        new("beta-agent", ApiKeys.Hash(TokenB), ["a2a:call"], "clerk", PartnerB),
        new("mcp-agent", ApiKeys.Hash(TokenMcp), null, "clerk", null),
    ];

    private static A2aOptions EnabledA2a => A2aWith(new Dictionary<string, A2aSkill>
    {
        ["draft-reply"] = new A2aSkill { Name = "Draft a reply", Description = "Drafts a reply.", Inbox = "clerk" },
    });

    /// <summary>The A2A section with these skills (the section's skill map is read-only, so a test builds it whole).</summary>
    private static A2aOptions A2aWith(Dictionary<string, A2aSkill> skills) => new()
    {
        Enabled = true,
        Partners = [new A2aPartner(PartnerA, "Acme"), new A2aPartner(PartnerB, "Beta")],
        Skills = skills,
    };

    /// <summary>A running Host with the A2A front door, on a real socket. The database is the caller's, so a test can restart on it.</summary>
    private sealed class Rig(OpenTailHost host, RunningHostHttp server, string dbPath) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        public OpenTailHost Host { get; } = host;
        public RunningHostHttp Server { get; } = server;
        public string DbPath { get; } = dbPath;

        public A2ATaskTable Table => new(DbPath, ((SqliteActionStore)Host.Store).Synchronous);

        /// <summary>The SDK's client, signed in with this key.</summary>
        public A2AClient Client(string token)
        {
            var http = new HttpClient { BaseAddress = Server.BaseUri };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _clients.Add(http);
            return new A2AClient(new Uri(Server.BaseUri, "/a2a"), http);
        }

        public HttpClient Raw()
        {
            var http = new HttpClient { BaseAddress = Server.BaseUri };
            _clients.Add(http);
            return http;
        }

        public long Count(string sql)
        {
            using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var http in _clients) http.Dispose();
            await Server.DisposeAsync();
            TempDb.ClearPools(DbPath);
        }
    }

    private static async Task<Rig> StartAsync(TempDb db, string policy = Policy, A2aOptions? a2a = null, ApiKeyEntry[]? keys = null,
        int rate = 0, int maxBody = 1024 * 1024, bool extension = true, Func<TimeSpan, CancellationToken, Task>? poll = null)
    {
        var extensions = extension ? new IHttpDeploymentExtension[] { new A2AFrontDoor { PollDelay = poll } } : Array.Empty<IHttpDeploymentExtension>();
        var options = new HostHttpOptions
        {
            ApiKeys = keys ?? DefaultKeys,
            A2A = a2a ?? EnabledA2a,
            Extensions = extensions,
            RateLimitPerMinute = rate,
            MaxBodyBytes = maxBody,
            PublicBaseUrl = "http://127.0.0.1",   // the agent card names this address (batch 04, A3); loopback http is allowed
        };
        // The fake module supplies clock.now, a tool a profile can name without it being A2A.
        var builder = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        foreach (var extension_ in extensions) extension_.AddModules(builder, options);
        var host = builder.Build();
        var server = await HostHttp.StartAsync(host, options);
        return new Rig(host, server, db.Path);
    }

    private static Message Msg(string messageId, string goal, Part? extra = null) => new()
    {
        MessageId = messageId, Role = Role.User, Parts = extra is null ? [Part.FromText(goal)] : [Part.FromText(goal), extra],
    };

    /// <summary>A send. <paramref name="immediate"/> null omits the configuration entirely (the field is not sent).</summary>
    private static SendMessageRequest Send(string messageId, string goal, bool? immediate = true, string? tenant = null, Part? extra = null) => new()
    {
        Message = Msg(messageId, goal, extra),
        Tenant = tenant,
        Configuration = immediate is null ? null : new SendMessageConfiguration { ReturnImmediately = immediate.Value },
    };

    /// <summary>A JSON-RPC request body, serialised the way the SDK serialises its own requests.</summary>
    private static string Rpc(string method, object? parameters)
    {
        var node = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["method"] = method };
        if (parameters is not null) node["params"] = JsonSerializer.SerializeToNode(parameters, A2AJsonUtilities.DefaultOptions);
        return node.ToJsonString();
    }

    private static async Task<(int Status, JsonNode? Body)> PostRaw(HttpClient http, string body, IEnumerable<string>? versions, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (versions is not null)
            foreach (var v in versions) req.Headers.TryAddWithoutValidation("A2A-Version", v);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static int? ErrorCodeOf(JsonNode? body) => body?["error"]?["code"]?.GetValue<int>();

    private static List<string> Refusals(Rig rig) =>
        ((SqliteAuditSink)rig.Host.Audit).ReadChain().Where(e => e.Event == "a2a.refused").Select(e => e.Detail ?? "").ToList();

    private static async Task<ActionRecord> SubmitActionOf(Rig rig, string taskId)
    {
        var row = rig.Table.FindByTask(PartnerA, taskId) ?? throw new InvalidOperationException("no row for " + taskId);
        return (await rig.Host.Store.GetAsync(row.SubmitActionId!))!;
    }

    // ---------------------------------------------------------------------------------------------------------- the endpoint and its sign-in

    [Fact]
    public async Task With_A2A_disabled_the_endpoint_does_not_exist_and_no_tool_is_registered()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db, policy: PolicyWithoutA2a, a2a: new A2aOptions { Enabled = false }, extension: true);
        using var raw = rig.Raw();
        var (status, _) = await PostRaw(raw, Rpc("SendMessage", Send("m-off", "hi")), null, TokenA);
        Assert.Equal(404, status);
        Assert.False(rig.Host.Catalog.TryGet("a2a.submit", out _));
    }

    [Fact]
    public async Task Only_a2a_call_keys_reach_the_endpoint_and_no_credential_is_401()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var raw = rig.Raw();
        var body = Rpc("SendMessage", Send("m-scope", "hi"));
        Assert.Equal(401, (await PostRaw(raw, body, ["1.0"], null)).Status);
        Assert.Equal(403, (await PostRaw(raw, body, ["1.0"], TokenMcp)).Status);
        Assert.Equal(200, (await PostRaw(raw, body, ["1.0"], TokenA)).Status);
    }

    [Fact]
    public async Task The_body_limit_and_the_rate_limit_apply_to_the_a2a_endpoint()
    {
        using var db = new TempDb();
        await using (var big = await StartAsync(db, maxBody: 2048))
        {
            using var raw = big.Raw();
            var oversized = Rpc("SendMessage", Send("m-big", new string('x', 4000)));
            var (oversizedStatus, oversizedBody) = await PostRaw(raw, oversized, ["1.0"], TokenA);
            // The limit is Kestrel's: the SDK reads the body and turns the refused read into a JSON-RPC error (HTTP 200), so the request
            // is refused either way, and no task row is left behind.
            Assert.True(oversizedStatus == 413 || ErrorCodeOf(oversizedBody) is not null, $"{oversizedStatus} {oversizedBody?.ToJsonString()}");
            Assert.Equal(0, big.Table.Count());
        }
        using var db2 = new TempDb();
        await using var limited = await StartAsync(db2, rate: 2);
        using var rawLimited = limited.Raw();
        var small = Rpc("SendMessage", Send("m-rate", "hi"));
        Assert.Equal(200, (await PostRaw(rawLimited, small, ["1.0"], TokenA)).Status);
        Assert.Equal(200, (await PostRaw(rawLimited, small, ["1.0"], TokenA)).Status);
        Assert.Equal(429, (await PostRaw(rawLimited, small, ["1.0"], TokenA)).Status);
    }

    // ---------------------------------------------------------------------------------------------------------- A2A-Version (D6)

    [Fact]
    public async Task The_version_matrix_gives_the_outcomes_of_D6_and_a_bad_credential_still_gets_401()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var raw = rig.Raw();
        var body = Rpc("SendMessage", Send("m-version", "versions", immediate: true));

        var missing = await PostRaw(raw, body, null, TokenA);
        Assert.NotNull(missing.Body!["result"]);                                    // missing: 1.0, the SDK's default
        var one = await PostRaw(raw, body, ["1.0"], TokenA);
        Assert.NotNull(one.Body!["result"]);
        foreach (var refused in new[] { new[] { "" }, new[] { "0.3" }, new[] { "2.0" }, new[] { "1.0", "0.3" }, new[] { "1.0, 0.3" } })
        {
            var (status, answer) = await PostRaw(raw, body, refused, TokenA);
            Assert.Equal(200, status);
            Assert.Equal(-32009, ErrorCodeOf(answer));                             // VersionNotSupported, the SDK's shape
            Assert.Null(answer!["id"]);
            Assert.Null(answer!["result"]);
        }
        var doubled = await PostRaw(raw, body, ["1.0", "1.0"], TokenA);
        Assert.True(doubled.Body!["result"] is not null, doubled.Body!.ToJsonString());                // every value is 1.0
        var commaOk = await PostRaw(raw, body, ["1.0,1.0"], TokenA);
        Assert.True(commaOk.Body!["result"] is not null, commaOk.Body!.ToJsonString());

        // Sign-in comes first: an unauthenticated caller with a bad version gets 401, never a version error.
        Assert.Equal(401, (await PostRaw(raw, body, ["2.0"], null)).Status);
    }

    // ---------------------------------------------------------------------------------------------------------- SendMessage (D8)

    [Fact]
    public async Task returnImmediately_creates_a_SUBMITTED_task_and_queues_a2a_submit_for_the_partner_and_taints_the_session()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var sent = await client.SendMessageAsync(Send("m-1", "Reply to the invoice question."));
        Assert.Equal(TaskState.Submitted, sent.Task!.Status.State);
        var action = await SubmitActionOf(rig, sent.Task.Id);
        Assert.Equal(ActionStatus.Pending, action.Status);
        Assert.Equal("a2a.submit", action.ToolName);
        Assert.Equal(PartnerA, JsonNode.Parse(action.ArgumentsJson)!["partner"]!.GetValue<string>());
        Assert.True(rig.Host.Sessions.IsTainted(action.SessionId!));
    }

    [Fact]
    public async Task The_queued_action_names_the_partner_from_the_credential_even_when_metadata_and_data_claim_another()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var claim = new JsonObject { ["partner"] = PartnerB }.ToJsonString();
        var inputs = Part.FromData(JsonSerializer.SerializeToElement(new { partner = PartnerB, note = "not me" }));
        var request = Send("m-claim", "Reply.", extra: inputs);
        request.Message!.Metadata = new Dictionary<string, JsonElement> { ["partner"] = JsonSerializer.SerializeToElement(PartnerB) };
        var sent = await client.SendMessageAsync(request);
        var action = await SubmitActionOf(rig, sent.Task!.Id);
        var args = JsonNode.Parse(action.ArgumentsJson)!;
        Assert.Equal(PartnerA, args["partner"]!.GetValue<string>());
        Assert.Contains(PartnerB, args["inputs"]!.GetValue<string>());           // the claim is only inputs text, never the partner
        Assert.DoesNotContain(claim, action.ArgumentsJson);
    }

    [Fact]
    public async Task A_reviewer_approval_gives_WORKING_and_a_rejection_gives_REJECTED_without_the_reviewers_reason()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);

        var approved = (await client.SendMessageAsync(Send("m-approve", "Approve me."))).Task!;
        var approve = await SubmitActionOf(rig, approved.Id);
        await rig.Host.Queue.ReviewAsync(approve.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(TaskState.Working, (await client.GetTaskAsync(new GetTaskRequest { Id = approved.Id })).Status.State);

        var rejected = (await client.SendMessageAsync(Send("m-reject", "Reject me."))).Task!;
        var reject = await SubmitActionOf(rig, rejected.Id);
        await rig.Host.Queue.ReviewAsync(reject.Id, new ActionReview("rita", ReviewDecision.Reject, "Internal: this customer is blocked", DateTimeOffset.UtcNow));
        var answer = await client.GetTaskAsync(new GetTaskRequest { Id = rejected.Id });
        Assert.Equal(TaskState.Rejected, answer.Status.State);
        Assert.DoesNotContain("blocked", JsonSerializer.Serialize(answer, A2AJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task A_partner_cancel_while_the_action_is_pending_gives_CANCELED()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var task = (await client.SendMessageAsync(Send("m-cancel", "Cancel me."))).Task!;
        var cancelled = await client.CancelTaskAsync(new CancelTaskRequest { Id = task.Id });
        Assert.Equal(TaskState.Canceled, cancelled.Status.State);
        Assert.Equal(TaskState.Canceled, (await client.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        var again = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }));
        Assert.Equal(A2AErrorCode.TaskNotCancelable, again.ErrorCode);
    }

    [Fact]
    public async Task The_same_messageId_with_the_same_content_gives_the_same_task_and_one_action()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var first = (await client.SendMessageAsync(Send("m-same", "Same text."))).Task!;
        var second = (await client.SendMessageAsync(Send("m-same", "Same text."))).Task!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM a2a_tasks"));
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.submit'"));
    }

    [Fact]
    public async Task The_same_messageId_with_different_content_is_refused()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        await client.SendMessageAsync(Send("m-reuse", "The first request."));
        var refused = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-reuse", "A different request.")));
        Assert.Equal(A2AErrorCode.InvalidParams, refused.ErrorCode);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.submit'"));
        Assert.Contains("message_id_reused", Refusals(rig));
    }

    [Fact]
    public async Task A_crash_between_the_row_and_the_submit_is_healed_by_the_retry_with_one_action()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        // The crash: the row exists, and no action was recorded for it.
        var hash = A2ARequestHandler.ContentHash("draft-reply", "Heal me.", "");
        var row = rig.Table.InsertWithoutAction(PartnerA, "m-crash", hash, "draft-reply", DateTimeOffset.UtcNow);
        Assert.Equal(0, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.submit'"));

        using var client = rig.Client(TokenA);
        var retried = (await client.SendMessageAsync(Send("m-crash", "Heal me."))).Task!;
        Assert.Equal(row.TaskId, retried.Id);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.submit'"));
        Assert.NotNull(rig.Table.FindByTask(PartnerA, row.TaskId)!.SubmitActionId);
    }

    [Fact]
    public async Task A_file_part_and_an_oversized_goal_are_refused_and_audited_and_a_tenant_leaves_no_trace()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);

        var file = Part.FromRaw(Encoding.UTF8.GetBytes("file body"), "text/plain", "notes.txt");
        var fileRefused = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-file", "With a file.", extra: file)));
        Assert.Equal(A2AErrorCode.ContentTypeNotSupported, fileRefused.ErrorCode);
        Assert.Contains("file_part", Refusals(rig));

        var big = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-big-goal", new string('g', 5000))));
        Assert.Equal(A2AErrorCode.InvalidParams, big.ErrorCode);
        Assert.Contains("goal_too_large", Refusals(rig));
        Assert.Equal(0, rig.Table.Count());

        // A body that carries a tenant creates an ordinary task, and the tenant appears nowhere.
        var tenanted = (await client.SendMessageAsync(Send("m-tenant", "Tenant text.", tenant: "tenant-zeta"))).Task!;
        Assert.NotNull(tenanted.Id);
        var action = await SubmitActionOf(rig, tenanted.Id);
        Assert.DoesNotContain("tenant-zeta", action.ArgumentsJson);
        Assert.DoesNotContain("tenant-zeta", string.Join("|", ((SqliteAuditSink)rig.Host.Audit).ReadChain().Select(e => e.Detail ?? "")));
        Assert.DoesNotContain("tenant-zeta", string.Join("|", ((SqliteAuditSink)rig.Host.Audit).ReadChain().Select(e => e.Actor ?? "")));
    }

    // ---------------------------------------------------------------------------------------------------------- GetTask, CancelTask (D11, D12)

    [Fact]
    public async Task Another_partners_key_gets_TaskNotFound_identical_to_an_unknown_id_for_GetTask_and_CancelTask()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var owner = rig.Client(TokenA);
        var task = (await owner.SendMessageAsync(Send("m-owned", "Mine."))).Task!;

        using var other = rig.Client(TokenB);
        var foreign = await Assert.ThrowsAsync<A2AException>(() => other.GetTaskAsync(new GetTaskRequest { Id = task.Id }));
        var unknown = await Assert.ThrowsAsync<A2AException>(() => other.GetTaskAsync(new GetTaskRequest { Id = "no-such-task" }));
        Assert.Equal(A2AErrorCode.TaskNotFound, foreign.ErrorCode);
        Assert.Equal(unknown.ErrorCode, foreign.ErrorCode);
        Assert.Equal(unknown.Message, foreign.Message);

        var foreignCancel = await Assert.ThrowsAsync<A2AException>(() => other.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }));
        var unknownCancel = await Assert.ThrowsAsync<A2AException>(() => other.CancelTaskAsync(new CancelTaskRequest { Id = "no-such-task" }));
        Assert.Equal(unknownCancel.ErrorCode, foreignCancel.ErrorCode);
        Assert.Equal(unknownCancel.Message, foreignCancel.Message);

        // The owner still sees it, and the other partner's cancel did not cancel it.
        Assert.Equal(TaskState.Submitted, (await owner.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
    }

    [Fact]
    public async Task Streaming_is_UnsupportedOperation_in_this_slice()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var streamed = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Send("m-stream", "Stream.")))
            {
            }
        });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, streamed.ErrorCode);
    }

    // ---------------------------------------------------------------------------------------------------------- skills, policy and startup (D9, D10)

    [Fact]
    public async Task Several_skills_or_none_refuse_with_the_generic_not_available_error()
    {
        using var db = new TempDb();
        var two = A2aWith(new Dictionary<string, A2aSkill>
        {
            ["draft-reply"] = new A2aSkill { Name = "Draft a reply", Inbox = "clerk" },
            ["second"] = new A2aSkill { Name = "Second", Inbox = "clerk" },
        });
        await using (var rig = await StartAsync(db, a2a: two))
        {
            using var client = rig.Client(TokenA);
            var refused = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-two", "Which skill?")));
            Assert.Equal(A2AErrorCode.InvalidParams, refused.ErrorCode);
            Assert.Equal("That capability is not available to you.", refused.Message);
            Assert.Contains("skill_unavailable", Refusals(rig));
        }
        using var db2 = new TempDb();
        var none = A2aWith(new Dictionary<string, A2aSkill>());
        var error = Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db2, a2a: none));
        Assert.Contains("no skills", error.Message);
    }

    [Fact]
    public void Startup_refuses_a_partner_key_whose_profile_does_not_list_a2a_submit()
    {
        // Until batch 03b-2 this was refused at the request ("not available to you"). The startup check now refuses the configuration
        // itself; the handler keeps its own profile check as a second line.
        using var db = new TempDb();
        var keys = new[] { new ApiKeyEntry("acme-agent", ApiKeys.Hash(TokenA), ["a2a:call"], "reply-assistant", PartnerA) };
        var refused = Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, keys: keys));
        Assert.Contains("a2a.submit", refused.Message);
        Assert.Contains("reply-assistant", refused.Message);
    }

    [Fact]
    public void Startup_refuses_without_an_a2a_submit_policy_entry_and_an_inbox_the_policy_does_not_define()
    {
        using var db = new TempDb();
        var noEntry = Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: """
            { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce", "Tools": {},
              "Agents": { "clerk": { "Tools": [ "a2a.submit" ] } } }
            """));
        Assert.Contains("a2a.submit", noEntry.Message);

        using var db2 = new TempDb();
        var badInbox = A2aWith(new Dictionary<string, A2aSkill>
        {
            ["draft-reply"] = new A2aSkill { Name = "Draft", Inbox = "missing-profile" },
        });
        var noInbox = Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db2, a2a: badInbox));
        Assert.Contains("missing-profile", noInbox.Message);
    }

    [Fact]
    public void Execute_with_no_rule_is_a_warning_and_does_not_refuse_startup()
    {
        using var db = new TempDb();
        var policy = """
            { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce", "Work": { "Enabled": true },
              "Tools": { "a2a.submit": { "Mode": "Execute" }, "a2a.reply": { "Mode": "Queue", "AllowEdits": false } },
              "Agents": { "clerk": { "Tools": [ "a2a.submit", "a2a.reply" ] } } }
            """;
        var builder = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        var extension = new A2AFrontDoor();
        var options = new HostHttpOptions { A2A = EnabledA2a, Extensions = [extension], PublicBaseUrl = "http://127.0.0.1" };
        extension.AddModules(builder, options);
        var host = builder.Build();
        Assert.Empty(extension.Check(host, options));
        var warnings = extension.Warnings(host, options);
        Assert.Single(warnings);
        Assert.Contains("without a person's review", warnings[0]);
    }

    [Fact]
    public async Task The_preview_names_the_partner_the_skill_and_the_goal_as_quoted_text()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        Assert.True(rig.Host.Catalog.TryGet("a2a.submit", out var tool));
        var args = new JsonObject
        {
            ["partner"] = PartnerA, ["taskId"] = "t-1", ["skill"] = "draft-reply", ["goal"] = "Refund the order", ["inputs"] = "",
        }.ToJsonString();
        var preview = await tool.Preview!.PreviewAsync(args);
        Assert.Contains("Acme", preview.Summary);
        Assert.Contains("Draft a reply", preview.Summary);
        Assert.Contains("\"Refund the order\"", preview.Summary);
        Assert.Equal(ToolRisk.Write, tool.Descriptor.Risk);
        Assert.True(tool.Descriptor.ReadsUntrusted);
    }

    // ---------------------------------------------------------------------------------------------------------- migration and restart (D7)

    [Fact]
    public async Task A_schema_12_database_migrates_to_13_with_actions_intact()
    {
        using var db = new TempDb();
        long actionsBefore;
        await using (var rig = await StartAsync(db))
        {
            using var client = rig.Client(TokenA);
            await client.SendMessageAsync(Send("m-before", "Before the migration."));
            actionsBefore = rig.Count("SELECT COUNT(*) FROM actions");
        }

        // Make the file look like schema 12: no a2a_tasks table, and user_version 12. A schema 12 Host had no tasks to keep.
        using (var c = new SqliteConnection($"Data Source={db.Path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE a2a_tasks; PRAGMA user_version=12;";
            cmd.ExecuteNonQuery();
        }
        TempDb.ClearPools(db.Path);

        string? backup = null;
        try
        {
            await using var rig = await StartAsync(db);
            backup = ((SqliteActionStore)rig.Host.Store).MigrationBackupPath;
            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            Assert.Equal(13, rig.Count("SELECT user_version FROM pragma_user_version"));
            Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM sqlite_master WHERE name='a2a_tasks'"));
            Assert.Equal(actionsBefore, rig.Count("SELECT COUNT(*) FROM actions"));
        }
        finally
        {
            if (backup is not null)
                foreach (var f in new[] { backup, backup + "-wal", backup + "-shm" })
                    try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_restart_finds_the_tasks_a_partner_was_given()
    {
        using var db = new TempDb();
        string taskId;
        await using (var rig = await StartAsync(db))
        {
            using var client = rig.Client(TokenA);
            taskId = (await client.SendMessageAsync(Send("m-restart", "Survive a restart."))).Task!.Id;
        }
        await using (var rig = await StartAsync(db))
        {
            using var client = rig.Client(TokenA);
            Assert.Equal(TaskState.Submitted, (await client.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State);
            Assert.Equal(1, rig.Table.Count());
        }
    }

    // ---------------------------------------------------------------------------------------------------------- helpers

    // ---------------------------------------------------------------------------------------------------------- replies (batch 03b-2: E3 to E7)

    /// <summary>The inbox agent: the profile the skill's inbox names. It claims and completes the work item.</summary>
    private static readonly CallerIdentity Inbox = new("key:inbox-agent", "inbox agent", CallerKind.Agent) { Profile = "clerk" };

    /// <summary>The policy with a finance rule: a reply is allowed to partner A only. A reply to any other partner is denied by the exit check.</summary>
    private static readonly string PolicyWithFlow = Policy.Replace("\"Work\": { \"Enabled\": true },",
        "\"Work\": { \"Enabled\": true }, \"DataFlow\": { \"finance\": { \"Destinations\": [ { \"Tool\": \"a2a.reply\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" } } ], \"Otherwise\": \"Deny\" } },");

    private static async Task<ActionRecord> SubmitOfAsync(Rig rig, string partner, string taskId) =>
        (await rig.Host.Store.GetAsync(rig.Table.FindByTask(partner, taskId)?.SubmitActionId ?? throw new InvalidOperationException("no submit")))!;

    private static async Task<ActionRecord> ReplyOfAsync(Rig rig, string partner, string taskId) =>
        (await rig.Host.Store.GetAsync(rig.Table.FindByTask(partner, taskId)?.ReplyActionId ?? throw new InvalidOperationException("no reply")))!;

    /// <summary>Submits for the partner and has the reviewer approve the submit. Returns the task id and, once approval created it, the item id.</summary>
    private static async Task<(string TaskId, string ItemId)> ApprovedTaskAsync(Rig rig, A2AClient client, string partner, string messageId, string goal)
    {
        var task = (await client.SendMessageAsync(Send(messageId, goal))).Task!;
        var submit = await SubmitOfAsync(rig, partner, task.Id);
        await rig.Host.Queue.ReviewAsync(submit.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        return (task.Id, rig.Table.FindByTask(partner, task.Id)!.WorkItemId!);
    }

    /// <summary>The inbox agent claims the oldest open item and completes it with the summary, in a session that holds the labels given.</summary>
    private static async Task CompleteAsInboxAsync(Rig rig, string summary, params CarriedLabel[] labels)
    {
        var session = rig.Host.Sessions.Start(Inbox.Subject);
        var item = await rig.Host.Work.ClaimNextAsync(Inbox, session) ?? throw new InvalidOperationException("nothing to claim");
        if (labels.Length > 0) rig.Host.Labels.GainCarried(session, Inbox.Subject, labels);
        await rig.Host.Work.CompleteAsync(item.Id, Inbox, summary, session);
    }

    private static void Exec(Rig rig, string sql)
    {
        using var c = new SqliteConnection($"Data Source={rig.DbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task An_approved_submit_creates_one_tainted_work_item_for_the_inbox_profile_from_the_partner_and_the_partner_never_sees_its_id()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);

        var (taskId, itemId) = await ApprovedTaskAsync(rig, client, PartnerA, "m-item", "Check the invoice.");

        var item = rig.Host.Work.Store.Get(itemId)!;
        Assert.Equal((WorkStatus.Open, WorkService.ForProfile("clerk"), "partner:" + PartnerA, true), (item.Status, item.For, item.From, item.Tainted));
        Assert.StartsWith("a2a:", item.Thread);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM work_items"));
        var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId });
        Assert.Equal(TaskState.Working, task.Status.State);
        Assert.DoesNotContain(itemId, JsonSerializer.Serialize(task, A2AJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task Re_running_the_submit_and_a_crash_before_the_link_each_leave_one_work_item_and_the_row_is_healed()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (taskId, itemId) = await ApprovedTaskAsync(rig, client, PartnerA, "m-heal", "Heal me.");
        var submit = await SubmitOfAsync(rig, PartnerA, taskId);
        var work = new A2AWork(rig.Host, EnabledA2a, rig.Table);
        var args = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            Context = new Dictionary<object, object?>
            {
                [HostContract.RequesterContextKey] = "partner:" + PartnerA,
                [HostContract.ActionIdContextKey] = submit.Id,
            },
        };

        await work.SubmitAsync(PartnerA, taskId, "draft-reply", "Heal me.", "", args);     // the row has its link: nothing happens
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM work_items"));

        Exec(rig, "UPDATE a2a_tasks SET work_item_id=NULL");                              // the crash: the item exists, the link does not
        await work.SubmitAsync(PartnerA, taskId, "draft-reply", "Heal me.", "", args);     // the retry finds the item on the thread
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM work_items"));
        Assert.Equal(itemId, rig.Table.FindByTask(PartnerA, taskId)!.WorkItemId);
    }

    [Fact]
    public async Task A_completed_item_queues_a_reply_for_review_and_approval_sends_exactly_the_summary_with_no_artifact_before_it()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (taskId, _) = await ApprovedTaskAsync(rig, client, PartnerA, "m-reply", "Check the invoice.");
        await CompleteAsInboxAsync(rig, "Paid on 3 Nov.");

        var pending = await client.GetTaskAsync(new GetTaskRequest { Id = taskId });
        Assert.Equal(TaskState.Working, pending.Status.State);
        Assert.True(pending.Artifacts is null or { Count: 0 });
        var reply = await ReplyOfAsync(rig, PartnerA, taskId);
        Assert.Equal(ActionStatus.Pending, reply.Status);
        Assert.Contains("Paid on 3 Nov.", reply.PreviewJson);

        await rig.Host.Queue.ReviewAsync(reply.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        var done = await client.GetTaskAsync(new GetTaskRequest { Id = taskId });
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal("Paid on 3 Nov.", Assert.Single(done.Artifacts!).Parts.Single().Text);
    }

    [Fact]
    public async Task A_rejected_reply_gives_FAILED_and_the_summary_appears_nowhere_in_what_the_partner_receives()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (taskId, _) = await ApprovedTaskAsync(rig, client, PartnerA, "m-reject-reply", "Check the invoice.");
        await CompleteAsInboxAsync(rig, "Secret summary text.");
        await client.GetTaskAsync(new GetTaskRequest { Id = taskId });   // the partner's read queues the reply
        var reply = await ReplyOfAsync(rig, PartnerA, taskId);
        await rig.Host.Queue.ReviewAsync(reply.Id, new ActionReview("rita", ReviewDecision.Reject, "Internal: not for this customer", DateTimeOffset.UtcNow));

        var failed = await client.GetTaskAsync(new GetTaskRequest { Id = taskId });
        Assert.Equal(TaskState.Failed, failed.Status.State);
        Assert.True(failed.Artifacts is null or { Count: 0 });
        var wire = JsonSerializer.Serialize(failed, A2AJsonUtilities.DefaultOptions);
        Assert.DoesNotContain("Secret summary text.", wire);
        Assert.DoesNotContain("Internal", wire);
    }

    [Fact]
    public async Task GetTask_alone_queues_the_reply_when_the_completion_event_never_came_and_two_reads_at_once_make_one_reply()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (taskId, itemId) = await ApprovedTaskAsync(rig, client, PartnerA, "m-alone", "Check the invoice.");
        var session = rig.Host.Sessions.Start(Inbox.Subject);
        await rig.Host.Work.ClaimNextAsync(Inbox, session);
        // The item becomes Done in the store without the Completed event: as if the Host stopped between the two.
        rig.Host.Work.Store.Transition(itemId, WorkStatus.Claimed, i => i with { Status = WorkStatus.Done, Summary = "Done by the store.", LeaseUntil = null });

        var reads = await Task.WhenAll(client.GetTaskAsync(new GetTaskRequest { Id = taskId }), client.GetTaskAsync(new GetTaskRequest { Id = taskId }));

        Assert.All(reads, r => Assert.Equal(TaskState.Working, r.Status.State));
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.reply'"));
        Assert.Contains("Done by the store.", (await ReplyOfAsync(rig, PartnerA, taskId)).ArgumentsJson);
    }

    [Fact]
    public async Task A_label_the_completer_held_is_checked_at_the_exit_for_the_reply_and_a_partner_it_is_not_allowed_to_reach_gets_FAILED()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db, policy: PolicyWithFlow);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);
        var finance = new CarriedLabel("finance", "erp:ledger");

        var (taskA, _) = await ApprovedTaskAsync(rig, a, PartnerA, "m-flow-a", "Invoice for A.");
        await CompleteAsInboxAsync(rig, "Finance summary for A.", finance);
        await a.GetTaskAsync(new GetTaskRequest { Id = taskA });
        Assert.Equal(ActionStatus.Pending, (await ReplyOfAsync(rig, PartnerA, taskA)).Status);   // allowed for A: queued normally

        var (taskB, _) = await ApprovedTaskAsync(rig, b, PartnerB, "m-flow-b", "Invoice for B.");
        await CompleteAsInboxAsync(rig, "Finance summary for B.", finance);
        await b.GetTaskAsync(new GetTaskRequest { Id = taskB });
        var denied = await ReplyOfAsync(rig, PartnerB, taskB);
        Assert.Equal(ActionStatus.Denied, denied.Status);
        var failed = await b.GetTaskAsync(new GetTaskRequest { Id = taskB });
        Assert.Equal(TaskState.Failed, failed.Status.State);
        Assert.DoesNotContain("Finance summary for B.", JsonSerializer.Serialize(failed, A2AJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task A_forged_submit_or_reply_by_an_MCP_agent_with_a_real_partner_and_task_id_creates_nothing_and_delivers_nothing()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (taskId, itemId) = await ApprovedTaskAsync(rig, client, PartnerA, "m-real", "Real request.");
        var before = rig.Count("SELECT COUNT(*) FROM work_items");
        var session = rig.Host.Sessions.Start("key:mcp-agent");
        var forgedArgs = new JsonObject { ["partner"] = PartnerA, ["taskId"] = taskId, ["skill"] = "draft-reply", ["goal"] = "Forged.", ["inputs"] = "" };

        var forgedSubmit = await rig.Host.Queue.SubmitAsync(new ActionRequest("a2a.submit", forgedArgs.ToJsonString(),
            new ActionRequester(RequesterKind.AiSession, "key:mcp-agent"), session, "forge-submit") { Profile = "clerk" });
        await rig.Host.Queue.ReviewAsync(forgedSubmit.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Failed, (await rig.Host.Store.GetAsync(forgedSubmit.Id))!.Status);
        Assert.Equal(before, rig.Count("SELECT COUNT(*) FROM work_items"));

        // The crash window: a row with no submit action yet. Only the requester check refuses a forged submit here.
        var windowRow = rig.Table.InsertWithoutAction(PartnerA, "m-window", "unused-hash", "draft-reply", DateTimeOffset.UtcNow);
        var windowSubmit = await rig.Host.Queue.SubmitAsync(new ActionRequest("a2a.submit",
            new JsonObject { ["partner"] = PartnerA, ["taskId"] = windowRow.TaskId, ["skill"] = "draft-reply", ["goal"] = "Forged in the window.", ["inputs"] = "" }.ToJsonString(),
            new ActionRequester(RequesterKind.AiSession, "key:mcp-agent"), session, "forge-window") { Profile = "clerk" });
        await rig.Host.Queue.ReviewAsync(windowSubmit.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Failed, (await rig.Host.Store.GetAsync(windowSubmit.Id))!.Status);
        Assert.Equal(before, rig.Count("SELECT COUNT(*) FROM work_items"));

        await CompleteAsInboxAsync(rig, "The real answer.");
        var forgedReply = await rig.Host.Queue.SubmitAsync(new ActionRequest("a2a.reply",
            new JsonObject { ["partner"] = PartnerA, ["taskId"] = taskId, ["text"] = "Forged answer." }.ToJsonString(),
            new ActionRequester(RequesterKind.AiSession, "key:mcp-agent"), session, "forge-reply") { Profile = "clerk" });
        await rig.Host.Queue.ReviewAsync(forgedReply.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Failed, (await rig.Host.Store.GetAsync(forgedReply.Id))!.Status);
        Assert.Equal(TaskState.Working, (await client.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State);
        Assert.Equal(itemId, rig.Table.FindByTask(PartnerA, taskId)!.WorkItemId);
    }

    [Fact]
    public async Task Another_partner_sees_TaskNotFound_at_every_stage_including_COMPLETED()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);
        var (taskId, _) = await ApprovedTaskAsync(rig, a, PartnerA, "m-iso", "Isolated.");
        var get = new GetTaskRequest { Id = taskId };

        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => b.GetTaskAsync(get))).ErrorCode);
        await CompleteAsInboxAsync(rig, "Only for A.");
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => b.GetTaskAsync(get))).ErrorCode);
        Assert.Equal(TaskState.Working, (await a.GetTaskAsync(get)).Status.State);   // A's read queues the reply
        await rig.Host.Queue.ReviewAsync((await ReplyOfAsync(rig, PartnerA, taskId)).Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(TaskState.Completed, (await a.GetTaskAsync(get)).Status.State);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => b.GetTaskAsync(get))).ErrorCode);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => b.CancelTaskAsync(new CancelTaskRequest { Id = taskId }))).ErrorCode);
    }

    [Fact]
    public async Task CancelTask_cancels_an_open_item_as_the_partner_and_refuses_a_claimed_one_which_stays_with_its_claimant()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);

        var (openTask, openItem) = await ApprovedTaskAsync(rig, client, PartnerA, "m-open", "Open one.");
        var canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = openTask });
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(WorkStatus.Cancelled, rig.Host.Work.Store.Get(openItem)!.Status);
        Assert.Contains(((SqliteAuditSink)rig.Host.Audit).ReadChain(), e => e.Event == "work.cancelled" && e.Actor == "partner:" + PartnerA);

        var (claimedTask, claimedItem) = await ApprovedTaskAsync(rig, client, PartnerA, "m-claimed", "Claimed one.");
        var session = rig.Host.Sessions.Start(Inbox.Subject);
        await rig.Host.Work.ClaimNextAsync(Inbox, session);
        var refused = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(new CancelTaskRequest { Id = claimedTask }));
        Assert.Equal(A2AErrorCode.TaskNotCancelable, refused.ErrorCode);
        var still = rig.Host.Work.Store.Get(claimedItem)!;
        Assert.Equal((WorkStatus.Claimed, Inbox.Subject), (still.Status, still.ClaimedBy));
    }

    [Fact]
    public async Task A_dead_or_expired_item_reads_as_FAILED_with_a_generic_text()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var (deadTask, deadItem) = await ApprovedTaskAsync(rig, client, PartnerA, "m-dead", "Dead one.");
        rig.Host.Work.Store.Transition(deadItem, WorkStatus.Open, i => i with { Status = WorkStatus.Dead });
        var (expiredTask, expiredItem) = await ApprovedTaskAsync(rig, client, PartnerA, "m-expired", "Expired one.");
        rig.Host.Work.Store.Transition(expiredItem, WorkStatus.Open, i => i with { Status = WorkStatus.Expired });

        foreach (var taskId in new[] { deadTask, expiredTask })
            Assert.Equal(TaskState.Failed, (await client.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State);
    }

    [Fact]
    public async Task A_restart_between_the_completion_and_GetTask_still_ends_COMPLETED()
    {
        using var db = new TempDb();
        string taskId;
        await using (var rig = await StartAsync(db))
        {
            using var client = rig.Client(TokenA);
            (taskId, var itemId) = await ApprovedTaskAsync(rig, client, PartnerA, "m-restart", "Restart me.");
            var session = rig.Host.Sessions.Start(Inbox.Subject);
            await rig.Host.Work.ClaimNextAsync(Inbox, session);
            rig.Host.Work.Store.Transition(itemId, WorkStatus.Claimed, i => i with { Status = WorkStatus.Done, Summary = "Before the restart.", LeaseUntil = null });
        }

        await using var restarted = await StartAsync(db);
        using var again = restarted.Client(TokenA);
        Assert.Equal(TaskState.Working, (await again.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State);
        await restarted.Host.Queue.ReviewAsync((await ReplyOfAsync(restarted, PartnerA, taskId)).Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        var done = await again.GetTaskAsync(new GetTaskRequest { Id = taskId });
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal("Before the restart.", Assert.Single(done.Artifacts!).Parts.Single().Text);
    }

    [Fact]
    public void Startup_refuses_A2A_without_work_items_a_reply_entry_an_Execute_reply_or_a_profile_without_a2a_reply()
    {
        var noWork = Policy.Replace("\"Work\": { \"Enabled\": true },", "").Replace(", \"work.*\"", "");
        using (var db = new TempDb())
            Assert.Contains("Work.Enabled", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: noWork)).Message);

        var noEntry = Policy.Replace(", \"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false }", "");
        using (var db = new TempDb())
            Assert.Contains("a2a.reply", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: noEntry)).Message);

        var execute = Policy.Replace("\"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false }", "\"a2a.reply\": { \"Mode\": \"Execute\", \"AllowEdits\": false }");
        using (var db = new TempDb())
            Assert.Contains("Execute", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: execute)).Message);

        var executeRule = Policy.Replace("\"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false }",
            "\"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false, \"Rules\": [ { \"Name\": \"relax\", \"Mode\": \"Execute\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" }, \"IgnoreArgs\": [ \"taskId\", \"text\" ] } ] }");
        using (var db = new TempDb())
            Assert.Contains("relax", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: executeRule)).Message);

        var aiRule = Policy.Replace("\"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false }",
            "\"a2a.reply\": { \"Mode\": \"Queue\", \"AllowEdits\": false, \"Rules\": [ { \"Name\": \"by-ai\", \"Mode\": \"Queue\", \"Reviewer\": \"Ai\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" } } ] }");
        using (var db = new TempDb())
            Assert.Contains("by-ai", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: aiRule)).Message);

        var noProfileReply = Policy.Replace("\"clerk\": { \"Tools\": [ \"a2a.submit\", \"a2a.reply\", \"work.*\" ] }", "\"clerk\": { \"Tools\": [ \"a2a.submit\", \"work.*\" ] }");
        using (var db = new TempDb())
            Assert.Contains("a2a.reply", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, policy: noProfileReply, keys: DefaultKeys)).Message);
    }

    // ---------------------------------------------------------------------------------------------------------- blocking sends and ListTasks (batch 03b-3)

    /// <summary>The A2A section with a short blocking wait and this cap. The waits are counted in polls, so a test's poll delay decides the
    /// timing and no test sleeps.</summary>
    private static A2aOptions WithWait(TimeSpan wait, int cap = 4) =>
        EnabledA2a with { BlockingWait = wait, MaxBlockingWaitsPerPartner = cap };

    /// <summary>A poll delay that does not wait. <paramref name="onPoll"/> runs at each poll, numbered from 1.</summary>
    private static Func<TimeSpan, CancellationToken, Task> NoWait(Func<int, Task>? onPoll = null)
    {
        var polls = 0;
        return async (_, _) =>
        {
            polls++;
            if (onPoll is not null) await onPoll(polls);
        };
    }

    /// <summary>The newest task id of a partner, read from the table (the blocking send's own answer is not in hand while it waits).</summary>
    private static string TaskIdOf(Rig rig, string partner)
    {
        using var c = new SqliteConnection($"Data Source={rig.DbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT task_id FROM a2a_tasks WHERE partner_id=$p ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$p", partner);
        return (string)cmd.ExecuteScalar()!;
    }

    private static async Task ApproveSubmitAsync(Rig rig, string partner, string taskId, ReviewDecision decision, string? reason = null) =>
        await rig.Host.Queue.ReviewAsync((await SubmitOfAsync(rig, partner, taskId)).Id,
            new ActionReview("rita", decision, reason, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task A_blocking_send_answers_COMPLETED_with_the_reply_when_approval_and_completion_all_happen_inside_the_wait(bool? immediate)
    {
        using var db = new TempDb();
        Rig? rig = null;
        var poll = NoWait(async n =>
        {
            var r = rig!;
            var task = TaskIdOf(r, PartnerA);
            if (n == 1) await ApproveSubmitAsync(r, PartnerA, task, ReviewDecision.Approve);
            else if (n == 2) await CompleteAsInboxAsync(r, "Paid on 3 Nov.");
            else if (n == 3) await r.Host.Queue.ReviewAsync((await ReplyOfAsync(r, PartnerA, task)).Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        });
        rig = await StartAsync(db, a2a: WithWait(TimeSpan.FromSeconds(5)), poll: poll);
        await using var owned = rig;
        using var client = rig.Client(TokenA);

        var answer = (await client.SendMessageAsync(Send("m-blocking", "Check the invoice.", immediate))).Task!;

        Assert.Equal(TaskState.Completed, answer.Status.State);
        Assert.Equal("Paid on 3 Nov.", Assert.Single(answer.Artifacts!).Parts.Single().Text);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM a2a_tasks"));
    }

    [Fact]
    public async Task A_blocking_send_that_outlives_the_wait_answers_an_error_naming_the_task_and_the_task_keeps_running()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db, a2a: WithWait(TimeSpan.FromSeconds(1)), poll: NoWait());
        using var client = rig.Client(TokenA);

        var timedOut = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-slow", "Slow.", immediate: false)));

        var taskId = TaskIdOf(rig, PartnerA);
        Assert.Equal(A2AErrorCode.InternalError, timedOut.ErrorCode);
        Assert.Contains(taskId, timedOut.Message);
        Assert.Equal(TaskState.Submitted, (await client.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State);
        Assert.Equal(ActionStatus.Pending, (await SubmitOfAsync(rig, PartnerA, taskId)).Status);

        // A retry with the same messageId and content waits on the same task: one row, one action.
        var again = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-slow", "Slow.", immediate: null)));
        Assert.Contains(taskId, again.Message);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM a2a_tasks"));
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM actions WHERE tool='a2a.submit'"));
    }

    [Fact]
    public async Task A_blocking_send_rejected_during_the_wait_answers_the_task_in_REJECTED_without_the_reviewers_reason()
    {
        using var db = new TempDb();
        Rig? rig = null;
        rig = await StartAsync(db, a2a: WithWait(TimeSpan.FromSeconds(5)),
            poll: NoWait(n => n == 1 ? ApproveSubmitAsync(rig!, PartnerA, TaskIdOf(rig!, PartnerA), ReviewDecision.Reject, "Internal: blocked customer") : Task.CompletedTask));
        await using var owned = rig;
        using var client = rig.Client(TokenA);

        var answer = (await client.SendMessageAsync(Send("m-reject-wait", "Rejected while waiting.", immediate: false))).Task!;

        Assert.Equal(TaskState.Rejected, answer.Status.State);
        Assert.DoesNotContain("blocked customer", JsonSerializer.Serialize(answer, A2AJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task A_blocking_client_that_aborts_mid_wait_leaves_the_task_unchanged_and_gives_its_waiting_place_back()
    {
        using var db = new TempDb();
        using var cts = new CancellationTokenSource();
        var armed = true;
        Rig? rig = null;
        // The first poll aborts the client and then waits, so the server sees the abort as a cancelled wait. Later polls do nothing.
        rig = await StartAsync(db, a2a: WithWait(TimeSpan.FromSeconds(5), cap: 1), poll: async (_, ct) =>
        {
            if (!armed) return;
            armed = false;
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        });
        await using var owned = rig;
        using var client = rig.Client(TokenA);

        var aborted = client.SendMessageAsync(Send("m-abort", "Abort me.", immediate: false), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);

        var taskId = TaskIdOf(rig, PartnerA);
        Assert.Equal(ActionStatus.Pending, (await SubmitOfAsync(rig, PartnerA, taskId)).Status);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM a2a_tasks"));

        // The waiting place is back: a new blocking send is answered as a wait (its error), not refused as too many. The server may still be
        // finishing the aborted request, so this retries briefly.
        A2AException? answered = null;
        for (var attempt = 0; attempt < 200 && answered is null; attempt++)
        {
            try
            {
                await client.SendMessageAsync(Send("m-after-abort", "After the abort.", immediate: false));
            }
            catch (A2AException ex) when (ex.Message.Contains("Too many", StringComparison.Ordinal))
            {
                await Task.Delay(20);
            }
            catch (A2AException ex)
            {
                answered = ex;
            }
        }
        Assert.NotNull(answered);
        Assert.Contains("still in progress", answered!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cap_refuses_a_second_blocking_send_before_any_row_and_another_partner_is_not_affected()
    {
        using var db = new TempDb();
        Rig? rig = null;
        A2AClient client = null!;
        A2AClient otherClient = null!;
        var busy = false;
        var outerPolls = 0;
        A2AException? secondRefusal = null;
        A2AException? otherPartnerAnswer = null;
        rig = await StartAsync(db, a2a: WithWait(TimeSpan.FromSeconds(5), cap: 1), poll: NoWait(async _ =>
        {
            // The nested sends below make their own polls; those do nothing here.
            if (busy) return;
            outerPolls++;
            if (outerPolls == 1)
            {
                busy = true;
                try { await client.SendMessageAsync(Send("m-second", "Second.", immediate: false)); }
                catch (A2AException ex) { secondRefusal = ex; }
                try { await otherClient.SendMessageAsync(Send("m-other", "Other partner.", immediate: false)); }
                catch (A2AException ex) { otherPartnerAnswer = ex; }
                busy = false;
            }
            else if (outerPolls == 2)
            {
                await ApproveSubmitAsync(rig!, PartnerA, TaskIdOf(rig!, PartnerA), ReviewDecision.Reject, "Internal");
            }
        }));
        await using var owned = rig;
        client = rig.Client(TokenA);
        otherClient = rig.Client(TokenB);

        var first = (await client.SendMessageAsync(Send("m-first", "First.", immediate: false))).Task!;

        Assert.Equal(TaskState.Rejected, first.Status.State);
        Assert.NotNull(secondRefusal);
        Assert.Contains("Too many", secondRefusal!.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM a2a_tasks WHERE partner_id='" + PartnerA + "'"));
        Assert.Contains(((SqliteAuditSink)rig.Host.Audit).ReadChain(), e => e.Event == "a2a.refused" && e.Detail == "too_many_waits");
        Assert.NotNull(otherPartnerAnswer);
        Assert.Contains("still in progress", otherPartnerAnswer!.Message, StringComparison.Ordinal);

        // Once the first send has answered, the partner's place is free again: the next blocking send waits, rather than being refused.
        var later = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-later", "Later.", immediate: false)));
        Assert.Contains("still in progress", later.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTasks_pages_the_callers_tasks_newest_first_and_never_shows_another_partners_task()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);
        var created = new List<string>();
        for (var i = 0; i < 5; i++)
            created.Add((await a.SendMessageAsync(Send("m-list-" + i, "Task " + i))).Task!.Id);
        var foreign = (await b.SendMessageAsync(Send("m-foreign", "Partner B's task."))).Task!;

        var sizes = new List<int>();
        var seen = new List<string>();
        var token = "";
        for (var page = 0; page < 10; page++)
        {
            var listed = await a.ListTasksAsync(new ListTasksRequest { PageSize = 2, PageToken = token });
            sizes.Add(listed.Tasks.Count);
            Assert.Equal(5, listed.TotalSize);
            seen.AddRange(listed.Tasks.Select(t => t.Id));
            token = listed.NextPageToken;
            if (string.IsNullOrEmpty(token)) break;
        }

        Assert.Equal(new[] { 2, 2, 1 }, sizes);
        Assert.Equal(created.AsEnumerable().Reverse(), seen);
        Assert.DoesNotContain(foreign.Id, seen);
        Assert.Empty((await a.ListTasksAsync(new ListTasksRequest { ContextId = foreign.ContextId })).Tasks);
    }

    [Fact]
    public async Task ListTasks_refuses_a_bad_page_size_token_or_status_filter_with_InvalidParams()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var a = rig.Client(TokenA);
        _ = await a.SendMessageAsync(Send("m-one", "One."));

        foreach (var bad in new[]
                 {
                     new ListTasksRequest { PageSize = 0 },
                     new ListTasksRequest { PageSize = 101 },
                     new ListTasksRequest { PageToken = "not-a-token!" },
                     new ListTasksRequest { PageToken = "bm9wZQ" },
                     new ListTasksRequest { Status = TaskState.Working },
                 })
        {
            var refused = await Assert.ThrowsAsync<A2AException>(() => a.ListTasksAsync(bad));
            Assert.Equal(A2AErrorCode.InvalidParams, refused.ErrorCode);
        }
    }

    [Fact]
    public async Task ListTasks_shows_a_completed_tasks_artifact_only_when_IncludeArtifacts_is_true()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var a = rig.Client(TokenA);
        var (taskId, _) = await ApprovedTaskAsync(rig, a, PartnerA, "m-art", "Check the invoice.");
        await CompleteAsInboxAsync(rig, "Paid.");
        _ = await a.GetTaskAsync(new GetTaskRequest { Id = taskId });
        await rig.Host.Queue.ReviewAsync((await ReplyOfAsync(rig, PartnerA, taskId)).Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        var hidden = await a.ListTasksAsync(new ListTasksRequest { IncludeArtifacts = false });
        Assert.Equal(TaskState.Completed, hidden.Tasks[0].Status.State);
        Assert.True(hidden.Tasks[0].Artifacts is null or { Count: 0 });

        var shown = await a.ListTasksAsync(new ListTasksRequest { IncludeArtifacts = true });
        Assert.Equal("Paid.", Assert.Single(shown.Tasks[0].Artifacts!).Parts.Single().Text);
    }

    [Fact]
    public void Startup_refuses_a_blocking_wait_outside_1_second_to_5_minutes_and_a_cap_outside_1_to_64()
    {
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(10) })
        {
            using var db = new TempDb();
            Assert.Contains("BlockingWait", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, a2a: WithWait(wait))).Message);
        }
        using (var db = new TempDb())
            Assert.Contains("MaxBlockingWaits", Assert.Throws<ArgumentException>(() => StartIsRefusedWith(db, a2a: WithWait(TimeSpan.FromSeconds(30), cap: 0))).Message);
    }

    // ---------------------------------------------------------------------------------------------------------- the agent card (batch 04, part A)

    private const string TokenTools = "mcp-tools-token-0123456789";

    private static async Task<(HttpStatusCode Status, HttpResponseHeaders Headers, string Body)> GetCard(Rig rig, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await rig.Raw().SendAsync(req);
        return (resp.StatusCode, resp.Headers, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_card_takes_the_same_sign_in_and_role_as_a2a_no_credential_is_401_and_the_wrong_scopes_are_403()
    {
        using var db = new TempDb();
        var keys = DefaultKeys.Append(new ApiKeyEntry("tools-agent", ApiKeys.Hash(TokenTools), ["mcp:tools"], "clerk", null)).ToArray();
        await using var rig = await StartAsync(db, keys: keys);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetCard(rig, null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetCard(rig, TokenMcp)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetCard(rig, TokenTools)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetCard(rig, TokenA)).Status);
    }

    [Fact]
    public async Task The_card_does_not_exist_when_A2A_is_disabled()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db, policy: PolicyWithoutA2a, a2a: new A2aOptions { Enabled = false }, extension: true);

        Assert.Equal(HttpStatusCode.NotFound, (await GetCard(rig, TokenA)).Status);
    }

    /// <summary>The open question in batch 04 part A1: an anonymous caller can tell whether A2A is on from the card's status, because the
    /// card answers 401 when it exists and 404 when it does not. This test records that fact, so the question is answered from a run.</summary>
    [Fact]
    public async Task An_anonymous_caller_gets_401_for_the_card_with_A2A_on_and_404_with_it_off()
    {
        using var db = new TempDb();
        await using (var on = await StartAsync(db))
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetCard(on, null)).Status);
        using var off = new TempDb();
        await using (var disabled = await StartAsync(off, policy: PolicyWithoutA2a, a2a: new A2aOptions { Enabled = false }, extension: true))
            Assert.Equal(HttpStatusCode.NotFound, (await GetCard(disabled, null)).Status);
    }

    [Fact]
    public async Task The_card_parses_with_the_SDK_and_has_the_fields_of_A2()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);

        var (status, _, body) = await GetCard(rig, TokenA);
        var card = JsonSerializer.Deserialize<AgentCard>(body, A2AJsonUtilities.DefaultOptions)!;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("OpenTail Host", "1.0"), (card.Name, card.Version));
        var iface = Assert.Single(card.SupportedInterfaces);
        Assert.Equal(("JSONRPC", "1.0", "http://127.0.0.1/a2a"), (iface.ProtocolBinding, iface.ProtocolVersion, iface.Url));
        Assert.Equal((false, false, false), (card.Capabilities!.Streaming, card.Capabilities.PushNotifications, card.Capabilities.ExtendedAgentCard));
        Assert.Equal(["text/plain", "application/json"], card.DefaultInputModes);
        Assert.Equal(["text/plain", "application/json"], card.DefaultOutputModes);
        Assert.Empty(card.Skills);
        Assert.Equal(new[] { "apiKey", "bearer" }, card.SecuritySchemes!.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_card_names_no_tool_profile_partner_or_policy_detail()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);

        var body = (await GetCard(rig, TokenA)).Body;

        foreach (var forbidden in new[] { "a2a.submit", "a2a.reply", "clock.now", "clerk", "reply-assistant", "work.", PartnerA, PartnerB, "Acme", "Beta", "Policy", "reviewer" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_interface_URL_follows_PublicBaseUrl_and_the_card_is_only_what_it_is_configured_to_be()
    {
        var card = A2ACard.Build(new A2aCardOptions { Name = "Lab", Description = "A test host.", Version = "2.1" }, "https://host.example/base/");

        Assert.Equal("https://host.example/base/a2a", Assert.Single(card.SupportedInterfaces).Url);
        Assert.Equal(("Lab", "A test host.", "2.1"), (card.Name, card.Description, card.Version));
    }

    [Fact]
    public async Task The_card_is_private_and_the_SDK_gives_it_an_ETag_behind_sign_in()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);

        var (status, headers, _) = await GetCard(rig, TokenA);

        Assert.Equal(HttpStatusCode.OK, status);
        var cacheControl = headers.CacheControl?.ToString() ?? "";
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("public", cacheControl, StringComparison.Ordinal);
        Assert.NotNull(headers.ETag);
    }

    [Fact]
    public async Task The_card_read_is_audited_once_per_partner_in_the_window()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);

        await GetCard(rig, TokenA);
        await GetCard(rig, TokenA);
        await GetCard(rig, TokenB);

        var served = ((SqliteAuditSink)rig.Host.Audit).ReadChain().Where(e => e.Event == "a2a.card_served").Select(e => e.Actor).ToList();
        Assert.Equal(["partner:" + PartnerA, "partner:" + PartnerB], served);
    }

    [Fact]
    public void Startup_refuses_a_PublicBaseUrl_that_is_missing_or_plain_http_off_loopback_and_accepts_https_or_loopback_http()
    {
        static IReadOnlyList<string> ErrorsFor(string? publicBaseUrl)
        {
            using var db = new TempDb();
            var options = new HostHttpOptions { A2A = EnabledA2a, PublicBaseUrl = publicBaseUrl, Extensions = [new A2AFrontDoor()] };
            var builder = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(Policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
            foreach (var extension in options.Extensions) extension.AddModules(builder, options);
            var host = builder.Build();
            return options.Extensions.SelectMany(extension => extension.Check(host, options)).Where(e => e.Contains("PublicBaseUrl", StringComparison.Ordinal)).ToList();
        }

        Assert.NotEmpty(ErrorsFor(null));
        Assert.NotEmpty(ErrorsFor("http://partner-facing.example.com"));
        Assert.Empty(ErrorsFor("https://partner-facing.example.com"));
        Assert.Empty(ErrorsFor("http://localhost:8080"));
    }

    [Fact]
    public async Task GetExtendedAgentCard_stays_UnsupportedOperation()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);

        var refused = await Assert.ThrowsAsync<A2AException>(() => client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, refused.ErrorCode);
    }

    // ---------------------------------------------------------------------------------------------------------- part C: trust, suspension, the cap (batch 04)

    [Fact]
    public async Task C1_a_per_partner_rule_runs_one_partners_submit_and_leaves_the_other_queued()
    {
        using var db = new TempDb();
        var trusted = Policy.Replace("\"a2a.submit\": { \"Mode\": \"Queue\" }",
            "\"a2a.submit\": { \"Mode\": \"Queue\", \"Rules\": [ { \"Name\": \"trust-acme\", \"Mode\": \"Execute\", \"When\": { \"arg\": \"/partner\", \"equals\": \""
            + PartnerA + "\" }, \"IgnoreArgs\": [ \"taskId\", \"skill\", \"goal\", \"inputs\" ] } ] }");
        await using var rig = await StartAsync(db, policy: trusted);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);

        var fromA = (await a.SendMessageAsync(Send("m-trust-a", "Trusted partner's request."))).Task!;
        var fromB = (await b.SendMessageAsync(Send("m-trust-b", "Untrusted partner's request."))).Task!;

        Assert.Equal(TaskState.Working, fromA.Status.State);
        Assert.Equal(ActionStatus.Succeeded, (await SubmitOfAsync(rig, PartnerA, fromA.Id)).Status);
        Assert.Equal(TaskState.Submitted, fromB.Status.State);
        Assert.Equal(ActionStatus.Pending, (await SubmitOfAsync(rig, PartnerB, fromB.Id)).Status);
        Assert.Equal(1, rig.Count("SELECT COUNT(*) FROM work_items"));
        Assert.Equal("partner:" + PartnerA, rig.Host.Work.Store.Query().Single().From);
    }

    [Fact]
    public async Task C2_a_suspended_partner_is_answered_REJECTED_with_nothing_new_run_and_GetTask_and_ListTasks_still_read_its_tasks()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var a = rig.Client(TokenA);
        await rig.Host.Levels.SuspendAsync("partner:" + PartnerA, "rita", "incident for the test");

        var refused = (await a.SendMessageAsync(Send("m-suspended", "After the suspension."))).Task!;

        Assert.Equal(TaskState.Rejected, refused.Status.State);
        Assert.Equal(ActionStatus.Denied, (await SubmitOfAsync(rig, PartnerA, refused.Id)).Status);
        Assert.Empty(rig.Host.Work.Store.Query());
        Assert.DoesNotContain("incident", JsonSerializer.Serialize(refused, A2AJsonUtilities.DefaultOptions));
        // Today: the refused task is still the partner's, so GetTask and ListTasks answer for it.
        Assert.Equal(TaskState.Rejected, (await a.GetTaskAsync(new GetTaskRequest { Id = refused.Id })).Status.State);
        Assert.Contains(refused.Id, (await a.ListTasksAsync(new ListTasksRequest())).Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task C3_a_partner_at_the_pending_cap_gets_the_next_send_as_REJECTED_and_the_row_stays_and_another_partner_is_not_affected()
    {
        using var db = new TempDb();
        var capped = Policy.Replace("\"DataFlowMode\": \"Enforce\",", "\"DataFlowMode\": \"Enforce\", \"MaxPendingPerRequester\": 1,");
        await using var rig = await StartAsync(db, policy: capped);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);

        var first = (await a.SendMessageAsync(Send("m-cap-1", "First."))).Task!;
        var second = (await a.SendMessageAsync(Send("m-cap-2", "Second."))).Task!;

        Assert.Equal(TaskState.Submitted, first.Status.State);
        Assert.Equal(TaskState.Rejected, second.Status.State);
        var refusedAction = await SubmitOfAsync(rig, PartnerA, second.Id);
        Assert.Equal((ActionStatus.Denied, "queue_full"), (refusedAction.Status, refusedAction.Error!.Code));
        Assert.Equal(2, rig.Table.Count());   // the row is left behind: a retry with the same messageId answers the same task
        Assert.Equal(second.Id, (await a.SendMessageAsync(Send("m-cap-2", "Second."))).Task!.Id);
        Assert.Equal(TaskState.Submitted, (await b.SendMessageAsync(Send("m-cap-b", "Another partner's first."))).Task!.Status.State);
    }

    // ---------------------------------------------------------------------------------------------------------- the tasks chat command (batch 05, B2)

    [Fact]
    public async Task The_tasks_command_shows_the_state_age_and_wait_and_never_the_goal_or_the_answer()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        var command = new A2AFrontDoor().ChatCommands(rig.Host, new HostHttpOptions { A2A = EnabledA2a }).Single();
        using var client = rig.Client(TokenA);
        _ = await client.SendMessageAsync(Send("m-tasks", "GOAL-MARKER-TASKS: the partner's goal."));

        var waiting = await command.Lines(CancellationToken.None);
        Assert.Contains(waiting, l => l.Contains("Acme", StringComparison.Ordinal) && l.Contains("· submitted ·", StringComparison.Ordinal)
            && l.EndsWith("waits for a reviewer: request", StringComparison.Ordinal));

        var submit = await SubmitOfAsync(rig, PartnerA, TaskIdOf(rig, PartnerA));
        await rig.Host.Queue.ReviewAsync(submit.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        var inFlight = await command.Lines(CancellationToken.None);
        Assert.True(inFlight.Any(l => l.Contains("· working ·", StringComparison.Ordinal) && l.EndsWith("waits for an agent", StringComparison.Ordinal)),
            string.Join(" | ", inFlight));

        await CompleteAsInboxAsync(rig, "ANSWER-MARKER-TASKS: the answer.");
        // The command is read-only: until something reads the task (as the partner's GetTask does) the reply is still to be queued.
        Assert.Contains(await command.Lines(CancellationToken.None), l => l.EndsWith("waits for the reply to be queued", StringComparison.Ordinal));
        _ = await client.GetTaskAsync(new GetTaskRequest { Id = TaskIdOf(rig, PartnerA) });
        var reviewing = await command.Lines(CancellationToken.None);
        Assert.Contains(reviewing, l => l.EndsWith("waits for a reviewer: reply", StringComparison.Ordinal));

        var everything = string.Join("\n", waiting.Concat(inFlight).Concat(reviewing));
        Assert.DoesNotContain("GOAL-MARKER-TASKS", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("ANSWER-MARKER-TASKS", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tasks_command_does_not_exist_when_A2A_is_off()
    {
        using var db = new TempDb();
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(PolicyWithoutA2a)).UseSqlite(db.Path, SqliteSynchronous.Full).Build();

        Assert.Empty(new A2AFrontDoor().ChatCommands(host, new HostHttpOptions { A2A = new A2aOptions { Enabled = false } }));
    }

    /// <summary>Batch 06, R5. A task whose submit session has expired (the registry's own purge) before its item completes: the reply is still
    /// queued, on a new session that holds the item's labels, and a labelled reply to a partner with no allowed destination is still refused.</summary>
    [Fact]
    public async Task A_submit_session_that_expires_before_the_item_completes_still_gets_its_reply_on_a_new_session_with_the_labels_and_a_refused_partner_stays_refused()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db, policy: PolicyWithFlow);
        using var a = rig.Client(TokenA);
        using var b = rig.Client(TokenB);
        var finance = new CarriedLabel("finance", "erp:ledger");

        var (taskA, _) = await ApprovedTaskAsync(rig, a, PartnerA, "m-r5-a", "Invoice for A.");
        var (taskB, _) = await ApprovedTaskAsync(rig, b, PartnerB, "m-r5-b", "Invoice for B.");
        var submitSessionA = (await SubmitOfAsync(rig, PartnerA, taskA)).SessionId!;
        var submitSessionB = (await SubmitOfAsync(rig, PartnerB, taskB)).SessionId!;

        // The submit sessions expire. The row is removed directly, the state the registry's purge leaves: a purge with a short idle time
        // would also end the labels, which last as long as the session's idle time, so this keeps the policy's own lifetimes.
        Exec(rig, "DELETE FROM sessions WHERE id IN ('" + submitSessionA + "', '" + submitSessionB + "')");
        Assert.False(rig.Host.Sessions.Exists(submitSessionA));
        Assert.False(rig.Host.Sessions.Exists(submitSessionB));

        await CompleteAsInboxAsync(rig, "Finance summary for A.", finance);
        await CompleteAsInboxAsync(rig, "Finance summary for B.", finance);

        // A: the partner's read queues the reply, on a new session, with the item's label on it. The rule allows A.
        Assert.Equal(TaskState.Working, (await a.GetTaskAsync(new GetTaskRequest { Id = taskA })).Status.State);
        var replyA = await ReplyOfAsync(rig, PartnerA, taskA);
        Assert.Equal(ActionStatus.Pending, replyA.Status);
        Assert.NotEqual(submitSessionA, replyA.SessionId);
        Assert.True(rig.Host.Sessions.Exists(replyA.SessionId!, "partner:" + PartnerA));
        Assert.Contains(rig.Host.Labels.Held(replyA.SessionId, "partner:" + PartnerA), h => h.Label == "finance");

        // B: the same, but the rule names only A, so the exit check refuses B's reply. The task is FAILED and B's text is not in the answer.
        var failedB = await b.GetTaskAsync(new GetTaskRequest { Id = taskB });
        Assert.Equal(TaskState.Failed, failedB.Status.State);
        var replyB = await ReplyOfAsync(rig, PartnerB, taskB);
        Assert.Equal(ActionStatus.Denied, replyB.Status);
        Assert.Contains(rig.Host.Labels.Held(replyB.SessionId, "partner:" + PartnerB), h => h.Label == "finance");
        Assert.DoesNotContain("Finance summary for B.", JsonSerializer.Serialize(failedB, A2AJsonUtilities.DefaultOptions), StringComparison.Ordinal);
    }

    /// <summary>Builds the options and the Host, and starts the extension's startup checks; it must throw.</summary>
    private static void StartIsRefusedWith(TempDb db, string? policy = null, A2aOptions? a2a = null, ApiKeyEntry[]? keys = null)
    {
        var options = new HostHttpOptions { A2A = a2a ?? EnabledA2a, ApiKeys = keys ?? [], Extensions = [new A2AFrontDoor()] };
        var builder = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy ?? Policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        foreach (var extension in options.Extensions) extension.AddModules(builder, options);
        var host = builder.Build();
        var errors = options.Extensions.SelectMany(extension => extension.Check(host, options)).ToList();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
    }
}
