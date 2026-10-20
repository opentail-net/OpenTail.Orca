namespace OpenTail.Host.Tests;

public class PolicyTests
{
    private static ToolDescriptor Tool(ToolRisk risk) => new("t", "d", risk, "m");

    [Fact]
    public void Unlisted_non_read_tools_are_denied_and_unlisted_reads_follow_the_default()
    {
        var denyReads = new PolicyResolver(PolicyConfig.Parse("{}"));
        Assert.Equal(ToolMode.Deny, denyReads.Resolve(Tool(ToolRisk.Read), false).Mode);
        Assert.Equal(ToolMode.Deny, denyReads.Resolve(Tool(ToolRisk.Write), false).Mode);

        var execReads = new PolicyResolver(PolicyConfig.Parse("{\"DefaultReadMode\":\"Execute\"}"));
        Assert.Equal(ToolMode.Execute, execReads.Resolve(Tool(ToolRisk.Read), false).Mode);
        Assert.Equal(ToolMode.Deny, execReads.Resolve(Tool(ToolRisk.Write), false).Mode);
    }

    [Theory]
    [InlineData(ToolRisk.Read, ToolMode.Execute)]
    [InlineData(ToolRisk.Write, ToolMode.Execute)]
    [InlineData(ToolRisk.Destructive, ToolMode.Queue)]
    [InlineData(ToolRisk.ExternalEffect, ToolMode.Queue)]
    public void Taint_only_tightens_high_impact_tools(ToolRisk risk, ToolMode expected)
    {
        var cfg = PolicyConfig.Parse("{\"Tools\":{\"t\":{\"Mode\":\"Execute\"}}}");
        Assert.Equal(expected, new PolicyResolver(cfg).Resolve(Tool(risk), tainted: true).Mode);
        Assert.Equal(ToolMode.Execute, new PolicyResolver(cfg).Resolve(Tool(risk), tainted: false).Mode);
    }

    [Fact]
    public void Taint_never_loosens_a_queue_or_deny_policy()
    {
        var cfg = PolicyConfig.Parse("{\"Tools\":{\"t\":{\"Mode\":\"Deny\"}}}");
        Assert.Equal(ToolMode.Deny, new PolicyResolver(cfg).Resolve(Tool(ToolRisk.ExternalEffect), true).Mode);
    }

    [Fact]
    public void Rule_names_explain_the_decision()
    {
        var cfg = PolicyConfig.Parse("{\"Tools\":{\"t\":{\"Mode\":\"Execute\"}}}");
        var d = new PolicyResolver(cfg).Resolve(Tool(ToolRisk.ExternalEffect), tainted: true);
        Assert.Equal("tool-policy+taint:queue", d.Rule);
    }

    [Fact]
    public void Advertised_means_not_denied_for_a_clean_caller()
    {
        var r = new PolicyResolver(PolicyConfig.Parse("{\"Tools\":{\"t\":{\"Mode\":\"Queue\"}}}"));
        Assert.True(r.IsAdvertised(Tool(ToolRisk.ExternalEffect)));
        Assert.False(new PolicyResolver(PolicyConfig.Deny).IsAdvertised(Tool(ToolRisk.Write)));
    }

    [Fact]
    public void Unknown_json_members_are_rejected_so_typos_cannot_silently_loosen_policy()
        => Assert.ThrowsAny<Exception>(() => PolicyConfig.Parse("{\"Tools\":{\"t\":{\"Mod\":\"Execute\"}}}"));

    [Fact]
    public void Host_refuses_to_start_when_policy_names_an_unknown_tool()
    {
        var b = new OpenTailHostBuilder()
            .AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("{\"Tools\":{\"mail.sned\":{\"Mode\":\"Execute\"}}}"));
        var ex = Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Contains("mail.sned", ex.Message);
    }

    [Fact]
    public void Duplicate_and_reserved_tool_names_fail_at_startup()
    {
        var catalog = new ToolCatalog();
        var f = AIFunctionFactory.Create(() => "x", "dup");
        catalog.Add(new ToolDescriptor("dup", "d", ToolRisk.Read, "m"), f);
        Assert.Throws<InvalidOperationException>(() => catalog.Add(new ToolDescriptor("dup", "d", ToolRisk.Read, "m"), f));

        var reserved = AIFunctionFactory.Create(() => "x", "start_session");
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Add(new ToolDescriptor("start_session", "d", ToolRisk.Read, "m"), reserved));
    }
}
