using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>V6: who may decide, who may not approve their own request, and several approvers.</summary>
public class ApprovalControlTests
{
    private static readonly ActionRequester Alice = new(RequesterKind.AiSession, "key:alice-agent");

    private static string Policy(string tool, string top = "") =>
        "{ \"TaintWindow\": \"00:00:00\"" + (top == "" ? "" : ", " + top) + ", \"Tools\": { \"mail.send\": { \"Mode\": \"Queue\"" + (tool == "" ? "" : ", " + tool) + " } } }";

    private static async Task<(OpenTailHost Host, FakeInboxModule Module, ActionRecord Action)> Queued(string policy)
    {
        var (host, module, _) = Hosts.Build(policy);
        var s = host.Sessions.Start("key:alice-agent");
        var a = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"x@y.z","body":"b"}""", Alice, s));
        Assert.Equal(ActionStatus.Pending, a.Status);
        return (host, module, a);
    }

    private static ActionReview By(string name, ReviewDecision d = ReviewDecision.Approve, string? edited = null, params string[] groups) =>
        new(name, d, d is ReviewDecision.Approve ? null : "reason", DateTimeOffset.UtcNow)
        {
            Subject = Subjects.Admin(name),
            Groups = groups,
            EditedArgumentsJson = edited,
        };

    // ---- groups ----

    [Fact]
    public async Task Only_a_reviewer_in_the_required_group_may_decide()
    {
        var (host, module, a) = await Queued(Policy("\"ReviewerGroup\": \"finance\""));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(a.Id, By("bob")));
        Assert.Contains("'finance' group", ex.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(a.Id, By("bob", ReviewDecision.Reject)));   // not even to reject
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(a.Id))!.Status);
        Assert.Equal(0, module.SendCalls);

        var done = await host.Queue.ReviewAsync(a.Id, By("rita", groups: "finance"));
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task A_group_can_come_from_a_rule_that_matched_this_call()
    {
        const string rules = """
            "Rules": [ { "Name": "money", "Mode": "Queue", "ReviewerGroup": "finance", "When": { "arg": "/body", "contains": "invoice" } } ]
            """;
        var (host, _, _) = Hosts.Build(Policy(rules));
        var s = host.Sessions.Start("key:alice-agent");
        var plain = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"x@y.z","body":"hello"}""", Alice, s));
        var money = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"x@y.z","body":"the invoice"}""", Alice, s));

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(plain.Id, By("bob"))).Status);                      // anyone
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(money.Id, By("bob")));              // finance only
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(money.Id, By("rita", groups: "finance"))).Status);
    }

    [Fact]
    public async Task An_ai_reviewer_never_decides_a_human_only_action_or_stands_in_for_a_group()
    {
        var (host, _, humanOnly) = await Queued(Policy("\"Reviewer\": \"Human\""));
        var ai = new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };
        Assert.Contains("human reviewer", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(humanOnly.Id, ai))).Message);

        var (host2, _, grouped) = await Queued(Policy("\"Reviewer\": \"Either\", \"ReviewerGroup\": \"finance\""));
        Assert.Contains("AI reviewer cannot stand in", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host2.Queue.ReviewAsync(grouped.Id, ai))).Message);

        // But an action the operator handed to an AI reviewer is the AI's to decide.
        var (host3, _, handedOver) = await Queued(Policy("\"Reviewer\": \"Ai\", \"AiMaxRisk\": \"ExternalEffect\""));
        Assert.Equal(ActionStatus.Succeeded, (await host3.Queue.ReviewAsync(handedOver.Id, ai)).Status);
    }

    [Fact]
    public async Task The_ai_reviewer_runner_stamps_an_ai_identity_the_agent_cannot_fake()
    {
        var (host, _, a) = await Queued(Policy("\"Reviewer\": \"Either\", \"AiMaxRisk\": \"ExternalEffect\""));
        var runner = new ReviewerRunner(host);
        // The reviewer claims to be a person; the runner stamps who it really is.
        var liar = new DelegateReviewer("bot", (_, _) => Task.FromResult<ActionReview?>(
            new ActionReview("rita the human", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "admin:rita" }));
        await runner.RunOnceAsync(liar);
        var stored = await host.Store.GetAsync(a.Id);
        Assert.Equal("ai:bot", Assert.Single(stored!.Reviews).Subject);
    }

    // ---- self approval ----

    [Fact]
    public async Task Self_approval_is_allowed_by_default_and_refused_when_forbidden()
    {
        var (host, _, allowed) = await Queued(Policy(""));
        var self = new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "key:alice-agent" };
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(allowed.Id, self)).Status);

        var (host2, module2, forbidden) = await Queued(Policy("\"SelfApproval\": \"Forbid\""));
        Assert.Contains("your own request", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host2.Queue.ReviewAsync(forbidden.Id, self))).Message);
        Assert.Equal(0, module2.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await host2.Queue.ReviewAsync(forbidden.Id, By("bob"))).Status);   // a different person is fine
    }

    [Fact]
    public async Task The_people_map_says_which_subjects_are_the_same_human()
    {
        const string people = "\"People\": { \"alice\": [ \"admin:alice\", \"key:alice-agent\" ] }";
        var (host, _, a) = await Queued(Policy("\"SelfApproval\": \"Forbid\"", people));

        // Alice the reviewer is Alice's agent's owner: not allowed to approve what her agent asked for.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(a.Id, By("alice")));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(a.Id, By("bob"))).Status);

        // Without the map the host cannot know that, and says nothing: it only knows the namespaced subjects differ.
        var (host2, _, b) = await Queued(Policy("\"SelfApproval\": \"Forbid\""));
        Assert.Equal(ActionStatus.Succeeded, (await host2.Queue.ReviewAsync(b.Id, By("alice"))).Status);
    }

    [Fact]
    public async Task Rejecting_your_own_request_is_always_fine_and_an_unknown_identity_cannot_approve_when_it_matters()
    {
        var (host, _, a) = await Queued(Policy("\"SelfApproval\": \"Forbid\""));
        var anonymous = new ActionReview("someone", ReviewDecision.Approve, null, DateTimeOffset.UtcNow);              // no Subject
        Assert.Contains("identity is not known", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(a.Id, anonymous))).Message);
        var self = new ActionReview("alice", ReviewDecision.Reject, "changed my mind", DateTimeOffset.UtcNow) { Subject = "key:alice-agent" };
        Assert.Equal(ActionStatus.Rejected, (await host.Queue.ReviewAsync(a.Id, self)).Status);
    }

    // ---- several approvers ----

    [Fact]
    public async Task An_edit_remakes_the_preview_so_the_next_approver_sees_what_will_run()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2"));
        Assert.Contains("x@y.z", a.PreviewJson);

        var edited = await host.Queue.ReviewAsync(a.Id, By("rita") with { EditedArgumentsJson = """{"to":"other@corp.example","body":"b"}""" });

        Assert.Equal(ActionStatus.Pending, edited.Status);
        Assert.Contains("other@corp.example", edited.PreviewJson);
        Assert.DoesNotContain("x@y.z", edited.PreviewJson);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task When_the_edited_arguments_have_no_preview_the_old_one_is_not_kept()
    {
        var (host, _, a) = await Queued(Policy("\"RequiredApprovals\": 2"));

        var edited = await host.Queue.ReviewAsync(a.Id, By("rita") with { EditedArgumentsJson = """{"body":"no recipient"}""" });

        Assert.Null(edited.PreviewJson);
        Assert.True(edited.PreviewUnavailable);
    }

    [Fact]
    public async Task Two_approvals_are_needed_and_the_same_person_counts_once()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2"));
        Assert.Equal(2, a.RequiredApprovals);

        var first = await host.Queue.ReviewAsync(a.Id, By("rita"));
        Assert.Equal(ActionStatus.Pending, first.Status);
        Assert.Equal(0, module.SendCalls);
        Assert.Contains(((IAuditReader)host.Audit).Read(a.Id), e => e.Event == "approval_recorded" && e.Detail == "1 of 2");

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(a.Id, By("rita")));
        Assert.Contains("already approved", again.Message);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(a.Id))!.Status);

        var done = await host.Queue.ReviewAsync(a.Id, By("bob"));
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(1, module.SendCalls);
        Assert.Equal(2, done.Reviews.Count);
    }

    [Fact]
    public async Task Two_names_for_one_person_count_once()
    {
        const string people = "\"People\": { \"rita\": [ \"admin:rita\", \"jwt:https://idp|rita-42\" ] }";
        var (host, _, a) = await Queued(Policy("\"RequiredApprovals\": 2", people));
        await host.Queue.ReviewAsync(a.Id, By("rita"));
        var viaSso = new ActionReview("rita (sso)", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "jwt:https://idp|rita-42" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(a.Id, viaSso));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(a.Id, By("bob"))).Status);
    }

    [Fact]
    public async Task One_rejection_by_an_eligible_reviewer_ends_it_even_after_an_approval()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2"));
        await host.Queue.ReviewAsync(a.Id, By("rita"));
        var rejected = await host.Queue.ReviewAsync(a.Id, By("bob", ReviewDecision.Reject));
        Assert.Equal(ActionStatus.Rejected, rejected.Status);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task An_edit_voids_the_approvals_given_before_it()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2"));
        await host.Queue.ReviewAsync(a.Id, By("rita"));                                                  // 1 approval of the original

        var afterEdit = await host.Queue.ReviewAsync(a.Id, By("bob", edited: """{"to":"safe@corp.example","body":"edited"}"""));
        Assert.Equal(ActionStatus.Pending, afterEdit.Status);                                            // Rita approved different arguments
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(1, ReviewAuthority.Approvals(afterEdit, host.Policy.People));

        var done = await host.Queue.ReviewAsync(a.Id, By("rita"));                                       // Rita approves the edited version
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal(("safe@corp.example", "edited"), Assert.Single(module.Sent));                       // what ran is what both approved
    }

    [Fact]
    public async Task An_ai_approval_never_makes_up_the_numbers_when_several_are_needed()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2, \"Reviewer\": \"Either\", \"AiMaxRisk\": \"ExternalEffect\""));
        var ai = new ActionReview("bot", ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = Subjects.Ai("bot") };
        Assert.Equal(ActionStatus.Pending, (await host.Queue.ReviewAsync(a.Id, ai)).Status);            // recorded, counts for nothing
        Assert.Equal(ActionStatus.Pending, (await host.Queue.ReviewAsync(a.Id, By("rita"))).Status);    // 1 human of 2
        Assert.Equal(0, module.SendCalls);
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(a.Id, By("bob"))).Status);
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Eight_reviewers_approving_at_once_run_the_action_exactly_once()
    {
        var (host, module, a) = await Queued(Policy("\"RequiredApprovals\": 2"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { return await host.Queue.ReviewAsync(a.Id, By($"r{i}")); }
            catch (InvalidOperationException) { return null; }                                           // too late: it was already decided
        })));

        Assert.Equal(1, module.SendCalls);
        Assert.Contains(results, r => r?.Status == ActionStatus.Succeeded);
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(a.Id))!.Status);
    }

    [Fact]
    public async Task Several_approvers_hold_across_two_hosts_sharing_one_database()
    {
        using var db = new TempDb();
        var m1 = new FakeInboxModule();
        var m2 = new FakeInboxModule();
        var h1 = Hosts.BuildSqlite(m1, Policy("\"RequiredApprovals\": 2"), db.Path);
        var h2 = Hosts.BuildSqlite(m2, Policy("\"RequiredApprovals\": 2"), db.Path);
        var a = await h1.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"x@y.z","body":"b"}""", Alice, h1.Sessions.Start("key:alice-agent")));

        await h1.Queue.ReviewAsync(a.Id, By("rita"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h2.Queue.ReviewAsync(a.Id, By("rita")));   // the other process sees Rita already approved
        Assert.Equal(ActionStatus.Succeeded, (await h2.Queue.ReviewAsync(a.Id, By("bob"))).Status);
        Assert.Equal(1, m1.SendCalls + m2.SendCalls);
        TempDb.ClearPools(db.Path);
    }

    [Fact]
    public async Task A_retry_goes_through_the_same_gate()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var (host, module, _) = Hosts.Build(Policy("\"ReviewerGroup\": \"ops\", \"RateLimit\": { \"PerHour\": 1 }"), clock);
        var s = host.Sessions.Start("key:alice-agent");
        var first = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@y.z","body":"b"}""", Alice, s));
        var second = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"b@y.z","body":"b"}""", Alice, s));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(first.Id, By("ola", groups: "ops"))).Status);

        // The hourly limit makes the second approval fail in a way that can be retried later.
        var failed = await host.Queue.ReviewAsync(second.Id, By("ola", groups: "ops"));
        Assert.Equal(ActionStatus.Failed, failed.Status);
        Assert.True(failed.Error!.Retryable);

        clock.Advance(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Queue.ReviewAsync(second.Id, By("bob", ReviewDecision.Retry)));   // not in the group
        Assert.Equal(ActionStatus.Failed, (await host.Store.GetAsync(second.Id))!.Status);
        Assert.Equal(1, module.SendCalls);

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(second.Id, By("ola", ReviewDecision.Retry, groups: "ops"))).Status);
        Assert.Equal(2, module.SendCalls);
    }

    // ---- configuration ----

    [Fact]
    public void Bad_approval_settings_are_refused_at_startup()
    {
        string Build(string policy)
        {
            var b = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UsePolicy(PolicyConfig.Parse(policy));
            return Assert.Throws<InvalidOperationException>(() => b.Build()).Message;
        }
        Assert.Contains("between 1 and 5", Build(Policy("\"RequiredApprovals\": 0")));
        Assert.Contains("between 1 and 5", Build(Policy("\"RequiredApprovals\": 9")));
        Assert.Contains("trust ramp makes no sense", Build(Policy("\"RequiredApprovals\": 2, \"TrustRamp\": { \"AfterCleanApprovals\": 3 }")));
        Assert.Contains("not a namespaced subject", Build(Policy("", "\"People\": { \"alice\": [ \"alice\" ] }")));
        Assert.Contains("listed under both", Build(Policy("", "\"People\": { \"a\": [ \"admin:x\" ], \"b\": [ \"admin:x\" ] }")));
    }

    // ---- the approval page and API ----

    [Fact]
    public async Task The_api_shows_what_each_reviewer_may_do_and_refuses_the_rest_with_403()
    {
        const string finance = "reviewer-finance-0123456789";
        const string other = "reviewer-other-0123456789";
        var module = new FakeInboxModule();
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse(Policy("\"ReviewerGroup\": \"finance\", \"RequiredApprovals\": 2"))).Build();
        var a = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"x@y.z","body":"b"}""", Alice, host.Sessions.Start("key:alice-agent")));

        await using var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [finance] = "fran", [other] = "otto" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [finance] = new HashSet<string> { Roles.Review, "group:finance" },
                [other] = new HashSet<string> { Roles.Review },
            },
        });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        HttpRequestMessage Req(HttpMethod m, string path, string token, string? body = null)
        {
            var r = new HttpRequestMessage(m, path);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }

        var seenByFran = JsonNode.Parse(await (await http.SendAsync(Req(HttpMethod.Get, $"/api/actions/{a.Id}", finance))).Content.ReadAsStringAsync())!;
        Assert.True(seenByFran["canDecide"]!.GetValue<bool>());
        Assert.Equal(2, seenByFran["requiredApprovals"]!.GetValue<int>());
        Assert.Equal(0, seenByFran["approvals"]!.GetValue<int>());
        Assert.Equal("finance", seenByFran["reviewerGroup"]!.ToString());

        var seenByOtto = JsonNode.Parse(await (await http.SendAsync(Req(HttpMethod.Get, $"/api/actions/{a.Id}", other))).Content.ReadAsStringAsync())!;
        Assert.False(seenByOtto["canDecide"]!.GetValue<bool>());
        Assert.Contains("'finance' group", seenByOtto["cannotDecideReason"]!.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Req(HttpMethod.Post, $"/api/actions/{a.Id}/review", other, """{"decision":"approve"}"""))).StatusCode);

        var first = await http.SendAsync(Req(HttpMethod.Post, $"/api/actions/{a.Id}/review", finance, """{"decision":"approve"}"""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        Assert.Equal("Pending", afterFirst["status"]!.ToString());
        Assert.Equal(1, afterFirst["approvals"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Req(HttpMethod.Post, $"/api/actions/{a.Id}/review", finance, """{"decision":"approve"}"""))).StatusCode); // same person again
        Assert.Equal(0, module.SendCalls);
    }
}
