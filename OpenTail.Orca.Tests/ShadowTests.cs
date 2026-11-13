using System.Text.Json.Nodes;
using OpenTail.Modules.Skills;

namespace OpenTail.Orca.Tests;

public sealed class ShadowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"shadow-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    [Fact]
    public async Task A_shadow_host_reads_live_and_records_everything_else()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Hosts.SendExecute)).UseShadow().Build();
        await using var pair = await McpPair.StartAsync(host);
        var clean = S(await pair.CallAsync("start_session"), "sessionId");

        var sent = await pair.CallAsync("mail.send", ("to", "a@b.example"), ("body", "hi"), ("sessionId", clean)); // before any read taints it
        var read = await pair.CallAsync("inbox.read", ("sessionId", clean));

        Assert.Contains("x@evil.example", read.ToJsonString()); // the real module answered the read
        Assert.Equal("succeeded", S(sent, "status"));
        Assert.Equal(0, module.SendCalls);                      // the send was recorded, not run
        Assert.True(host.IsSandbox);
        Assert.Contains(host.StartupWarnings, w => w.StartsWith("Shadow mode", StringComparison.Ordinal));
    }

    private async Task<(OpenTailHost Host, McpPair Pair)> WithDecisions()
    {
        var store = new SkillStore(Path.Combine(_root, "store"));
        store.Publish("reply-draft", "4", "production", """{ "format": 1, "id": "reply-draft", "version": "4", "summary": "Reply.", "owner": "cs" }""", "Reply politely.");
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).AddModule(new SkillsModule(new SkillsModuleOptions { Store = store.Root }))
            .UsePolicy(PolicyConfig.Parse(Hosts.SendQueued)).Build();
        var pair = await McpPair.StartAsync(host);
        for (var i = 0; i < 4; i++)
        {
            var session = S(await pair.CallAsync("start_session"), "sessionId");
            await pair.CallAsync("skills.get", ("id", "reply-draft"), ("sessionId", session));
            await pair.CallAsync("inbox.read", ("messageId", $"<m{i}@mail.example>"), ("sessionId", session));
            var send = await pair.CallAsync("mail.send", ("to", "customer@shop.example"), ("body", $"Reply {i}"), ("sessionId", session));
            await host.Queue.ReviewAsync(S(send, "actionId"), new ActionReview("alice", i == 0 ? ReviewDecision.Reject : ReviewDecision.Approve,
                i == 0 ? "Too long." : null, DateTimeOffset.UtcNow));
        }
        return (host, pair);
    }

    [Fact]
    public async Task Approved_actions_can_be_sampled_into_the_export_for_grading()
    {
        var (host, pair) = await WithDecisions();
        await using (pair)
        {
            Assert.Single(await ReviewOutcomeExport.CollectAsync(host));                       // rejections only, by default
            var all = await ReviewOutcomeExport.CollectAsync(host, approvedSample: 1.0);
            Assert.Equal(4, all.Count);
            Assert.Equal(3, all.Count(o => o.Status == "approved"));
            Assert.All(all, o => Assert.Matches("^person[0-9]+@redacted[.]example$", o.Arguments!["to"]!.ToString())); // still redacted
        }
    }

    [Fact]
    public async Task Shadow_cases_carry_the_real_message_ids_the_skill_served_and_peoples_decisions()
    {
        var (host, pair) = await WithDecisions();
        await using (pair)
        {
            var cases = await ShadowCases.CollectAsync(host);
            Assert.Equal(4, cases.Count);
            var rejected = Assert.Single(cases, c => c.Decisions.Any(d => d.Decision == "rejected"));
            Assert.Equal(["reply-draft@4"], rejected.SkillsServed);
            Assert.Single(rejected.MessageIds);
            Assert.StartsWith("<m", rejected.MessageIds[0]);                                   // real, not redacted
            Assert.Equal("customer@shop.example", rejected.Decisions[0].Arguments!["to"]!.ToString());
            Assert.Equal("Too long.", rejected.Decisions[0].Reason);
            Assert.Contains("not redacted", ShadowCases.ToJsonLines(cases));
        }
    }
}
