namespace OpenTail.Host.Tests;

/// <summary>Batch 03b-2, work side: completion carries what the completer holds (E1), the <c>Completed</c> event (E2), and cancelling only
/// an open item (E7). The existing work tests are not changed; these run beside them.</summary>
public class WorkCompletionTests
{
    private static readonly CallerIdentity Bot = new("key:bot", "bot", CallerKind.Agent) { Profile = "clerk" };
    private static readonly CallerIdentity OtherClerk = new("key:bot2", "bot2", CallerKind.Agent) { Profile = "clerk" };

    private const string Policy = """
        { "DefaultReadMode": "Execute",
          "Work": { "Enabled": true },
          "Agents": { "clerk": { "Tools": [ "inbox.*", "clock.*", "work.*" ] } },
          "Tools": { "work.delegate": { "Mode": "Execute" } } }
        """;

    private static OpenTailHost Build(string policy = Policy)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-20T09:00:00Z"));
        return new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy)).UseClock(clock).Build();
    }

    private static Task<WorkItem> NewTask(OpenTailHost host, string goal = "Check the ledger.") =>
        host.Work.CreateAsync(goal, WorkService.ForProfile("clerk"), "test");

    [Fact]
    public async Task A_completer_holding_a_label_leaves_it_on_the_done_item()
    {
        var host = Build();
        var item = await NewTask(host);
        var session = host.Sessions.Start(Bot.Subject);
        await host.Work.ClaimNextAsync(Bot, session);
        host.Labels.GainCarried(session, Bot.Subject, [new CarriedLabel("finance", "erp:ledger")]);

        var done = await host.Work.CompleteAsync(item.Id, Bot, "Balance is 40.", session);

        Assert.Equal(WorkStatus.Done, done.Status);
        Assert.Contains(new CarriedLabel("finance", "erp:ledger"), done.Labels);
        Assert.Contains(new CarriedLabel("finance", "erp:ledger"), host.Work.Store.Get(item.Id)!.Labels);
    }

    [Fact]
    public async Task A_reader_of_the_done_item_gains_the_completers_label()
    {
        var host = Build();
        var item = await NewTask(host);
        var session = host.Sessions.Start(Bot.Subject);
        await host.Work.ClaimNextAsync(Bot, session);
        host.Labels.GainCarried(session, Bot.Subject, [new CarriedLabel("finance", "erp:ledger")]);
        await host.Work.CompleteAsync(item.Id, Bot, "Balance is 40.", session);

        var readerSession = host.Sessions.Start(OtherClerk.Subject);
        Assert.Empty(host.Labels.Held(readerSession, OtherClerk.Subject));

        Assert.NotNull(host.Work.Read(item.Id, OtherClerk, readerSession, out _));

        var held = Assert.Single(host.Labels.Held(readerSession, OtherClerk.Subject));
        Assert.Equal("finance", held.Label);
        Assert.Contains("erp:ledger", held.Sources);
    }

    [Fact]
    public async Task A_completion_carries_its_session_taint_and_a_clean_session_completes_clean()
    {
        var host = Build(Policy.Replace("\"DefaultReadMode\": \"Execute\",", "\"DefaultReadMode\": \"Execute\", \"SessionlessTainted\": false,"));

        var clean = await NewTask(host, "Clean task.");
        var cleanSession = host.Sessions.Start(Bot.Subject);
        await host.Work.ClaimNextAsync(Bot, cleanSession);
        Assert.False((await host.Work.CompleteAsync(clean.Id, Bot, "ok", cleanSession)).Tainted);

        var dirty = await NewTask(host, "Dirty task.");
        var dirtySession = host.Sessions.Start(Bot.Subject);
        await host.Work.ClaimNextAsync(Bot, dirtySession);
        host.Sessions.Taint(dirtySession);
        Assert.True((await host.Work.CompleteAsync(dirty.Id, Bot, "ok", dirtySession)).Tainted);
    }

    [Fact]
    public async Task A_completion_without_a_session_carries_the_callers_own_taint_which_defaults_to_tainted()
    {
        var host = Build();   // SessionlessTainted is not set: the policy's default is true
        var item = await NewTask(host);
        await host.Work.ClaimNextAsync(Bot, null);

        Assert.True((await host.Work.CompleteAsync(item.Id, Bot, "ok")).Tainted);
    }

    [Fact]
    public async Task Completed_is_raised_once_with_the_done_item_and_a_failing_handler_does_not_fail_the_completion()
    {
        var host = Build();
        var seen = new List<WorkItem>();
        host.Work.Completed += _ => throw new InvalidOperationException("a handler that fails");
        host.Work.Completed += seen.Add;
        var item = await NewTask(host);
        await host.Work.ClaimNextAsync(Bot, null);

        var done = await host.Work.CompleteAsync(item.Id, Bot, "fine");

        Assert.Equal(WorkStatus.Done, done.Status);
        var only = Assert.Single(seen);
        Assert.Equal((item.Id, WorkStatus.Done, "fine"), (only.Id, only.Status, only.Summary));

        await Assert.ThrowsAsync<WorkException>(() => host.Work.CompleteAsync(item.Id, Bot, "again"));
        Assert.Single(seen);   // a refused completion raises nothing
    }

    [Fact]
    public async Task Cancel_only_if_open_cancels_an_open_item_and_leaves_a_claimed_one_to_its_claimant()
    {
        var host = Build();
        var open = await NewTask(host, "Open task.");
        var cancelled = await host.Work.CancelAsync(open.Id, "partner:acme", onlyIfOpen: true);
        Assert.Equal(WorkStatus.Cancelled, cancelled!.Status);

        var claimed = await NewTask(host, "Claimed task.");
        await host.Work.ClaimNextAsync(Bot, null);
        Assert.Null(await host.Work.CancelAsync(claimed.Id, "partner:acme", onlyIfOpen: true));
        var still = host.Work.Store.Get(claimed.Id)!;
        Assert.Equal((WorkStatus.Claimed, Bot.Subject), (still.Status, still.ClaimedBy));
    }

    [Fact]
    public async Task Without_only_if_open_cancel_still_stops_a_claimed_item()
    {
        var host = Build();
        var item = await NewTask(host);
        await host.Work.ClaimNextAsync(Bot, null);

        var cancelled = await host.Work.CancelAsync(item.Id, "operator:rita");

        Assert.Equal((WorkStatus.Cancelled, (string?)null), (cancelled!.Status, cancelled.ClaimedBy));
    }
}
