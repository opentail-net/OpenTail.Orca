using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Modules.Skills.Standard;

/// <summary>
/// What a publish request's instructions become when stored, and what the reviewer is told about them. The instructions may begin with a
/// standard frontmatter block: the stored instructions are the body after it, its name must be the skill id, and its description must be
/// the one the summary and appliesTo give (one source for the description). The checks apply to new versions only: a version already
/// stored cannot change, so a rollback is not re-checked. The same function runs at preview and at execution, so what is stored is what
/// the reviewer saw.
/// </summary>
public static class StandardPublish
{
    public sealed record Prepared(SkillVersion Target, IReadOnlyList<string> Warnings);

    /// <summary>Prepares a publish. Throws <see cref="ArgumentException"/> with every refusal, each tagged with its code.</summary>
    public static Prepared Prepare(string id, string version, string skillJson, string instructions, bool isNewVersion, IReadOnlyCollection<string>? ignoreKeys)
    {
        var split = StandardSkill.SplitFrontmatter(instructions);
        if (split.HasFrontmatter && !split.Closed)
            throw new ArgumentException("[SKILL-FRONTMATTER] The instructions start with a frontmatter block that is not closed by a line of ---.");
        var body = split.HasFrontmatter ? split.Body : instructions;
        var target = SkillStore.Parse(skillJson, body, id, version);
        var warnings = new List<string>();
        if (!isNewVersion)
            return new Prepared(target, warnings);

        if (split.HasFrontmatter)
        {
            var file = StandardSkill.Parse(instructions, ignoreKeys);
            var errors = file.Diagnostics.Where(d => d.IsError)
                .Concat(StandardSkill.Check(file, directoryName: null, saving: true))
                .ToList();
            if (file.Name is not null && file.Name != id)
                errors.Add(new StandardDiagnostic("SKILL-FRONTMATTER-NAME", $"The frontmatter name '{file.Name}' must be the skill id '{id}'.", true));
            var (described, refusal) = StandardSkill.Describe(target.Summary, target.AppliesTo);
            if (refusal is not null)
                errors.Add(refusal);
            else if (file.Description != described)
                errors.Add(new StandardDiagnostic("SKILL-FRONTMATTER-DESCRIPTION",
                    $"The frontmatter description must be the one the summary and appliesTo give: \"{described}\".", true));
            if (file.Metadata.TryGetValue("version", out var declared) && declared != version)
                errors.Add(new StandardDiagnostic("SKILL-FRONTMATTER-VERSION", $"metadata.version is '{declared}', but this version is '{version}'.", true));
            // The standard's fields are stored in skill.json, so they are served and identified (agent-skills-library plan, step 3).
            var merged = Preserve(skillJson, file, errors);
            if (errors.Count > 0)
                throw new ArgumentException(Refusal(errors));
            target = target with { MetadataJson = merged };

            warnings.AddRange(file.Diagnostics.Where(d => !d.IsError).Select(d => d.Message));
            if (file.AllowedTools is not null)
                warnings.Add("'allowed-tools' is stored as written and is never acted on: approval is the Host's policy, not the skill's.");
        }
        else
        {
            var format = StandardSkill.Format(id, version, target.Summary, target.AppliesTo, body);
            if (format.Text is null)
                throw new ArgumentException(Refusal(format.Errors));
        }
        return new Prepared(target, warnings);
    }

    /// <summary>
    /// The stored metadata: skill.json with the frontmatter's standard fields added. <c>license</c>, <c>compatibility</c> and <c>allowed-tools</c> are
    /// text; <c>metadata</c> is a map, without <c>version</c>, which is the Host's own. A field that both sources give with different values is a
    /// refusal, not a choice between them.
    /// </summary>
    private static string Preserve(string skillJson, StandardSkillFile file, List<StandardDiagnostic> errors)
    {
        var node = (JsonObject)JsonNode.Parse(skillJson)!;
        void Set(string key, string value)
        {
            if (node[key] is JsonValue existing && existing.GetValueKind() == JsonValueKind.String && existing.GetValue<string>() != value)
                errors.Add(new StandardDiagnostic("SKILL-FRONTMATTER-CONFLICT", $"'{key}' is set in skill.json and in the frontmatter with different values.", true, key));
            else
                node[key] = JsonValue.Create(value);
        }
        if (file.License is not null) Set("license", file.License);
        if (file.Compatibility is not null) Set("compatibility", file.Compatibility);
        if (file.AllowedTools is not null) Set("allowed-tools", file.AllowedTools);

        var map = file.Metadata.Where(kv => kv.Key != "version").ToList();
        if (map.Count > 0)
        {
            var existing = node["metadata"] as JsonObject;
            if (existing is null)
            {
                existing = new JsonObject();
                node["metadata"] = existing;
            }
            foreach (var (key, value) in map)
            {
                if (existing[key] is JsonValue current && current.GetValueKind() == JsonValueKind.String && current.GetValue<string>() != value)
                    errors.Add(new StandardDiagnostic("SKILL-FRONTMATTER-CONFLICT", $"metadata '{key}' is set in skill.json and in the frontmatter with different values.", true, key));
                else
                    existing[key] = JsonValue.Create(value);
            }
        }
        return node.ToJsonString();
    }

    private static string Refusal(IEnumerable<StandardDiagnostic> errors) =>
        string.Join(" ", errors.Select(e => $"[{e.Code}] {e.Message}" + (e.Suggestion is null ? "" : $" Suggested name: '{e.Suggestion}'.")));
}
