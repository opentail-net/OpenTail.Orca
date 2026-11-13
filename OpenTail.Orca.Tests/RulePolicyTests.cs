using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenTail.Orca.Tests;

/// <summary>Conditional rules inside the real queue: what executes, what waits, what is refused, and why.</summary>
public class RulePolicyTests
{
    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    private const string Internal = """
        { "Name": "internal", "Mode": "Execute", "IgnoreArgs": ["body"],
          "When": { "arg": "/to", "all": { "domainIn": ["corp.example"] } } }
        """;

    private const string NoRivals = """
        { "Name": "no-rivals", "Mode": "Deny",
          "When": { "arg": "/to", "any": { "domainIn": ["rival.example"] } } }
        """;

    private static string Policy(string rules, string extraTool = "", string top = "") =>
        "{ \"DefaultReadMode\": \"Execute\", \"TaintWindow\": \"00:00:00\"" + (top == "" ? "" : ", " + top) +
        ", \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\"" + (extraTool == "" ? "" : ", " + extraTool) +
        ", \"Rules\": [" + rules + "] } } }";

    private static ActionRequest Send(string session, string to, string body = "hi", string? extra = null) =>
        new("mail.send", extra is null
                ? $$"""{"to":{{JsonSerializer.Serialize(to)}},"body":"{{body}}"}"""
                : $$"""{"to":{{JsonSerializer.Serialize(to)}},"body":"{{body}}",{{extra}}}""",
            Me, session);

    private static ActionRequest SendTo(string session, string rawToJson) =>
        new("mail.send", $$"""{"to":{{rawToJson}},"body":"hi"}""", Me, session);

    // ---- relaxing and tightening ----

    [Fact]
    public async Task An_execute_rule_runs_the_call_when_it_is_definitely_true_and_covers_every_argument()
    {
        var (host, module, _) = Hosts.Build(Policy(Internal));
        var s = host.Sessions.Start();
        var r = await host.Queue.SubmitAsync(Send(s, "alice@corp.example"));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal("internal", r.PolicyRule);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Anything_the_rule_does_not_match_still_waits_for_a_reviewer()
    {
        var (host, module, _) = Hosts.Build(Policy(Internal));
        var s = host.Sessions.Start();
        var r = await host.Queue.SubmitAsync(Send(s, "someone@elsewhere.example"));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Null(r.PolicyRule);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_extra_argument_the_rule_never_looked_at_defeats_the_rule()
    {
        var (host, module, _) = Hosts.Build(Policy(Internal));
        var s = host.Sessions.Start();
        // The model adds a bcc. The rule checked "to" and ignores "body", but it says nothing about "bcc".
        var r = await host.Queue.SubmitAsync(Send(s, "alice@corp.example", extra: "\"bcc\":\"leak@rival.example\""));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_tainted_caller_is_never_relaxed_by_a_rule()
    {
        var (host, module, audit) = Hosts.Build(Policy(Internal));
        var s = host.Sessions.Start();
        host.Sessions.Taint(s);
        var r = await host.Queue.SubmitAsync(Send(s, "alice@corp.example"));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "policy_decision" && (e.Detail ?? "").Contains("+taint:queue"));
    }

    [Fact]
    public async Task A_deny_rule_refuses_and_beats_an_execute_rule()
    {
        var (host, module, _) = Hosts.Build(Policy(Internal + "," + NoRivals));
        var s = host.Sessions.Start();
        var r = await host.Queue.SubmitAsync(Send(s, "ceo@rival.example"));
        Assert.Equal(ActionStatus.Denied, r.Status);
        Assert.Equal("denied", r.Error!.Code);
        Assert.Contains("no-rivals", r.Error.Message);
        Assert.Equal(0, module.SendCalls);
    }

    [Theory]
    [InlineData("\"Bob <b@rival.example>\"")]   // a display name: unreadable, so the deny rule applies
    [InlineData("\"a@corp.example@rival.example\"")]
    [InlineData("[\"ok@corp.example\",\"b@rival.example\"]")]
    public async Task A_deny_rule_also_applies_when_it_cannot_read_the_value(string rawTo)
    {
        var (host, module, _) = Hosts.Build(Policy(NoRivals));
        var r = await host.Queue.SubmitAsync(SendTo(host.Sessions.Start(), rawTo));
        Assert.Equal(ActionStatus.Denied, r.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_misspelt_deny_rule_fails_loudly_by_denying_everything_not_by_matching_nothing()
    {
        const string typo = """{ "Name": "no-rivals", "Mode": "Deny", "When": { "arg": "/too", "any": { "domainIn": ["rival.example"] } } }""";
        var (host, _, _) = Hosts.Build(Policy(typo));
        var r = await host.Queue.SubmitAsync(Send(host.Sessions.Start(), "alice@corp.example"));
        Assert.Equal(ActionStatus.Denied, r.Status); // the operator notices on day one
    }

    [Fact]
    public async Task A_negated_condition_cannot_open_the_door_when_the_field_is_missing()
    {
        const string notRival = """{ "Name": "not-rival", "Mode": "Execute", "IgnoreArgs": ["body"], "When": { "not": { "arg": "/to", "any": { "domainIn": ["rival.example"] } } } }""";
        var (host, module, _) = Hosts.Build(Policy(notRival));
        var s = host.Sessions.Start();
        // No "to" at all: the condition is unknown, so the Execute rule does not apply and the call waits.
        var r = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"body":"hi"}""", Me, s));
        Assert.Equal(ActionStatus.Pending, r.Status);
        Assert.Equal(0, module.SendCalls);
        // An unreadable recipient is the same.
        var r2 = await host.Queue.SubmitAsync(SendTo(s, "\"Bob <b@rival.example>\""));
        Assert.Equal(ActionStatus.Pending, r2.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task A_queue_rule_overrides_who_decides_and_for_how_long()
    {
        const string finance = """
            { "Name": "finance", "Mode": "Queue", "ReviewerGroup": "finance", "RequiredApprovals": 2, "ExpireAfter": "00:30:00", "Reviewer": "Human",
              "When": { "arg": "/body", "matches": "invoice|payment", "ignoreCase": true } }
            """;
        var (host, _, _) = Hosts.Build(Policy(finance, "\"ExpireAfter\": \"08:00:00\""));
        var s = host.Sessions.Start();

        var plain = await host.Queue.SubmitAsync(Send(s, "x@y.z", "hello"));
        Assert.Null(plain.ReviewerGroup);
        Assert.Equal(1, plain.RequiredApprovals);

        var money = await host.Queue.SubmitAsync(Send(s, "x@y.z", "Please pay this Invoice"));
        Assert.Equal("finance", money.PolicyRule);
        Assert.Equal("finance", money.ReviewerGroup);
        Assert.Equal(2, money.RequiredApprovals);
        Assert.Equal(TimeSpan.FromMinutes(30), money.ExpiresAt - money.CreatedAt);       // the stricter of rule and tool
        Assert.Null(money.PolicySnapshot!.Rules);                                         // the snapshot stays small
    }

    [Fact]
    public async Task A_promotion_never_overrides_a_queue_rule_that_matched()
    {
        const string alwaysCheckBig = """{ "Name": "big", "Mode": "Queue", "When": { "arg": "/body", "contains": "BIG" } }""";
        var (host, _, _) = Hosts.Build(Policy(alwaysCheckBig, "\"TrustRamp\": { \"AfterCleanApprovals\": 1 }"));
        var s = host.Sessions.Start();
        await host.Queue.ReviewAsync((await host.Queue.SubmitAsync(Send(s, "x@y.z"))).Id, new ActionReview("a", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.True(await host.Trust.PromoteAsync("mail.send", "op"));

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(s, "x@y.z", "small"))).Status);   // promoted: runs
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send(s, "x@y.z", "BIG"))).Status);       // the rule still holds it
    }

    [Fact]
    public async Task A_rule_can_tighten_a_tool_whose_base_mode_is_execute()
    {
        const string holdBig = """{ "Name": "big", "Mode": "Queue", "When": { "arg": "/body", "contains": "BIG" } }""";
        var policy = "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Execute\", \"Rules\": [" + holdBig + "] } } }";
        var (host, module, _) = Hosts.Build(policy);
        var s = host.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(s, "x@y.z", "small"))).Status);
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send(s, "x@y.z", "BIG"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task A_reviewer_edit_that_now_matches_a_deny_rule_is_refused()
    {
        var (host, module, audit) = Hosts.Build(Policy(NoRivals));
        var pending = await host.Queue.SubmitAsync(Send(host.Sessions.Start(), "x@elsewhere.example"));
        Assert.Equal(ActionStatus.Pending, pending.Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(pending.Id,
            new ActionReview("rita", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { EditedArgumentsJson = """{"to":"ceo@rival.example","body":"hi"}""" }));
        Assert.Contains("no-rivals", ex.Message);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(pending.Id))!.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "edit_refused");
    }

    // ---- budgets ----

    private const string BudgetedInternal = """
        { "Name": "internal", "Mode": "Execute", "IgnoreArgs": ["body"],
          "Budget": { "Per": "24:00:00", "MaxCount": 2, "Scope": "Requester" },
          "When": { "arg": "/to", "all": { "domainIn": ["corp.example"] } } }
        """;

    [Fact]
    public async Task A_spent_budget_turns_auto_execution_back_into_a_queue_until_the_window_passes()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, module, audit) = Hosts.Build(Policy(BudgetedInternal), clock);
        var s = host.Sessions.Start();

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(s, "a@corp.example"))).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(s, "b@corp.example"))).Status);
        var third = await host.Queue.SubmitAsync(Send(s, "c@corp.example"));
        Assert.Equal(ActionStatus.Pending, third.Status);
        Assert.Null(third.PolicyRule);
        Assert.Equal(2, module.SendCalls);
        Assert.Contains(audit.Entries, e => e.Event == "policy_decision" && (e.Detail ?? "").Contains("budget:queue"));

        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Send(s, "d@corp.example"))).Status);
    }

    [Fact]
    public async Task A_requester_budget_is_per_requester()
    {
        var (host, _, _) = Hosts.Build(Policy(BudgetedInternal));
        var s = host.Sessions.Start();
        var other = new ActionRequester(RequesterKind.User, "other");
        for (var i = 0; i < 2; i++) await host.Queue.SubmitAsync(Send(s, $"a{i}@corp.example"));
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Send(s, "x@corp.example"))).Status);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(
            new ActionRequest("mail.send", """{"to":"y@corp.example","body":"hi"}""", other, s))).Status);
    }

    [Fact]
    public async Task A_budget_holds_across_two_hosts_sharing_one_database()
    {
        using var db = new TempDb();
        var policy = Policy(BudgetedInternal.Replace("\"MaxCount\": 2", "\"MaxCount\": 3"));
        var m1 = new FakeInboxModule();
        var m2 = new FakeInboxModule();
        var h1 = Hosts.BuildSqlite(m1, policy, db.Path);
        var h2 = Hosts.BuildSqlite(m2, policy, db.Path);
        var s1 = h1.Sessions.Start();
        var s2 = h2.Sessions.Start();

        var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(i =>
            Task.Run(() => (i % 2 == 0 ? h1 : h2).Queue.SubmitAsync(Send(i % 2 == 0 ? s1 : s2, $"u{i}@corp.example")))));

        Assert.Equal(3, results.Count(r => r.Status == ActionStatus.Succeeded));
        Assert.Equal(11, results.Count(r => r.Status == ActionStatus.Pending));
        Assert.Equal(3, m1.SendCalls + m2.SendCalls);
        SqliteConnection.ClearAllPools();
    }

    // ---- explain / simulate ----

    [Fact]
    public void Simulate_shows_how_each_rule_evaluated_and_executes_nothing()
    {
        var (host, module, _) = Hosts.Build(Policy(Internal + "," + NoRivals));
        var d = host.Policy.Simulate(host.Catalog, "mail.send", """{"to":"alice@corp.example","body":"hi"}""", tainted: false);
        Assert.Equal(ToolMode.Execute, d.Mode);
        Assert.Equal("internal", d.RuleName);
        Assert.Contains(d.Trace, l => l.Contains("rule 'internal'"));
        Assert.Contains(d.Trace, l => l.Contains("domainIn => True"));
        Assert.Contains(d.Trace, l => l.Contains("rule 'no-rivals'") );
        Assert.Equal(0, module.SendCalls);

        var extra = host.Policy.Simulate(host.Catalog, "mail.send", """{"to":"alice@corp.example","body":"hi","bcc":"x@y.z"}""", tainted: false);
        Assert.Equal(ToolMode.Queue, extra.Mode);
        Assert.Contains(extra.Trace, l => l.Contains("does not account for argument(s) bcc"));

        var tainted = host.Policy.Simulate(host.Catalog, "mail.send", """{"to":"alice@corp.example","body":"hi"}""", tainted: true);
        Assert.Equal(ToolMode.Queue, tainted.Mode);
        Assert.Contains(tainted.Trace, l => l.Contains("taint"));

        Assert.Equal(ToolMode.Deny, host.Policy.Simulate(host.Catalog, "no.such.tool", "{}", false).Mode);
    }

    [Fact]
    public void A_time_rule_is_judged_at_the_time_it_is_given()
    {
        const string officeHours = """
            { "Name": "office", "Mode": "Execute", "IgnoreArgs": ["body"],
              "When": { "all": [ { "arg": "/to", "all": { "domainIn": ["corp.example"] } },
                                 { "time": { "days": ["Mon","Tue","Wed","Thu","Fri"], "from": "09:00", "to": "17:00", "zone": "Europe/Amsterdam" } } ] } }
            """;
        var (host, _, _) = Hosts.Build(Policy(officeHours));
        const string args = """{"to":"a@corp.example","body":"hi"}""";
        Assert.Equal(ToolMode.Execute, host.Policy.Simulate(host.Catalog, "mail.send", args, false, DateTimeOffset.Parse("2026-10-14T10:00:00Z")).Mode);  // Wed 12:00 local
        Assert.Equal(ToolMode.Queue, host.Policy.Simulate(host.Catalog, "mail.send", args, false, DateTimeOffset.Parse("2026-10-14T20:00:00Z")).Mode);    // Wed 22:00 local
        Assert.Equal(ToolMode.Queue, host.Policy.Simulate(host.Catalog, "mail.send", args, false, DateTimeOffset.Parse("2026-10-17T10:00:00Z")).Mode);    // Saturday
    }

    // ---- loading the file ----

    [Fact]
    public void A_policy_file_with_rules_loads_and_a_typo_in_a_rule_is_an_error()
    {
        Assert.NotNull(PolicyConfig.Parse(Policy(Internal)));
        Assert.ThrowsAny<JsonException>(() => PolicyConfig.Parse(Policy("""{ "Name": "x", "Mod": "Deny", "When": { "arg": "/a", "equals": 1 } }""")));
    }

    [Theory]
    [InlineData("""{ "Name": "a", "Mode": "Execute", "When": { "arg": "/x", "equals": 1 } }""", "{ \"Tools\": { \"mail.send\": { \"Mode\": \"Execute\", \"Rules\": [", "only makes sense when the tool's own Mode is Queue")]
    [InlineData("""{ "Name": "a", "Mode": "Deny", "When": { "arg": "/x", "equals": 1 } }, { "Name": "a", "Mode": "Deny", "When": { "arg": "/x", "equals": 2 } }""", null, "duplicate rule name")]
    [InlineData("""{ "Name": "a", "Mode": "Queue", "Budget": { "Per": "01:00:00", "MaxCount": 1 }, "When": { "arg": "/x", "equals": 1 } }""", null, "Budget belongs on an Execute rule")]
    [InlineData("""{ "Name": "a", "Mode": "Execute", "Budget": { "Per": "00:00:00", "MaxCount": 1 }, "When": { "arg": "/x", "equals": 1 } }""", null, "positive Per and MaxCount")]
    [InlineData("""{ "Name": "a", "Mode": "Deny", "ReviewerGroup": "x", "When": { "arg": "/x", "equals": 1 } }""", null, "only apply to Queue rules")]
    [InlineData("""{ "Name": "a", "Mode": "Queue", "RequiredApprovals": 9, "When": { "arg": "/x", "equals": 1 } }""", null, "between 1 and 5")]
    [InlineData("""{ "Name": "a", "Mode": "Queue" }""", null, "'When' is required")]
    [InlineData("""{ "Name": "a", "Mode": "Queue", "When": { "arg": "/x", "equalz": 1 } }""", null, "unknown test")]
    [InlineData("""{ "Name": "", "Mode": "Queue", "When": { "arg": "/x", "equals": 1 } }""", null, "needs a Name")]
    public void The_host_refuses_to_start_with_a_bad_rule(string rules, string? wholePolicyPrefix, string expected)
    {
        var json = wholePolicyPrefix is null ? Policy(rules) : wholePolicyPrefix + rules + "] } } }";
        var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(json));
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => b.Build()).Message);
    }
}
