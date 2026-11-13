using OpenTail.Orca.A2A;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

/// <summary>P0 of the reviewer gate plan: the retry guard, the A2A submit pin, and fail-closed AI review without a policy snapshot.</summary>
public class P0GateTests
{
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
}
