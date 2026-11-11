using System.Diagnostics;
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
        Assert.Contains(audit.Inner.Entries, e => e.Event == "bookkeeping_failed" && e.Detail == "result audit: IOException");
    }

    [Fact]
    public void A_skill_file_without_format_1_is_refused_at_publish()
    {
        var store = new SkillStore(Path.Combine(_root, "store"));

        Assert.Throws<ArgumentException>(() => store.Publish("invoice-check", "1", "production",
            Meta("1").Replace("\"format\": 1", "\"format\": 2", StringComparison.Ordinal), "Never pay from an email.", "none"));
        Assert.Throws<ArgumentException>(() => store.Publish("invoice-check", "1", "production",
            Meta("1").Replace("\"format\": 1, ", "", StringComparison.Ordinal), "Never pay from an email.", "none"));
        Assert.Null(store.LabelledVersion("invoice-check", "production"));
    }

    [Fact]
    public async Task A_stored_version_in_another_format_is_named_by_find_and_not_served()
    {
        // Written by hand, as a future Host or a damaged file would leave it: find names it, get refuses it, and nothing else is hidden.
        var dir = Path.Combine(_root, "store", "invoice-check", "1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.json"), Meta("1").Replace("\"format\": 1", "\"format\": 2", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "Never pay from an email.");
        File.WriteAllText(Path.Combine(_root, "store", "labels.json"), """{ "format": 1, "skills": { "invoice-check": { "production": "1" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find", ("situation", "invoice by email"));

        Assert.Empty(found["result"]!["skills"]!.AsArray());
        Assert.Contains("invoice-check@1", Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString());
        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));
        Assert.Equal("failed", S(got, "status"));
        Assert.Contains("must say \"format\": 1", got["error"]!["message"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_publish_is_refused_while_another_process_holds_the_store_and_writes_nothing()
    {
        // Another Host process holds the store's lock file; this publish must not check expectedFrom and then write over whatever it does.
        var root = Path.Combine(_root, "store");
        Directory.CreateDirectory(root);
        var store = new SkillStore(root);
        using (new FileStream(Path.Combine(root, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", "none"));
            Assert.Contains("Nothing was published", ex.Message, StringComparison.Ordinal);
            Assert.Null(store.LabelledVersion("invoice-check", "production"));
        }

        // Released: the same publish goes through.
        store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", "none");
        Assert.Equal("1", store.LabelledVersion("invoice-check", "production"));
    }

    [Theory]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": 42, "appliesTo": ["x"] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": [{ "when": "x" }] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": 1, "summary": "s", "appliesTo": ["x"] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": "x" }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "tools": "mail.read" }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "tools": [3] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "policies": { "ref": "p.md" } }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "policies": [{ "ref": 5 }] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "policies": [{ "ref": "p.md", "hash": 5 }] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": ["x"], "owner": 7 }""")]
    public void A_skill_file_with_a_field_of_the_wrong_type_is_refused_at_publish(string metadataJson)
    {
        var store = new SkillStore(Path.Combine(_root, "store"));

        Assert.Throws<ArgumentException>(() =>
            store.Publish("invoice-check", "1", "production", metadataJson, "Never pay from an email.", "none"));
        Assert.Null(store.LabelledVersion("invoice-check", "production"));
    }

    private static string MetaFor(string id, string version) => $$"""
        { "format": 1, "id": "{{id}}", "version": "{{version}}",
          "summary": "Reply to a routine customer enquiry, following the customer-service policies.",
          "appliesTo": [ "a customer asks about an order's status" ],
          "tools": [ "mail.send" ], "policies": [], "owner": "customer-service" }
        """;

    /// <summary>Publishes a version straight into the store, without approval, for a test that needs a second skill in the store.</summary>
    private void PublishDirect(string id, string version) =>
        new SkillStore(Path.Combine(_root, "store")).Publish(id, version, "production", MetaFor(id, version), "Reply briefly and politely.", "none");

    private void WriteLabels(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "store"));
        File.WriteAllText(Path.Combine(_root, "store", "labels.json"), json);
    }

    [Fact]
    public async Task A_malformed_labels_file_is_named_by_find_and_nothing_is_served_from_it()
    {
        PublishDirect("reply-draft", "1");
        WriteLabels("this is not json");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find");
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        Assert.Contains("labels.json", Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString());

        // skills.get refuses by name rather than guessing.
        var ex = Assert.Throws<ArgumentException>(() => new SkillStore(Path.Combine(_root, "store")).LabelledVersion("reply-draft", "production"));
        Assert.Contains("labels.json", ex.Message);
    }

    [Fact]
    public async Task A_null_label_names_that_skill_and_serves_the_others()
    {
        PublishDirect("reply-draft", "1");
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": { "production": null }, "reply-draft": { "production": "1" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find");
        var skills = found["result"]!["skills"]!.AsArray();
        Assert.Single(skills);
        Assert.Equal("reply-draft", skills[0]!["id"]!.ToString());
        var warning = Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString();
        Assert.Contains("invoice-check", warning);
        Assert.Contains("production", warning);
    }

    [Fact]
    public async Task A_skill_entry_that_is_not_an_object_is_named()
    {
        PublishDirect("reply-draft", "1");
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": 5, "reply-draft": { "production": "1" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find");
        var skills = found["result"]!["skills"]!.AsArray();
        Assert.Single(skills);
        Assert.Equal("reply-draft", skills[0]!["id"]!.ToString());
        var warning = Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString();
        Assert.Contains("invoice-check", warning);
        Assert.Contains("not an object", warning);
    }

    [Fact]
    public async Task A_numeric_label_value_is_refused_and_named()
    {
        PublishDirect("reply-draft", "1");
        WriteLabels("""{ "format": 1, "skills": { "reply-draft": { "production": 1 } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find");
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        var warning = Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString();
        Assert.Contains("reply-draft", warning);
        Assert.Contains("not a version text", warning);
    }

    [Fact]
    public async Task A_label_that_is_not_a_version_number_is_named_as_such_by_find_and_get()
    {
        PublishDirect("reply-draft", "1");
        WriteLabels("""{ "format": 1, "skills": { "reply-draft": { "production": "latest" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find");
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        Assert.Contains("not a version number", Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString());

        var ex = Assert.Throws<ArgumentException>(() => new SkillStore(Path.Combine(_root, "store")).LabelledVersion("reply-draft", "production"));
        Assert.Contains("not a version number", ex.Message);
    }

    [Fact]
    public async Task A_version_whose_files_cannot_be_read_does_not_hide_the_others()
    {
        PublishDirect("invoice-check", "1");
        PublishDirect("reply-draft", "1");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        // Another process holds one version's skill.json open exclusively: its files cannot be read, and the other skill must still be listed.
        using var locked = new FileStream(Path.Combine(_root, "store", "invoice-check", "1", "skill.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var found = await pair.CallAsync("skills.find");
        var skills = found["result"]!["skills"]!.AsArray();
        Assert.Single(skills);
        Assert.Equal("reply-draft", skills[0]!["id"]!.ToString());
        Assert.Contains("invoice-check@1", Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""["an", "array"]""")]
    [InlineData("""{ "format": 1, "skills": "a string" }""")]
    [InlineData("""{ "format": 1, "skills": { "invoice-check": 5 } }""")]
    [InlineData("""{ "format": 1, "skills": { "invoice-check": { "production": null } } }""")]
    [InlineData("""{ "format": 1, "skills": { "invoice-check": { "production": 1 } } }""")]
    [InlineData("""{ "format": 1, "skills": { "invoice-check": { "production": "1", "staging": null } } }""")]
    public void A_publish_is_refused_before_any_write_when_labels_is_malformed(string labelsJson)
    {
        // expectedFrom is null, so nothing else would read the labels before the version folder is written: the damage must still be found first.
        var storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "labels.json"), labelsJson);
        var store = new SkillStore(storeDir);

        Assert.Throws<ArgumentException>(() =>
            store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", expectedFrom: null));

        Assert.False(Directory.Exists(Path.Combine(storeDir, "invoice-check", "1")), "no version folder may be written");
        Assert.Equal(labelsJson, File.ReadAllText(Path.Combine(storeDir, "labels.json")));
    }

    [Fact]
    public void A_publish_is_refused_before_any_write_when_labels_is_malformed_and_expectedFrom_is_given()
    {
        var storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "labels.json"), "not json at all");
        var store = new SkillStore(storeDir);

        var ex = Assert.Throws<ArgumentException>(() =>
            store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", expectedFrom: "none"));

        Assert.Contains("labels.json", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(storeDir, "invoice-check", "1")));
    }

    [Fact]
    public void A_publish_never_replaces_a_damaged_labels_entry_and_other_labels_survive()
    {
        var storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "labels.json"), """
            { "format": 1, "skills": { "email-triage": null, "reply-draft": { "production": "2", "staging": "3" } } }
            """);
        var store = new SkillStore(storeDir);

        store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", expectedFrom: "none");

        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(storeDir, "labels.json")))!;
        Assert.Equal("1", written["skills"]!["invoice-check"]!["production"]!.ToString());
        Assert.Equal("2", written["skills"]!["reply-draft"]!["production"]!.ToString());
        Assert.Equal("3", written["skills"]!["reply-draft"]!["staging"]!.ToString());
        // The damaged entry of another skill is kept as it was, not replaced by an empty object.
        Assert.True(written["skills"]!.AsObject().ContainsKey("email-triage"));
        Assert.Null(written["skills"]!["email-triage"]);
    }

    [Fact]
    public void A_failed_publish_after_the_folder_is_created_leaves_no_folder_and_no_label_change()
    {
        var storeDir = Path.Combine(_root, "store");
        var store = new SkillStore(storeDir);
        store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", expectedFrom: "none");
        var labelsBefore = File.ReadAllText(Path.Combine(storeDir, "labels.json"));
        // The label write cannot complete: its temporary file name is taken by a folder, so the write fails after the version folder was moved into place.
        Directory.CreateDirectory(Path.Combine(storeDir, "labels.json.tmp"));

        var ex = Record.Exception(() =>
            store.Publish("invoice-check", "2", "production", Meta("2"), "Never pay from an email, and check the bank details by phone.", expectedFrom: "1"));

        Assert.True(ex is IOException or UnauthorizedAccessException, $"expected a file error, got {ex?.GetType().Name}");
        Assert.False(Directory.Exists(Path.Combine(storeDir, "invoice-check", "2")), "the version folder this publish created must be removed");
        Assert.Empty(Directory.GetDirectories(Path.Combine(storeDir, "invoice-check"), "*.tmp-*"));
        Assert.Equal(labelsBefore, File.ReadAllText(Path.Combine(storeDir, "labels.json")));
        Assert.Equal("1", store.LabelledVersion("invoice-check", "production"));
    }

    [Fact]
    public void A_publish_into_an_incomplete_version_folder_is_refused_and_leaves_no_temporary_folder()
    {
        var storeDir = Path.Combine(_root, "store");
        var folder = Path.Combine(storeDir, "invoice-check", "1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skill.json"), Meta("1"));
        var store = new SkillStore(storeDir);

        var ex = Assert.Throws<ArgumentException>(() =>
            store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", expectedFrom: "none"));

        Assert.Contains("invoice-check@1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a complete version", ex.Message, StringComparison.Ordinal);
        // The folder is named and left as it was: nothing is repaired or overwritten by a publish.
        Assert.True(File.Exists(Path.Combine(folder, "skill.json")));
        Assert.False(File.Exists(Path.Combine(folder, "SKILL.md")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(storeDir, "invoice-check"), "*.tmp-*"));
        Assert.False(File.Exists(Path.Combine(storeDir, "labels.json")), "no label may be written");
    }

    [Theory]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": [ "" ], "tools": [], "policies": [] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": [ "   " ], "tools": [], "policies": [] }""")]
    [InlineData("""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "s", "appliesTo": [ "an invoice arrives" ], "tools": [ "" ], "policies": [] }""")]
    public async Task A_blank_applies_to_or_tools_entry_is_named_when_read(string metadataJson)
    {
        // Written by hand, as a damaged file would be: find names it, get refuses it, and publish never creates a version with it.
        var storeDir = Path.Combine(_root, "store");
        var dir = Path.Combine(storeDir, "invoice-check", "1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.json"), metadataJson);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "Never pay from an email.");
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": { "production": "1" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find", ("situation", "invoice by email"));
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        var warning = Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString();
        Assert.Contains("invoice-check@1", warning, StringComparison.Ordinal);
        Assert.Contains("blank entry", warning, StringComparison.Ordinal);

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));
        Assert.Equal("failed", S(got, "status"));
        Assert.Contains("blank entry", got["error"]!["message"]!.ToString(), StringComparison.Ordinal);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SkillStore(storeDir).Publish("invoice-check", "2", "production", metadataJson.Replace("\"1\"", "\"2\"", StringComparison.Ordinal), "Never pay from an email.", expectedFrom: "1"));
        Assert.Contains("blank entry", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(storeDir, "invoice-check", "2")));
    }

    public static TheoryData<string> BadPolicyHashes() =>
    [
        "sha256:" + new string('A', 64), // upper case is not the form SkillText.Hash writes
        "sha256:" + new string('a', 63),
        "sha256:" + new string('a', 65),
        "sha256:" + new string('g', 64), // not hex
        "sha1:" + new string('a', 40),   // another algorithm, not known to this Host
        "",                              // text, but not a hash
    ];

    [Theory]
    [MemberData(nameof(BadPolicyHashes))]
    public async Task A_policy_hash_that_is_not_sha256_syntax_is_named_when_read_and_refused_at_publish(string hash)
    {
        var storeDir = Path.Combine(_root, "store");
        var dir = Path.Combine(storeDir, "invoice-check", "1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.json"), Meta("1", hash));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "Never pay from an email.");
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": { "production": "1" } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var found = await pair.CallAsync("skills.find", ("situation", "invoice by email"));
        Assert.Empty(found["result"]!["skills"]!.AsArray());
        var warning = Assert.Single(found["result"]!["warnings"]!.AsArray())!.ToString();
        Assert.Contains("invoice-check@1", warning, StringComparison.Ordinal);
        Assert.Contains("'hash' must be null", warning, StringComparison.Ordinal);

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));
        Assert.Equal("failed", S(got, "status"));

        var ex = Assert.Throws<ArgumentException>(() =>
            new SkillStore(storeDir).Publish("invoice-check", "2", "production", Meta("2", hash), "Never pay from an email.", expectedFrom: "1"));
        Assert.Contains("'hash' must be null", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(storeDir, "invoice-check", "2")));
    }

    private void PublishWithPolicies(string refs)
    {
        var metadata = $$"""
            { "format": 1, "id": "invoice-check", "version": "1",
              "summary": "Check whether an unpaid invoice is genuine before anyone pays or chases it.",
              "appliesTo": [ "an invoice or payment demand arrives by email" ],
              "tools": [ "mail.read" ], "policies": [ {{refs}} ], "owner": "finance-ops" }
            """;
        new SkillStore(Path.Combine(_root, "store")).Publish("invoice-check", "1", "production", metadata, "Never pay from an email.", "none");
    }

    [Fact]
    public async Task Two_policy_references_with_one_file_name_are_named()
    {
        PublishWithPolicies("""{ "ref": "policies/payment-verification.md", "hash": null }, { "ref": "archive/payment-verification.md", "hash": null }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));

        var warning = Assert.Single(got["result"]!["policyWarnings"]!.AsArray())!.ToString();
        Assert.Contains("policies/payment-verification.md and archive/payment-verification.md", warning, StringComparison.Ordinal);
        Assert.Contains("payment-verification.md", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_identical_policy_references_are_one_policy_and_are_not_named()
    {
        PublishWithPolicies("""{ "ref": "policies/payment-verification.md", "hash": null }, { "ref": "policies/payment-verification.md", "hash": null }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));

        Assert.Empty(got["result"]!["policyWarnings"]!.AsArray());
    }

    [Fact]
    public async Task An_unlabelled_version_folder_is_never_served_and_the_store_check_reports_it()
    {
        PublishDirect("invoice-check", "1");
        // A complete version 2 that no label points at: what a publish leaves when it stops before its label moves.
        var folder = Path.Combine(_root, "store", "invoice-check", "2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skill.json"), Meta("2"));
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), "Pay whoever asks.");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var got = await pair.CallAsync("skills.get", ("id", "invoice-check"));
        Assert.Equal("invoice-check@1", got["result"]!["served"]!.ToString());
        Assert.DoesNotContain("Pay whoever asks.", got.ToJsonString(), StringComparison.Ordinal);

        var check = new SkillStore(Path.Combine(_root, "store")).Check();
        Assert.Empty(check.Problems);
        Assert.Single(check.Notes, n => n.StartsWith("invoice-check@2 is complete but no label points at it", StringComparison.Ordinal));
    }

    private static string SkillJson(string id, string version, string appliesTo = "\"an invoice or payment demand arrives by email\"", string policies = "") =>
        $$"""{ "format": 1, "id": "{{id}}", "version": "{{version}}", "summary": "Check an invoice.", "appliesTo": [ {{appliesTo}} ], "tools": [ "mail.read" ], "policies": [ {{policies}} ], "owner": "finance-ops" }""";

    [Fact]
    public void The_store_check_lists_every_problem_and_serves_nothing()
    {
        var storeDir = Path.Combine(_root, "store");
        void Version(string id, string version, string? metadata, string? instructions)
        {
            var folder = Path.Combine(storeDir, id, version);
            Directory.CreateDirectory(folder);
            if (metadata is not null) File.WriteAllText(Path.Combine(folder, "skill.json"), metadata);
            if (instructions is not null) File.WriteAllText(Path.Combine(folder, "SKILL.md"), instructions);
        }
        // Complete and labelled, but two policy references share a file name: a problem the read path names too.
        Version("invoice-check", "1", SkillJson("invoice-check", "1", policies: """{ "ref": "policies/payment-verification.md", "hash": null }, { "ref": "archive/payment-verification.md", "hash": null }"""), "Never pay from an email.");
        // Complete, labelled nowhere: a note, not a problem.
        Version("invoice-check", "2", SkillJson("invoice-check", "2"), "Pay whoever asks.");
        // A temporary folder a publish left behind.
        Version("invoice-check", "3.tmp-abc123", SkillJson("invoice-check", "3"), "Pay whoever asks.");
        // A blank applies-to entry: damaged, and labelled.
        Version("email-triage", "1", SkillJson("email-triage", "1", appliesTo: "\"\""), "Triage the inbox.");
        // Incomplete: no SKILL.md, and labelled.
        Version("reply-draft", "1", SkillJson("reply-draft", "1"), null);
        // A folder whose name is not a skill id.
        Version("Bad Name", "1", SkillJson("bad-name", "1"), "x");
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": { "production": "1" }, "email-triage": { "staging": "1" }, "reply-draft": { "production": "1" }, "ghost-skill": { "production": "4" }, "bad-entry": 5 } }""");
        File.WriteAllText(Path.Combine(storeDir, "labels.json.tmp"), "{ partial");

        var before = Snapshot(storeDir);
        var check = new SkillStore(storeDir).Check();

        // Serves nothing and writes nothing: the store is exactly as it was.
        Assert.Equal(before, Snapshot(storeDir));
        Assert.Equal(8, check.Problems.Count);
        Assert.Contains(check.Problems, p => p.StartsWith("labels.json's entry for bad-entry is not an object", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("production of ghost-skill points at 4, which is not in the store.", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("labels.json.tmp is left over", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("Bad Name: a folder in the store whose name is not a skill id.", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("invoice-check@1: Policies policies/payment-verification.md and archive/payment-verification.md have the same file name", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("invoice-check/3.tmp-abc123: a temporary folder left by a publish", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("email-triage@1: skill.json 'appliesTo' has a blank entry", StringComparison.Ordinal));
        Assert.Contains(check.Problems, p => p.StartsWith("reply-draft@1: an incomplete version folder", StringComparison.Ordinal));
        Assert.Single(check.Notes, n => n.StartsWith("invoice-check@2 is complete but no label points at it", StringComparison.Ordinal));
    }

    private static List<string> Snapshot(string dir) =>
        Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p) + "=" + (File.Exists(p) ? File.ReadAllText(p) : "<folder>"))
            .Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void The_store_check_command_exits_1_on_a_problem_and_0_on_a_clean_store_and_ignores_other_commands()
    {
        var storeDir = Path.Combine(_root, "store");
        PublishDirect("invoice-check", "1");

        var clean = new StringWriter();
        Assert.Equal(0, SkillStoreCli.TryRun(["--check-skill-store", storeDir], clean));
        Assert.Contains("The skill store has no problems.", clean.ToString(), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(storeDir, "labels.json.tmp"), "{ partial");
        var dirty = new StringWriter();
        Assert.Equal(1, SkillStoreCli.TryRun(["--check-skill-store", storeDir], dirty));
        Assert.Contains("PROBLEM: labels.json.tmp is left over", dirty.ToString(), StringComparison.Ordinal);

        Assert.Equal(64, SkillStoreCli.TryRun(["--check-skill-store"], new StringWriter()));
        Assert.Null(SkillStoreCli.TryRun(["--policy", "policy.json", "--db", "queue.db"], new StringWriter()));
    }

    // The preview is what skills.publish returns before approval: a refused preview is an invalid_arguments error, a shown one is pending_approval.
    private static Task<JsonObject> Propose(McpPair pair, string version, string expectedFrom) =>
        pair.CallAsync("skills.publish", ("skillId", "invoice-check"), ("version", version), ("reason", "Check it."), ("expectedFrom", expectedFrom),
            ("skillJson", Meta(version)), ("instructions", "Never pay from an email."));

    [Fact]
    public async Task A_preview_with_malformed_labels_is_refused_and_names_the_file()
    {
        WriteLabels("this is not json");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("labels.json", refused["error"]!["message"]!.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "store", "invoice-check", "2")));
    }

    [Fact]
    public async Task A_preview_with_a_damaged_label_entry_is_refused()
    {
        WriteLabels("""{ "format": 1, "skills": { "invoice-check": { "production": null } } }""");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        var message = refused["error"]!["message"]!.ToString();
        Assert.Contains("'production' label for invoice-check", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_with_a_label_pointing_at_a_missing_version_is_refused_and_names_it()
    {
        PublishDirect("invoice-check", "1");
        Directory.Delete(Path.Combine(_root, "store", "invoice-check", "1"), recursive: true);
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("points at version 1, whose files are missing", refused["error"]!["message"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_with_an_incomplete_target_folder_is_refused_not_shown_as_new()
    {
        PublishDirect("invoice-check", "1");
        var folder = Path.Combine(_root, "store", "invoice-check", "2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skill.json"), Meta("2"));
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("not a complete version", refused["error"]!["message"]!.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "SKILL.md")), "the preview must not complete the folder");
    }

    [Fact]
    public async Task A_preview_with_a_stale_expectedFrom_is_refused()
    {
        PublishDirect("invoice-check", "1");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "none");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("is at 1, not none", refused["error"]!["message"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_with_a_corrupt_target_version_is_refused_with_the_reason()
    {
        PublishDirect("invoice-check", "1");
        File.WriteAllText(Path.Combine(_root, "store", "invoice-check", "1", "skill.json"), "{ not json");
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.Contains("not valid JSON", refused["error"]!["message"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_with_an_unreadable_target_version_is_refused_with_the_reason()
    {
        PublishDirect("invoice-check", "1");
        // Held exclusively, as another program would hold it: the version cannot be read, so the preview refuses rather than guessing.
        using var locked = new FileStream(Path.Combine(_root, "store", "invoice-check", "1", "skill.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var refused = await Propose(pair, "2", "1");

        Assert.Equal("invalid_arguments", refused["error"]!["code"]!.ToString());
        Assert.False(string.IsNullOrWhiteSpace(refused["error"]!["message"]!.ToString()));
    }

    [Fact]
    public async Task A_preview_writes_nothing()
    {
        PublishDirect("invoice-check", "1");
        var storeDir = Path.Combine(_root, "store");
        var before = Snapshot(storeDir);
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);

        var queued = await Propose(pair, "2", "1");

        Assert.Equal("pending_approval", S(queued, "status"));
        Assert.Equal(before, Snapshot(storeDir));
    }

    [Fact]
    public async Task A_publish_is_refused_while_a_separate_process_holds_the_store()
    {
        // The lock is held by another process, not by this test: a second program, built with this test project, opens the lock file
        // exclusively and reports that it has. It needs no PowerShell, so the test cannot be skipped (store plan, design item 9).
        var holderDll = LockHolderDll();
        Assert.True(File.Exists(holderDll), $"The lock holder is not built: {holderDll}");
        var root = Path.Combine(_root, "store");
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".publish.lock");
        var holder = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        holder.ArgumentList.Add(holderDll);
        holder.ArgumentList.Add(lockPath);
        using var child = Process.Start(holder)!;
        try
        {
            Assert.Equal("held", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)));

            var store = new SkillStore(root);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Publish("invoice-check", "1", "production", Meta("1"), "Never pay from an email.", "none"));
            Assert.Contains("Nothing was published", ex.Message, StringComparison.Ordinal);
            Assert.Null(store.LabelledVersion("invoice-check", "production"));
        }
        finally
        {
            // Closing standard input ends the hold. The process is killed only if it does not end by itself.
            child.StandardInput.Close();
            if (!child.WaitForExit(10_000)) child.Kill(entireProcessTree: true);
        }
    }

    /// <summary>The lock holder program. It is built because this test project references it; found by the same walk as the isolation tests use for their worker.</summary>
    private static string LockHolderDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = dir.FullName.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenTail.Host.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "OpenTail.Host.Tests.LockHolder", "bin", config, "net10.0", "OpenTail.Host.Tests.LockHolder.dll");
    }

    /// <summary>An audit trail that refuses one event, as a failing disk would.</summary>
    private sealed class FailingAuditSink(string failOn) : IAuditSink
    {
        public InMemoryAuditSink Inner { get; } = new();

        public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
            entry.Event == failOn ? throw new IOException("audit trail unavailable") : Inner.AppendAsync(entry, ct);
    }
}
