namespace OpenTail.Host.Tests;

/// <summary>Phase 4: previews, blast-radius limits, trust ramps, explain, redaction.</summary>
public class SafetyTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    private static ActionRequest Send(string to = "a@b.c", string? session = null) =>
        new("mail.send", $"{{\"to\":\"{to}\",\"body\":\"hi\"}}", Me, session);

    private static ActionReview Approve(string? reviewer = "alice") =>
        new(reviewer!, ReviewDecision.Approve, null, DateTimeOffset.UtcNow);

    // ---- previews ----

    [Fact]
    public async Task Preview_is_generated_at_submit_and_stored_for_the_reviewer()
    {
        var (host, _, audit) = Hosts.Build(Hosts.SendQueued);
        var pending = await host.Queue.SubmitAsync(Send("boss@corp.example"));
        Assert.Contains("Send an email to boss@corp.example", pending.PreviewJson);
        Assert.False(pending.PreviewUnavailable);
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("boss@corp.example"));
    }

    [Fact]
    public async Task High_impact_tool_without_a_preview_is_flagged_not_silently_summarised()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "files.delete": { "Mode": "Queue" }, "inbox.read": { "Mode": "Execute" } } }""");
        var pending = await host.Queue.SubmitAsync(new ActionRequest("files.delete", "{\"path\":\"x\"}", Me));
        Assert.Null(pending.PreviewJson);
        Assert.True(pending.PreviewUnavailable);
    }

    // ---- blast radius ----

    [Fact]
    public async Task Rate_limit_stops_execute_mode_and_a_reviewer_can_retry_after_the_window()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, module, _) = Hosts.Build(
            """{ "Tools": { "mail.send": { "Mode": "Execute", "RateLimit": { "PerHour": 2 } } } }""", clock);
        var s = host.Sessions.Start();

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(session: s))).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(session: s))).Status);
        var third = await host.Queue.SubmitAsync(Send(session: s));

        Assert.Equal(ActionStatus.Failed, third.Status);
        Assert.Equal("rate_limited", third.Error!.Code);
        Assert.True(third.Error.Retryable);
        Assert.Equal(2, module.SendCalls);

        clock.Advance(TimeSpan.FromHours(2));
        var retried = await host.Queue.ReviewAsync(third.Id,
            new ActionReview("alice", ReviewDecision.Retry, "window passed", clock.GetUtcNow()));
        Assert.Equal(ActionStatus.Succeeded, retried.Status);
        Assert.Equal(3, module.SendCalls);
    }

    [Fact]
    public async Task Rate_limit_binds_approved_actions_too()
    {
        var (host, module, _) = Hosts.Build(
            """{ "Tools": { "mail.send": { "Mode": "Queue", "RateLimit": { "PerDay": 1 } } } }""");
        var a = await host.Queue.SubmitAsync(Send("a@b.c"));
        var b = await host.Queue.SubmitAsync(Send("b@b.c"));

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(a.Id, Approve())).Status);
        var second = await host.Queue.ReviewAsync(b.Id, Approve());

        Assert.Equal(ActionStatus.Failed, second.Status);
        Assert.Equal("rate_limited", second.Error!.Code);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Pending_cap_per_requester_is_enforced()
    {
        var (host, _, _) = Hosts.Build("""{ "MaxPendingPerRequester": 2, "Tools": { "mail.send": { "Mode": "Queue" } } }""");
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send("1@b.c"))).Status);
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send("2@b.c"))).Status);
        var third = await host.Queue.SubmitAsync(Send("3@b.c"));
        Assert.Equal(ActionStatus.Denied, third.Status);
        Assert.Equal("queue_full", third.Error!.Code);

        // Another requester is unaffected.
        var other = await host.Queue.SubmitAsync(Send("4@b.c") with { Requester = new ActionRequester(RequesterKind.User, "someone-else") });
        Assert.Equal(ActionStatus.Pending, other.Status);
    }

    // ---- trust ramp ----

    private const string RampPolicy = """{ "Tools": { "flaky.op": { "Mode": "Queue", "TrustRamp": { "AfterCleanApprovals": 2 } } } }""";

    private static ActionRequest Flaky(bool fail = false) => new("flaky.op", $"{{\"fail\":{fail.ToString().ToLowerInvariant()}}}", Me);

    [Fact]
    public async Task Trust_ramp_offers_promotion_only_after_clean_approvals_and_only_an_operator_promotes()
    {
        var (host, _, audit) = Hosts.Build(RampPolicy);

        Assert.False((await host.Trust.EvaluateAsync("flaky.op")).Eligible);
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id, Approve());
        Assert.False((await host.Trust.EvaluateAsync("flaky.op")).Eligible);
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id, Approve());

        var s = await host.Trust.EvaluateAsync("flaky.op");
        Assert.True(s.Eligible);
        Assert.Equal(2, s.CleanApprovals);
        // Eligible is not promoted: the next call still queues.
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Flaky())).Status);

        Assert.True(await host.Trust.PromoteAsync("flaky.op", "operator-dmitri"));
        Assert.Contains(audit.Entries, e => e.Event == "trust_promoted" && e.Actor == "operator-dmitri");
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Flaky())).Status);
    }

    [Fact]
    public async Task First_failure_after_promotion_withdraws_it()
    {
        var (host, _, audit) = Hosts.Build(RampPolicy);
        for (var i = 0; i < 2; i++)
            await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id, Approve());
        Assert.True(await host.Trust.PromoteAsync("flaky.op", "op"));

        var failed = await host.Queue.SubmitAsync(Flaky(fail: true));
        Assert.Equal(ActionStatus.Failed, failed.Status);
        Assert.False(host.Policy.IsPromoted("flaky.op"));
        Assert.Contains(audit.Entries, e => e.Event == "trust_demoted");
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Flaky())).Status);
    }

    [Fact]
    public async Task A_rejection_or_an_edit_breaks_the_clean_streak()
    {
        var (host, _, _) = Hosts.Build(RampPolicy);
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id, Approve());
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id,
            new ActionReview("alice", ReviewDecision.Reject, "no", DateTimeOffset.UtcNow));
        Assert.Equal(0, (await host.Trust.EvaluateAsync("flaky.op")).CleanApprovals);
        Assert.False(await host.Trust.PromoteAsync("flaky.op", "op"));

        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Flaky())).Id, Approve() with { EditedArgumentsJson = "{\"fail\":false}" });
        Assert.Equal(0, (await host.Trust.EvaluateAsync("flaky.op")).CleanApprovals);
    }

    [Fact]
    public async Task Promotion_never_overrides_the_taint_rule_for_high_impact_tools()
    {
        var (host, _, _) = Hosts.Build(
            """{ "Tools": { "mail.send": { "Mode": "Queue", "TrustRamp": { "AfterCleanApprovals": 1 } } } }""");
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Send())).Id, Approve());
        Assert.True(await host.Trust.PromoteAsync("mail.send", "op"));

        var clean = host.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(session: clean))).Status);

        var dirty = host.Sessions.Start();
        host.Sessions.Taint(dirty);
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send(session: dirty))).Status);
    }

    [Fact]
    public void Trust_ramp_on_a_non_queue_tool_fails_validation()
    {
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "flaky.op": { "Mode": "Execute", "TrustRamp": { "AfterCleanApprovals": 2 } } } }"""));
        Assert.Contains("trust ramp", Assert.Throws<InvalidOperationException>(() => b.Build()).Message);
    }

    // ---- explain ----

    [Fact]
    public void Explain_names_the_rule_that_decided()
    {
        var (host, _, _) = Hosts.Build("""{ "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Execute" } } }""");

        var unknown = host.Explain("nope");
        Assert.False(unknown.Known);

        var read = host.Explain("clock.now");
        Assert.Equal("default-read", read.Rule);
        Assert.Equal(ToolMode.Execute, read.EffectiveMode);

        var send = host.Explain("mail.send");
        Assert.Equal(ToolMode.Execute, send.EffectiveMode);
        var sendTainted = host.Explain("mail.send", tainted: true);
        Assert.Equal(ToolMode.Queue, sendTainted.EffectiveMode);
        Assert.Equal("tool-policy+taint:queue", sendTainted.Rule);
        Assert.Contains(sendTainted.Notes, n => n.Contains("untrusted"));

        var del = host.Explain("files.delete");
        Assert.Equal("no-policy:deny", del.Rule);
        Assert.False(del.Advertised);
        Assert.Contains(host.Explain("inbox.read").Notes, n => n.Contains("taints"));
    }

    // ---- redaction ----

    [Theory]
    [InlineData("login failed: password=hunter2 for user bob", "hunter2")]
    [InlineData("Authorization: Bearer abcdef1234567890abcdef", "abcdef1234567890abcdef")]
    [InlineData("connectionString=Server=db;Pwd=xyz123;User=sa", "xyz123")]
    [InlineData("key sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345 rejected", "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    [InlineData("blob QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5 end", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5")]
    public void Scrub_removes_secrets(string input, string secret)
    {
        var scrubbed = Redaction.Scrub(input);
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    [Fact]
    public void Scrub_leaves_ordinary_messages_alone_and_truncates_long_ones()
    {
        Assert.Equal("File not found: report.pdf", Redaction.Scrub("File not found: report.pdf"));
        Assert.Equal("", Redaction.Scrub(null));
        var long_ = Redaction.Scrub(string.Join(' ', Enumerable.Repeat("word", 400)));
        Assert.True(long_.Length <= Redaction.MaxMessageLength + 1);
    }

    [Fact]
    public async Task Errors_returned_to_the_model_and_stored_are_scrubbed()
    {
        var (host, _, audit) = Hosts.Build("""{ "Tools": { "leaky.op": { "Mode": "Execute" } } }""");
        var r = await host.Queue.SubmitAsync(new ActionRequest("leaky.op", "{}", Me));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.DoesNotContain("hunter2", r.Error!.Message);
        Assert.DoesNotContain("hunter2", ActionViews.Response(r));
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("hunter2"));
    }
}
