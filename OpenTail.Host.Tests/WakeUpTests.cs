using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenTail.Host.Notifications;

namespace OpenTail.Host.Tests;

/// <summary>Work design, step 3: an item that becomes ready reaches an agent by pull, by a signed URL carrying ids only, or by a
/// command the Host starts.</summary>
public class WakeUpTests
{
    private static readonly CallerIdentity Clerk = new("key:clerk", "clerk", CallerKind.Agent) { Profile = "clerk" };

    private static OpenTailHost Build(string extra = "") => new OpenTailHostBuilder().AddModule(new FakeInboxModule())
        .UsePolicy(PolicyConfig.Parse($$"""{ "Work": { "Enabled": true {{extra}} }, "Agents": { "clerk": { "Tools": [ "work.*" ] } } }""")).Build();

    [Fact]
    public async Task Ready_fires_whenever_an_item_waits_for_someone_and_not_when_it_ends()
    {
        var host = Build();
        var seen = new List<string>();
        host.Work.Ready += i => seen.Add(i.Status.ToString());
        host.Work.Ready += _ => throw new InvalidOperationException("a broken wake-up never breaks the work");
        var item = await host.Work.CreateAsync("Reconcile.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.ClaimNextAsync(Clerk, null);
        await host.Work.HandBackAsync(item.Id, Clerk, "Not mine.");
        await host.Work.ClaimNextAsync(Clerk, null);
        await host.Work.CompleteAsync(item.Id, Clerk, "done");
        Assert.Equal(["Open", "Open"], seen);
    }

    [Fact]
    public async Task A_configured_runner_is_started_with_the_work_id_up_to_its_limit()
    {
        var host = Build(""", "Runners": [ { "For": "profile:clerk", "Command": "agent-run", "Arguments": [ "--work", "{workId}" ], "MaxConcurrent": 1 } ]""");
        var started = new List<ProcessStartInfo>();
        using var runners = new WorkRunners(host.Work, host.Audit, info => { started.Add(info); return Process.GetCurrentProcess(); });
        var first = await host.Work.CreateAsync("One.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.CreateAsync("Two.", WorkService.ForProfile("clerk"), "admin:rita");   // busy: stays in the inbox
        await host.Work.CreateAsync("Other.", WorkService.ForProfile("nobody"), "admin:rita"); // no runner for it

        // The Host's own runner (from the policy) and the test's both listen; count the test's.
        var mine = Assert.Single(started);
        Assert.Equal("agent-run", mine.FileName);
        Assert.Equal(["--work", first.Id], mine.ArgumentList);
        Assert.Equal(first.Id, mine.Environment["OPENTAIL_WORK_ID"]);
        Assert.False(mine.UseShellExecute);
    }

    private const string Config = """
        {
          "Channels": [ { "Name": "platform", "Type": "webhook", "Url": "https://agents.test/wake", "SecretEnv": "HOOK_SECRET" } ],
          "Routes": [ { "On": ["work_ready"], "Work": [ "profile:clerk" ], "To": ["platform"] } ]
        }
        """;

    [Fact]
    public async Task A_signed_wake_up_carries_ids_only_and_only_for_the_profiles_its_route_names()
    {
        using var db = new TempDb();
        var handler = new ScriptedHandler();
        using var notifier = Notifier.Create(NotificationConfig.Parse(Config), db.Path, null, null, n => n == "HOOK_SECRET" ? "s3cret-s3cret-s3cret" : null, handler);
        var host = Build();
        host.Work.Ready += i => notifier.EnqueueWorkReady(i.Id, i.For, i.Kind.ToString().ToLowerInvariant());
        var item = await host.Work.CreateAsync("Pay the ACME invoice of 12,400 EUR.", WorkService.ForProfile("clerk"), "admin:rita");
        await host.Work.CreateAsync("Research.", WorkService.ForProfile("researcher"), "admin:rita");
        await notifier.DispatchOnceAsync();

        var wake = Assert.Single(handler.Requests);
        var work = JsonNode.Parse(wake.Body)!["work"]!;
        Assert.Equal((item.Id, "profile:clerk", "task"), (work["workId"]!.ToString(), work["for"]!.ToString(), work["kind"]!.ToString()));
        Assert.DoesNotContain("ACME", wake.Body);
    }

    [Fact]
    public void Wake_ups_go_only_to_webhook_channels()
    {
        var errors = NotificationConfig.Parse("""
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["work_ready"], "To": ["slack"] } ] }
            """).Validate(n => n == "SLACK_URL" ? "https://hooks.slack.test/x" : null);
        Assert.Contains(errors, e => e.Contains("goes to a webhook channel (an agent platform)"));
    }
}
