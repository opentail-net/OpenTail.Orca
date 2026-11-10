using System.Text.Json.Nodes;
using OpenTail.Modules.Skills;

namespace OpenTail.Host.Tests;

/// <summary>Skills served from the Host's own store, published and rolled back through approval, audited when served.</summary>
public sealed class SkillsModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");

    private const string Policy = """
        { "DefaultReadMode": "Execute", "Tools": {
            "skills.find": { "Mode": "Execute" }, "skills.get": { "Mode": "Execute" },
            "skills.publish": { "Mode": "Queue", "Reviewer": "Human", "ReviewerGroup": "skill-approvers", "SelfApproval": "Forbid",
                                "AllowEdits": false, "ExpireAfter": "1.00:00:00" } } }
        """;

    /// <summary>A reviewer in the approver group for administration tools.</summary>
    private static ActionReview Approve(string who = "alice") =>
        new(who, ReviewDecision.Approve, null, DateTimeOffset.UtcNow) { Subject = "admin:" + who, Groups = ["skill-approvers"] };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    private static string Meta(string version, string? policyHash = null) => $$"""
        { "format": 1, "id": "invoice-check", "version": "{{version}}",
          "summary": "Check whether an unpaid invoice is genuine before anyone pays or chases it.",
          "appliesTo": [ "an invoice or payment demand arrives by email" ],
          "tools": [ "mail.read", "servicenow.create_record" ],
          "policies": [ { "ref": "policies/payment-verification.md", "hash": {{(policyHash is null ? "null" : $"\"{policyHash}\"")}} } ],
          "owner": "finance-ops" }
        """;

    private (OpenTailHost Host, InMemoryAuditSink Audit) Build(string? policyFolder = null)
    {
        var audit = new InMemoryAuditSink();
        var module = new SkillsModule(new SkillsModuleOptions
        {
            Store = Path.Combine(_root, "store"),
            PolicyFolder = policyFolder,
            Manifest = new SkillsModuleFactory().Describe("skills", default),
        });
        return (new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).UseAudit(audit).Build(), audit);
    }

    private static async Task<JsonObject> Publish(McpPair pair, OpenTailHost host, string version, string? meta, string? instructions, string reason = "Better checks.",
        string from = "none")
    {
        var args = new List<(string, object?)> { ("skillId", "invoice-check"), ("version", version), ("reason", reason), ("label", "production"), ("expectedFrom", from) };
        if (meta is not null) args.Add(("skillJson", meta));
        if (instructions is not null) args.Add(("instructions", instructions));
        var queued = await pair.CallAsync("skills.publish", [.. args]);
        Assert.Equal("pending_approval", S(queued, "status"));
        await host.Queue.ReviewAsync(S(queued, "actionId"), Approve());
        return queued;
    }

    [Fact]
    public async Task Publishing_waits_for_approval_then_the_skill_is_served_and_audited()
    {
        var (host, audit) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var before = await pair.CallAsync("skills.find", ("situation", "invoice by email"));
        Assert.Empty(before["result"]!["skills"]!.AsArray());

        var queued = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First version."), ("expectedFrom", "none"),
            ("skillJson", Meta("1")), ("instructions", "Find the vendor. Never pay from an email."));
        Assert.Equal("pending_approval", S(queued, "status"));
        var pending = await host.Store.GetAsync(S(queued, "actionId"));
        Assert.Contains("first publication", pending!.PreviewJson);
        Assert.Null(new SkillStore(Path.Combine(_root, "store")).Get("invoice-check", "1")); // nothing is written before approval

        await host.Queue.ReviewAsync(S(queued, "actionId"), Approve());

        var found = await pair.CallAsync("skills.find", ("situation", "an invoice arrived by email"));
        Assert.Equal("invoice-check", found["result"]!["skills"]![0]!["id"]!.ToString());
        var session = S(await pair.CallAsync("start_session"), "sessionId");
        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"), ("sessionId", session));
        Assert.Equal("succeeded", S(got, "status"));
        Assert.Equal("invoice-check@1", got["result"]!["served"]!.ToString());
        Assert.Contains("Never pay from an email.", got["result"]!["instructions"]!.ToString());
        Assert.False(host.Sessions.IsTainted(session)); // the business's own procedure, not outside content

        var served = Assert.Single(audit.Entries, e => e.Event == "skill_served");
        Assert.Equal("invoice-check@1", served.Detail);
        Assert.Equal("skills.get", served.ToolName);
    }

    [Fact]
    public async Task Rolling_back_is_publishing_an_earlier_version_through_the_same_approval()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Version one.");
        await Publish(pair, host, "2", Meta("2"), "Version two.", from: "1");
        Assert.Contains("Version two.", (await pair.CallAsync("skills.get", ("id", "invoice-check")))["result"]!["instructions"]!.ToString());

        var back = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "v2 raised rejections."), ("expectedFrom", "2"));
        var record = await host.Store.GetAsync(S(back, "actionId"));
        var preview = JsonNode.Parse(record!.PreviewJson!)!;
        Assert.Contains("(rollback)", preview["Summary"]!.ToString());
        var diff = JsonNode.Parse(preview["DiffJson"]!.ToString())!;
        Assert.Equal("rollback", diff["change"]!.ToString());
        Assert.Equal(["- Version two.", "+ Version one."], diff["instructionsDiff"]!.AsArray().Select(l => l!.ToString()));
        await host.Queue.ReviewAsync(S(back, "actionId"), Approve());

        Assert.Contains("Version one.", (await pair.CallAsync("skills.get", ("id", "invoice-check")))["result"]!["instructions"]!.ToString());
        Assert.Equal("1", new SkillStore(Path.Combine(_root, "store")).LabelledVersion("invoice-check", "production"));
    }

    [Fact]
    public async Task A_version_cannot_be_republished_with_different_content_and_bad_requests_are_refused_at_once()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Version one.");

        var changed = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "Edit in place."), ("expectedFrom", "1"),
            ("skillJson", Meta("1")), ("instructions", "Quietly different."));
        Assert.Equal("denied", S(changed, "status"));
        Assert.Equal("invalid_arguments", changed["error"]!["code"]!.ToString());

        var noReason = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", " "), ("expectedFrom", "1"));
        Assert.Equal("invalid_arguments", noReason["error"]!["code"]!.ToString());
        var unknown = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "7"), ("reason", "x"), ("expectedFrom", "1"));
        Assert.Equal("invalid_arguments", unknown["error"]!["code"]!.ToString());
        var mismatch = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "3"), ("reason", "x"), ("expectedFrom", "1"),
            ("skillJson", Meta("2")), ("instructions", "x"));
        Assert.Equal("invalid_arguments", mismatch["error"]!["code"]!.ToString());
    }

    [Fact]
    public async Task A_policy_changed_since_the_skill_was_tested_is_reported_when_the_skill_is_served()
    {
        var policies = Path.Combine(_root, "policies");
        Directory.CreateDirectory(policies);
        var file = Path.Combine(policies, "payment-verification.md");
        File.WriteAllText(file, "Invoices above 10,000.00 need a second approver.\n");
        var tested = SkillText.Hash(File.ReadAllText(file));
        var (host, _) = Build(policies);
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1", tested), "Read the payment verification policy.");

        File.WriteAllText(file, "Invoices  above 10,000.00 need a second approver.\r\n\r\n"); // cosmetic
        Assert.Empty((await pair.CallAsync("skills.get", ("id", "invoice-check")))["result"]!["policyWarnings"]!.AsArray());

        File.WriteAllText(file, "Invoices above 5,000.00 need a second approver.\n");
        var warning = (await pair.CallAsync("skills.get", ("id", "invoice-check")))["result"]!["policyWarnings"]!.AsArray();
        Assert.Contains("older version of policies/payment-verification.md", Assert.Single(warning)!.ToString());
    }

    [Fact]
    public async Task The_server_instructions_point_at_skills_only_when_the_module_is_advertised()
    {
        var (host, _) = Build();
        Assert.Contains("call skills.find", host.CreateServerOptions().ServerInstructions);
        var (plain, _, _) = Hosts.Build(Hosts.SendQueued);
        Assert.DoesNotContain("skills.find", plain.CreateServerOptions().ServerInstructions);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_module_loads_from_configuration_with_its_suggested_policy()
    {
        Directory.CreateDirectory(_root);
        var modules = ModulesConfig.Parse("""{ "Modules": [ { "Type": "skills", "Id": "skills", "Config": { "Store": "store" } } ] }""", _root);
        var loaded = await ModuleLoader.LoadAsync(modules, ModuleLoadMode.Run);
        var m = Assert.Single(loaded.Modules);
        var host = new OpenTailHostBuilder().AddModules(loaded).UsePolicy(PolicyConfig.Parse(Policy)).Build();
        Assert.Equal(["skills.find", "skills.get", "skills.publish"], host.Catalog.All.Select(t => t.Descriptor.Name).Order(StringComparer.Ordinal));
        var suggested = PolicyConfig.ParseFragment(m.Module.Manifest.SuggestedPolicyJson!, "skills", "test");
        Assert.Equal(ToolMode.Queue, suggested["skills.publish"].Mode);
        Assert.Equal(ReviewerKind.Human, suggested["skills.publish"].Reviewer);
        Assert.Equal(SelfApproval.Forbid, suggested["skills.publish"].SelfApproval);
        Assert.True(Directory.Exists(Path.Combine(_root, "store")));
    }

    [Fact]
    public void Publish_is_an_administration_tool_and_a_loose_policy_for_it_stops_startup()
    {
        var module = new SkillsModule(new SkillsModuleOptions { Store = Path.Combine(_root, "store") });
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse("""
            { "Tools": { "skills.publish": { "Mode": "Queue", "Reviewer": "Human", "ExpireAfter": "1.00:00:00" } } }
            """)).Build());
        Assert.Contains("ReviewerGroup", ex.Message);
        Assert.Contains("SelfApproval Forbid", ex.Message);
        Assert.Contains("AllowEdits false", ex.Message);
    }

    [Fact]
    public async Task A_reviewer_cannot_edit_a_publish()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        var queued = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."), ("expectedFrom", "none"),
            ("skillJson", Meta("1")), ("instructions", "Never pay from an email."));
        var edit = Approve() with { EditedArgumentsJson = """{"skillId":"invoice-check","version":"1","reason":"x","instructions":"Pay anyone."}""" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(S(queued, "actionId"), edit));
        Assert.Contains("cannot be edited", ex.Message);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(S(queued, "actionId")))!.Status);
    }

    [Fact]
    public async Task The_suggested_policy_needs_two_approvals_for_production_and_one_for_staging()
    {
        Directory.CreateDirectory(_root);
        var modules = ModulesConfig.Parse("""{ "Modules": [ { "Type": "skills", "Id": "skills", "Config": { "Store": "store" } } ] }""", _root);
        var loaded = await ModuleLoader.LoadAsync(modules, ModuleLoadMode.Run);
        var suggested = PolicyConfig.ParseFragment(Assert.Single(loaded.Modules).Module.Manifest.SuggestedPolicyJson!, "skills", "test");
        var host = new OpenTailHostBuilder().AddModules(loaded).UsePolicy(new PolicyConfig { Tools = suggested }).Build();
        await using var pair = await McpPair.StartAsync(host);

        var staging = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."),
            ("label", "staging"), ("expectedFrom", "none"), ("skillJson", Meta("1")), ("instructions", "Never pay from an email."));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(S(staging, "actionId"), Approve("alice"))).Status);

        var production = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "Promote."), ("expectedFrom", "none"));
        Assert.Equal(ActionStatus.Pending, (await host.Queue.ReviewAsync(S(production, "actionId"), Approve("alice"))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Queue.ReviewAsync(S(production, "actionId"), Approve("alice"))); // not twice
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(S(production, "actionId"), Approve("bob"))).Status);
    }

    [Fact]
    public async Task A_publish_without_expectedFrom_or_with_a_wrong_one_is_refused_at_once()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        var missing = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."),
            ("skillJson", Meta("1")), ("instructions", "x"));
        Assert.Contains("expectedFrom is required", missing.ToJsonString());
        var wrong = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."), ("expectedFrom", "4"),
            ("skillJson", Meta("1")), ("instructions", "x"));
        Assert.Equal("invalid_arguments", wrong["error"]!["code"]!.ToString());
        Assert.Contains("is at none, not 4", wrong.ToJsonString());
    }

    [Fact]
    public async Task Approving_an_old_proposal_after_the_label_moved_publishes_nothing()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Version one.");

        // Two proposals made while production is at 1; the later one is approved first.
        var a = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "2"), ("reason", "A."), ("expectedFrom", "1"),
            ("skillJson", Meta("2")), ("instructions", "Version two."));
        var b = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "3"), ("reason", "B."), ("expectedFrom", "1"),
            ("skillJson", Meta("3")), ("instructions", "Version three."));
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.ReviewAsync(S(b, "actionId"), Approve())).Status);

        var late = await host.Queue.ReviewAsync(S(a, "actionId"), Approve());
        Assert.Equal(ActionStatus.Failed, late.Status);
        Assert.Contains("moved since this was reviewed", late.Error!.Message);
        Assert.Equal("3", new SkillStore(Path.Combine(_root, "store")).LabelledVersion("invoice-check", "production"));
    }

    [Fact]
    public async Task A_report_bound_to_other_content_is_refused_and_a_matching_one_is_marked_bound()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        var proposed = SkillStore.Parse(Meta("1"), "Never pay from an email.", "invoice-check", "1");
        var other = SkillStore.Parse(Meta("1"), "Pay whoever asks.", "invoice-check", "1");

        var forged = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."), ("expectedFrom", "none"),
            ("skillJson", Meta("1")), ("instructions", "Never pay from an email."), ("report", SkillText.BindingLine(other) + "\n\nAll 31 scenarios pass."));
        Assert.Equal("invalid_arguments", forged["error"]!["code"]!.ToString());
        Assert.Contains("describes other content", forged.ToJsonString());

        var bound = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."), ("expectedFrom", "none"),
            ("skillJson", Meta("1")), ("instructions", "Never pay from an email."), ("report", SkillText.BindingLine(proposed) + "\n\nAll 31 scenarios pass."));
        var diff = JsonNode.Parse(JsonNode.Parse((await host.Store.GetAsync(S(bound, "actionId")))!.PreviewJson!)!["DiffJson"]!.ToString())!;
        Assert.StartsWith("Bound:", diff["reportCheck"]!.ToString());

        var loose = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "First."), ("expectedFrom", "none"),
            ("skillJson", Meta("1")), ("instructions", "Never pay from an email."), ("report", "Trust me, it is great."));
        diff = JsonNode.Parse(JsonNode.Parse((await host.Store.GetAsync(S(loose, "actionId")))!.PreviewJson!)!["DiffJson"]!.ToString())!;
        Assert.StartsWith("Not bound:", diff["reportCheck"]!.ToString());
    }

    [Fact]
    public async Task A_metadata_change_to_a_published_version_is_refused_when_requested_not_at_approval()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Version one.");

        // The same text with a different skill.json: the store refuses it at approval, so the request itself must be refused now.
        var owner = Meta("1").Replace("\"owner\": \"finance-ops\"", "\"owner\": \"someone-else\"", StringComparison.Ordinal);
        var refused = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "Change the owner."),
            ("expectedFrom", "1"), ("skillJson", owner), ("instructions", "Version one."));
        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("already published with different content", refused.ToJsonString());

        // Identical content is still an ordinary proposal.
        var same = await pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", "1"), ("reason", "Again."),
            ("expectedFrom", "1"), ("skillJson", Meta("1")), ("instructions", "Version one."));
        Assert.Equal("pending_approval", S(same, "status"));
    }

    [Fact]
    public async Task Find_names_a_label_whose_version_files_are_missing_instead_of_hiding_it()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Version one.");
        Directory.Delete(Path.Combine(_root, "store", "invoice-check", "1"), recursive: true); // the store is damaged behind the Host's back

        var found = await pair.CallAsync("skills.find", ("situation", "invoice by email"));
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        Assert.Contains("invoice-check@1", Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString());
    }

    [Fact]
    public async Task A_failure_after_a_call_is_committed_as_succeeded_does_not_turn_the_call_into_a_failure()
    {
        // skills.get writes its audit line after the Succeeded commit. When that write fails, the caller still gets the skill and the
        // action stays Succeeded: reporting a failure would invite a retry of a call that already ran.
        var audit = new FailingAuditSink(failOn: "skill_served");
        var module = new SkillsModule(new SkillsModuleOptions
        {
            Store = Path.Combine(_root, "store"),
            Manifest = new SkillsModuleFactory().Describe("skills", default),
        });
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(Policy)).UseAudit(audit).Build();
        await using var pair = await McpPair.StartAsync(host);
        await Publish(pair, host, "1", Meta("1"), "Never pay from an email.");

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));

        Assert.Equal("succeeded", S(got, "status"));
        Assert.Contains("Never pay from an email.", got["result"]!["instructions"]!.ToString());
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(S(got, "actionId")))!.Status);
        Assert.Contains(audit.Inner.Entries, e => e.Event == "post_success_failed" && e.Detail == "result audit: IOException");
    }

    /// <summary>An audit trail that refuses one event, as a failing disk would.</summary>
    private sealed class FailingAuditSink(string failOn) : IAuditSink
    {
        public InMemoryAuditSink Inner { get; } = new();

        public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
            entry.Event == failOn ? throw new IOException("audit trail unavailable") : Inner.AppendAsync(entry, ct);
    }
}
