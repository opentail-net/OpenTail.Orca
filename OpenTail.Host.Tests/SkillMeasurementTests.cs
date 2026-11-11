using System.Text.Json.Nodes;
using OpenTail.Host.Admin;
using OpenTail.Modules.Skills;

namespace OpenTail.Host.Tests;

/// <summary>Step 0 of the attribution plan: the read-only measurement, on the real queue and SQLite store, with a clock the test controls.</summary>
public sealed class SkillMeasurementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"measure-{Guid.NewGuid():N}");
    private readonly TempDb _db = new();

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Delayed = """{ "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Execute", "DelayFor": "00:10:00" } } }""";
    private const string Queued = """{ "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Queue" } } }""";
    private const string TwoApprovals ="""{ "DefaultReadMode": "Execute", "Tools": { "mail.send": { "Mode": "Queue", "RequiredApprovals": 2 } } }""";

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    private static string Meta(string v) => $$"""{ "format": 1, "id": "reply-draft", "version": "{{v}}", "summary": "Reply to customers.", "owner": "cs" }""";

    private void Publish(string version, string? expectedFrom = null) =>
        new SkillStore(Path.Combine(_root, "store")).Publish("reply-draft", version, "production", Meta(version), $"Version {version}.", expectedFrom);

    private OpenTailHost Build(TimeProvider clock, string policy = Hosts.SendQueued) =>
        new OpenTailHostBuilder()
            .AddModule(new FakeInboxModule())
            .AddModule(new SkillsModule(new SkillsModuleOptions { Store = Path.Combine(_root, "store") }))
            .UsePolicy(PolicyConfig.Parse(policy))
            .UseClock(clock)
            .UseSqlite(_db.Path)
            .Build();

    private static async Task<string> SessionWithServe(McpPair pair)
    {
        var session = S(await pair.CallAsync("start_session"), "sessionId");
        await pair.CallAsync("skills.get", ("id", "reply-draft"), ("sessionId", session));
        return session;
    }

    private static async Task<string> Send(McpPair pair, string? session)
    {
        var args = new List<(string, object?)> { ("to", "a@b.example"), ("body", "Hello") };
        if (session is not null) args.Add(("sessionId", session));
        return S(await pair.CallAsync("mail.send", [.. args]), "actionId");
    }

    /// <summary>A review by a person (a subject the classes count), at the clock's time.</summary>
    private static Task<ActionRecord> Decide(OpenTailHost host, TimeProvider clock, string actionId, ReviewDecision decision, string who = "alice") =>
        host.Queue.ReviewAsync(actionId, new ActionReview(who, decision, decision == ReviewDecision.Approve ? null : "Not as asked.", clock.GetUtcNow())
        {
            Subject = "admin:" + who,
            Groups = ["skill-approvers"],
        });

    private static Task<SkillMeasurementReport> Measure(OpenTailHost host, DateTimeOffset decidedFrom, DateTimeOffset now) =>
        SkillMeasurement.MeasureAsync(host, (IAuditReader)host.Audit, decidedFrom, decidedFrom, now);

    private static int Clean(SkillMeasurementReport r, string version) =>
        r.Versions.SingleOrDefault(v => v.Version == version)?.Clean ?? 0;

    [Fact]
    public async Task A_session_with_one_version_attributes_its_actions_to_it_and_a_second_version_leaves_later_ones_ambiguous()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        clock.Advance(TimeSpan.FromMinutes(5));
        var first = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, first, ReviewDecision.Approve);

        Publish("2", expectedFrom: "1");
        await pair.CallAsync("skills.get", ("id", "reply-draft"), ("sessionId", session));  // now two versions were served in this session
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, second, ReviewDecision.Approve);

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, Clean(report, "1"));
        Assert.Equal(0, Clean(report, "2"));
        Assert.Equal(1, report.Ambiguous);
        Assert.Equal(0, report.Unattributed);
    }

    [Fact]
    public async Task A_serve_after_an_action_does_not_count_it_and_a_decided_action_with_no_serve_is_unattributed()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = S(await pair.CallAsync("start_session"), "sessionId");
        var sent = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, sent, ReviewDecision.Approve);
        await pair.CallAsync("skills.get", ("id", "reply-draft"), ("sessionId", session));  // served after the action was created

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Empty(report.Versions);
        Assert.Equal(1, report.Unattributed);
    }

    [Fact]
    public async Task A_failed_serve_is_ignored_so_the_action_after_it_is_unattributed()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = S(await pair.CallAsync("start_session"), "sessionId");
        await pair.CallAsync("skills.get", ("id", "no-such-skill"), ("sessionId", session));  // refused: not a served version
        var sent = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, sent, ReviewDecision.Reject);

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Empty(report.Versions);
        Assert.Equal(1, report.Unattributed);
    }

    [Fact]
    public async Task An_elapsed_undo_window_is_excluded_and_counted_as_system()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, Delayed);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await host.Queue.RunDueDelayedAsync());

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.SystemElapsed);
        Assert.Empty(report.Versions);
    }

    [Fact]
    public async Task A_stop_during_the_undo_window_is_excluded_and_counted_separately()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, Delayed);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(2));
        await host.Queue.StopAsync(id, "ops", []);

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.UndoWindowStopped);
        Assert.Empty(report.Versions);
        Assert.Equal(0, report.Unattributed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Late_stop_boundary_is_classed_by_scheduled_time(int secondsAfterScheduled)
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, Delayed);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);  // scheduled for T0 + 10 minutes, to the second
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(secondsAfterScheduled));
        await host.Queue.StopAsync(id, "ops", []);
        clock.Advance(TimeSpan.FromMinutes(10));  // the window has ended

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.Stops.Size);
        if (secondsAfterScheduled < 0)
        {
            Assert.Equal(1, report.Stops.UndoWindowStops);
            Assert.Equal(0, report.Stops.LateStops);
        }
        else
        {
            Assert.Equal(0, report.Stops.UndoWindowStops);
            Assert.Equal(1, report.Stops.LateStops);
        }
    }

    [Fact]
    public async Task The_scheduled_time_is_read_from_the_queues_own_detail_in_utc()
    {
        // The queue writes the scheduled time as UTC, to the second. A clock with another offset must not shift it.
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.FromHours(2)));   // 09:00 UTC
        var host = Build(clock, Delayed);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        await Send(pair, session);                                                                          // runs at 09:10 UTC

        var detail = Assert.Single(((IAuditReader)host.Audit).Read(), e => e.Event == "delayed").Detail;
        Assert.Equal("runs at 2026-11-01 09:10:00Z unless stopped", detail);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), SkillMeasurement.ScheduledTime(detail));
        Assert.Null(SkillMeasurement.ScheduledTime("runs at tomorrow unless stopped"));
        Assert.Null(SkillMeasurement.ScheduledTime(null));
    }

    [Fact]
    public async Task An_operator_cancellation_is_excluded_and_counted_as_an_operator_cancellation()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var cancelled = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        // The admin command cancels in the same way: a rejection by the operator, under the operator subject.
        await host.Queue.ReviewAsync(cancelled, new ActionReview("ops", ReviewDecision.Reject, "Cancelled by an operator: wrong recipient.", clock.GetUtcNow())
        {
            Subject = "operator:ops",
        });

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.OperatorCancelled);
        Assert.Empty(report.Versions);
        Assert.Equal(0, report.Unattributed);
    }

    [Fact]
    public async Task An_action_created_before_the_window_and_finally_reviewed_inside_it_is_returned_by_UpdatedSince()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, Queued);               // no expiry: the action waits two hours for its decision
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);            // created at T0, before the window opens
        var windowStart = T0 + TimeSpan.FromHours(1);
        clock.Advance(TimeSpan.FromHours(2));           // the final approval is inside the window
        await Decide(host, clock, id, ReviewDecision.Approve);

        var returned = await host.Store.QueryAsync(new ActionQuery { UpdatedSince = windowStart, Limit = int.MaxValue });
        var action = Assert.Single(returned, a => a.Id == id);
        Assert.True(action.Reviews.Last().At >= windowStart, "the effective review is inside the window");

        var report = await Measure(host, windowStart, clock.GetUtcNow());
        Assert.Equal(1, Clean(report, "1"));  // the serve before the window still explains the decision inside it
    }

    [Fact]
    public async Task A_first_of_two_required_approvals_is_not_counted_while_the_action_is_pending()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, TwoApprovals);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        var partial = await Decide(host, clock, id, ReviewDecision.Approve, "alice");
        Assert.Equal(ActionStatus.Pending, partial.Status);

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.AwaitingApprovals);
        Assert.Empty(report.Versions);
        Assert.Equal(0, report.Unattributed);
    }

    [Fact]
    public async Task Two_required_approvals_produce_one_outcome_at_the_final_approval()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, TwoApprovals);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, id, ReviewDecision.Approve, "alice");
        clock.Advance(TimeSpan.FromMinutes(10));       // the final approval, at T0 + 15 minutes
        var final = await Decide(host, clock, id, ReviewDecision.Approve, "bob");

        Assert.Equal(ActionStatus.Succeeded, final.Status);  // it ran once, on the completing approval
        Assert.Equal(1, (await Measure(host, T0, clock.GetUtcNow())).Versions.Single().Clean);
        // The decision is timed by the completing approval: a window that opens after it does not contain it.
        var late = await Measure(host, T0 + TimeSpan.FromMinutes(16), clock.GetUtcNow());
        Assert.Empty(late.Versions);
        Assert.Equal(0, late.AwaitingApprovals);
        // A window that opens between the first approval and the final one still sees the decision, once.
        var between = await Measure(host, T0 + TimeSpan.FromMinutes(12), clock.GetUtcNow());
        Assert.Equal(1, Clean(between, "1"));
        Assert.Equal(0, between.AwaitingApprovals);
    }

    [Fact]
    public async Task A_partial_approval_then_rejection_counts_only_the_rejection()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock, TwoApprovals);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, id, ReviewDecision.Approve, "alice");
        clock.Advance(TimeSpan.FromMinutes(10));
        await Decide(host, clock, id, ReviewDecision.Reject, "bob");

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, Clean(report, "1"));
        Assert.Equal(0, report.AwaitingApprovals);
        // The first approval, before the rejection, is not a decision of its own: a window that holds only it counts nothing.
        var beforeRejection = await Measure(host, T0 + TimeSpan.FromMinutes(6), T0 + TimeSpan.FromMinutes(12));
        Assert.Empty(beforeRejection.Versions);
    }

    [Fact]
    public async Task A_review_with_no_subject_is_excluded_as_unclassed()
    {
        Publish("1");
        var clock = new ManualClock(T0);
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await host.Queue.ReviewAsync(id, new ActionReview("alice", ReviewDecision.Approve, null, clock.GetUtcNow()));  // no subject

        var report = await Measure(host, T0, clock.GetUtcNow());
        Assert.Equal(1, report.Unclassed);
        Assert.Empty(report.Versions);
    }

    [Fact]
    public async Task The_measurement_command_reports_clean_decisions_and_refuses_since()
    {
        Publish("1");
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(-30));   // inside the default 30-day window
        var host = Build(clock);
        await using var pair = await McpPair.StartAsync(host);
        var session = await SessionWithServe(pair);
        var id = await Send(pair, session);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Decide(host, clock, id, ReviewDecision.Approve);

        Directory.CreateDirectory(_root);
        var policy = Path.Combine(_root, "policy.json");
        File.WriteAllText(policy, Queued);
        void AddSkills(OpenTailHostBuilder b) => b.AddModule(new FakeInboxModule())
            .AddModule(new SkillsModule(new SkillsModuleOptions { Store = Path.Combine(_root, "store") }));

        var output = new StringWriter();
        var errors = new StringWriter();
        Console.SetError(errors);
        var code = await PolicyCli.TryRunAsync(["--policy", policy, "--db", _db.Path, "--skill-outcomes-measure", "--decided-since", "30d"], "t", AddSkills, output);
        Assert.True(code == 0, $"exit {code}: {errors}");
        var text = output.ToString();
        Assert.Contains("reply-draft@1: 1 clean human decision(s)", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", text, StringComparison.Ordinal);

        Assert.Equal(64, await PolicyCli.TryRunAsync(["--policy", policy, "--db", _db.Path, "--skill-outcomes-measure", "--since", "30d"], "t", AddSkills, new StringWriter()));
    }

    [Fact]
    public void A_review_is_classed_by_subject_and_not_by_display_name()
    {
        var at = T0;
        Assert.Equal(ReviewClass.Person, SkillMeasurement.ClassOf(new ActionReview("ai:checker", ReviewDecision.Approve, null, at) { Subject = "admin:alice" }));
        Assert.Equal(ReviewClass.Ai, SkillMeasurement.ClassOf(new ActionReview("alice", ReviewDecision.Approve, null, at) { Subject = "ai:checker" }));
        Assert.Equal(ReviewClass.Unclassed, SkillMeasurement.ClassOf(new ActionReview("alice", ReviewDecision.Approve, null, at)));
        Assert.Equal(ReviewClass.Operator, SkillMeasurement.ClassOf(new ActionReview("ops", ReviewDecision.Reject, null, at) { Subject = "operator:ops" }));
        Assert.Equal(ReviewClass.OtherSubject, SkillMeasurement.ClassOf(new ActionReview("bot", ReviewDecision.Approve, null, at) { Subject = "key:bot" }));
    }
}
