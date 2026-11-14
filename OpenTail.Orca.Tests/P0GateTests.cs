using OpenTail.Orca.A2A;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>P0 of the reviewer gate plan: the retry guard, the A2A submit pin, and fail-closed AI review without a policy snapshot.</summary>
public class P0GateTests
{
    private const string PartnerA = "5f1c2a9e-8b7d-4c3e-9a10-2b4d6e8f0a12";

    [Theory]
    [InlineData(null, false, false, false, null, true)]                          // no recorded error: refused
    [InlineData("tool_exception", false, false, false, "checked", true)]         // not retryable: refused for everyone
    [InlineData("tool_unavailable", true, false, false, null, false)]            // transient, a person retries: allowed
    [InlineData("tool_unavailable", true, false, true, null, true)]              // transient, an AI, not idempotent: refused
    [InlineData("interrupted", true, false, false, null, true)]                  // unknown outcome, no check recorded: refused
    [InlineData("interrupted", true, false, false, "Checked the mail server; not sent.", false)] // unknown outcome, person checked: allowed
    [InlineData("interrupted", true, false, true, "Checked the mail server.", true)]             // unknown outcome, an AI: refused
    [InlineData("interrupted", true, true, true, null, false)]                   // unknown outcome, idempotent by key, an AI: allowed
    public void Retry_rules_refuse_unsafe_retries_for_every_reviewer(string? code, bool retryable, bool idempotent, bool byAi, string? reconciliation, bool refused)
    {
        var error = code is null ? null : new ActionError(code, "message", retryable);
        Assert.Equal(refused, RetryRules.Refusal(error, idempotent, byAi, reconciliation) is not null);
    }

    private static IReadOnlyList<string> StartupErrors(TempDb db, string submitExtra)
    {
        var policy = $$"""
            { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce", "Work": { "Enabled": true },
              "Tools": { "a2a.submit": { "Mode": "Queue"{{submitExtra}} }, "a2a.reply": { "Mode": "Queue", "AllowEdits": false } },
              "Agents": { "clerk": { "Tools": [ "a2a.submit", "a2a.reply" ] } } }
            """;
        var builder = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(policy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        var extension = new A2AFrontDoor();
        var options = new HostHttpOptions { A2A = A2aSliceTests.EnabledA2a, Extensions = [extension], PublicBaseUrl = "http://127.0.0.1" };
        extension.AddModules(builder, options);
        var host = builder.Build();
        return extension.Check(host, options);
    }

    [Fact]
    public void A2A_submit_that_an_ai_would_decide_is_refused_at_startup()
    {
        using var db = new TempDb();
        Assert.Contains(StartupErrors(db, ", \"Reviewer\": \"Ai\""), e => e.Contains("must have Reviewer Human"));
    }

    [Fact]
    public void A2A_submit_with_the_default_reviewer_passes_the_pin()
    {
        using var db = new TempDb();
        Assert.DoesNotContain(StartupErrors(db, ""), e => e.Contains("must have Reviewer Human"));
    }

    [Fact]
    public void A2A_submit_rule_that_an_ai_would_decide_is_refused_at_startup()
    {
        using var db = new TempDb();
        var rules = ", \"Rules\": [ { \"Name\": \"by-ai\", \"Mode\": \"Queue\", \"Reviewer\": \"Ai\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" } } ]";
        Assert.Contains(StartupErrors(db, rules), e => e == "\"a2a.submit\" rule \"by-ai\" names reviewer Ai: a person must decide each partner request.");
    }

    [Fact]
    public void A2A_submit_rules_that_keep_a_person_deciding_still_start()
    {
        using var db = new TempDb();
        var human = ", \"Rules\": [ { \"Name\": \"by-human\", \"Mode\": \"Queue\", \"Reviewer\": \"Human\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" } } ]";
        Assert.DoesNotContain(StartupErrors(db, human), e => e.Contains("\"a2a.submit\" rule"));
        var trusted = ", \"Rules\": [ { \"Name\": \"trusted\", \"Mode\": \"Execute\", \"When\": { \"arg\": \"/partner\", \"equals\": \"" + PartnerA + "\" } } ]";
        Assert.DoesNotContain(StartupErrors(db, trusted), e => e.Contains("\"a2a.submit\" rule"));
    }

    private const string ReplyPolicy = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "DataFlowMode": "Enforce", "Work": { "Enabled": true },
          "Tools": { "a2a.submit": { "Mode": "Queue" }, "a2a.reply": { "Mode": "Queue", "AllowEdits": false, "AiMayRefuse": true, "AiReviewers": ["bot"] } },
          "Agents": { "clerk": { "Tools": [ "a2a.submit", "a2a.reply" ] } } }
        """;

    /// <summary>G3 (E1): a reply a person decides may still be refused by an AI reviewer, and never approved by one.</summary>
    [Fact]
    public async Task An_ai_may_refuse_a_queued_reply_but_never_approve_it()
    {
        using var db = new TempDb();
        var builder = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse(ReplyPolicy)).UseSqlite(db.Path, SqliteSynchronous.Full).UseHttpSignIn();
        var extension = new A2AFrontDoor();
        var options = new HostHttpOptions { A2A = A2aSliceTests.EnabledA2a, Extensions = [extension], PublicBaseUrl = "http://127.0.0.1" };
        extension.AddModules(builder, options);
        var host = builder.Build();
        Assert.Empty(extension.Check(host, options));

        var requester = new ActionRequester(RequesterKind.AiSession, "key:mcp-agent");
        var session = host.Sessions.Start("key:mcp-agent");
        async Task<ActionRecord> QueueReplyAsync(string key) => await host.Queue.SubmitAsync(new ActionRequest("a2a.reply",
            """{"partner":"5f1c2a9e-8b7d-4c3e-9a10-2b4d6e8f0a12","taskId":"task-1","text":"Hello."}""", requester, session, key) { Profile = "clerk" });

        var toRefuse = await QueueReplyAsync("e1-reject");
        var rejected = await host.Queue.ReviewAsync(toRefuse.Id, new ActionReview("bot", ReviewDecision.Reject, "Not this one.", DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") });
        Assert.Equal(ActionStatus.Rejected, rejected.Status);

        var toApprove = await QueueReplyAsync("e1-approve");
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Queue.ReviewAsync(toApprove.Id, new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") }));
        Assert.Contains("human reviewer", ex.Message);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(toApprove.Id))!.Status);
        Assert.Contains(((IAuditReader)host.Audit).Read(toApprove.Id), e => e.Event == "review_refused");
    }
}
