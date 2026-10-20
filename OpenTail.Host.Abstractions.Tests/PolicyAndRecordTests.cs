using OpenTail.Host.Abstractions;

namespace OpenTail.Host.Abstractions.Tests;

public class PolicyAndRecordTests
{
    [Fact]
    public void Default_policy_is_deny()
    {
        Assert.Equal(ToolMode.Deny, new ToolPolicy().Mode);
        Assert.Equal(ToolMode.Deny, ToolPolicy.Denied.Mode);
    }

    [Theory]
    [InlineData(ToolMode.Execute, ToolMode.Queue, ToolMode.Queue)]
    [InlineData(ToolMode.Queue, ToolMode.Execute, ToolMode.Queue)]
    [InlineData(ToolMode.Queue, ToolMode.Deny, ToolMode.Deny)]
    [InlineData(ToolMode.Execute, ToolMode.Deny, ToolMode.Deny)]
    [InlineData(ToolMode.Execute, ToolMode.Execute, ToolMode.Execute)]
    public void Combining_modes_only_tightens(ToolMode a, ToolMode b, ToolMode expected)
    {
        Assert.Equal(expected, ToolPolicy.Stricter(a, b));
        Assert.Equal(expected, ToolPolicy.Stricter(b, a));
    }

    [Theory]
    [InlineData(ToolRisk.Read, false)]
    [InlineData(ToolRisk.Write, false)]
    [InlineData(ToolRisk.Destructive, true)]
    [InlineData(ToolRisk.ExternalEffect, true)]
    public void High_impact_means_destructive_or_external(ToolRisk risk, bool expected)
        => Assert.Equal(expected, new ToolDescriptor("t", "d", risk, "m").IsHighImpact);

    private static ActionRecord NewRecord() => new()
    {
        Id = "a1",
        ToolName = "email.send",
        ModuleId = "email",
        ArgumentsJson = "{\"to\":\"a@b.c\"}",
        ArgumentsHash = "h",
        Requester = new ActionRequester(RequesterKind.AiSession, "s1"),
        Status = ActionStatus.Pending,
        PolicyMode = ToolMode.Queue,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Session_is_optional_and_schema_version_is_stamped()
    {
        var r = NewRecord();
        Assert.Null(r.SessionId);
        Assert.Equal(HostContract.ActionSchemaVersion, r.SchemaVersion);
    }

    [Fact]
    public void Effective_arguments_prefer_the_last_reviewer_edit_and_never_overwrite_the_original()
    {
        var r = NewRecord();
        Assert.Equal(r.ArgumentsJson, r.EffectiveArgumentsJson);

        var edited = r with
        {
            Reviews =
            [
                new ActionReview("bob", ReviewDecision.RequestChanges, "fix recipient", DateTimeOffset.UnixEpoch),
                new ActionReview("bob", ReviewDecision.Approve, null, DateTimeOffset.UnixEpoch) { EditedArgumentsJson = "{\"to\":\"x@y.z\"}" },
            ],
        };
        Assert.Equal("{\"to\":\"x@y.z\"}", edited.EffectiveArgumentsJson);
        Assert.Equal("{\"to\":\"a@b.c\"}", edited.ArgumentsJson);
    }
}
