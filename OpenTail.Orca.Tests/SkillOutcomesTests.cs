using System.Text.Json.Nodes;
using OpenTail.Modules.Skills;

namespace OpenTail.Orca.Tests;

public sealed class SkillOutcomesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"outcomes-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    private static string Meta(string v) => $$"""{ "format": 1, "id": "reply-draft", "version": "{{v}}", "summary": "Reply to customers.", "owner": "cs" }""";

    /// <summary>Serves the labelled version in <paramref name="sessions"/> sessions, each followed by one send that a person decides.</summary>
    private static async Task Sessions(McpPair pair, OpenTailHost host, int sessions, int rejectEvery)
    {
        for (var i = 0; i < sessions; i++)
        {
            var session = S(await pair.CallAsync("start_session"), "sessionId");
            await pair.CallAsync("skills.get", ("id", "reply-draft"), ("sessionId", session));
            var send = await pair.CallAsync("mail.send", ("to", "a@b.example"), ("body", "Hello"), ("sessionId", session));
            var reject = rejectEvery > 0 && i % rejectEvery == 0;
            await host.Queue.ReviewAsync(S(send, "actionId"), new ActionReview("alice", reject ? ReviewDecision.Reject : ReviewDecision.Approve,
                reject ? "Promises a refund the policy does not give." : null, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task A_version_whose_actions_are_turned_down_more_often_gets_a_rollback_suggestion()
    {
        var store = new SkillStore(Path.Combine(_root, "store"));
        store.Publish("reply-draft", "1", "production", Meta("1"), "Version one.");
        var host = new OpenTailHostBuilder()
            .AddModule(new FakeInboxModule())
            .AddModule(new SkillsModule(new SkillsModuleOptions { Store = store.Root }))
            .UsePolicy(PolicyConfig.Parse(Hosts.SendQueued))
            .Build();
        await using var pair = await McpPair.StartAsync(host);

        await Sessions(pair, host, 20, rejectEvery: 10);  // version 1: 2 of 20 turned down
        store.Publish("reply-draft", "2", "production", Meta("2"), "Version two.");
        await Sessions(pair, host, 20, rejectEvery: 2);   // version 2: 10 of 20

        var outcomes = await SkillOutcomes.CollectAsync(host);
        var v1 = outcomes.Single(o => o.Version == "1");
        var v2 = outcomes.Single(o => o.Version == "2");
        Assert.Equal((20, 18, 2), (v1.Decided, v1.Approved, v1.Rejected));
        Assert.Equal((20, 10, 10), (v2.Decided, v2.Approved, v2.Rejected));

        var suggestion = Assert.Single(SkillOutcomes.Suggest(outcomes));
        Assert.Equal(("reply-draft", "2", "1"), (suggestion.Skill, suggestion.Current, suggestion.Previous));
        Assert.Contains("skills.publish of version 1", suggestion.Why);

        // Too few cases is not evidence: no suggestion.
        Assert.Empty(SkillOutcomes.Suggest(outcomes, minCases: 50));
        // A small rise is not a reason either.
        Assert.Empty(SkillOutcomes.Suggest(outcomes, worseBy: 0.5));
        // Nothing was rolled back: the Host only suggests.
        Assert.Equal("2", store.LabelledVersion("reply-draft", "production"));
    }

    [Fact]
    public void Similar_rates_are_not_flagged()
    {
        var now = DateTimeOffset.UtcNow;
        var outcomes = new[]
        {
            new SkillVersionOutcome("invoice-check", "1", 40, 36, 4, 0, now.AddDays(-2)),
            new SkillVersionOutcome("invoice-check", "2", 40, 34, 5, 1, now),
        };
        Assert.Empty(SkillOutcomes.Suggest(outcomes));
    }
}
