namespace OpenTail.Host.Tests;

public class QueueBehaviourTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    private static ActionRequest Send(string? key = null, string? session = null) =>
        new("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}", Me, session, key);

    [Fact]
    public async Task Two_concurrent_approvals_execute_once()
    {
        var (host, module, _) = Hosts.Build(Hosts.SendQueued);
        var pending = await host.Queue.SubmitAsync(Send());
        var review = new ActionReview("r", ReviewDecision.Approve, null, DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { return await host.Queue.ReviewAsync(pending.Id, review); }
            catch (InvalidOperationException) { return null; }
        }));

        Assert.Equal(1, module.SendCalls);
        Assert.Contains(results, r => r?.Status == ActionStatus.Succeeded);
    }

    [Fact]
    public async Task Idempotency_key_returns_the_same_action_and_runs_once()
    {
        var (host, module, _) = Hosts.Build(Hosts.SendExecute);
        var untaintedSession = host.Sessions.Start();
        var a = await host.Queue.SubmitAsync(Send("k1", untaintedSession));
        var b = await host.Queue.SubmitAsync(Send("k1", untaintedSession));
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Pending_actions_expire_on_sweep()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, _, _) = Hosts.Build(Hosts.SendQueued, clock);
        var pending = await host.Queue.SubmitAsync(Send());
        Assert.Equal(clock.GetUtcNow().AddHours(1), pending.ExpiresAt);

        Assert.Equal(0, await host.Queue.ExpireDueAsync());
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await host.Queue.ExpireDueAsync());
        Assert.Equal(ActionStatus.Expired, (await host.Store.GetAsync(pending.Id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(pending.Id,
            new ActionReview("r", ReviewDecision.Approve, null, clock.GetUtcNow())));
    }

    [Fact]
    public async Task An_overdue_action_cannot_be_approved_even_before_the_sweep_runs()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, module, _) = Hosts.Build(Hosts.SendQueued, clock);
        var pending = await host.Queue.SubmitAsync(Send());

        clock.Advance(TimeSpan.FromHours(2)); // past the 1 hour ExpireAfter, no sweep yet
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(pending.Id,
            new ActionReview("r", ReviewDecision.Approve, null, clock.GetUtcNow())));
        Assert.Contains("expired", ex.Message);
        Assert.Equal(ActionStatus.Expired, (await host.Store.GetAsync(pending.Id))!.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task Only_the_requester_can_cancel_and_only_while_pending()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        var pending = await host.Queue.SubmitAsync(Send());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Queue.CancelAsync(pending.Id, new ActionRequester(RequesterKind.User, "someone-else")));
        var cancelled = await host.Queue.CancelAsync(pending.Id, Me);
        Assert.Equal(ActionStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.CancelAsync(pending.Id, Me));
    }

    [Fact]
    public async Task Sessionless_actions_can_be_created_and_queried()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        var r = await host.Queue.SubmitAsync(Send());
        Assert.Null(r.SessionId);
        Assert.True(r.Tainted);
        var rows = await host.Store.QueryAsync(new ActionQuery { RequesterId = "me" });
        Assert.Single(rows);
    }

    [Fact]
    public async Task Unknown_session_handle_is_rejected()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Queue.SubmitAsync(Send(session: "ses_nope")));
    }

    [Fact]
    public async Task Edited_arguments_win_but_the_original_is_kept()
    {
        var (host, module, _) = Hosts.Build(Hosts.SendQueued);
        var pending = await host.Queue.SubmitAsync(Send());
        var done = await host.Queue.ReviewAsync(pending.Id, new ActionReview("r", ReviewDecision.Approve, null, DateTimeOffset.UtcNow)
        {
            EditedArgumentsJson = "{\"to\":\"safe@corp.example\",\"body\":\"edited\"}",
        });
        Assert.Equal(("safe@corp.example", "edited"), Assert.Single(module.Sent));
        Assert.Contains("a@b.c", done.ArgumentsJson);
    }

    [Fact]
    public async Task Input_guardrail_blocks_before_the_tool_runs()
    {
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder()
            .AddModule(module)
            .UsePolicy(PolicyConfig.Parse(Hosts.SendExecute))
            .AddInputGuardrail(new BlockWord("evil"))
            .Build();
        var session = host.Sessions.Start();
        var r = await host.Queue.SubmitAsync(new ActionRequest("mail.send", "{\"to\":\"evil@x.y\",\"body\":\"b\"}", Me, session));
        // Tainted? No: clean session + Execute policy => ran, but the guardrail stopped it.
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Equal("guardrail_blocked", r.Error!.Code);
        Assert.Equal(0, module.SendCalls);
    }

    private sealed class BlockWord(string word) : OpenTail.Core.Tools.Abstractions.IToolInputGuardrail
    {
        public string Key => "block-word";
        public Task<OpenTail.Core.Tools.Abstractions.ToolGuardrailResult> CheckInputAsync(string toolName, string args, CancellationToken ct = default) =>
            Task.FromResult(args.Contains(word)
                ? OpenTail.Core.Tools.Abstractions.ToolGuardrailResult.Reject("blocked")
                : OpenTail.Core.Tools.Abstractions.ToolGuardrailResult.Ok());
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
