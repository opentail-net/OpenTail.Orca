using System.Text.Json;
using OpenTail.AgentSkills;
using OpenTail.Modules.Skills;
using OpenTail.Modules.Skills.Standard;

namespace OpenTail.Host.Tests;

/// <summary>A publish request whose instructions may carry a standard frontmatter block: what is stored, what is refused, what the reviewer is told.</summary>
public sealed class StandardPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"standard-publish-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private const string Steps = "# Steps\n\n1. Look.\n";

    private static string Meta(string summary = "Check an invoice.", string appliesTo = "\"an invoice arrives\"") => $$"""
        { "format": 1, "id": "invoice-check", "version": "5", "summary": "{{summary}}",
          "appliesTo": [ {{appliesTo}} ], "tools": [], "policies": [], "owner": "finance-ops" }
        """;

    private const string Good = "---\nname: invoice-check\ndescription: \"Check an invoice. Use when an invoice arrives.\"\nmetadata:\n  version: \"5\"\n---\n\n# Steps\n\n1. Look.\n";

    private static string FrontmatterOf(string extra) =>
        "---\nname: invoice-check\ndescription: \"Check an invoice. Use when an invoice arrives.\"\nmetadata:\n  version: \"5\"\n" + extra + "---\n\n" + Steps;

    [Fact]
    public void A_new_version_without_frontmatter_is_stored_as_sent()
    {
        var prepared = StandardPublish.Prepare("invoice-check", "5", Meta(), Steps, isNewVersion: true, ignoreKeys: null);

        Assert.Equal(Steps, prepared.Target.Instructions);
        Assert.Empty(prepared.Warnings);
    }

    [Fact]
    public void A_frontmatter_request_stores_only_the_body_and_has_the_same_content_hash_as_the_plain_body()
    {
        var plain = StandardPublish.Prepare("invoice-check", "5", Meta(), Steps, isNewVersion: true, ignoreKeys: null);
        var framed = StandardPublish.Prepare("invoice-check", "5", Meta(), Good, isNewVersion: true, ignoreKeys: null);

        Assert.Equal(Steps, framed.Target.Instructions);
        Assert.Equal(SkillText.ContentHash(plain.Target), SkillText.ContentHash(framed.Target));
    }

    [Fact]
    public void A_frontmatter_name_must_be_the_skill_id_and_a_non_ASCII_name_comes_with_a_suggestion()
    {
        var text = Good.Replace("name: invoice-check", "name: café-review", StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: null));

        Assert.Contains("[SKILL-FRONTMATTER-NAME]", error.Message, StringComparison.Ordinal);
        Assert.Contains("'cafe-review'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frontmatter_description_must_be_the_one_the_summary_and_appliesTo_give()
    {
        var text = Good.Replace("Check an invoice. Use when an invoice arrives.", "Something else.", StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: null));

        Assert.Contains("[SKILL-FRONTMATTER-DESCRIPTION]", error.Message, StringComparison.Ordinal);
        Assert.Contains("Check an invoice. Use when an invoice arrives.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void metadata_version_must_be_this_version()
    {
        var text = Good.Replace("version: \"5\"", "version: \"6\"", StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: null));

        Assert.Contains("[SKILL-FRONTMATTER-VERSION]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_is_refused_unless_acknowledged_and_is_then_a_warning()
    {
        var text = FrontmatterOf("owner: ops\n");

        var refused = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: null));
        Assert.Contains("[SKILL-UNKNOWN-KEY]", refused.Message, StringComparison.Ordinal);

        var acknowledged = StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: ["owner"]);
        Assert.Contains(acknowledged.Warnings, w => w.Contains("is ignored", StringComparison.Ordinal));
        Assert.Equal(Steps, acknowledged.Target.Instructions);
    }

    [Fact]
    public void Fields_the_store_has_no_place_for_are_reported_as_warnings_not_stored()
    {
        var text = FrontmatterOf("license: MIT\nallowed-tools: Read\ncompatibility: Needs nothing.\n").Replace(
            "  version: \"5\"\n", "  version: \"5\"\n  author: ops\n", StringComparison.Ordinal);

        var prepared = StandardPublish.Prepare("invoice-check", "5", Meta(), text, isNewVersion: true, ignoreKeys: null);

        Assert.Contains(prepared.Warnings, w => w.Contains("'license' is not stored", StringComparison.Ordinal));
        Assert.Contains(prepared.Warnings, w => w.Contains("'allowed-tools' is not stored and is never acted on", StringComparison.Ordinal));
        Assert.Contains(prepared.Warnings, w => w.Contains("'compatibility' is not stored", StringComparison.Ordinal));
        Assert.Contains(prepared.Warnings, w => w.Contains("metadata 'author' is not stored", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rollback_to_a_stored_version_is_not_rechecked()
    {
        var wrongDescription = Good.Replace("Check an invoice. Use when an invoice arrives.", "Something else.", StringComparison.Ordinal);

        var prepared = StandardPublish.Prepare("invoice-check", "5", Meta(), wrongDescription, isNewVersion: false, ignoreKeys: null);

        Assert.Equal(Steps, prepared.Target.Instructions);
    }

    [Fact]
    public void A_new_version_over_length_or_without_appliesTo_is_refused_with_its_code()
    {
        var overLong = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(summary: new string('x', 1000)), Steps, isNewVersion: true, ignoreKeys: null));
        Assert.Contains("[SKILL-DESCRIPTION-LENGTH]", overLong.Message, StringComparison.Ordinal);

        var noAppliesTo = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(appliesTo: ""), Steps, isNewVersion: true, ignoreKeys: null));
        Assert.Contains("[SKILL-APPLIES-TO]", noAppliesTo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_frontmatter_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => StandardPublish.Prepare("invoice-check", "5", Meta(), "---\nname: invoice-check\n", isNewVersion: true, ignoreKeys: null));

        Assert.Contains("not closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Through_the_module_the_preview_reports_the_warnings_and_the_publish_stores_only_the_body()
    {
        var registry = new CapturingRegistry();
        new SkillsModule(new SkillsModuleOptions { Store = Path.Combine(_root, "store") }).Register(registry);
        var (publish, preview) = registry.Tools["skills.publish"];

        var args = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["skillId"] = "invoice-check", ["version"] = "5", ["reason"] = "Clearer steps.", ["label"] = "production", ["expectedFrom"] = "none",
            ["skillJson"] = Meta(), ["instructions"] = FrontmatterOf("license: MIT\n"),
        });
        var result = await preview!.PreviewAsync(args);
        using (var diff = JsonDocument.Parse(result.DiffJson!))
            Assert.Contains(diff.RootElement.GetProperty("frontmatterWarnings").EnumerateArray(), w => w.GetString()!.Contains("'license' is not stored", StringComparison.Ordinal));

        await publish.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["skillId"] = "invoice-check", ["version"] = "5", ["reason"] = "Clearer steps.", ["label"] = "production", ["expectedFrom"] = "none",
            ["skillJson"] = Meta(), ["instructions"] = FrontmatterOf("license: MIT\n"),
        }));

        Assert.Equal(Steps, File.ReadAllText(Path.Combine(_root, "store", "invoice-check", "5", "SKILL.md")));
    }

    [Fact]
    public async Task Through_the_module_an_unacknowledged_unknown_key_is_refused_at_preview()
    {
        var registry = new CapturingRegistry();
        new SkillsModule(new SkillsModuleOptions { Store = Path.Combine(_root, "store") }).Register(registry);
        var (_, preview) = registry.Tools["skills.publish"];
        var args = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["skillId"] = "invoice-check", ["version"] = "5", ["reason"] = "Clearer steps.", ["label"] = "production", ["expectedFrom"] = "none",
            ["skillJson"] = Meta(), ["instructions"] = FrontmatterOf("owner: ops\n"),
        });

        var error = await Assert.ThrowsAsync<ArgumentException>(() => preview!.PreviewAsync(args));

        Assert.Contains("[SKILL-UNKNOWN-KEY]", error.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingRegistry : IToolRegistry
    {
        public Dictionary<string, (AIFunction Function, IPreviewableTool? Preview)> Tools { get; } = new(StringComparer.Ordinal);

        public void Add(ToolDescriptor descriptor, AIFunction function, IPreviewableTool? preview = null) => Tools[descriptor.Name] = (function, preview);
    }
}
