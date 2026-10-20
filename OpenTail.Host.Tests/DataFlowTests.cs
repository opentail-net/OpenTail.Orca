using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Where data goes, step 1: labels at the source, on the session, and the flow check at outbound tools (report only, or enforced).</summary>
public class DataFlowTests
{
    /// <summary>erp.query returns finance data (the module says so); crm.get is labelled customer by the operator.</summary>
    private sealed class LabelledModule : IHostModule
    {
        public string Id => "lab";

        public void Register(IToolRegistry registry)
        {
            registry.Add(new ToolDescriptor("erp.query", "Query the ERP.", ToolRisk.Read, Id) { Labels = ["finance"] },
                AIFunctionFactory.Create(() => new { rows = new[] { new { invoice = "INV-2026-00071", amount = 1200 } } }, "erp.query"));
            registry.Add(new ToolDescriptor("crm.get", "Read a customer.", ToolRisk.Read, Id),
                AIFunctionFactory.Create(() => new { name = "Ada" }, "crm.get"));
            registry.Add(new ToolDescriptor("mail.send", "Send mail.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string to) => new { sent = true }, "mail.send"));
            registry.Add(new ToolDescriptor("mail.internal", "Send mail inside the company.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string to) => new { sent = true }, "mail.internal"));
        }
    }

    private static string Policy(string mode) => $$"""
        {
          "DefaultReadMode": "Execute", "TaintWindow": "00:30:00", "SessionlessTainted": false, "DataFlowMode": "{{mode}}",
          "Tools": {
            "crm.get": { "Mode": "Execute", "Labels": [ "customer" ] },
            "mail.send": { "Mode": "Execute" }, "mail.internal": { "Mode": "Execute" }
          },
          "DataFlow": {
            "finance":  { "Allow": [ "mail.internal" ], "Otherwise": "Queue" },
            "customer": { "Allow": [], "Otherwise": "Deny" }
          }
        }
        """;

    private static OpenTailHost Host(string mode = "Enforce", string? db = null)
    {
        var b = new OpenTailHostBuilder().AddModule(new LabelledModule()).UsePolicy(PolicyConfig.Parse(Policy(mode)));
        if (db is not null) b.UseSqlite(db);
        return b.Build();
    }

    private static ActionRequest Call(string tool, string? session, string args = "{}", string who = "key:bot") =>
        new(tool, args, new ActionRequester(RequesterKind.AiSession, who), session);

    [Fact]
    public async Task A_session_gains_module_and_operator_labels_with_their_sources()
    {
        var host = Host();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));
        await host.Queue.SubmitAsync(Call("crm.get", s));
        var held = host.Labels.Held(s, "key:bot");
        Assert.Equal(["customer", "finance"], held.Select(h => h.Label));
        Assert.Equal(["erp.query"], held.Single(h => h.Label == "finance").Sources);
    }

    [Fact]
    public async Task Finance_data_may_go_to_the_allowed_destination_and_waits_for_a_person_anywhere_else()
    {
        var host = Host();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("mail.internal", s, """{"to":"cfo@corp.example"}"""))).Status);
        var outside = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"someone@else.example"}"""));
        Assert.Equal(ActionStatus.Pending, outside.Status);
        Assert.Equal(["finance (erp.query)"], outside.SessionLabels);   // the reviewer's line
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "flow_checked" && e.ActionId == outside.Id);
    }

    [Fact]
    public async Task Secret_style_rules_deny_and_every_label_must_be_satisfied()
    {
        var host = Host();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));
        await host.Queue.SubmitAsync(Call("crm.get", s));
        // mail.internal is allowed for finance but not for customer, whose rule denies.
        Assert.Equal(ActionStatus.Denied, (await host.Queue.SubmitAsync(Call("mail.internal", s, """{"to":"a@corp.example"}"""))).Status);
    }

    [Fact]
    public async Task A_new_session_does_not_launder_labels_within_the_window()
    {
        var host = Host();
        await host.Queue.SubmitAsync(Call("erp.query", host.Sessions.Start("key:bot")));
        var fresh = host.Sessions.Start("key:bot");
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Call("mail.send", fresh, """{"to":"x@else.example"}"""))).Status);
        Assert.Empty(host.Labels.Held(null, "key:other"));
    }

    [Fact]
    public async Task Report_only_audits_what_would_happen_and_runs_the_call()
    {
        var host = Host("ReportOnly");
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));
        var r = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"x@else.example"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "flow_would_check" && e.Detail!.StartsWith("finance: mail.send"));
    }

    [Fact]
    public void Report_only_is_the_default_and_bad_rules_stop_startup()
    {
        Assert.Equal(LimitMode.ReportOnly, PolicyConfig.Parse("{}").DataFlowMode);
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new LabelledModule())
            .UsePolicy(PolicyConfig.Parse("""{ "DataFlow": { "Finance": { "Allow": [ "nope.tool" ], "Otherwise": "Execute" } } }""")).Build());
        Assert.Contains("lower-case", ex.Message);
        Assert.Contains("Otherwise must be Queue or Deny", ex.Message);
        Assert.Contains("unknown tool 'nope.tool'", ex.Message);
    }

    [Fact]
    public async Task Labels_survive_a_restart()
    {
        using var db = new TempDb();
        var host = Host(db: db.Path);
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));
        TempDb.ClearPools(db.Path);
        Assert.Equal(["finance"], Host(db: db.Path).Labels.Held(s, "key:bot").Select(h => h.Label));
    }

    [Fact]
    public async Task Explain_shows_a_tools_labels_and_the_flow_outcome_for_held_labels()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var policy = Path.Combine(dir, "policy.json");
        await File.WriteAllTextAsync(policy, Policy("Enforce"));
        var add = (OpenTailHostBuilder b) => { b.AddModule(new LabelledModule()); };

        var output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--explain", "erp.query", "{}"], "t", add, output));
        Assert.Contains("returns data labelled: finance", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await PolicyCli.TryRunAsync(["--policy", policy, "--explain", "mail.send", "{\"to\":\"x\"}", "--labels", "finance"], "t", add, output));
        Assert.Contains("data flow: finance may not go to mail.send -> Queue", output.ToString());
    }

    [Fact]
    public async Task Modules_json_labels_an_instance_and_mail_reads_are_personal()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "ledger.csv"), "id;amount\n1;10\n");
        var loaded = await ModuleLoader.LoadAsync(ModulesConfig.Parse("""
            { "Modules": [ { "Type": "csv", "Id": "erp", "Labels": [ "finance" ], "Config": { "Root": "." } } ] }
            """, dir), ModuleLoadMode.Run);
        var host = new OpenTailHostBuilder().AddModules(loaded).UsePolicy(PolicyConfig.Parse("{}")).Build();
        Assert.All(host.Catalog.All.Where(t => t.Descriptor.Risk == ToolRisk.Read), t => Assert.Contains("finance", t.Descriptor.Labels));

        var mail = new OpenTailHostBuilder().AddModule(new OpenTail.Modules.Email.EmailModule(new StubInbox(), new StubSender())).UsePolicy(PolicyConfig.Parse("{}")).Build();
        Assert.Contains("personal", mail.Catalog.All.Single(t => t.Descriptor.Name == "email.read").Descriptor.Labels);
        Assert.Empty(mail.Catalog.All.Single(t => t.Descriptor.Name == "email.send").Descriptor.Labels);

        var bad = await Assert.ThrowsAnyAsync<Exception>(() => ModuleLoader.LoadAsync(ModulesConfig.Parse("""
            { "Modules": [ { "Type": "csv", "Id": "erp", "Labels": [ "Fin Ance" ], "Config": { "Root": "." } } ] }
            """, dir), ModuleLoadMode.Run));
        Assert.Contains("lower-case", bad.Message);
    }

    [Fact]
    public async Task A_destination_can_be_allowed_only_for_matching_arguments()
    {
        var policy = """
            {
              "DefaultReadMode": "Execute", "TaintWindow": "00:30:00", "SessionlessTainted": false, "DataFlowMode": "Enforce",
              "Tools": { "mail.send": { "Mode": "Execute" } },
              "DataFlow": { "finance": { "Destinations": [ { "Tool": "mail.send", "When": { "arg": "/to", "domainIn": [ "corp.example" ] } } ] } }
            }
            """;
        var host = new OpenTailHostBuilder().AddModule(new LabelledModule()).UsePolicy(PolicyConfig.Parse(policy)).Build();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));

        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"cfo@corp.example"}"""))).Status);
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"cfo@corp.example.evil.com"}"""))).Status);
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Call("mail.send", s, "{}"))).Status); // unknown is not allowed
    }

    [Fact]
    public void A_destination_condition_that_does_not_compile_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new LabelledModule()).UsePolicy(PolicyConfig.Parse("""
            { "DataFlow": { "finance": { "Destinations": [ { "Tool": "mail.send", "When": { "arg": "to", "domainIn": [ "x" ] } } ] } } }
            """)).Build());
        Assert.Contains("DataFlow 'finance', destination 1", ex.Message);
    }

    [Fact]
    public async Task Value_matching_finds_copied_values_and_may_relax_a_rule_when_none_are_sent()
    {
        var policy = """
            {
              "DefaultReadMode": "Execute", "TaintWindow": "00:30:00", "SessionlessTainted": false, "DataFlowMode": "Enforce",
              "Tools": { "mail.send": { "Mode": "Execute" } },
              "DataFlow": { "finance": { "Allow": [], "RelaxWithoutMatch": true } }
            }
            """;
        var host = new OpenTailHostBuilder().AddModule(new LabelledModule()).UsePolicy(PolicyConfig.Parse(policy)).Build();
        var s = host.Sessions.Start("key:bot");
        await host.Queue.SubmitAsync(Call("erp.query", s));

        // Nothing from the finance read in it: relaxed, runs.
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"x@else.example"}"""))).Status);
        // The invoice number the read returned, copied into the call: queued, with the evidence for the reviewer.
        var copied = await host.Queue.SubmitAsync(Call("mail.send", s, """{"to":"x@else.example re: INV-2026-00071"}"""));
        Assert.Equal(ActionStatus.Pending, copied.Status);
        Assert.Contains("the arguments contain values returned by these reads", copied.SessionLabels);
        Assert.Contains(((InMemoryAuditSink)host.Audit).Entries, e => e.Event == "flow_matched" && e.ActionId == copied.Id);
    }

    [Fact]
    public void Distinctive_values_are_long_strings_and_long_numbers_and_words_of_text()
    {
        var values = DataLabels.Distinctive("""{"id":"INV-2026-00071","n":1234567,"short":"abc","amount":12,"note":"see ACCT-99887766 now"}""", splitText: true).ToList();
        Assert.Contains("inv-2026-00071", values);
        Assert.Contains("1234567", values);
        Assert.Contains("acct-99887766", values);
        Assert.DoesNotContain("abc", values);
        Assert.DoesNotContain("12", values);
    }
}
