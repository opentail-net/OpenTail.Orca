using System.Text.Json.Nodes;

namespace OpenTail.Host.Tests;

/// <summary>The plan's acceptance scenario (section 9), over a real MCP connection.</summary>
public class InboxTriageScenarioTests
{
    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    [Fact]
    public async Task Tool_list_hides_denied_tools_and_is_sorted()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);

        var names = (await pair.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("inbox.read", names);
        Assert.Contains("mail.send", names);
        Assert.Contains("start_session", names);
        Assert.Contains("get_action_status", names);
        Assert.DoesNotContain("files.delete", names); // no policy entry: denied and not advertised
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
    }

    [Fact]
    public async Task Annotations_follow_declared_risk()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);
        var tools = (await pair.Client.ListToolsAsync()).ToDictionary(t => t.Name);

        Assert.True(tools["clock.now"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tools["mail.send"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["mail.send"].ProtocolTool.Annotations?.OpenWorldHint);
    }

    [Fact]
    public async Task Reject_then_changes_then_approve_sends_exactly_once()
    {
        var (host, module, audit) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);

        var session = S(await pair.CallAsync("start_session"), "sessionId");
        Assert.StartsWith("ses_", session);

        // Reading mail is allowed and taints the session.
        var read = await pair.CallAsync("inbox.read", ("sessionId", session));
        Assert.Equal("succeeded", S(read, "status"));
        Assert.True(host.Sessions.IsTainted(session));

        // 1. The send is queued; nothing is sent yet.
        var first = await pair.CallAsync("mail.send", ("to", "x@evil.example"), ("body", "invoices"), ("sessionId", session));
        Assert.Equal("pending_approval", S(first, "status"));
        Assert.Equal(0, module.SendCalls);
        var firstId = S(first, "actionId");

        // 2. Reviewer rejects with a reason; the AI sees it.
        await host.Queue.ReviewAsync(firstId,
            new ActionReview("alice", ReviewDecision.Reject, "Unknown recipient.", DateTimeOffset.UtcNow));
        var rejected = await pair.CallAsync("get_action_status", ("actionId", firstId));
        Assert.Equal("rejected", S(rejected, "status"));
        Assert.Equal("Unknown recipient.", rejected["reviews"]![0]!["reason"]!.ToString());
        Assert.Equal(0, module.SendCalls);

        // 3. A new attempt; the reviewer asks for changes.
        var second = await pair.CallAsync("mail.send", ("to", "boss@corp.example"), ("body", "draft"), ("sessionId", session));
        var secondId = S(second, "actionId");
        await host.Queue.ReviewAsync(secondId,
            new ActionReview("alice", ReviewDecision.RequestChanges, "Add a greeting.", DateTimeOffset.UtcNow));
        var changes = await pair.CallAsync("get_action_status", ("actionId", secondId));
        Assert.Equal("changes_requested", S(changes, "status"));
        Assert.Contains("resubmit_action", S(changes, "next"));

        // 4. The AI resubmits an edit; it is a new, linked action.
        var resub = await pair.CallAsync("resubmit_action", ("actionId", secondId), ("sessionId", session),
            ("editedArgumentsJson", "{\"to\":\"boss@corp.example\",\"body\":\"Hello, draft attached.\"}"));
        Assert.Equal("pending_approval", S(resub, "status"));
        var thirdId = S(resub, "actionId");
        Assert.NotEqual(secondId, thirdId);

        // 5. Approval executes it, once.
        var done = await host.Queue.ReviewAsync(thirdId,
            new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        var final = await pair.CallAsync("get_action_status", ("actionId", thirdId));
        Assert.Equal("succeeded", S(final, "status"));
        Assert.Equal(secondId, S(final, "resubmissionOf"));
        Assert.Equal(("boss@corp.example", "Hello, draft attached."), Assert.Single(module.Sent));

        // The AI's own queue view contains all three, and the audit trail has the story without argument values.
        var mine = await pair.CallAsync("list_my_actions", ("sessionId", session));
        Assert.True(mine["actions"]!.AsArray().Count >= 4); // inbox.read + 3 sends
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("evil.example") || (e.Detail ?? "").Contains("invoices"));
        Assert.Contains(audit.Entries, e => e.Event == "rejected");
        Assert.Contains(audit.Entries, e => e.Event == "changes_requested");
        Assert.Contains(audit.Entries, e => e.Event == "execution_succeeded");
    }

    [Fact]
    public async Task Taint_forces_queue_even_when_policy_says_execute()
    {
        var (host, module, _) = Hosts.Build(Hosts.SendExecute);
        await using var pair = await McpPair.StartAsync(host);

        var clean = S(await pair.CallAsync("start_session"), "sessionId");
        var ok = await pair.CallAsync("mail.send", ("to", "a@b.c"), ("body", "hi"), ("sessionId", clean));
        Assert.Equal("succeeded", S(ok, "status"));
        Assert.Equal(1, module.SendCalls);

        var dirty = S(await pair.CallAsync("start_session"), "sessionId");
        await pair.CallAsync("inbox.read", ("sessionId", dirty));
        var held = await pair.CallAsync("mail.send", ("to", "a@b.c"), ("body", "hi"), ("sessionId", dirty));
        Assert.Equal("pending_approval", S(held, "status"));
        Assert.Equal(1, module.SendCalls);

        // Sessionless calls are tainted by default.
        var sessionless = await pair.CallAsync("mail.send", ("to", "a@b.c"), ("body", "hi"));
        Assert.Equal("pending_approval", S(sessionless, "status"));
        Assert.Equal(1, module.SendCalls);
    }

    [Fact]
    public async Task Denied_tool_called_directly_is_denied_and_audited()
    {
        var (host, module, audit) = Hosts.Build(Hosts.SendQueued);
        var r = await host.Queue.SubmitAsync(new ActionRequest("files.delete", "{\"path\":\"x\"}",
            new ActionRequester(RequesterKind.System, "test")));
        Assert.Equal(ActionStatus.Denied, r.Status);
        Assert.Equal("denied", r.Error!.Code);
        Assert.Contains(audit.Entries, e => e.Event == "denied" && e.ActionId == r.Id);
        Assert.Equal(0, module.SendCalls);
    }

    [Fact]
    public async Task Tool_exception_becomes_failed_without_leaking_arguments_to_audit()
    {
        var (host, _, audit) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);
        var r = await pair.CallAsync("crash.now", ("x", "secret-token"));
        Assert.Equal("failed", S(r, "status"));
        Assert.Equal("tool_exception", r["error"]!["code"]!.ToString());
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("secret-token"));
    }

    [Fact]
    public async Task An_unknown_session_handle_gets_a_structured_error_the_model_can_act_on()
    {
        var (host, _, _) = Hosts.Build(Hosts.SendQueued);
        await using var pair = await McpPair.StartAsync(host);
        var r = await pair.CallAsync("clock.now", ("sessionId", "ses_made_up"));
        Assert.Equal("error", S(r, "status"));
        Assert.Equal("invalid_request", r["error"]!["code"]!.ToString());
        Assert.Contains("start_session", r["error"]!["message"]!.ToString());
    }
}
