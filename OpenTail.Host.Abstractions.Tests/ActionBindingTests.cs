namespace OpenTail.Host.Abstractions.Tests;

/// <summary>
/// The binding digest (governance A.2): one digest over the whole action. Equal actions hash equally whatever their JSON spelling;
/// any change to what the approval names changes the digest.
/// </summary>
public class ActionBindingTests
{
    private const string Schema = "sha256:" + "aa";
    private const string Policy = "sha256:" + "bb";

    private static ActionBinding Sample(string args = """{"to":"a@b.c","body":"hi"}""") =>
        new(ActionBinding.ToolCall, "key:bot", null, "mail.send", Schema, null, args, Policy);

    [Fact]
    public void Digest_has_the_sha256_prefix_and_64_lowercase_hex_digits()
    {
        Assert.Matches("^sha256:[0-9a-f]{64}$", Sample().Digest());
    }

    [Fact]
    public void The_canonical_form_uses_the_naming_plan_keys_in_sorted_order()
    {
        Assert.Equal(
            """{"agent_id":"key:bot","operation":"tool_call","parameters":{"body":"hi","to":"a@b.c"},"policy_version":"sha256:bb","schema_version":1,"subject_id":null,"target":{"resource":null,"tool_name":"mail.send","tool_schema_version":"sha256:aa"}}""",
            Sample().CanonicalJson());
    }

    [Fact]
    public void Argument_spelling_does_not_change_the_digest()
    {
        var spaced = Sample("""{ "body" : "hi" , "to" : "a@b.c" }""");
        Assert.Equal(Sample().Digest(), spaced.Digest());
    }

    [Fact]
    public void Number_spelling_does_not_change_the_digest()
    {
        var a = Sample("""{"n":1.0}""");
        var b = Sample("""{"n":1}""");
        var c = Sample("""{"n":1E0}""");
        Assert.Equal(a.Digest(), b.Digest());
        Assert.Equal(b.Digest(), c.Digest());
    }

    [Fact]
    public void Each_field_the_approval_names_changes_the_digest()
    {
        var baseline = Sample().Digest();

        Assert.NotEqual(baseline, Sample("""{"to":"a@b.c","body":"changed"}""").Digest());
        Assert.NotEqual(baseline, Sample("""{"to":"a@b.c","body":"hi","cc":"x"}""").Digest());
        Assert.NotEqual(baseline, Sample("""{"to":"a@b.c","body":"hi","n":null}""").Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:other", null, "mail.send", Schema, null, """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:bot", "person:rita", "mail.send", Schema, null, """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.reply", Schema, null, """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.send", "sha256:cc", null, """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.send", Schema, "mailbox:inbox", """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
        Assert.NotEqual(baseline,
            new ActionBinding(ActionBinding.ToolCall, "key:bot", null, "mail.send", Schema, null, """{"to":"a@b.c","body":"hi"}""", "sha256:dd").Digest());
        Assert.NotEqual(baseline,
            new ActionBinding("tool_read", "key:bot", null, "mail.send", Schema, null, """{"to":"a@b.c","body":"hi"}""", Policy).Digest());
    }

    [Fact]
    public void Arguments_that_are_not_json_are_refused()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Sample("{not json").Digest());
    }

    [Fact]
    public void Arguments_with_a_repeated_name_are_refused()
    {
        Assert.Throws<FormatException>(() => Sample("""{"to":"a","to":"b"}""").Digest());
    }

    [Fact]
    public void A_tool_schema_version_is_the_digest_of_its_canonical_schema()
    {
        var a = ActionBinding.SchemaVersionOf("""{"type":"object","properties":{"to":{"type":"string"}}}""");
        var b = ActionBinding.SchemaVersionOf("""{ "properties" : { "to" : { "type" : "string" } }, "type" : "object" }""");
        var c = ActionBinding.SchemaVersionOf("""{"type":"object","properties":{"to":{"type":"integer"}}}""");

        Assert.Matches("^sha256:[0-9a-f]{64}$", a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
