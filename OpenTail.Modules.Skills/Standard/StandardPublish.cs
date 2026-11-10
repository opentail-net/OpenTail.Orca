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
            if (errors.Count > 0)
                throw new ArgumentException(Refusal(errors));

            warnings.AddRange(file.Diagnostics.Where(d => !d.IsError).Select(d => d.Message));
            if (file.License is not null)
                warnings.Add("'license' is not stored, so it is not served.");
            if (file.Compatibility is not null)
                warnings.Add("'compatibility' is not stored, so it is not served.");
            if (file.AllowedTools is not null)
                warnings.Add("'allowed-tools' is not stored and is never acted on: approval is the Host's policy, not the skill's.");
            warnings.AddRange(file.Metadata.Keys.Where(key => key != "version")
                .Select(key => $"metadata '{key}' is not stored, so it is not served."));
        }
        else
        {
            var format = StandardSkill.Format(id, version, target.Summary, target.AppliesTo, body);
            if (format.Text is null)
                throw new ArgumentException(Refusal(format.Errors));
        }
        return new Prepared(target, warnings);
    }

    private static string Refusal(IEnumerable<StandardDiagnostic> errors) =>
        string.Join(" ", errors.Select(e => $"[{e.Code}] {e.Message}" + (e.Suggestion is null ? "" : $" Suggested name: '{e.Suggestion}'.")));
}
