using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Extensions.Tasks;
#pragma warning disable MCPEXP002 // the Tasks client API is marked experimental in SDK 2.2.0

namespace OpenTail.Host.Tests;

/// <summary>Queued and delayed actions as MCP Tasks (docs/design/mcp-tasks.md, step 2): only for a client that opts in, the task id
/// is the action id, and tasks/get reads through the same path as get_action_status.</summary>
public class McpTasksTests
{
    private sealed class Pair : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        public McpClient Client { get; }

        private Pair(McpServer server, Task run, McpClient client, CancellationTokenSource cts) { _server = server; _run = run; Client = client; _cts = cts; }

        public static async Task<Pair> StartAsync(OpenTailHost host, bool tasks)
        {
            var c2s = new Pipe();
            var s2c = new Pipe();
            var cts = new CancellationTokenSource();
            var server = host.CreateServer(new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream(), "test-host"));
            var run = server.RunAsync(cts.Token);
            var options = new McpClientOptions();
            if (tasks) options.Capabilities = new ClientCapabilities { Extensions = new Dictionary<string, object> { [TasksProtocol.ExtensionId] = new JsonObject() } };
            var client = await McpClient.CreateAsync(new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()), options,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            return new Pair(server, run, client, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            try { await _run; } catch (OperationCanceledException) { }
        }
    }

    private static CallToolRequestParams Send(string to = "x@y.example") => new()
    {
        Name = "mail.send",
        Arguments = new Dictionary<string, JsonElement>
        {
            ["to"] = JsonSerializer.SerializeToElement(to),
            ["body"] = JsonSerializer.SerializeToElement("hello"),
        },
    };

    private static CancellationToken Soon => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task A_queued_call_from_a_client_that_opted_in_becomes_a_task_whose_id_is_the_action_id()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await Pair.StartAsync(host, tasks: true);

        var answer = await pair.Client.CallToolAsTaskAsync(Send(), Soon);
        Assert.True(answer.IsTask);
        var task = answer.TaskCreated!;
        Assert.StartsWith("act_", task.TaskId);
        Assert.Equal(McpTaskStatus.Working, task.Status);
        Assert.StartsWith("Waiting for a reviewer", task.StatusMessage);
        Assert.Equal(60_000, task.PollIntervalMs);

        var action = await host.Store.GetAsync(task.TaskId);
        Assert.Equal(ActionStatus.Pending, action!.Status);
        Assert.Equal(McpTaskStatus.Working, (await pair.Client.GetTaskAsync(task.TaskId, Soon)).Status);
    }

    [Fact]
    public async Task A_rejection_completes_the_task_with_the_reviewers_reason_as_an_error_result()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await Pair.StartAsync(host, tasks: true);
        var task = (await pair.Client.CallToolAsTaskAsync(Send(), Soon)).TaskCreated!;

        await host.Queue.ReviewAsync(task.TaskId, new ActionReview("rita", ReviewDecision.Reject, "Wrong customer.", DateTimeOffset.UtcNow) { Subject = "admin:rita" });

        var done = Assert.IsType<CompletedTaskResult>(await pair.Client.GetTaskAsync(task.TaskId, Soon));
        var result = done.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
        Assert.True(result.IsError);
        Assert.Contains("Wrong customer.", result.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Fact]
    public async Task Cancelling_the_task_cancels_the_pending_action()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await Pair.StartAsync(host, tasks: true);
        var task = (await pair.Client.CallToolAsTaskAsync(Send(), Soon)).TaskCreated!;

        await pair.Client.CancelTaskAsync(task.TaskId, Soon);
        Assert.Equal(ActionStatus.Cancelled, (await host.Store.GetAsync(task.TaskId))!.Status);
        Assert.IsType<CancelledTaskResult>(await pair.Client.GetTaskAsync(task.TaskId, Soon));
    }

    [Fact]
    public async Task A_client_that_did_not_opt_in_sees_exactly_what_it_saw_before()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await Pair.StartAsync(host, tasks: false);
        var result = await pair.Client.CallToolAsync(Send(), Soon);
        var made = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
        Assert.Equal("pending_approval", made["status"]!.ToString());
    }

    [Fact]
    public async Task A_call_that_finishes_inline_is_an_ordinary_result_even_for_a_client_that_opted_in()
    {
        // A call without a session counts as tainted by default, which would hold the send; this test wants it to run.
        var (host, module, _) = Hosts.Build("""{ "SessionlessTainted": false, "Tools": { "mail.send": { "Mode": "Execute" } } }""");
        await using var pair = await Pair.StartAsync(host, tasks: true);
        var answer = await pair.Client.CallToolAsTaskAsync(Send(), Soon);
        Assert.False(answer.IsTask);
        Assert.Single(module.Sent);
    }

    [Fact]
    public async Task An_unknown_task_id_is_not_found()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await Pair.StartAsync(host, tasks: true);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await pair.Client.GetTaskAsync("act_" + Guid.NewGuid().ToString("N"), Soon));
        Assert.Contains("Unknown task", ex.Message);
    }

    private sealed class SlowModule : IHostModule
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "slow";
        public void Register(IToolRegistry registry) => registry.Add(new ToolDescriptor("slow.report", "A long report.", ToolRisk.Read, Id),
            AIFunctionFactory.Create(async () => { await Release.Task; return "report ready"; }, "slow.report", "A long report."));
    }

    private static (OpenTailHost Host, SlowModule Slow) SlowHost()
    {
        var slow = new SlowModule();
        return (new OpenTailHostBuilder().AddModule(slow)
            .UsePolicy(PolicyConfig.Parse("""{ "TaskInlineLimit": "1s", "Tools": { "slow.report": { "Mode": "Execute" } } }""")).Build(), slow);
    }

    [Fact]
    public async Task A_call_that_outlasts_the_inline_limit_becomes_a_task_and_finishes_in_the_background()
    {
        var (host, slow) = SlowHost();
        await using var pair = await Pair.StartAsync(host, tasks: true);
        var answer = await pair.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "slow.report" }, Soon);
        Assert.True(answer.IsTask);
        Assert.Equal(("Running.", 2_000L), (answer.TaskCreated!.StatusMessage, answer.TaskCreated.PollIntervalMs));

        slow.Release.SetResult();
        GetTaskResult read;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { await Task.Delay(100); read = await pair.Client.GetTaskAsync(answer.TaskCreated.TaskId, Soon); }
        while (read is WorkingTaskResult && DateTime.UtcNow < deadline);
        var done = Assert.IsType<CompletedTaskResult>(read);
        Assert.Contains("report ready", done.Result.GetRawText());
    }

    [Fact]
    public async Task A_client_that_did_not_opt_in_waits_for_a_long_call_as_before()
    {
        var (host, slow) = SlowHost();
        await using var pair = await Pair.StartAsync(host, tasks: false);
        var call = pair.Client.CallToolAsync(new CallToolRequestParams { Name = "slow.report" }, Soon).AsTask();
        await Task.Delay(1500);
        Assert.False(call.IsCompleted);
        slow.Release.SetResult();
        var result = await call;
        Assert.Contains("report ready", result.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Fact]
    public void Statuses_map_as_the_design_says()
    {
        var now = DateTimeOffset.UtcNow;
        ActionRecord R(ActionStatus s, params ActionReview[] reviews) => new()
        {
            Id = "act_1", ToolName = "mail.send", ModuleId = "fake", ArgumentsJson = "{}", ArgumentsHash = "h",
            Requester = new ActionRequester(RequesterKind.AiSession, "key:bot"), Status = s, PolicyMode = ToolMode.Queue,
            CreatedAt = now, UpdatedAt = now, Reviews = reviews,
        };
        Assert.Equal(McpTaskStatus.Working, ActionTasks.StatusOf(R(ActionStatus.Executing)));
        Assert.Equal(McpTaskStatus.Completed, ActionTasks.StatusOf(R(ActionStatus.Rejected)));
        Assert.Equal(McpTaskStatus.Cancelled, ActionTasks.StatusOf(R(ActionStatus.Cancelled)));
        // A person pressed Stop during the undo window: a decision the model must read, not a cancellation.
        Assert.Equal(McpTaskStatus.Completed, ActionTasks.StatusOf(R(ActionStatus.Cancelled, new ActionReview("rita", ReviewDecision.Reject, "Stopped.", now))));
        Assert.Equal(2_000, ActionTasks.PollIntervalMs(R(ActionStatus.Executing), now));
        Assert.Equal(5_000, ActionTasks.PollIntervalMs(R(ActionStatus.Pending) with { RunAt = now.AddSeconds(5) }, now));
        Assert.True(ActionTasks.IsTasksRevision("2026-07-28"));
        Assert.False(ActionTasks.IsTasksRevision("2025-11-25"));
    }
}
