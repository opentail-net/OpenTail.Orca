using OpenTail.Modules.OpenApi;

namespace OpenTail.Orca.Tests;

/// <summary>Failures design, step 1: every failed action says what kind of failure it was, from the module when it knows, and
/// unclassified failures are bugs, which are never retried.</summary>
public class FailureKindTests
{
    private sealed class FailingModule : IHostModule
    {
        public string Id => "fail";

        public void Register(IToolRegistry registry)
        {
            Add(registry, "fail.auth", () => throw new ToolFailureException(ToolFailureKind.Auth, "The token was refused."));
            Add(registry, "fail.disk", () => throw new ToolFailureException(ToolFailureKind.Environment, "Disk full.", "disk_full"));
            Add(registry, "fail.busy", () => throw new ToolTransientException("Busy.", TimeSpan.FromSeconds(5)));
            Add(registry, "fail.bug", () => throw new InvalidOperationException("Null somewhere."));
            Add(registry, "fail.args", () => throw new ArgumentException("No such folder."));
        }

        private void Add(IToolRegistry registry, string name, Func<string> body) =>
            registry.Add(new ToolDescriptor(name, name, ToolRisk.Write, Id), AIFunctionFactory.Create(body, name, name));
    }

    private static async Task<ActionRecord> Run(string tool)
    {
        var host = new OpenTailHostBuilder().AddModule(new FailingModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "fail.*": { "Mode": "Execute" } } }""".Replace("fail.*", tool))).Build();
        return await host.Queue.SubmitAsync(new ActionRequest(tool, "{}", new ActionRequester(RequesterKind.User, "ops"), null));
    }

    [Theory]
    [InlineData("fail.auth", ToolFailureKind.Auth, "auth", true)]
    [InlineData("fail.disk", ToolFailureKind.Environment, "disk_full", true)]
    [InlineData("fail.busy", ToolFailureKind.Transient, "tool_unavailable", true)]
    [InlineData("fail.bug", ToolFailureKind.Bug, "tool_exception", false)]
    [InlineData("fail.args", ToolFailureKind.Contract, "tool_exception", false)]
    public async Task A_failed_action_records_its_kind(string tool, ToolFailureKind kind, string code, bool retryable)
    {
        var r = await Run(tool);
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Equal((kind, code, retryable), (r.Error!.Kind, r.Error.Code, r.Error.Retryable));
    }

    [Fact]
    public async Task The_model_sees_the_kind()
    {
        var r = await Run("fail.auth");
        Assert.Contains("\"kind\":\"auth\"", ActionViews.DetailJson(r));
    }

    [Theory]
    [InlineData("tool_unavailable", ToolFailureKind.Transient)]
    [InlineData("interrupted", ToolFailureKind.UnknownOutcome)]
    [InlineData("guardrail_blocked", ToolFailureKind.Policy)]
    [InlineData("tool_exception", ToolFailureKind.Bug)]
    [InlineData("anything_else", ToolFailureKind.Bug)]
    public void Records_from_before_kinds_get_one_from_their_code(string code, ToolFailureKind expected) =>
        Assert.Equal(expected, new ActionError(code, "x", false).EffectiveKind);

    [Theory]
    [InlineData(401, ToolFailureKind.Auth)]
    [InlineData(403, ToolFailureKind.Auth)]
    [InlineData(404, ToolFailureKind.Contract)]
    [InlineData(422, ToolFailureKind.Contract)]
    [InlineData(429, ToolFailureKind.Transient)]
    [InlineData(503, ToolFailureKind.Transient)]
    [InlineData(507, ToolFailureKind.Environment)]
    [InlineData(501, ToolFailureKind.Bug)]
    public void HTTP_statuses_have_default_kinds(int status, ToolFailureKind expected) =>
        Assert.Equal(expected, new ProfileErrors().KindOf(status));

    [Fact]
    public void A_profile_can_say_its_404_means_not_allowed()
    {
        var errors = new ProfileErrors { Kinds = new() { ["404"] = ToolFailureKind.Auth } };
        Assert.Equal(ToolFailureKind.Auth, errors.KindOf(404));
        Assert.Equal(ToolFailureKind.Contract, errors.KindOf(400));
    }
}
