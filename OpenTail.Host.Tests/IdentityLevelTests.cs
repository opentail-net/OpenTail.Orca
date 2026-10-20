using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Adaptive limits, step 2: the out-of-profile tripwire (Suspended) and reviewer rejections (Elevated), report only or enforced.</summary>
public class IdentityLevelTests
{
    private static string Policy(string outOfProfile = "Enforce", string rejections = "Enforce") => $$"""
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:00:00",
          "Tools": { "mail.send": { "Mode": "Queue" }, "ledger.append": { "Mode": "Execute" } },
          "Agents": { "reader": { "Tools": [ "inbox.read", "clock.now", "ledger.append", "mail.send" ] }, "narrow": { "Tools": [ "clock.now" ] } },
          "Limits": { "OutOfProfile": "{{outOfProfile}}", "Rejections": "{{rejections}}" }
        }
        """;

    private static OpenTailHost Host(string policy, string? db = null)
    {
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy));
        if (db is not null) b.UseSqlite(db);
        return b.Build();
    }

    private static ActionRequest Call(string tool, string args = "{}", string profile = "narrow", string who = "key:bot") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, who)) { Profile = profile };

    private static ActionReview Review(ReviewDecision d) => new("rita", d, "no", DateTimeOffset.UtcNow) { Subject = "admin:rita" };

    [Fact]
    public async Task A_call_outside_the_profile_suspends_the_identity_and_everything_after_is_refused()
    {
        var host = Host(Policy());
        Assert.Equal("not_in_profile", (await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""))).Error!.Code);

        Assert.Equal(IdentityLevel.Suspended, host.Levels.Effective("key:bot"));
        var after = await host.Queue.SubmitAsync(Call("clock.now"));
        Assert.Equal(("suspended", ActionStatus.Denied), (after.Error!.Code, after.Status));
        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:other"));
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "level_changed" && e.Actor == "key:bot" && e.Detail!.Contains("Suspended"));
    }

    [Fact]
    public async Task Report_only_records_what_would_happen_and_changes_nothing()
    {
        var host = Host(Policy("ReportOnly", "ReportOnly"));
        await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));

        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:bot"));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("clock.now"))).Status);
        var row = host.Levels.Get("key:bot")!;
        Assert.Equal((IdentityLevel.Suspended, false), (row.Level, row.Enforced));
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "level_would_change");
    }

    [Fact]
    public void Both_signals_report_only_by_default()
    {
        var c = PolicyConfig.Parse("{}").Limits;
        Assert.Equal((LimitMode.ReportOnly, LimitMode.ReportOnly), (c.OutOfProfile, c.Rejections));
    }

    [Fact]
    public async Task A_rejection_elevates_the_identity_so_its_writes_wait_for_a_person()
    {
        var host = Host(Policy());
        var send = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}""", profile: "reader"));
        await host.Queue.ReviewAsync(send.Id, Review(ReviewDecision.Reject));
        Assert.Equal(IdentityLevel.Elevated, host.Levels.Effective("key:bot"));

        var write = await host.Queue.SubmitAsync(Call("ledger.append", """{"text":"x"}""", profile: "reader"));
        Assert.Equal(ActionStatus.Pending, write.Status);                                  // was Execute
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("clock.now", profile: "reader"))).Status); // reads still run
    }

    [Fact]
    public async Task Requests_for_changes_and_in_process_callers_never_raise_a_level()
    {
        var host = Host(Policy());
        var a = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}""", profile: "reader"));
        await host.Queue.ReviewAsync(a.Id, Review(ReviewDecision.RequestChanges));
        var mine = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"b"}""", new ActionRequester(RequesterKind.User, "me")));
        await host.Queue.ReviewAsync(mine.Id, Review(ReviewDecision.Reject));
        Assert.Empty(host.Levels.List());
    }

    [Fact]
    public async Task A_suspended_identitys_pending_actions_are_frozen()
    {
        var host = Host(Policy());
        var pending = await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}""", profile: "reader"));
        await host.Queue.SubmitAsync(Call("files.delete", """{"path":"x"}""", profile: "reader")); // outside: suspends
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Queue.ReviewAsync(pending.Id, Review(ReviewDecision.Approve)));
        Assert.Contains("frozen", ex.Message);
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(pending.Id, Review(ReviewDecision.Reject))).Status);
    }

    [Fact]
    public async Task Only_a_person_clears_and_a_suspended_key_needs_rotating_first()
    {
        var host = Host(Policy());
        await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Levels.ClearAsync("key:bot", "rita", keyRotated: false));
        // Saying so is not enough: the Host checks its own record of the rotation (SOP gap G5).
        var unrotated = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Levels.ClearAsync("key:bot", "rita", keyRotated: true));
        Assert.Contains("no key rotation has been recorded", unrotated.Message);
        await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow.AddSeconds(1), "key_rotated", null, null, "system", "key:bot (sha256:aa… -> sha256:bb…)"));
        Assert.True(await host.Levels.ClearAsync("key:bot", "rita", keyRotated: true));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("clock.now"))).Status);
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "level_cleared" && e.Actor == "rita" && e.Detail!.Contains("key rotated"));
    }

    [Fact]
    public async Task A_lower_signal_never_loosens_a_higher_level()
    {
        var host = Host(Policy());
        await host.Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));   // Suspended
        await host.Levels.RejectedAsync("key:bot", "act_x", "mail.send", default);            // Elevated would be lower
        Assert.Equal(IdentityLevel.Suspended, host.Levels.Effective("key:bot"));
    }

    [Fact]
    public async Task Levels_survive_a_restart_and_the_cli_lists_and_clears_them()
    {
        using var db = new TempDb();
        await Host(Policy(), db.Path).Queue.SubmitAsync(Call("mail.send", """{"to":"a@b.c","body":"b"}"""));
        TempDb.ClearPools(db.Path);
        Assert.Equal(IdentityLevel.Suspended, Host(Policy(), db.Path).Levels.Effective("key:bot"));

        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--levels"], "t", _ => { }, output));
        Assert.Contains("key:bot  Suspended  since", output.ToString());
        Assert.Equal(2, await PolicyCli.TryRunAsync(["--db", db.Path, "--clear-level", "key:bot", "--by", "rita"], "t", _ => { }, new StringWriter()));
        await new SqliteAuditSink(new SqliteActionStore(db.Path)).AppendAsync(new AuditEntry(DateTimeOffset.UtcNow.AddSeconds(1), "key_rotated", null, null, "system", "key:bot (sha256:aa… -> sha256:bb…)"));
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--clear-level", "key:bot", "--by", "rita", "--key-rotated"], "t", _ => { }, new StringWriter()));
        Assert.Equal(IdentityLevel.Normal, Host(Policy(), db.Path).Levels.Effective("key:bot"));
    }
}
