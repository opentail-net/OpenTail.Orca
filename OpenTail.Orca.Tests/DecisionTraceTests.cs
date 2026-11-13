using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Audit D1 and D2: every action carries the checks that decided it and the hash of the policy it was decided under; every
/// policy a Host runs under is kept and its loading audited.</summary>
public class DecisionTraceTests
{
    private const string Policy = """
        { "DefaultReadMode": "Execute",
          "Tools": {
            "mail.send": { "Mode": "Execute",
                           "Rules": [ { "Name": "outside-domain", "Mode": "Queue", "When": { "arg": "/to", "any": { "domainIn": ["elsewhere.example"] } } } ] },
            "files.delete": { "Mode": "Deny" } } }
        """;

    private static ActionRequest Call(string tool, string? session, string args = "{}") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, "key:bot"), session);

    private static string Checks(ActionRecord r) => string.Join(" > ", r.DecisionTrace.Select(t => $"{t.Check}:{t.Result}{(t.Changed ? "*" : "")}"));

    [Fact]
    public async Task A_call_that_runs_records_every_check_it_passed_and_the_policy_hash()
    {
        var (host, _, _) = Hosts.Build(Policy);
        var s = host.Sessions.Start("key:bot");
        var r = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"cfo@corp.example","body":"hi"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal("profile:allowed > level:Normal > policy:Execute > taint:clean", Checks(r));
        Assert.Equal(host.Policy.PolicyHash, r.PolicyHash);
        Assert.Matches("^sha256:[0-9a-f]{64}$", r.PolicyHash!);
    }

    [Fact]
    public async Task A_matching_rule_shows_as_the_check_that_changed_the_outcome()
    {
        var (host, _, _) = Hosts.Build(Policy);
        var s = host.Sessions.Start("key:bot");
        var r = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"x@elsewhere.example","body":"hi"}"""));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Equal("profile:allowed > level:Normal > policy:Execute > rule:Queue* > taint:clean > queue:Queue", Checks(r));
        Assert.Equal("rule outside-domain", r.DecisionTrace.Single(t => t.Check == "rule").Evidence);
    }

    [Fact]
    public async Task Taint_that_holds_a_call_the_policy_would_run_is_recorded_as_the_change()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendExecute);
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("inbox.read", s));
        var r = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"a@b.example","body":"hi"}"""));
        Assert.Equal(ActionStatus.Pending, r.Status);
        var taint = r.DecisionTrace.Single(t => t.Check == "taint");
        Assert.Equal(("Queue", true), (taint.Result, taint.Changed));
        Assert.Contains("untrusted", taint.Evidence);
    }

    [Fact]
    public async Task Refusals_before_policy_carry_the_refusing_check()
    {
        var (host, _, _) = Hosts.Build(Policy);
        var unknown = await host.Queue.SubmitAsync(Call("no.such", null));
        Assert.Equal("tool:Deny*", Checks(unknown));
        var denied = await host.Queue.SubmitAsync(Call("files.delete", null, """{"path":"x"}"""));
        Assert.Equal(ActionStatus.Denied, denied.Status);
        Assert.Equal("profile:allowed > level:Normal > policy:Deny > taint:no change", Checks(denied));
        Assert.Equal("tool-policy", denied.DecisionTrace.Single(t => t.Check == "policy").Evidence);
    }

    [Fact]
    public async Task The_trace_survives_the_database()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        var s = host.Sessions.Start("key:bot");
        var r = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"x@elsewhere.example","body":"hi"}"""));
        var read = await host.Store.GetAsync(r.Id);
        Assert.Equal(r.DecisionTrace, read!.DecisionTrace);
        Assert.Equal(r.PolicyHash, read.PolicyHash);
    }

    [Fact]
    public void The_policy_hash_ignores_order_and_follows_content()
    {
        var a = PolicyConfig.Parse("""{ "DefaultReadMode": "Execute", "Tools": { "a.x": { "Mode": "Queue" }, "b.y": { "Mode": "Deny" } } }""");
        var b = PolicyConfig.Parse("""{ "Tools": { "b.y": { "Mode": "Deny" }, "a.x": { "Mode": "Queue" } }, "DefaultReadMode": "Execute" }""");
        var c = PolicyConfig.Parse("""{ "DefaultReadMode": "Execute", "Tools": { "a.x": { "Mode": "Execute" }, "b.y": { "Mode": "Deny" } } }""");
        Assert.Equal(a.Hash(), b.Hash());
        Assert.NotEqual(a.Hash(), c.Hash());
    }

    [Fact]
    public async Task Start_up_records_the_policy_version_and_audits_it_once_per_process()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        await host.RunMaintenanceOnceAsync();
        await host.RunMaintenanceOnceAsync();
        var loaded = ((IAuditReader)host.Audit).Read().Where(e => e.Event == "policy_loaded").ToList();
        var entry = Assert.Single(loaded);
        Assert.StartsWith(host.Policy.PolicyHash + " (new version); host ", entry.Detail);
        Assert.Contains("instance ", entry.Detail);

        // A restart under the same policy: audited again, not a new version.
        TempDb.ClearPools(db.Path);
        var again = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        await again.RunMaintenanceOnceAsync();
        var second = ((IAuditReader)again.Audit).Read().Where(e => e.Event == "policy_loaded").ToList();
        Assert.Equal(2, second.Count);
        Assert.DoesNotContain("new version", second[1].Detail);
        Assert.Single(again.PolicyVersions.List());
    }

    [Fact]
    public async Task Policy_versions_and_a_diff_between_them_from_the_command_line()
    {
        using var db = new TempDb();
        var first = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        await first.RunMaintenanceOnceAsync();
        TempDb.ClearPools(db.Path);
        var second = Hosts.BuildSqlite(new FakeInboxModule(), Policy.Replace("\"Deny\" }", "\"Queue\" }"), db.Path);
        await second.RunMaintenanceOnceAsync();
        TempDb.ClearPools(db.Path);

        var list = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--policy-versions"], "host", _ => { }, list));
        Assert.Equal(2, list.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var diff = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--db", db.Path, "--policy-diff", first.Policy.PolicyHash[7..19], second.Policy.PolicyHash], "host", _ => { }, diff));
        Assert.Contains("- ", diff.ToString());
        Assert.Contains("\"Deny\"", diff.ToString());
        Assert.Contains("+ ", diff.ToString());
        Assert.Contains("\"Queue\"", diff.ToString());
    }
}
