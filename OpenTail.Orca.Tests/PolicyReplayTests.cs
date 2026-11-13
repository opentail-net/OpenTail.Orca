using Microsoft.Data.Sqlite;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

public class PolicyReplayTests
{
    private const string Current = """{ "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" } } }""";

    private const string Candidate = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue", "Rules": [
          { "Name": "internal", "Mode": "Execute", "IgnoreArgs": ["body"], "When": { "arg": "/to", "all": { "domainIn": ["corp.example"] } } },
          { "Name": "no-rivals", "Mode": "Deny", "When": { "arg": "/to", "any": { "domainIn": ["rival.example"] } } }
        ] } } }
        """;

    private static async Task SeedAsync(string db)
    {
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Current, db);
        var clean = host.Sessions.Start();
        var dirty = host.Sessions.Start();
        host.Sessions.Taint(dirty);
        var who = new ActionRequester(RequesterKind.User, "me");
        string Args(string to) => $$"""{"to":"{{to}}","body":"hi"}""";
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Args("a@corp.example"), who, clean));
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Args("b@corp.example"), who, clean));
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Args("c@corp.example"), who, dirty));     // tainted: stays queued either way
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Args("d@rival.example"), who, clean));
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", Args("e@elsewhere.example"), who, clean)); // no rule matches
        TempDb.ClearPools(db);
    }

    [Fact]
    public async Task Replay_reports_what_a_candidate_policy_would_have_changed_and_changes_nothing()
    {
        using var db = new TempDb();
        await SeedAsync(db.Path);

        var module = new FakeInboxModule();
        var candidate = Hosts.BuildSqlite(module, Candidate, db.Path);
        var report = await PolicyReplay.RunAsync(candidate);

        Assert.Equal(5, report.Examined);
        Assert.Equal(2, report.Changes.Count(c => c.WouldBe == ToolMode.Execute && c.RuleName == "internal"));   // the two clean internal mails
        var denied = Assert.Single(report.Changes, c => c.WouldBe == ToolMode.Deny);
        Assert.Equal("no-rivals", denied.RuleName);
        Assert.Equal(2, report.Unchanged);                                                                         // tainted internal + elsewhere: still Queue
        Assert.Contains(report.Summary(), l => l.Contains("Queue -> Execute") && l.Contains("internal"));

        // Nothing ran and nothing was rewritten: the stored actions are exactly as they were.
        Assert.Equal(0, module.SendCalls);
        var stored = await candidate.Store.QueryAsync(new ActionQuery { Limit = 100 });
        Assert.Equal(5, stored.Count);
        Assert.All(stored, r => Assert.Equal(ActionStatus.Pending, r.Status)); // still waiting for a reviewer, exactly as before
    }

    [Fact]
    public async Task Replay_leaves_out_denials_that_were_never_policy_decisions()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), """{ "MaxPendingPerRequester": 1, "Tools": { "mail.send": { "Mode": "Queue" } } }""", db.Path);
        var who = new ActionRequester(RequesterKind.User, "me");
        var s = host.Sessions.Start();
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"x"}""", who, s));
        var full = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"y"}""", who, s));
        Assert.Equal("queue_full", full.Error!.Code);

        var report = await PolicyReplay.RunAsync(host);
        Assert.Equal(1, report.SkippedNotPolicy);
        Assert.Empty(report.Changes);
        TempDb.ClearPools(db.Path);
    }

    [Fact]
    public async Task The_command_line_explains_a_call_and_replays_a_policy()
    {
        using var db = new TempDb();
        await SeedAsync(db.Path);
        var dir = Path.GetDirectoryName(db.Path)!;
        var current = Path.Combine(dir, "cur-" + Guid.NewGuid().ToString("N") + ".json");
        var candidate = Path.Combine(dir, "cand-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(current, Candidate);
        await File.WriteAllTextAsync(candidate, Candidate);
        try
        {
            var explain = new StringWriter();
            var code = await PolicyCli.TryRunAsync(
                ["--policy", current, "--explain", "mail.send", """{"to":"a@corp.example","body":"hi","bcc":"x@y.z"}"""], "t",
                b => b.AddModule(new FakeInboxModule()), explain);
            Assert.Equal(0, code);
            Assert.Contains("mail.send -> Queue", explain.ToString());
            Assert.Contains("does not account for argument(s) bcc", explain.ToString());

            var replay = new StringWriter();
            code = await PolicyCli.TryRunAsync(["--replay-policy", candidate, "--db", db.Path, "--since", "7d"], "t",
                b => b.AddModule(new FakeInboxModule()), replay);
            Assert.Equal(0, code);
            Assert.Contains("would have been handled differently", replay.ToString());
            Assert.Contains("Queue -> Execute (rule internal)", replay.ToString());
            Assert.Contains("Nothing was executed", replay.ToString());

            // Not a policy command: the runner carries on to its normal job.
            Assert.Null(await PolicyCli.TryRunAsync(["--policy", current], "t", b => { }, new StringWriter()));
        }
        finally
        {
            File.Delete(current);
            File.Delete(candidate);
            TempDb.ClearPools(db.Path);
        }
    }

    [Fact]
    public async Task The_team_sample_policy_does_what_its_comments_say()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenTail.Orca.slnx"))) root = root.Parent;
        var policy = PolicyConfig.Load(Path.Combine(root!.FullName, "OpenTail.Orca.Sample.TeamHttp", "policy.team.json"));

        var dir = Path.Combine(Path.GetTempPath(), "otail-team-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var host = new OpenTailHostBuilder().AddModule(new OpenTail.Modules.Files.FilesModule(new OpenTail.Modules.Files.FilesModuleOptions { Root = dir }))
                .UsePolicy(policy).Build();
            var who = new ActionRequester(RequesterKind.User, "me");
            var s = host.Sessions.Start();
            async Task<ActionRecord> Write(string path) =>
                await host.Queue.SubmitAsync(new ActionRequest("files.write", System.Text.Json.JsonSerializer.Serialize(new { path, content = "x" }), who, s));

            Assert.Equal(ActionStatus.Succeeded, (await Write("drafts/a.txt")).Status);          // the scratch rule
            Assert.Equal(ActionStatus.Pending, (await Write("report.txt")).Status);              // everything else waits
            Assert.Equal(ActionStatus.Pending, (await Write("drafts/../report.txt")).Status);    // traversal does not ride the scratch rule
            Assert.Equal(ActionStatus.Pending, (await Write("drafts/sub/a.txt")).Status);
            Assert.Equal(ActionStatus.Denied, (await Write("drafts/password.txt")).Status);      // the deny rule wins over the scratch rule
            Assert.Equal(ActionStatus.Denied, (await Write("notes/.env")).Status);
        }
        finally { Directory.Delete(dir, true); }
    }
}
