using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>
/// Channel parity (G8, batch 10): the same action, decided by an AI reviewer through the in-process queue, the in-process runner, and the HTTP
/// review route, gets the same governance decision. MCP, A2A and Teams are not in this test: MCP has no review route, A2A has no review step (a
/// partner submits and a person decides in Teams or the page), and the Teams bot needs a live tenant. Those are recorded as not covered.
/// </summary>
public class Batch10ChannelParityTests
{
    private const string AdminToken = "admin-token-0123456789";
    private const string BotSecret = "bot-secret-0123456789abcdef";
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";
    private const string Policy = """{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect", "AiMayRefuse": true, "AiReviewers": ["bot"] } } }""";

    private static string HashOf(string secret) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static AdminOptions Options() => new()
    {
        Tokens = new Dictionary<string, string> { [AdminToken] = "alice" },
        AiReviewers = new Dictionary<string, string> { ["bot"] = HashOf(BotSecret) },
    };

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    private static ActionReview Decision(ReviewDecision d) =>
        new("bot", d, d == ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };

    [Theory]
    [InlineData(ReviewDecision.Approve, ActionStatus.Succeeded, 1)]
    [InlineData(ReviewDecision.Reject, ActionStatus.Rejected, 0)]
    public async Task The_direct_queue_the_runner_and_the_http_route_give_one_answer(ReviewDecision decision, ActionStatus expected, int sends)
    {
        // The in-process queue.
        var (direct, directModule, _) = Hosts.Build(Policy);
        var viaQueue = await Submit(direct);
        await direct.Queue.ReviewAsync(viaQueue.Id, Decision(decision));

        // The in-process runner, offering the same action to the same reviewer.
        var (viaRunner, runnerModule, _) = Hosts.Build(Policy);
        var runnerAction = await Submit(viaRunner);
        await new ReviewerRunner(viaRunner).RunOnceAsync(new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(Decision(decision))));

        // The HTTP review route, with the reviewer's own credential.
        var (viaHttp, httpModule, _) = Hosts.Build(Policy);
        var httpAction = await Submit(viaHttp);
        await using var admin = await AdminServer.StartAsync(viaHttp, Options());
        using var bot = new HttpClient { BaseAddress = admin.BaseUri };
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BotSecret);
        var body = decision == ReviewDecision.Approve
            ? JsonContent.Create(new { decision = "approve" })
            : JsonContent.Create(new { decision = "reject", reason = "not this one" });
        await bot.PostAsync($"/api/actions/{httpAction.Id}/review", body);

        Assert.Equal(expected, (await direct.Store.GetAsync(viaQueue.Id))!.Status);
        Assert.Equal(expected, (await viaRunner.Store.GetAsync(runnerAction.Id))!.Status);
        Assert.Equal(expected, (await viaHttp.Store.GetAsync(httpAction.Id))!.Status);
        Assert.Equal(sends, directModule.SendCalls);
        Assert.Equal(sends, runnerModule.SendCalls);
        Assert.Equal(sends, httpModule.SendCalls);
    }
}
