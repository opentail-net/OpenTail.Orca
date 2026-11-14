using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>A module with one tool that reports an unknown outcome, as a tool does when it cannot tell whether its effect happened.</summary>
public sealed class UnknownOutcomeModule : IHostModule
{
    public string Id => "outbox";
    public int Calls { get; private set; }

    public void Register(IToolRegistry registry)
    {
        registry.Add(
            new ToolDescriptor("outbox.send", "Send a message; the outcome may be unknown.", ToolRisk.ExternalEffect, Id),
            AIFunctionFactory.Create((string text) =>
            {
                Calls++;
                throw new ToolFailureException(ToolFailureKind.UnknownOutcome, "The connection dropped after the send.");
            }, "outbox.send", "Send a message; the outcome may be unknown."));
    }
}

/// <summary>
/// Batch 08 of the reviewer gate (section 3, the three settings of section 1a): <c>UnknownOutcomeRetry</c> (D-32), <c>AiOwnRequest</c> (D-37) and
/// <c>DefaultAiReviewers</c> (D-36). Each is set per tool, or as a policy-wide default; the queue resolves it into the action's snapshot; the
/// strict value applies when neither says otherwise; a loosening prints a startup warning. Also the G5 trace and the G8 tests that need no new
/// production code.
/// </summary>
public class Batch08SettingsTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";
    private const string TenMinutes = """{ "Tools": { "mail.send": { "Mode": "Queue", "ApproveDelay": "00:10:00" } } }""";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-14T09:00:00Z");

    private static ActionReview PersonDecides(ReviewDecision d, string name = "rita") =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Admin(name) };

    private static ActionReview AiDecides(string name, ReviewDecision d) =>
        new(name, d, d is ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow) { Subject = Subjects.Ai(name) };

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    // ---- D-32: UnknownOutcomeRetry ----

    [Theory]
    // setting, by an AI, idempotent by key, retries already made, reason given, refused
    [InlineData("Never", false, false, 0, "Checked the mail server.", true)]
    [InlineData("Never", false, true, 0, "Checked the mail server.", false)]    // idempotent by key: the setting does not limit it
    [InlineData("Once", false, false, 0, "Checked the mail server.", false)]
    [InlineData("Once", false, false, 0, null, true)]                           // a reason is still needed
    [InlineData("Once", false, false, 1, "Checked the mail server.", true)]     // a second unknown outcome is final
    [InlineData("WithReason", false, false, 5, "Checked the mail server.", false)]
    [InlineData("WithReason", false, false, 0, null, true)]
    [InlineData("WithReason", true, false, 0, "Checked the mail server.", true)] // an AI never retries an unknown outcome without idempotency
    public void An_unknown_outcome_is_retried_as_its_setting_says(string setting, bool byAi, bool idempotent, int priorRetries, string? reason, bool refused)
    {
        var error = new ActionError(RetryRules.UnknownOutcomeCode, "The Host stopped while this action was running; the outcome is unknown.", true);
        Assert.Equal(refused, RetryRules.Refusal(error, idempotent, byAi, reason, Enum.Parse<UnknownOutcomeRetry>(setting), priorRetries) is not null);
    }

    [Fact]
    public void An_action_queued_before_the_setting_existed_is_retried_as_once()
    {
        var error = new ActionError(RetryRules.UnknownOutcomeCode, "The outcome is unknown.", true);
        Assert.Null(RetryRules.Refusal(error, false, false, "Checked.", null, 0));
        Assert.NotNull(RetryRules.Refusal(error, false, false, "Checked.", null, 1));
    }

    [Fact]
    public void The_setting_resolves_to_never_for_a_destructive_tool_and_to_once_otherwise_unless_it_says_more()
    {
        var plain = PolicyConfig.Parse("""{ "Tools": { "files.delete": { "Mode": "Queue" }, "mail.send": { "Mode": "Queue" } } }""");
        Assert.Equal(UnknownOutcomeRetry.Never, PolicySettings.Resolve(plain.Tools["files.delete"], ToolRisk.Destructive, plain).UnknownOutcomeRetry);
        Assert.Equal(UnknownOutcomeRetry.Once, PolicySettings.Resolve(plain.Tools["mail.send"], ToolRisk.ExternalEffect, plain).UnknownOutcomeRetry);

        var policyWide = PolicyConfig.Parse("""
            { "UnknownOutcomeRetry": "WithReason", "Tools": {
                "files.delete": { "Mode": "Queue" }, "mail.send": { "Mode": "Queue" },
                "ledger.append": { "Mode": "Queue", "UnknownOutcomeRetry": "Once" } } }
            """);
        Assert.Equal(UnknownOutcomeRetry.Never, PolicySettings.Resolve(policyWide.Tools["files.delete"], ToolRisk.Destructive, policyWide).UnknownOutcomeRetry);
        Assert.Equal(UnknownOutcomeRetry.WithReason, PolicySettings.Resolve(policyWide.Tools["mail.send"], ToolRisk.ExternalEffect, policyWide).UnknownOutcomeRetry);
        Assert.Equal(UnknownOutcomeRetry.Once, PolicySettings.Resolve(policyWide.Tools["ledger.append"], ToolRisk.Write, policyWide).UnknownOutcomeRetry);

        var own = PolicyConfig.Parse("""{ "Tools": { "files.delete": { "Mode": "Queue", "UnknownOutcomeRetry": "WithReason" } } }""");
        Assert.Equal(UnknownOutcomeRetry.WithReason, PolicySettings.Resolve(own.Tools["files.delete"], ToolRisk.Destructive, own).UnknownOutcomeRetry);
    }

    [Fact]
    public async Task A_queued_destructive_action_carries_never_in_its_snapshot()
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "files.delete": { "Mode": "Queue" } } }""");
        var queued = await host.Queue.SubmitAsync(new ActionRequest("files.delete", "{\"path\":\"x\"}", Alice, host.Sessions.Start("key:alice-agent")));
        Assert.Equal(UnknownOutcomeRetry.Never, queued.PolicySnapshot!.UnknownOutcomeRetry);
    }

    // ---- D-37: AiOwnRequest ----

    private static string OwnRequest(string? value) =>
        "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"Reviewer\": \"Ai\", \"AiMaxRisk\": \"ExternalEffect\", \"AiMayRefuse\": true, \"AiReviewers\": [\"bot\"]" +
        (value is null ? "" : ", \"AiOwnRequest\": \"" + value + "\"") + " } } }";

    [Theory]
    // setting, decision, refused
    [InlineData(null, ReviewDecision.Approve, true)]         // the default is ForbidApproval
    [InlineData(null, ReviewDecision.Reject, false)]
    [InlineData("ForbidApproval", ReviewDecision.Approve, true)]
    [InlineData("ForbidApproval", ReviewDecision.Reject, false)]
    [InlineData("ForbidAll", ReviewDecision.Approve, true)]
    [InlineData("ForbidAll", ReviewDecision.Reject, true)]
    [InlineData("Allow", ReviewDecision.Approve, false)]
    [InlineData("Allow", ReviewDecision.Reject, false)]
    public async Task An_ai_reviewer_on_a_request_from_its_own_identity_follows_ai_own_request(string? value, ReviewDecision decision, bool refused)
    {
        var (host, module, _) = Hosts.Build(OwnRequest(value));
        var mine = await host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, new ActionRequester(RequesterKind.AiSession, "ai:bot"),
            host.Sessions.Start("ai:bot")));

        if (refused) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mine.Id, AiDecides("bot", decision)));
        else await host.Queue.ReviewAsync(mine.Id, AiDecides("bot", decision));
        Assert.Equal(!refused && decision == ReviewDecision.Approve ? 1 : 0, module.SendCalls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Allow", false)]
    public async Task A_request_from_the_same_person_follows_the_same_setting(string? value, bool refused)
    {
        var json = "{ \"People\": { \"owner\": [\"ai:bot\", \"key:bot-agent\"] }, \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\", \"Reviewer\": \"Ai\", " +
                   "\"AiMaxRisk\": \"ExternalEffect\", \"AiReviewers\": [\"bot\"]" + (value is null ? "" : ", \"AiOwnRequest\": \"" + value + "\"") + " } } }";
        var (host, module, _) = Hosts.Build(json);
        var fromPerson = await host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, new ActionRequester(RequesterKind.AiSession, "key:bot-agent"),
            host.Sessions.Start("key:bot-agent")));

        if (refused) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(fromPerson.Id, AiDecides("bot", ReviewDecision.Approve)));
        else await host.Queue.ReviewAsync(fromPerson.Id, AiDecides("bot", ReviewDecision.Approve));
        Assert.Equal(refused ? 0 : 1, module.SendCalls);
    }

    [Fact]
    public void Ai_own_request_resolves_to_the_tool_then_the_policy_then_forbid_approval()
    {
        var cfg = PolicyConfig.Parse("""{ "AiOwnRequest": "Allow", "Tools": { "mail.send": { "Mode": "Queue" }, "files.delete": { "Mode": "Queue", "AiOwnRequest": "ForbidAll" } } }""");
        Assert.Equal(AiOwnRequest.Allow, PolicySettings.Resolve(cfg.Tools["mail.send"], ToolRisk.ExternalEffect, cfg).AiOwnRequest);
        Assert.Equal(AiOwnRequest.ForbidAll, PolicySettings.Resolve(cfg.Tools["files.delete"], ToolRisk.Destructive, cfg).AiOwnRequest);

        var bare = PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""");
        Assert.Equal(AiOwnRequest.ForbidApproval, PolicySettings.Resolve(bare.Tools["mail.send"], ToolRisk.ExternalEffect, bare).AiOwnRequest);
    }

    // ---- D-36: DefaultAiReviewers ----

    [Fact]
    public async Task The_policy_default_decides_a_tool_that_names_none_and_a_tool_list_replaces_the_default()
    {
        var json = """
            { "DefaultAiReviewers": ["bot"], "Tools": {
                "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect" },
                "ledger.append": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["second"] } } }
            """;
        var (host, module, _) = Hosts.Build(json);

        var mail = await Submit(host);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, AiDecides("second", ReviewDecision.Approve)));
        Assert.Contains("not assigned to this tool", ex.Message);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, AiDecides("bot", ReviewDecision.Approve))).Status);
        Assert.Equal(1, module.SendCalls);

        var ledger = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", "{\"text\":\"x\"}", Alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(ledger.Id, AiDecides("bot", ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(ledger.Id, AiDecides("second", ReviewDecision.Approve))).Status);
        Assert.Single(module.Ledger);
    }

    [Theory]
    [InlineData("""{ "DefaultAiReviewers": [] }""", "needs at least one reviewer name")]
    [InlineData("""{ "DefaultAiReviewers": ["ai:bot"] }""", "is not a valid AI reviewer name")]
    public void The_default_reviewer_list_is_validated(string json, string message)
    {
        var errors = PolicyConfig.Parse(json).Validate(["mail.send"]);
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void With_two_or_more_ai_reviewers_configured_each_open_tool_must_name_its_reviewers_or_a_default_must()
    {
        var open = PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai" }, "ledger.append": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] }, "files.delete": { "Mode": "Queue" } } }""");
        var refusals = PolicySettings.Refusals(open, configuredAiReviewers: 2);
        Assert.Single(refusals);
        Assert.Contains("'mail.send'", refusals[0], StringComparison.Ordinal);
        Assert.Empty(PolicySettings.Refusals(open, configuredAiReviewers: 1));
        Assert.Empty(PolicySettings.Refusals(open, configuredAiReviewers: 0));

        var defaulted = PolicyConfig.Parse("""{ "DefaultAiReviewers": ["bot"], "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai" } } }""");
        Assert.Empty(PolicySettings.Refusals(defaulted, configuredAiReviewers: 2));
    }

    [Fact]
    public void Each_loosened_setting_prints_a_startup_warning_and_the_strict_values_print_none()
    {
        var loose = Hosts.Build("""
            { "DefaultAiReviewers": ["bot"], "UnknownOutcomeRetry": "WithReason", "AiOwnRequest": "Allow",
              "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }
            """);
        Assert.Contains(loose.Host.StartupWarnings, w => w.StartsWith("DefaultAiReviewers names bot", StringComparison.Ordinal));
        Assert.Contains(loose.Host.StartupWarnings, w => w.StartsWith("UnknownOutcomeRetry is WithReason", StringComparison.Ordinal));
        Assert.Contains(loose.Host.StartupWarnings, w => w.StartsWith("AiOwnRequest is Allow", StringComparison.Ordinal));

        var strict = Hosts.Build("""
            { "UnknownOutcomeRetry": "Never", "AiOwnRequest": "ForbidAll",
              "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }
            """);
        Assert.DoesNotContain(strict.Host.StartupWarnings, w => w.Contains("WithReason") || w.Contains("Allow") || w.Contains("DefaultAiReviewers"));
    }

    [Fact]
    public void A_policy_that_sets_none_of_the_settings_keeps_its_canonical_text_and_a_set_one_does_not()
    {
        var plain = PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiReviewers": ["bot"] } } }""");
        var text = plain.ToCanonicalJson();
        Assert.DoesNotContain("UnknownOutcomeRetry", text);
        Assert.DoesNotContain("AiOwnRequest", text);
        Assert.DoesNotContain("DefaultAiReviewers", text);

        var defaulted = PolicyConfig.Parse("""{ "DefaultAiReviewers": ["bot"], "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai" } } }""");
        Assert.Contains("DefaultAiReviewers", defaulted.ToCanonicalJson(), StringComparison.Ordinal);
        Assert.NotEqual(plain.Hash(), defaulted.Hash());
    }

    [Fact]
    public async Task Explain_shows_who_would_decide_a_queued_call_and_how_an_unknown_outcome_is_retried()
    {
        var path = Path.Combine(Path.GetTempPath(), "otail-b08-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "UnknownOutcomeRetry": "WithReason", "Tools": { "mail.send": { "Mode": "Queue", "Reviewer": "Ai", "AiMaxRisk": "ExternalEffect" } } }""");
        var output = new StringWriter();
        try
        {
            await PolicyCli.TryRunAsync(["--policy", path, "--explain", "mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}"], "b08-test",
                b => b.AddModule(new FakeInboxModule()), output);
        }
        finally { File.Delete(path); }

        var text = output.ToString();
        Assert.Contains("AI reviewers: nobody (no AiReviewers, no DefaultAiReviewers)", text, StringComparison.Ordinal);
        Assert.Contains("AI on its own request: ForbidApproval", text, StringComparison.Ordinal);
        Assert.Contains("unknown outcome retry: WithReason", text, StringComparison.Ordinal);
    }

    // ---- G5: can an action be queued with no policy snapshot? ----

    [Fact]
    public async Task Every_queued_action_carries_a_snapshot_and_an_ai_decides_nothing_on_one_without_it()
    {
        var (host, _, _) = Hosts.Build(OwnRequest(null));
        var queued = await Submit(host);
        Assert.NotNull(queued.PolicySnapshot);

        var withoutSnapshot = queued with { PolicySnapshot = null };
        var approve = await host.Queue.AiVerdictAsync(withoutSnapshot, ReviewDecision.Approve);
        Assert.False(approve.Allowed);
        Assert.Contains("no policy snapshot", approve.Reason, StringComparison.Ordinal);
        Assert.False((await host.Queue.AiVerdictAsync(withoutSnapshot, ReviewDecision.Reject)).Allowed);
    }

    // ---- G8: tests that need no new production code ----

    [Fact]
    public async Task A_restart_inside_a_scheduled_window_still_runs_the_approved_action_on_time()
    {
        using var db = new TempDb();
        var clock = new ManualClock(Start);
        var before = new FakeInboxModule();
        var first = Hosts.BuildSqlite(before, TenMinutes, db.Path, clock);
        var mail = await first.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, first.Sessions.Start("key:alice-agent")));
        var scheduled = await first.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve));
        Assert.Equal(Start + TimeSpan.FromMinutes(10), scheduled.RunAt);
        Assert.Equal(0, before.SendCalls);

        // The Host restarts four minutes into the window, on the same database.
        clock.Advance(TimeSpan.FromMinutes(4));
        var after = new FakeInboxModule();
        var second = Hosts.BuildSqlite(after, TenMinutes, db.Path, clock);
        Assert.Equal(0, await second.Queue.RunDueDelayedAsync());

        // Eleven minutes in: one minute late, within the window's own length, so the run goes ahead.
        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(1, await second.Queue.RunDueDelayedAsync());
        Assert.Equal(ActionStatus.Succeeded, (await second.Store.GetAsync(mail.Id))!.Status);
        Assert.Equal(1, after.SendCalls);
        Assert.Equal(0, before.SendCalls);
    }

    [Theory]
    [InlineData("Reject", ActionStatus.Rejected)]
    [InlineData("Approve", ActionStatus.Succeeded)]
    public async Task A_person_decision_on_a_queued_action_gives_the_row_of_the_decision_table(string decision, ActionStatus expected)
    {
        var (host, _, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""");
        var mail = await Submit(host);
        Assert.Equal(expected, (await host.Queue.ReviewAsync(mail.Id, PersonDecides(Enum.Parse<ReviewDecision>(decision)))).Status);
    }

    [Fact]
    public async Task An_approval_short_of_the_required_approvals_is_recorded_and_stays_pending()
    {
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "RequiredApprovals": 2 } } }""");
        var mail = await Submit(host);

        Assert.Equal(ActionStatus.Pending, (await host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve, "rita"))).Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve, "lee"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task An_approval_past_the_expiry_expires_the_action_and_does_not_run_it()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "ExpireAfter": "01:00:00" } } }""", clock);
        var mail = await Submit(host);

        clock.Advance(TimeSpan.FromHours(2));
        // Decision table 2.4: the action expires (stored as Expired); the approver is told it was too late, and nothing runs.
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(mail.Id, PersonDecides(ReviewDecision.Approve)));
        Assert.Equal(ActionStatus.Expired, (await host.Store.GetAsync(mail.Id))!.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_approval_with_edits_is_refused_where_edits_are_not_allowed()
    {
        var (host, module, audit) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "AllowEdits": false } } }""");
        var mail = await Submit(host);
        var edited = new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow)
        {
            Subject = Subjects.Admin("rita"),
            EditedArgumentsJson = "{\"to\":\"other@x.z\",\"body\":\"b\"}",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(mail.Id, edited));
        Assert.Contains(audit.Entries, e => e.Event == "edit_refused" && e.ActionId == mail.Id);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_tool_with_no_policy_entry_is_denied_and_never_runs()
    {
        var (host, module, _) = Hosts.Build("""{ "Tools": { "ledger.append": { "Mode": "Queue" } } }""");
        var side = await Submit(host);

        Assert.Equal(ActionStatus.Denied, side.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ToolMode.Deny, host.Policy.Simulate(host.Catalog, "mail.send", Mail, false, Start).Mode);
    }

    // A failed run whose outcome is unknown: the state a restart leaves (ActionExecutor's recovery path), set directly for the test.
    private static async Task MakeUnknown(OpenTailHost host, string id)
    {
        // The legal path of the state machine: Pending, Approved, Executing, then Failed with the error.
        await host.Store.TransitionAsync(id, ActionStatus.Pending, ActionStatus.Approved, r => r);
        await host.Store.TransitionAsync(id, ActionStatus.Approved, ActionStatus.Executing, r => r);
        await host.Store.TransitionAsync(id, ActionStatus.Executing, ActionStatus.Failed,
            r => r with { Error = new ActionError(RetryRules.UnknownOutcomeCode, "The Host stopped while this action was running; the outcome is unknown.", true) });
    }

    private static ActionReview CheckedNotSent() =>
        new("rita", ReviewDecision.Retry, "Checked the mail server; not sent.", DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };

    [Theory]
    [InlineData("Never", false, ActionStatus.Failed, 0)]     // refused: the unknown outcome stays
    [InlineData("Once", true, ActionStatus.Succeeded, 1)]    // the first retry, with a reason
    [InlineData("WithReason", true, ActionStatus.Succeeded, 1)]
    public async Task A_person_retries_an_unknown_outcome_as_the_setting_says(string setting, bool allowed, ActionStatus expected, int sends)
    {
        var (host, module, _) = Hosts.Build($$"""{ "UnknownOutcomeRetry": "{{setting}}", "Tools": { "mail.send": { "Mode": "Queue" } } }""");
        var mail = await Submit(host);
        await MakeUnknown(host, mail.Id);

        if (allowed) Assert.Equal(expected, (await host.Queue.ReviewAsync(mail.Id, CheckedNotSent())).Status);
        else
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, CheckedNotSent()));
            Assert.Contains("never to retry an unknown outcome", ex.Message, StringComparison.Ordinal);
        }
        Assert.Equal(sends, module.SendCalls);
    }

    [Fact]
    public async Task Under_once_a_tool_reported_unknown_outcome_needs_a_reason_and_a_second_one_is_final()
    {
        var module = new UnknownOutcomeModule();
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "UnknownOutcomeRetry": "Once", "Tools": { "outbox.send": { "Mode": "Queue" } } }""")).Build();
        var sent = await host.Queue.SubmitAsync(new ActionRequest("outbox.send", "{\"text\":\"hi\"}", Alice, host.Sessions.Start("key:alice-agent")));

        var first = await host.Queue.ReviewAsync(sent.Id, PersonDecides(ReviewDecision.Approve));
        Assert.Equal(ActionStatus.Failed, first.Status);
        Assert.Equal("unknown_outcome", first.Error!.Code);
        Assert.Equal(1, module.Calls);

        // Without a reason the retry is refused: a tool's own unknown outcome is an unknown outcome, as an interrupted run is.
        var noReason = new ActionReview("rita", ReviewDecision.Retry, null, DateTimeOffset.UtcNow) { Subject = Subjects.Admin("rita") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(sent.Id, noReason));
        Assert.Equal(1, module.Calls);

        // With a reason the one retry runs; its unknown outcome again is final.
        Assert.Equal(ActionStatus.Failed, (await host.Queue.ReviewAsync(sent.Id, CheckedNotSent())).Status);
        Assert.Equal(2, module.Calls);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(sent.Id, CheckedNotSent()));
        Assert.Contains("second unknown outcome", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, module.Calls);
    }

    [Fact]
    public async Task Under_never_a_tool_reported_unknown_outcome_is_not_retried_even_with_a_reason()
    {
        var module = new UnknownOutcomeModule();
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "UnknownOutcomeRetry": "Never", "Tools": { "outbox.send": { "Mode": "Queue" } } }""")).Build();
        var sent = await host.Queue.SubmitAsync(new ActionRequest("outbox.send", "{\"text\":\"hi\"}", Alice, host.Sessions.Start("key:alice-agent")));
        Assert.Equal(ActionStatus.Failed, (await host.Queue.ReviewAsync(sent.Id, PersonDecides(ReviewDecision.Approve))).Status);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(sent.Id, CheckedNotSent()));
        Assert.Contains("never to retry an unknown outcome", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, module.Calls);
    }

    [Fact]
    public async Task An_idempotent_tool_is_not_limited_by_never()
    {
        var (host, module, _) = Hosts.Build("""{ "UnknownOutcomeRetry": "Never", "Tools": { "ledger.append": { "Mode": "Queue" } } }""");
        var entry = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", "{\"text\":\"x\"}", Alice));
        await MakeUnknown(host, entry.Id);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(entry.Id, CheckedNotSent())).Status);
        Assert.Single(module.Ledger);
    }

    /// <summary>G8, channel parity: the runner and a direct decision (the route MCP, HTTP, Teams and A2A all take into the queue) give one answer.
    /// Approval and refusal are both checked: the runner leaves a refused approval for a person, the direct call throws it.</summary>
    [Theory]
    [InlineData(ReviewDecision.Reject, ActionStatus.Rejected, 0)]
    [InlineData(ReviewDecision.Approve, ActionStatus.Succeeded, 1)]
    public async Task The_runner_and_a_direct_decision_give_the_same_answer_for_an_ai_reviewer(ReviewDecision decision, ActionStatus expected, int sends)
    {
        var (viaRunner, runnerModule, _) = Hosts.Build(OwnRequest(null));
        var (direct, directModule, _) = Hosts.Build(OwnRequest(null));
        var runnerAction = await Submit(viaRunner);
        var directAction = await Submit(direct);

        var report = await new ReviewerRunner(viaRunner).RunOnceAsync(new DelegateReviewer("bot", (_, _) =>
            Task.FromResult<ActionReview?>(new ActionReview("bot", decision, decision == ReviewDecision.Approve ? null : "not this one", DateTimeOffset.UtcNow))));
        Assert.Equal(1, report.Offered);

        try { await direct.Queue.ReviewAsync(directAction.Id, AiDecides("bot", decision)); }
        catch (UnauthorizedAccessException) { /* a refusal the queue gives: the same answer the runner's skip gives */ }

        Assert.Equal(expected, (await viaRunner.Store.GetAsync(runnerAction.Id))!.Status);
        Assert.Equal(expected, (await direct.Store.GetAsync(directAction.Id))!.Status);
        Assert.Equal(sends, runnerModule.SendCalls);
        Assert.Equal(sends, directModule.SendCalls);
    }
}
