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
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce",
          "Tools": { "a2a.submit": { "Mode": "Queue" } },
          "Agents": { "clerk": { "Tools": [ "a2a.submit" ] }, "reply-assistant": { "Tools": [ "clock.now" ] } } }
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
        int rate = 0, int maxBody = 1024 * 1024, bool extension = true)
    {
        var extensions = extension ? new IHttpDeploymentExtension[] { new A2AFrontDoor() } : Array.Empty<IHttpDeploymentExtension>();
        var options = new HostHttpOptions
        {
            ApiKeys = keys ?? DefaultKeys,
            A2A = a2a ?? EnabledA2a,
            Extensions = extensions,
            RateLimitPerMinute = rate,
            MaxBodyBytes = maxBody,
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
    public async Task Blocking_sends_are_UnsupportedOperation_and_leave_no_row_behind()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var omitted = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-omit", "Blocking.", immediate: null)));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, omitted.ErrorCode);
        var explicitFalse = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-false", "Blocking.", immediate: false)));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, explicitFalse.ErrorCode);
        Assert.Equal(0, rig.Table.Count());
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
    public async Task ListTasks_and_streaming_are_UnsupportedOperation_in_this_slice()
    {
        using var db = new TempDb();
        await using var rig = await StartAsync(db);
        using var client = rig.Client(TokenA);
        var listed = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest { PageSize = 10 }));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, listed.ErrorCode);
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
    public async Task A_profile_that_does_not_list_a2a_submit_gets_the_generic_not_available_error()
    {
        using var db = new TempDb();
        var keys = new[] { new ApiKeyEntry("acme-agent", ApiKeys.Hash(TokenA), ["a2a:call"], "reply-assistant", PartnerA) };
        await using var rig = await StartAsync(db, keys: keys);
        using var client = rig.Client(TokenA);
        var refused = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Send("m-profile", "Not for me.")));
        Assert.Equal("That capability is not available to you.", refused.Message);
        Assert.Equal(0, rig.Table.Count());
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
            { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce",
              "Tools": { "a2a.submit": { "Mode": "Execute" } },
              "Agents": { "clerk": { "Tools": [ "a2a.submit" ] } } }
            """;
        var builder = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        var extension = new A2AFrontDoor();
        var options = new HostHttpOptions { A2A = EnabledA2a, Extensions = [extension] };
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

    /// <summary>Builds the options and the Host, and starts the extension's startup checks; it must throw.</summary>
    private static void StartIsRefusedWith(TempDb db, string? policy = null, A2aOptions? a2a = null)
    {
        var options = new HostHttpOptions { A2A = a2a ?? EnabledA2a, Extensions = [new A2AFrontDoor()] };
        var builder = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy ?? Policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        foreach (var extension in options.Extensions) extension.AddModules(builder, options);
        var host = builder.Build();
        var errors = options.Extensions.SelectMany(extension => extension.Check(host, options)).ToList();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
    }
}
