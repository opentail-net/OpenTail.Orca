using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using A2A;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTail.Orca.A2A;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>
/// Batch 05, part A: the A2A kit the owner runs (docs/guides/a2a-sample, scripts/a2a-smoke.ps1, docs/guides/a2a.md). The sample is loaded
/// through the server's own loader and the Host starts from it, and the full flow runs against it. The smoke script is read, never run:
/// its JSON-RPC methods and headers must be the ones these tests send.
/// </summary>
public class A2aKitTests
{
    // The sample's throwaway keys and partner. The sample holds only their hashes; these values are documented as sample only.
    private const string PartnerKey = "sample-only-partner-key-replace-me";
    private const string InboxKey = "sample-only-inbox-key-replace-me";
    private const string PartnerId = "2b6c9f0e-5d1a-4c8e-9f3b-7a1d0e6c4b25";

    // The JSON-RPC methods and the headers the smoke script sends. The raw requests below send exactly these, and the script must too.
    private const string MethodSend = "SendMessage";
    private const string MethodGet = "GetTask";
    private const string MethodCancel = "CancelTask";
    private const string VersionHeader = "A2A-Version";
    private const string AuthHeader = "Authorization";

    private static string KitDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "guides", "a2a-sample"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository's docs/guides/a2a-sample was not found.");
    }

    private sealed class Kit(OpenTailHost host, RunningHostHttp server, string dbPath, string configPath) : IAsyncDisposable
    {
        public OpenTailHost Host { get; } = host;
        public RunningHostHttp Server { get; } = server;
        public string DbPath { get; } = dbPath;

        public A2ATaskTable Table => new(DbPath, SqliteSynchronous.Full);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            File.Delete(configPath);
            TempDb.ClearPools(DbPath);
        }
    }

    /// <summary>The Host from the sample, started the way the server starts it: the HTTP config through its loader, the policy through the
    /// policy loader, the A2A front door as the server's program passes it. The only change is port 0, so parallel tests do not share a port.
    /// The database runs at full durability, as a sample run sets OPENTAIL_DB_SYNCHRONOUS=full (the A2A startup check requires it).</summary>
    private static async Task<Kit> StartSampleAsync(TempDb db)
    {
        var sample = Path.Combine(KitDirectory(), "docs", "guides", "a2a-sample");
        var configPath = db.Path + ".http.json";
        File.WriteAllText(configPath, File.ReadAllText(Path.Combine(sample, "http.json")).Replace("5600", "0", StringComparison.Ordinal));

        var frontDoor = new A2AFrontDoor();
        var options = HttpDeploymentConfig.Load(configPath).ToOptions(null, [frontDoor]);
        var builder = new OpenTailHostBuilder().WithName("opentail-orca").UsePolicy(PolicyConfig.Load(Path.Combine(sample, "policy.json")))
            .UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        foreach (var extension in options.Extensions) extension.AddModules(builder, options);
        var host = builder.Build();
        Assert.Empty(options.Extensions.SelectMany(extension => extension.Check(host, options)));
        var server = await HostHttp.StartAsync(host, options);
        return new Kit(host, server, db.Path, configPath);
    }

    private static HttpClient Bearer(Uri baseUri, string token)
    {
        var http = new HttpClient { BaseAddress = baseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static SendMessageRequest Goal(string messageId, string goal, bool returnImmediately = true) => new()
    {
        Message = new Message { MessageId = messageId, Role = global::A2A.Role.User, Parts = [Part.FromText(goal)] },
        Configuration = new SendMessageConfiguration { ReturnImmediately = returnImmediately },
    };

    [Fact]
    public async Task The_sample_config_and_policy_load_through_the_server_loader_and_the_Host_starts_with_them()
    {
        using var db = new TempDb();
        await using var kit = await StartSampleAsync(db);
        using var partner = Bearer(kit.Server.BaseUri, PartnerKey);

        var card = JsonNode.Parse(await partner.GetStringAsync("/.well-known/agent-card.json"))!;

        Assert.Equal("OpenTail sample", card["name"]!.GetValue<string>());
        Assert.Equal("1.0", card["supportedInterfaces"]![0]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_full_flow_passes_against_the_sample_send_approve_claim_complete_approve_and_the_partner_sees_the_summary()
    {
        using var db = new TempDb();
        await using var kit = await StartSampleAsync(db);
        using var partnerHttp = Bearer(kit.Server.BaseUri, PartnerKey);
        var partner = new A2AClient(new Uri(kit.Server.BaseUri, "/a2a"), partnerHttp);

        // 1. The partner sends, and returns at once (the kit's smoke script does the same).
        var sent = (await partner.SendMessageAsync(Goal("kit-1", "Check the invoice."))).Task!;
        Assert.Equal(TaskState.Submitted, sent.Status.State);

        // 2. A person approves the request on the review page (here, the same decision the page makes).
        var submit = (await kit.Host.Store.GetAsync(kit.Table.FindByTask(PartnerId, sent.Id)!.SubmitActionId!))!;
        await kit.Host.Queue.ReviewAsync(submit.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(TaskState.Working, (await partner.GetTaskAsync(new GetTaskRequest { Id = sent.Id })).Status.State);

        // 3. The inbox agent takes the work over MCP, with its own key, and completes it with a summary.
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(kit.Server.BaseUri, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { [AuthHeader] = "Bearer " + InboxKey },
        }));
        var claimed = JsonNode.Parse((await mcp.CallToolAsync("next_work", new Dictionary<string, object?>())).Content.OfType<TextContentBlock>().First().Text)!;
        Assert.Equal("claimed", claimed["status"]!.GetValue<string>());
        var workId = claimed["workId"]!.GetValue<string>();
        var completed = JsonNode.Parse((await mcp.CallToolAsync("complete_work",
            new Dictionary<string, object?> { ["workId"] = workId, ["summary"] = "Paid on 3 Nov." })).Content.OfType<TextContentBlock>().First().Text)!;
        Assert.Equal("done", completed["status"]!.GetValue<string>());

        // 4. The reply is queued for a person; the partner still sees WORKING, with no answer yet.
        var waiting = await partner.GetTaskAsync(new GetTaskRequest { Id = sent.Id });
        Assert.Equal(TaskState.Working, waiting.Status.State);
        Assert.True(waiting.Artifacts is null or { Count: 0 });
        var reply = (await kit.Host.Store.GetAsync(kit.Table.FindByTask(PartnerId, sent.Id)!.ReplyActionId!))!;
        Assert.Contains("Paid on 3 Nov.", reply.ArgumentsJson);

        // 5. A person approves the reply; the partner sees COMPLETED with exactly the summary.
        await kit.Host.Queue.ReviewAsync(reply.Id, new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        var done = await partner.GetTaskAsync(new GetTaskRequest { Id = sent.Id });
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal("Paid on 3 Nov.", Assert.Single(done.Artifacts!).Parts.Single().Text);
    }

    [Fact]
    public async Task The_raw_requests_use_the_methods_and_headers_the_smoke_script_sends_and_the_script_never_prints_the_key()
    {
        using var db = new TempDb();
        await using var kit = await StartSampleAsync(db);
        var script = File.ReadAllText(Path.Combine(KitDirectory(), "scripts", "a2a-smoke.ps1"));

        // The script's JSON-RPC methods are exactly the three the test's own requests use.
        var methods = Regex.Matches(script, @"method\s*=\s*'([A-Za-z]+)'").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(methods.SetEquals([MethodSend, MethodGet, MethodCancel]), string.Join(", ", methods));
        // The headers it sends are the two the test sends.
        Assert.Contains("'" + VersionHeader + "'", script, StringComparison.Ordinal);
        Assert.Contains("'" + AuthHeader + "'", script, StringComparison.Ordinal);
        // It never writes the key out.
        Assert.DoesNotContain(script.Split('\n').Where(line => line.TrimStart().StartsWith("Write-", StringComparison.Ordinal)), line => line.Contains("Key", StringComparison.Ordinal));

        // The same calls, raw, with the same methods and headers.
        using var http = Bearer(kit.Server.BaseUri, PartnerKey);
        http.DefaultRequestHeaders.Add(VersionHeader, "1.0");
        var sent = JsonNode.Parse(await PostJsonRpcAsync(http, MethodSend, new JsonObject
        {
            ["message"] = new JsonObject { ["messageId"] = "raw-1", ["role"] = "ROLE_USER", ["parts"] = new JsonArray(new JsonObject { ["text"] = "Raw check." }) },
            ["configuration"] = new JsonObject { ["returnImmediately"] = true },
        }))!;
        var taskId = sent["result"]!["task"]!["id"]!.GetValue<string>();
        // GetTask and CancelTask answer with the task itself; SendMessage answers with { "task": ... }.
        var got = JsonNode.Parse(await PostJsonRpcAsync(http, MethodGet, new JsonObject { ["id"] = taskId }))!;
        Assert.Equal(taskId, got["result"]!["id"]!.GetValue<string>());
        var cancelled = JsonNode.Parse(await PostJsonRpcAsync(http, MethodCancel, new JsonObject { ["id"] = taskId }))!;
        Assert.Equal("TASK_STATE_CANCELED", cancelled["result"]!["status"]!["state"]!.GetValue<string>());
    }

    private static async Task<string> PostJsonRpcAsync(HttpClient http, string method, JsonObject parameters)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["method"] = method, ["params"] = parameters }.ToJsonString();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/a2a", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
