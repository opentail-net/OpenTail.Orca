using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Tests;

/// <summary>Plan v7 A3: policy changes are proposed, checked, replayed and approved by a second admin; the Host never applies them itself.</summary>
public class PolicyProposalTests
{
    private const string Policy = """
        { "DefaultReadMode": "Execute", "TaintWindow": "00:00:00",
          "Tools": { "mail.send": { "Mode": "Queue" }, "ledger.append": { "Mode": "Queue" } },
          "Agents": { "clerk": { "Tools": [ "inbox.*" ] } },
          "People": { "ann": [ "admin:ann", "key:ann-bot" ] } }
        """;

    private static string WithClerk(OpenTailHost host, params string[] tools)
    {
        var p = JsonNode.Parse(host.Proposals.CurrentPolicyJson())!.AsObject();
        p["Agents"]!["clerk"]!["Tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]);
        return p.ToJsonString();
    }

    [Fact]
    public async Task A_proposal_is_checked_and_replayed_and_needs_a_second_person_to_approve()
    {
        var host = Hosts.Build(Policy).Host;
        await host.RunMaintenanceOnceAsync();
        // Queued under the running policy; the proposal would let it run.
        await host.Queue.SubmitAsync(new ActionRequest("ledger.append", """{"text":"x"}""", new ActionRequester(RequesterKind.AiSession, "key:x")));

        var candidate = JsonNode.Parse(WithClerk(host, "inbox.*", "mail.*"))!.AsObject();
        candidate["Tools"]!["ledger.append"]!["Mode"] = "Execute";
        var p = await host.Proposals.ProposeAsync(candidate.ToJsonString(), "admin:ann", "let the clerk send");

        Assert.Equal(ProposalStatus.Proposed, p.Status);
        Assert.Equal(host.Policy.PolicyHash, p.BaseHash);
        var replay = JsonNode.Parse(p.ReplayJson)!;
        Assert.Equal(1, replay["changed"]!.GetValue<int>());
        Assert.Equal("Execute", replay["examples"]![0]!["wouldBe"]!.GetValue<string>());

        // The proposer, and the same person under another identity, cannot approve it.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Proposals.ApproveAsync(p.Id, "admin:ann"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Proposals.ApproveAsync(p.Id, "key:ann-bot"));
        var approved = await host.Proposals.ApproveAsync(p.Id, "admin:bo");
        Assert.Equal(ProposalStatus.Approved, approved.Status);
        Assert.Equal("admin:bo", approved.DecidedBy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Proposals.ApproveAsync(p.Id, "admin:cy"));

        // Nothing changed in the running Host.
        Assert.Equal(p.BaseHash, host.Policy.PolicyHash);
        var events = ((IAuditReader)host.Audit).Read().Select(e => e.Event).ToList();
        Assert.Contains("policy_proposed", events);
        Assert.Contains("policy_proposal_approved", events);
    }

    [Theory]
    [InlineData("""{ "Agents": { "clerk": { "Tools": [ "no.such" ] } } }""", "would not start")]
    [InlineData("""{ "Include": [ "more.json" ] }""", "Include")]
    [InlineData("""{ "Tools": [ 1 ] }""", "Not a valid policy")]
    public async Task A_policy_that_would_not_start_is_refused(string json, string message)
    {
        var host = Hosts.Build(Policy).Host;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Proposals.ProposeAsync(json, "admin:ann", null));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Proposals.List());
    }

    [Fact]
    public async Task The_running_policy_itself_is_not_a_proposal_and_reject_needs_a_reason_and_only_the_proposer_withdraws()
    {
        var host = Hosts.Build(Policy).Host;
        await Assert.ThrowsAsync<ArgumentException>(() => host.Proposals.ProposeAsync(host.Proposals.CurrentPolicyJson(), "admin:ann", null));

        var p = await host.Proposals.ProposeAsync(WithClerk(host, "inbox.*", "clock.*"), "admin:ann", null);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Proposals.RejectAsync(p.Id, "admin:bo", " "));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Proposals.WithdrawAsync(p.Id, "admin:bo"));
        Assert.Equal(ProposalStatus.Withdrawn, (await host.Proposals.WithdrawAsync(p.Id, "key:ann-bot")).Status);

        var q = await host.Proposals.ProposeAsync(WithClerk(host, "inbox.*", "clock.*"), "admin:ann", null);
        var rejected = await host.Proposals.RejectAsync(q.Id, "admin:bo", "too broad");
        Assert.Equal("too broad", rejected.Reason);
    }

    [Fact]
    public async Task The_downloaded_file_starts_as_the_proposed_policy_marks_it_applied_and_leaves_others_stale()
    {
        using var db = new TempDb();
        var host = Hosts.BuildSqlite(new FakeInboxModule(), Policy, db.Path);
        await host.RunMaintenanceOnceAsync();
        var p = await host.Proposals.ProposeAsync(WithClerk(host, "inbox.*", "clock.*"), "admin:ann", null);
        var other = await host.Proposals.ProposeAsync(WithClerk(host, "inbox.*", "mail.*"), "admin:ann", null);
        await host.Proposals.ApproveAsync(p.Id, "admin:bo");
        var file = PolicyProposals.Deployable(host.Proposals.Get(p.Id)!.CandidateJson);
        Assert.Equal(p.CandidateHash, PolicyConfig.Parse(file).Hash());
        TempDb.ClearPools(db.Path);

        var next = Hosts.BuildSqlite(new FakeInboxModule(), file, db.Path);
        await next.RunMaintenanceOnceAsync();
        Assert.Equal(p.CandidateHash, next.Policy.PolicyHash);
        Assert.Equal(ProposalStatus.Applied, next.Proposals.Get(p.Id)!.Status);
        Assert.Equal("admin:bo", next.Proposals.Get(p.Id)!.DecidedBy);
        Assert.True(next.Proposals.IsStale(next.Proposals.Get(other.Id)!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.Proposals.ApproveAsync(other.Id, "admin:bo"));
        Assert.Contains(((IAuditReader)next.Audit).Read(), e => e.Event == "policy_proposal_applied" && e.Detail!.StartsWith(p.Id, StringComparison.Ordinal));
        TempDb.ClearPools(db.Path);
    }

    [Fact]
    public async Task Over_http_admins_propose_and_approve_and_a_reviewer_cannot()
    {
        const string ann = "ann-token-0123456789", bo = "bo-token-0123456789", rev = "rev-token-0123456789";
        var host = Hosts.Build(Policy).Host;
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [ann] = "ann", [bo] = "bo", [rev] = "rev" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [ann] = new HashSet<string> { Roles.Review, Roles.Admin },
                [bo] = new HashSet<string> { Roles.Review, Roles.Admin },
                [rev] = new HashSet<string> { Roles.Review },
            },
        });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        async Task<(HttpStatusCode Code, JsonNode? Body)> Send(HttpMethod method, string path, string token, object? body = null)
        {
            using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
        }

        var current = (await Send(HttpMethod.Get, "/api/admin/policy/current", ann)).Body!.AsObject();
        current["Agents"]!["clerk"]!["Tools"] = new JsonArray("inbox.*", "clock.*");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/admin/proposals", rev, new { policy = current })).Code);

        var (code, p) = await Send(HttpMethod.Post, "/api/admin/proposals", ann, new { policy = current, note = "clock for clerk" });
        Assert.Equal(HttpStatusCode.OK, code);
        var id = p!["id"]!.GetValue<string>();
        Assert.Contains(p["changes"]!.AsArray(), c => c!["section"]!.GetValue<string>() == "Agents" && c["key"]!.GetValue<string>() == "clerk");

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/admin/proposals/{id}/approve", ann, new { })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/admin/proposals/{id}/reject", bo, new { })).Code);
        var (ok, approved) = await Send(HttpMethod.Post, $"/api/admin/proposals/{id}/approve", bo, new { });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("Approved", approved!["status"]!.GetValue<string>());

        using var dl = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/proposals/{id}/policy.json");
        dl.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bo);
        var file = await http.SendAsync(dl);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(approved["candidateHash"]!.GetValue<string>(), PolicyConfig.Parse(await file.Content.ReadAsStringAsync()).Hash());

        var bad = await Send(HttpMethod.Post, "/api/admin/proposals", ann, new { policy = new { Agents = new { clerk = new { Tools = new[] { "no.such" } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Code);
    }
}
