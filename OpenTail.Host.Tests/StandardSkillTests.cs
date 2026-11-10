using System.Text.Json;
using OpenTail.Modules.Skills.Standard;

namespace OpenTail.Host.Tests;

/// <summary>The standard skill file: reading, the rules, suggested names, describing, and formatting.</summary>
public sealed class StandardSkillTests
{
    private const string Minimal = "---\nname: pdf-processing\ndescription: Extracts PDF text.\n---\n\n# PDF\n";

    private static IReadOnlyList<string> Codes(IEnumerable<StandardDiagnostic> diagnostics) => diagnostics.Select(d => d.Code).ToList();

    [Fact]
    public void A_minimal_file_reads_with_no_errors_and_keeps_its_body()
    {
        var file = StandardSkill.Parse(Minimal);

        Assert.Equal("pdf-processing", file.Name);
        Assert.Equal("Extracts PDF text.", file.Description);
        Assert.DoesNotContain(file.Diagnostics, d => d.IsError);
        Assert.Empty(StandardSkill.Check(file, "pdf-processing", saving: true));
        Assert.Equal("# PDF\n", file.Body);
    }

    [Fact]
    public void A_byte_order_mark_and_CRLF_line_endings_are_accepted()
    {
        var file = StandardSkill.Parse("﻿" + Minimal.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal("pdf-processing", file.Name);
        Assert.DoesNotContain(file.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void Quoted_and_folded_values_are_read()
    {
        var quoted = StandardSkill.Parse("---\nname: a\ndescription: \"Extracts PDF: tables # forms.\"\n---\n");
        Assert.Equal("Extracts PDF: tables # forms.", quoted.Description);

        var folded = StandardSkill.Parse("---\nname: a\ndescription: >\n  Extracts text\n  and tables.\n---\n");
        Assert.Equal("Extracts text and tables.", folded.Description);
    }

    [Fact]
    public void A_missing_name_or_description_is_reported()
    {
        Assert.Contains("SKILL-NAME-MISSING", Codes(StandardSkill.Check(StandardSkill.Parse("---\ndescription: x\n---\n"), null, saving: false)));
        Assert.Contains("SKILL-DESCRIPTION", Codes(StandardSkill.Check(StandardSkill.Parse("---\nname: a\n---\n"), null, saving: false)));
    }

    [Fact]
    public void Missing_or_unclosed_frontmatter_is_refused()
    {
        var none = StandardSkill.Parse("# No frontmatter here\n");
        Assert.Contains(none.Diagnostics, d => d.IsError && d.Code == "SKILL-FRONTMATTER");
        Assert.Null(none.Name);

        var unclosed = StandardSkill.Parse("---\nname: a\ndescription: b\n");
        Assert.Contains(unclosed.Diagnostics, d => d.IsError && d.Message.Contains("not closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_keys_are_errors()
    {
        var file = StandardSkill.Parse("---\nname: a\nname: b\ndescription: x\n---\n");

        Assert.Contains(file.Diagnostics, d => d.IsError && d.Key == "name" && d.Message.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_keys_are_errors_and_can_be_acknowledged_by_exact_name()
    {
        const string text = "---\nname: a\ndescription: x\nowner: ops\n---\n";

        var refused = StandardSkill.Parse(text);
        Assert.Contains(refused.Diagnostics, d => d.IsError && d.Code == "SKILL-UNKNOWN-KEY" && d.Key == "owner");

        var acknowledged = StandardSkill.Parse(text, ["owner"]);
        Assert.DoesNotContain(acknowledged.Diagnostics, d => d.IsError);
        Assert.Contains(acknowledged.Diagnostics, d => !d.IsError && d.Code == "SKILL-KEY-IGNORED" && d.Key == "owner");

        var typo = StandardSkill.Parse(text, ["ownr"]);
        Assert.Contains(typo.Diagnostics, d => d.IsError && d.Code == "SKILL-IGNORE-ABSENT" && d.Key == "ownr");
        Assert.Contains(typo.Diagnostics, d => d.IsError && d.Code == "SKILL-UNKNOWN-KEY" && d.Key == "owner");

        var standard = StandardSkill.Parse(text, ["name"]);
        Assert.Contains(standard.Diagnostics, d => d.IsError && d.Code == "SKILL-IGNORE-STANDARD" && d.Key == "name");
    }

    [Theory]
    [InlineData("name: &a pdf\ndescription: x")]
    [InlineData("name: *a\ndescription: x")]
    [InlineData("name: a\ndescription: x\nallowed-tools:\n  - Read")]
    [InlineData("name: a\ndescription: x\nmetadata:\n  deep:\n    x: y")]
    [InlineData("name: a\ndescription: x\nmetadata:\n  version: !!str 5")]
    public void Anchors_aliases_sequences_and_nested_metadata_are_refused(string frontmatter)
    {
        var file = StandardSkill.Parse("---\n" + frontmatter + "\n---\n");

        Assert.Contains(file.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void A_non_ASCII_name_is_read_but_cannot_be_saved_and_comes_with_a_suggestion()
    {
        var file = StandardSkill.Parse("---\nname: café-review\ndescription: Reviews cafés.\n---\n");

        Assert.Empty(StandardSkill.Check(file, "café-review", saving: false));

        var save = StandardSkill.Check(file, "café-review", saving: true);
        var refusal = Assert.Single(save, d => d.Code == "SKILL-NAME-ASCII");
        Assert.Equal("cafe-review", refusal.Suggestion);
    }

    [Theory]
    [InlineData("-pdf")]
    [InlineData("pdf-")]
    [InlineData("pdf--processing")]
    [InlineData("PDF-processing")]
    [InlineData("pdf_processing")]
    public void Name_rules_refuse_what_the_standard_refuses(string name)
    {
        var file = StandardSkill.Parse($"---\nname: {name}\ndescription: x\n---\n");

        Assert.Contains("SKILL-NAME", Codes(StandardSkill.Check(file, null, saving: false)));
    }

    [Fact]
    public void Names_are_at_most_64_characters()
    {
        var ok = StandardSkill.Parse($"---\nname: {new string('a', 64)}\ndescription: x\n---\n");
        var tooLong = StandardSkill.Parse($"---\nname: {new string('a', 65)}\ndescription: x\n---\n");

        Assert.Empty(StandardSkill.Check(ok, null, saving: true));
        Assert.Contains("SKILL-NAME", Codes(StandardSkill.Check(tooLong, null, saving: true)));
    }

    [Fact]
    public void A_name_must_match_the_directory_after_NFKC()
    {
        var file = StandardSkill.Parse("---\nname: pdf\ndescription: x\n---\n");

        Assert.Contains("SKILL-NAME-DIRECTORY", Codes(StandardSkill.Check(file, "pdf-x", saving: false)));
        Assert.Empty(StandardSkill.Check(file, "pdf", saving: false));
    }

    [Fact]
    public void Description_lengths_count_code_points_not_UTF16_units()
    {
        var atLimit = string.Concat(Enumerable.Repeat("😀", 1024)); // 1024 code points, 2048 UTF-16 units
        var over = atLimit + "😀";

        Assert.Empty(StandardSkill.Check(StandardSkill.Parse($"---\nname: a\ndescription: {atLimit}\n---\n"), null, saving: true));
        Assert.Contains("SKILL-DESCRIPTION-LENGTH", Codes(StandardSkill.Check(StandardSkill.Parse($"---\nname: a\ndescription: {over}\n---\n"), null, saving: true)));
    }

    [Fact]
    public void Compatibility_is_one_to_500_characters_when_given()
    {
        Assert.Contains("SKILL-COMPATIBILITY", Codes(StandardSkill.Check(StandardSkill.Parse("---\nname: a\ndescription: x\ncompatibility: \"\"\n---\n"), null, saving: true)));
        Assert.Contains("SKILL-COMPATIBILITY", Codes(StandardSkill.Check(StandardSkill.Parse($"---\nname: a\ndescription: x\ncompatibility: {new string('x', 501)}\n---\n"), null, saving: true)));
        Assert.Empty(StandardSkill.Check(StandardSkill.Parse($"---\nname: a\ndescription: x\ncompatibility: {new string('x', 500)}\n---\n"), null, saving: true));
    }

    [Theory]
    [InlineData("Café Review", "cafe-review")]
    [InlineData("Überweisung-prüfen", "uberweisung-prufen")]
    [InlineData("Straße", "strasse")]
    [InlineData("--Invoices--", "invoices")]
    [InlineData("日本語", null)]
    public void Suggested_names_are_the_expected_ASCII_names(string name, string? expected)
    {
        Assert.Equal(expected, StandardSkill.SuggestSaveName(name));
    }

    [Fact]
    public void A_suggested_name_is_at_most_64_characters_with_no_trailing_hyphen_and_always_saves()
    {
        var suggestion = StandardSkill.SuggestSaveName(new string('é', 70) + "-x");

        Assert.NotNull(suggestion);
        Assert.True(suggestion.Length <= 64);
        Assert.False(suggestion.EndsWith('-'));
        Assert.Empty(StandardSkill.Check(StandardSkill.Parse($"---\nname: {suggestion}\ndescription: x\n---\n"), null, saving: true));
    }

    [Fact]
    public void Describe_keeps_the_wording_the_packages_have_always_used()
    {
        var (text, refusal) = StandardSkill.Describe("Check an invoice.", ["an invoice arrives", "someone asks"]);

        Assert.Null(refusal);
        Assert.Equal("Check an invoice. Use when an invoice arrives; or when someone asks.", text);
        Assert.Equal("X. Use when Y.", StandardSkill.Describe("X", ["Y."]).Text);
    }

    [Fact]
    public void Describe_refuses_no_applies_to_blank_entries_and_over_length_descriptions()
    {
        Assert.Equal("SKILL-APPLIES-TO", StandardSkill.Describe("x", []).Refusal?.Code);
        Assert.Equal("SKILL-APPLIES-TO", StandardSkill.Describe("x", [""]).Refusal?.Code);
        Assert.Equal("SKILL-DESCRIPTION", StandardSkill.Describe(" ", ["y"]).Refusal?.Code);
        Assert.Equal("SKILL-DESCRIPTION-LENGTH", StandardSkill.Describe(new string('x', 1020), ["y"]).Refusal?.Code);
    }

    [Fact]
    public void Format_writes_the_standard_form_and_reads_back()
    {
        var format = StandardSkill.Format("invoice-check", "5", "Check an invoice.", ["an invoice arrives"], "# Steps\n\n1. Look.\n\n\n", bodyPrefix: "> note\n\n");

        Assert.NotNull(format.Text);
        var read = StandardSkill.Parse(format.Text);
        Assert.Equal("invoice-check", read.Name);
        Assert.Equal("Check an invoice. Use when an invoice arrives.", read.Description);
        Assert.Equal("5", read.Metadata["version"]);
        Assert.Null(read.AllowedTools);
        Assert.Equal("> note\n\n# Steps\n\n1. Look.\n", read.Body);
    }

    [Fact]
    public void Format_refuses_what_the_standard_refuses()
    {
        Assert.Equal("SKILL-APPLIES-TO", Assert.Single(StandardSkill.Format("a", "1", "x", [], "# Body").Errors).Code);
        Assert.Equal("SKILL-BODY", Assert.Single(StandardSkill.Format("a", "1", "x", ["y"], "  ").Errors).Code);
    }

    [Fact]
    public void Format_quotes_what_needs_quoting_and_reads_it_back_unchanged()
    {
        const string summary = "Handles \"quotes\", colons: and # marks, café, and\nnewlines.";
        IReadOnlyList<string> appliesTo = ["a colon: here", "a #hash"];
        var expected = StandardSkill.Describe(summary, appliesTo).Text;

        var format = StandardSkill.Format("quoted-skill", "2", summary, appliesTo, "# Body");

        Assert.NotNull(format.Text);
        Assert.Equal(expected, StandardSkill.Parse(format.Text).Description);
    }
}
