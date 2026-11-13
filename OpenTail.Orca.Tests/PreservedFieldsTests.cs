using System.Text.Json;
using OpenTail.Modules.Skills;
using OpenTail.Modules.Skills.Standard;

namespace OpenTail.Orca.Tests;

/// <summary>agent-skills-library plan, step 3: the standard's optional fields are preserved, stored and identified, and kept exactly as written.</summary>
public sealed class PreservedFieldsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"preserved-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Summary = "Check whether an unpaid invoice is genuine before anyone pays or chases it.";
    private const string Description = Summary + " Use when an invoice or payment demand arrives by email.";

    /// <summary>skill.json for version <paramref name="version"/>, with any extra members after the owner.</summary>
    private static string SkillJson(string version = "5", string extra = "") =>
        $$"""{ "format": 1, "id": "invoice-check", "version": "{{version}}", "summary": "{{Summary}}", "appliesTo": [ "an invoice or payment demand arrives by email" ], "tools": [ "mail.read" ], "policies": [], "owner": "finance-ops"{{extra}} }""";

    /// <summary>A standard file for version 5 with the optional fields given as YAML lines.</summary>
    private static string Frontmatter(string fields = "") =>
        $"---\nname: invoice-check\ndescription: \"{Description}\"\n{fields}metadata:\n  version: \"5\"\n  author: ops\n---\nNever pay from an email.\n";

    /// <summary>skill.json with one member added: the stored text a published version carries.</summary>
    private static string WithMember(string key, string value) =>
        SkillJson(extra: ", " + JsonSerializer.Serialize(key) + ": " + JsonSerializer.Serialize(value));

    [Fact]
    public void The_preserved_fields_are_stored_with_the_version_and_read_back_exactly()
    {
        var text = Frontmatter("license: \"MIT  \"\ncompatibility: Needs nothing.\nallowed-tools: Read\n");
        var prepared = StandardPublish.Prepare("invoice-check", "5", SkillJson(), text, isNewVersion: true, ignoreKeys: null);
        var store = new SkillStore(Path.Combine(_root, "store"));

        store.Publish("invoice-check", "5", "production", prepared.Target.MetadataJson, prepared.Target.Instructions, "none");
        var read = store.Get("invoice-check", "5")!;

        var preserved = SkillStore.Preserved(read);
        Assert.Equal("MIT  ", preserved.License);                       // exact: the trailing spaces are the author's
        Assert.Equal("Needs nothing.", preserved.Compatibility);
        Assert.Equal("Read", preserved.AllowedTools);
        Assert.Equal(new Dictionary<string, string> { ["author"] = "ops" }, preserved.Metadata);   // the version key is the Host's, not the skill's
        Assert.Equal("finance-ops", read.Owner);
    }

    [Theory]
    [InlineData("license", "MIT", "Apache-2.0")]
    [InlineData("compatibility", "Needs nothing.", "Needs a browser.")]
    [InlineData("allowed-tools", "Read", "Read Write")]
    public void A_changed_preserved_field_changes_the_v2_identity(string key, string before, string after)
    {
        var a = SkillStore.Parse(WithMember(key, before), "Never pay from an email.", "invoice-check", "5");
        var b = SkillStore.Parse(WithMember(key, after), "Never pay from an email.", "invoice-check", "5");

        Assert.NotEqual(SkillText.ContentHashV2(a), SkillText.ContentHashV2(b));
    }

    [Fact]
    public void A_changed_metadata_entry_changes_the_v2_identity()
    {
        var a = SkillStore.Parse(SkillJson(extra: ", \"metadata\": { \"author\": \"ops\" }"), "Never pay from an email.", "invoice-check", "5");
        var b = SkillStore.Parse(SkillJson(extra: ", \"metadata\": { \"author\": \"finance\" }"), "Never pay from an email.", "invoice-check", "5");

        Assert.NotEqual(SkillText.ContentHashV2(a), SkillText.ContentHashV2(b));
    }

    [Fact]
    public void The_order_of_metadata_keys_in_the_file_is_not_content()
    {
        var a = SkillStore.Parse(SkillJson(extra: ", \"metadata\": { \"author\": \"ops\", \"team\": \"cs\" }"), "Never pay from an email.", "invoice-check", "5");
        var b = SkillStore.Parse(SkillJson(extra: ", \"metadata\": { \"team\": \"cs\", \"author\": \"ops\" }"), "Never pay from an email.", "invoice-check", "5");

        Assert.Equal(SkillText.ContentHashV2(a), SkillText.ContentHashV2(b));
    }

    [Fact]
    public void A_preserved_field_given_differently_in_skill_json_and_in_the_frontmatter_is_refused()
    {
        var text = Frontmatter("license: \"MIT\"\n");

        var ex = Assert.Throws<ArgumentException>(() =>
            StandardPublish.Prepare("invoice-check", "5", WithMember("license", "Apache-2.0"), text, isNewVersion: true, ignoreKeys: null));

        Assert.Contains("[SKILL-FRONTMATTER-CONFLICT]", ex.Message, StringComparison.Ordinal);
    }
}
