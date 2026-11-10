namespace OpenTail.Host.Tests;

/// <summary>A rate limit is a slot taken in the same transaction that checks it: calls that arrive together must get exactly the permitted
/// number through, with a real database and real parallel callers.</summary>
public sealed class RateLimitConcurrencyTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    private static ActionRequest Send(string to, string session) =>
        new("mail.send", $"{{\"to\":\"{to}\",\"body\":\"hi\"}}", Me, session);

    [Fact]
    public async Task Calls_that_arrive_together_get_exactly_the_permitted_number_through_an_execute_limit()
    {
        // Every audit write waits, so every call is inserted before any call looks at the count: the callers overlap for as long as one
        // audit write takes. A fresh session, because a caller that has read untrusted content is queued before the rate limit is looked at.
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Execute", "RateLimit": { "PerHour": 2 } } } }"""))
            .UseStore(store).UseAudit(new SlowAuditSink(new SqliteAuditSink(store), TimeSpan.FromMilliseconds(300))).Build();
        var session = host.Sessions.Start();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => host.Queue.SubmitAsync(Send($"p{i}@b.c", session)))));

        Assert.Equal(2, results.Count(r => r.Status == ActionStatus.Succeeded));
        Assert.All(results.Where(r => r.Status != ActionStatus.Succeeded), r =>
        {
            Assert.Equal(ActionStatus.Failed, r.Status);
            Assert.Equal("rate_limited", r.Error!.Code);
            Assert.True(r.Error.Retryable);
        });
        Assert.Equal(2, module.SendCalls);
    }

    [Fact]
    public async Task Approved_calls_that_run_together_get_exactly_the_permitted_number_through_a_limit()
    {
        // The same race on the claim path: approved actions are run by ExecuteAsync, which must claim its slot with the rate limit check.
        using var db = new TempDb();
        var module = new FakeInboxModule();
        var store = new SqliteActionStore(db.Path);
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "RateLimit": { "PerHour": 2 } } } }"""))
            .UseStore(store).UseAudit(new SlowAuditSink(new SqliteAuditSink(store), TimeSpan.FromMilliseconds(300))).Build();
        var session = host.Sessions.Start();
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var pending = await host.Queue.SubmitAsync(Send($"p{i}@b.c", session));
            var approved = await host.Store.TransitionAsync(pending.Id, ActionStatus.Pending, ActionStatus.Approved, r => r);
            ids.Add(approved!.Id);
        }

        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => host.Executor.ExecuteAsync(id))));

        Assert.Equal(2, results.Count(r => r.Status == ActionStatus.Succeeded));
        Assert.All(results.Where(r => r.Status != ActionStatus.Succeeded), r =>
        {
            Assert.Equal(ActionStatus.Failed, r.Status);
            Assert.Equal("rate_limited", r.Error!.Code);
        });
        Assert.Equal(2, module.SendCalls);
    }

    /// <summary>An audit trail that is slow to write, as a busy disk is.</summary>
    private sealed class SlowAuditSink(IAuditSink inner, TimeSpan delay) : IAuditSink
    {
        public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            await inner.AppendAsync(entry, ct);
        }
    }
}
