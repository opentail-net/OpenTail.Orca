namespace OpenTail.Orca.Tests;

/// <summary>
/// P3 of the reviewer gate (D-23, section 5.6): a tool with an approve delay runs its final approval after the window, not at once. The run is
/// scheduled in the same transaction as the approval, cannot be approved early, and is held when it is late or the requester is suspended.
/// </summary>
public class P3ScheduledApprovalTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");
    private const string Mail = """{"to":"x@y.z","body":"b"}""";
    private const string TenMinutes = """{ "Tools": { "mail.send": { "Mode": "Queue", "ApproveDelay": "00:10:00" } } }""";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-13T09:00:00Z");

    private static Task<ActionRecord> Submit(OpenTailHost host) =>
        host.Queue.SubmitAsync(new ActionRequest("mail.send", Mail, Alice, host.Sessions.Start("key:alice-agent")));

    private static ActionReview PersonApproves(ManualClock clock, string name = "rita") =>
        new(name, ReviewDecision.Approve, null, clock.GetUtcNow()) { Subject = Subjects.Admin(name) };

    [Fact]
    public async Task A_final_approval_schedules_the_run_and_does_not_execute_it()
    {
        var clock = new ManualClock(Start);
        var (host, module, audit) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);

        var scheduled = await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));

        Assert.Equal(ActionStatus.Pending, scheduled.Status);
        Assert.Equal(Start + TimeSpan.FromMinutes(10), scheduled.RunAt);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "approval_scheduled" && e.ActionId == mail.Id);
    }

    [Fact]
    public async Task Nobody_can_approve_a_scheduled_run_again_and_the_attempt_is_audited()
    {
        var clock = new ManualClock(Start);
        var (host, module, audit) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock, "rita"));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(mail.Id, PersonApproves(clock, "sam")));
        Assert.Contains("cannot be approved again", ex.Message);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "review_refused" && e.ActionId == mail.Id);
    }

    [Fact]
    public async Task The_run_happens_when_its_window_is_over_and_not_before()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(1, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(mail.Id))!.Status);
    }

    [Fact]
    public async Task The_requester_can_stop_a_scheduled_run_in_its_window()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));

        await host.Queue.StopAsync(mail.Id, Alice.Id, []);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_rejection_in_the_window_prevents_the_run()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock, "rita"));

        var refusal = new ActionReview("sam", ReviewDecision.Reject, "not today", clock.GetUtcNow()) { Subject = Subjects.Admin("sam") };
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(mail.Id, refusal)).Status);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_run_that_is_late_past_its_window_is_held_for_a_person()
    {
        // Submitted at 09:00, approved at 09:20: the window is counted from the approval, so the run is due at 09:30.
        var clock = new ManualClock(Start);
        var (host, module, audit) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        clock.Advance(TimeSpan.FromMinutes(20));
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));

        // 09:45: fifteen minutes past its time, which is more than the ten-minute window.
        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);
        Assert.Null((await host.Store.GetAsync(mail.Id))!.RunAt);
        Assert.Contains(audit.Entries, e => e.Event == "delay_held" && e.ActionId == mail.Id);
    }

    [Fact]
    public async Task A_suspended_requester_has_its_scheduled_run_held()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));
        Assert.True(await host.Levels.SuspendAsync(Alice.Id, "ops", "test"));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);
        Assert.Null((await host.Store.GetAsync(mail.Id))!.RunAt);
    }

    [Fact]
    public async Task An_expired_scheduled_run_does_not_run()
    {
        var clock = new ManualClock(Start);
        var (host, module, _) = Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "ApproveDelay": "00:10:00", "ExpireAfter": "00:05:00" } } }""", clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock));

        clock.Advance(TimeSpan.FromMinutes(11));   // past both the expiry (09:05) and the run (09:10)
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_held_approval_is_withdrawn_so_the_same_single_reviewer_can_schedule_the_run_again()
    {
        // The review's point B: with one eligible reviewer, a held run must not be a dead end.
        var clock = new ManualClock(Start);
        var (host, module, audit) = Hosts.Build(TenMinutes, clock);
        var mail = await Submit(host);
        await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock, "rita"));

        clock.Advance(TimeSpan.FromMinutes(25));                  // late: the scheduler holds it, and withdraws the approval
        Assert.Equal(0, await host.Queue.RunDueDelayedAsync());
        Assert.Null((await host.Store.GetAsync(mail.Id))!.RunAt);
        Assert.Contains(audit.Entries, e => e.Event == "delay_held" && e.ActionId == mail.Id);

        var again = await host.Queue.ReviewAsync(mail.Id, PersonApproves(clock, "rita"));
        Assert.Equal(ActionStatus.Pending, again.Status);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), again.RunAt);
        Assert.Equal(0, module.SendCalls);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await host.Queue.RunDueDelayedAsync());
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public void Approve_delay_belongs_to_a_queued_tool_and_is_bounded()
    {
        var notQueued = Assert.Throws<InvalidOperationException>(() => Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Execute", "ApproveDelay": "00:10:00" } } }"""));
        Assert.Contains("ApproveDelay only applies to Mode Queue", notQueued.Message);

        var tooLong = Assert.Throws<InvalidOperationException>(() => Hosts.Build("""{ "Tools": { "mail.send": { "Mode": "Queue", "ApproveDelay": "192:00:01" } } }"""));
        Assert.Contains("ApproveDelay must be positive and at most 7 days", tooLong.Message);
    }
}
