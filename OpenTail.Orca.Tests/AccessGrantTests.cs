namespace OpenTail.Orca.Tests;

/// <summary>Temporary access for one task: access.request, decided by a person, grants a tool for a time or a number of calls.</summary>
public class AccessGrantTests
{
    private const string Policy = """
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "SessionlessTainted": false,
          "Tools": {
            "ledger.append": { "Mode": "Execute" }, "mail.send": { "Mode": "Queue" },
            "access.request": { "Mode": "Queue", "Reviewer": "Human", "ReviewerGroup": "approvers", "SelfApproval": "Forbid", "AllowEdits": false }
          },
          "Agents": {
            "reader": { "Tools": [ "clock.now" ], "Requestable": [ "ledger.append" ] },
            "plain":  { "Tools": [ "clock.now" ] }
          }
        }
        """;

    private static (OpenTailHost Host, ManualClock Clock, FakeInboxModule Module) Build(string policy = Policy)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var module = new FakeInboxModule();
        return (new OpenTailHostBuilder().AddModule(module).UseClock(clock).UsePolicy(PolicyConfig.Parse(policy)).Build(), clock, module);
    }

    private static ActionRequest Call(string tool, string args = "{}", string profile = "reader") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, "key:bot")) { Profile = profile };

    private static ActionReview Approve() => new("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "admin:rita", Groups = ["approvers"] };

    private static async Task<ActionRecord> Granted(OpenTailHost host, int minutes = 60, int calls = 2)
    {
        var ask = await host.Queue.SubmitAsync(Call("access.request", $$"""{"tool":"ledger.append","reason":"Record a refund for ticket 42.","minutes":{{minutes}},"calls":{{calls}}}"""));
        Assert.Equal(ActionStatus.Pending, ask.Status);
        Assert.Contains("Grant ledger.append for", ask.PreviewJson);
        return await host.Queue.ReviewAsync(ask.Id, Approve());
    }

    [Fact]
    public async Task An_approved_request_grants_the_tool_for_that_identity_only()
    {
        var (host, _, module) = Build();
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"x"}"""))).Error!.Code);

        Assert.Equal(ActionStatus.Succeeded, (await Granted(host)).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"refund"}"""))).Status);
        Assert.Single(module.Ledger);
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "granted_call" && e.Detail!.Contains("1 call(s) left"));

        var other = new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, "key:other")) { Profile = "reader" };
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(other)).Error!.Code);
    }

    [Fact]
    public async Task A_grant_ends_after_its_calls_or_its_time_whichever_first()
    {
        var (host, clock, _) = Build();
        await Granted(host, calls: 2);
        await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"1"}"""));
        await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"2"}"""));
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"3"}"""))).Error!.Code);

        await Granted(host, minutes: 30, calls: 10);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"late"}"""))).Error!.Code);
    }

    [Fact]
    public async Task Only_requestable_tools_can_be_asked_for_and_only_by_profiles_that_have_any()
    {
        var (host, _, _) = Build();
        var wrong = await host.Queue.SubmitAsync(Call("access.request", """{"tool":"mail.send","reason":"Need it."}"""));
        Assert.Equal(("invalid_arguments", ActionStatus.Denied), (wrong.Error!.Code, wrong.Status));
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("access.request", """{"tool":"ledger.append","reason":"x"}""", profile: "plain"))).Error!.Code);
    }

    [Fact]
    public async Task The_requester_cannot_grant_itself_and_the_grant_can_be_revoked()
    {
        var (host, _, _) = Build();
        var ask = await host.Queue.SubmitAsync(Call("access.request", """{"tool":"ledger.append","reason":"Refund."}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Queue.ReviewAsync(ask.Id, new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "key:bot", Groups = ["approvers"] }));

        await host.Queue.ReviewAsync(ask.Id, Approve());
        var grant = Assert.Single(host.Grants.Active("key:bot"));
        Assert.True(host.Grants.Revoke(grant.Id));
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"x"}"""))).Error!.Code);
    }

    [Fact]
    public async Task A_granted_tool_appears_in_tools_list_until_the_grant_ends()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock).UsePolicy(PolicyConfig.Parse(Policy))
            .UseCallerResolver(_ => new CallerIdentity("key:bot", "bot", CallerKind.Agent) { Profile = "reader" }).Build();
        await using var pair = await McpPair.StartAsync(host);
        var before = (await pair.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("access.request", before);
        Assert.DoesNotContain("ledger.append", before);

        await Granted(host);
        Assert.Contains("ledger.append", (await pair.Client.ListToolsAsync()).Select(t => t.Name));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.DoesNotContain("ledger.append", (await pair.Client.ListToolsAsync()).Select(t => t.Name));
    }

    [Theory]
    [InlineData("""{ "Agents": { "a": { "Tools": [ "clock.now" ], "Requestable": [ "no.such" ] } } }""", "unknown tool 'no.such'")]
    [InlineData("""{ "Tools": { "access.request": { "Mode": "Execute" } }, "Agents": { "a": { "Tools": [ "clock.now" ], "Requestable": [ "ledger.append" ] } } }""", "a person grants access")]
    public void Misconfigured_temporary_access_stops_startup(string policy, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(policy));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Without_requestable_tools_there_is_no_access_request_tool()
    {
        var (host, _, _) = Build("""{ "DefaultReadMode": "Execute" }""");
        Assert.False(host.Catalog.TryGet("access.request", out _));
    }
}
