using System.Text;
using OpenTail.Modules.Skills;

namespace OpenTail.Orca.Tests;

/// <summary>The v2 identity (content-hashing plan): the canonical text, the unambiguous encoding, and the republish rule. v1 is unchanged.</summary>
public sealed class SkillIdentityV2Tests
{
    private static SkillVersion Sample(string instructions = "Check the bank details.", IReadOnlyList<string>? appliesTo = null,
        IReadOnlyList<string>? tools = null, string? owner = "finance-ops")
    {
        var metadata = owner is null
            ? """{ "format": 1, "id": "invoice-check", "version": "1", "summary": "Check an invoice." }"""
            : $$"""{ "format": 1, "id": "invoice-check", "version": "1", "summary": "Check an invoice.", "owner": "{{owner}}" }""";
        return new SkillVersion("invoice-check", "1", "Check an invoice.", appliesTo ?? ["an invoice arrives"], tools ?? ["mail.read"], [],
            owner ?? "", instructions, metadata);
    }

    [Fact]
    public void V2_hashes_have_the_v2_prefix_and_v1_hashes_keep_theirs()
    {
        Assert.StartsWith("sha256v2:", SkillText.HashV2("x"), StringComparison.Ordinal);
        Assert.Matches("^sha256v2:[0-9a-f]{64}$", SkillText.HashV2("x"));
        Assert.Matches("^sha256:[0-9a-f]{64}$", SkillText.Hash("x"));
        Assert.Equal(SkillText.HashV2("x"), SkillText.HashLike("x", SkillText.HashV2("y")));
        Assert.Equal(SkillText.Hash("x"), SkillText.HashLike("x", SkillText.Hash("y")));
    }

    [Fact]
    public void V2_is_sensitive_to_indentation()
        => Assert.NotEqual(SkillText.HashV2("if (x == 1)\n    return true;"), SkillText.HashV2("if (x == 1)\nreturn true;"));

    [Fact]
    public void V2_is_sensitive_to_a_markdown_hard_break()
        => Assert.NotEqual(SkillText.HashV2("a  \nb"), SkillText.HashV2("a\nb"));

    [Fact]
    public void V2_ignores_line_endings_and_composition()
    {
        Assert.Equal(SkillText.HashV2("a\r\nb\rc"), SkillText.HashV2("a\nb\nc"));
        Assert.Equal(SkillText.HashV2("café"), SkillText.HashV2("café"));
    }

    [Fact]
    public void V2_keeps_unicode_white_space_at_line_ends()
        => Assert.NotEqual(SkillText.HashV2("a \nb"), SkillText.HashV2("a\nb"));

    [Fact]
    public void V2_keeps_inner_runs_of_spaces_and_tells_a_tab_from_spaces()
    {
        Assert.NotEqual(SkillText.HashV2("a  b"), SkillText.HashV2("a b"));
        Assert.NotEqual(SkillText.HashV2("a\tb"), SkillText.HashV2("a b"));
    }

    [Fact]
    public void V2_keeps_a_white_space_only_line_inside_the_text()
        => Assert.NotEqual(SkillText.HashV2("a\n  \nb"), SkillText.HashV2("a\nb"));

    [Fact]
    public void V2_removes_white_space_only_lines_at_start_and_end()
    {
        Assert.Equal("a", SkillText.CanonicalV2("\n  \na\n\t\n"));
        Assert.Equal(SkillText.HashV2("a"), SkillText.HashV2("\n  \na\n\t\n"));
    }

    [Fact]
    public void V2_treats_one_final_newline_and_none_as_equal()
    {
        Assert.Equal(SkillText.HashV2("a"), SkillText.HashV2("a\n"));
        Assert.Equal(SkillText.HashV2("a"), SkillText.HashV2("a\n\n"));
    }

    [Fact]
    public void V2_keeps_blank_line_runs()
        => Assert.NotEqual(SkillText.HashV2("a\n\n\nb"), SkillText.HashV2("a\n\nb"));

    [Fact]
    public void V2_does_not_collide_on_list_boundaries()
        => Assert.NotEqual(SkillText.ContentHashV2(Sample(tools: ["x\ny"])), SkillText.ContentHashV2(Sample(tools: ["x", "y"])));

    [Fact]
    public void V2_does_not_collide_when_an_entry_moves_between_empty_fields()
    {
        var inAppliesTo = Sample(appliesTo: [], tools: ["x"]);
        var inTools = Sample(appliesTo: ["x"], tools: []);
        Assert.NotEqual(SkillText.ContentHashV2(inAppliesTo), SkillText.ContentHashV2(inTools));
        // The v1 identity still joins the fields and cannot tell these apart: that is recorded, and v1 is kept as it is.
        Assert.Equal(SkillText.ContentHash(inAppliesTo), SkillText.ContentHash(inTools));
    }

    [Fact]
    public void V2_count_and_length_digits_cannot_run_together()
    {
        var oneLongItem = Sample(appliesTo: [new string('x', 23)]);
        var twelveItems = Sample(appliesTo: ["abc", .. Enumerable.Repeat("d", 11)]);
        Assert.Contains("1;23:" + new string('x', 23), SkillText.IdentityEncodingV2(oneLongItem), StringComparison.Ordinal);
        Assert.Contains("12;3:abc", SkillText.IdentityEncodingV2(twelveItems), StringComparison.Ordinal);
        Assert.NotEqual(SkillText.ContentHashV2(oneLongItem), SkillText.ContentHashV2(twelveItems));
    }

    [Fact]
    public void V2_distinguishes_an_absent_optional_value_from_an_empty_one()
    {
        var absent = Sample(owner: null);
        var empty = Sample(owner: "");
        Assert.NotEqual(SkillText.ContentHashV2(absent), SkillText.ContentHashV2(empty));
    }

    [Fact]
    public void V1_still_hashes_an_absent_owner_as_empty()
        => Assert.Equal(SkillText.ContentHash(Sample(owner: null)), SkillText.ContentHash(Sample(owner: "")));

    [Fact]
    public void V2_encoding_lengths_match_the_hashed_bytes()
    {
        const string instructions = "Check the café.";
        var encoding = SkillText.IdentityEncodingV2(Sample(instructions: instructions));
        Assert.Contains($"{Encoding.UTF8.GetByteCount(instructions)}:{instructions}", encoding, StringComparison.Ordinal);
        // A composed and a decomposed spelling declare the same bytes, so they are one value.
        Assert.Equal(SkillText.ContentHashV2(Sample(instructions: "café")), SkillText.ContentHashV2(Sample(instructions: "café")));
    }

    [Fact]
    public void The_store_keeps_an_absent_owner_apart_from_an_empty_one()
    {
        const string absent = """{ "format": 1, "id": "invoice-check", "version": "1", "summary": "Check an invoice." }""";
        const string empty = """{ "format": 1, "id": "invoice-check", "version": "1", "summary": "Check an invoice.", "owner": "" }""";
        var a = SkillStore.Parse(absent, "Check the bank details.", "invoice-check", "1");
        var b = SkillStore.Parse(empty, "Check the bank details.", "invoice-check", "1");
        Assert.Equal(a.Owner, b.Owner);   // the store reads both as an empty owner, as before
        Assert.NotEqual(SkillText.ContentHashV2(a), SkillText.ContentHashV2(b));
    }

    [Fact]
    public void A_republish_differing_only_in_indentation_is_refused_at_publish_and_formatting_of_skill_json_is_not_content()
    {
        var root = Path.Combine(Path.GetTempPath(), $"v2-{Guid.NewGuid():N}");
        try
        {
            var store = new SkillStore(root);
            const string meta = """{ "format": 1, "id": "invoice-check", "version": "1", "summary": "Check an invoice.", "owner": "finance-ops" }""";
            store.Publish("invoice-check", "1", "production", meta, "if (x == 1)\n    return true;", "none");

            // An indentation-only change at the same version was accepted under v1. Under v2 it is different content.
            Assert.Throws<ArgumentException>(() =>
                store.Publish("invoice-check", "1", "production", meta, "if (x == 1)\nreturn true;", "1"));

            // The same metadata written without spaces is the same content: formatting between tokens is not content.
            const string compact = """{"format":1,"id":"invoice-check","version":"1","summary":"Check an invoice.","owner":"finance-ops"}""";
            store.Publish("invoice-check", "1", "production", compact, "if (x == 1)\n    return true;", "1");
            Assert.Equal("1", store.LabelledVersion("invoice-check", "production"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
