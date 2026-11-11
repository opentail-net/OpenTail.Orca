using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTail.AgentSkills;

namespace OpenTail.Modules.Skills.Standard;

/// <summary>The result of formatting a skill version: the file text, or the errors that refused it (then <see cref="Text"/> is null).</summary>
public sealed record StandardFormat(string? Text, IReadOnlyList<StandardDiagnostic> Errors);

/// <summary>
/// The Host's use of the Agent Skills standard. Reading and the standard's rules come from the library (<see cref="AgentSkill"/>); this class adds
/// what the Host decides: a saved name uses only a-z, 0-9 and hyphens (with a suggestion), the description is the one a skill's summary and
/// <c>appliesTo</c> give, and the standard file is written for a version. Nothing here loads, runs or grants anything: skills are text, and the
/// standard's <c>allowed-tools</c> is preserved, never acted on.
/// </summary>
public static class StandardSkill
{
    public const int MaxNameLength = AgentSkill.MaxNameLength;
    public const int MaxDescriptionLength = AgentSkill.MaxDescriptionLength;
    public const int MaxCompatibilityLength = AgentSkill.MaxCompatibilityLength;

    /// <summary>Reads a standard skill file (see <see cref="AgentSkill.Parse"/>).</summary>
    public static StandardSkillFile Parse(string text, IReadOnlyCollection<string>? ignoreKeys = null) => AgentSkill.Parse(text, ignoreKeys);

    /// <summary>
    /// The standard's rules for a file's fields (<see cref="AgentSkill.Check"/>). When <paramref name="saving"/>, also the Host's rule that a saved
    /// name uses only a-z, 0-9 and hyphens, with a suggestion when one exists.
    /// </summary>
    public static IReadOnlyList<StandardDiagnostic> Check(StandardSkillFile file, string? directoryName, bool saving)
    {
        var found = AgentSkill.Check(file, directoryName).ToList();
        var name = file.Name;
        // The saved name is judged after NFKC, as the library judges a name (a fullwidth name normalises to ASCII letters and digits).
        if (saving && !string.IsNullOrEmpty(name) && name.Normalize(NormalizationForm.FormKC).EnumerateRunes().Any(r => !IsSaveable(r)))
            found.Add(Error("SKILL-NAME-ASCII", "A name saved to a skill uses only a-z, 0-9 and hyphens.", suggestion: SuggestSaveName(name)));
        return found;
    }

    /// <summary>
    /// The description the standard file gets from a skill's summary and the situations it applies to: the same wording the workbench's
    /// packages have always used. Refused (not cut) when there is nothing to say about when the skill applies, or when it is too long.
    /// </summary>
    public static (string? Text, StandardDiagnostic? Refusal) Describe(string summary, IReadOnlyList<string> appliesTo)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return (null, Error("SKILL-DESCRIPTION", "The summary is blank: say what the skill does."));
        if (appliesTo.Count == 0 || appliesTo.Any(string.IsNullOrWhiteSpace))
            return (null, Error("SKILL-APPLIES-TO", "appliesTo needs at least one entry, and none may be blank: say when the skill applies."));

        var text = summary.TrimEnd('.') + ". Use when " + string.Join("; or when ", appliesTo.Select(a => a.TrimEnd('.'))) + ".";
        var count = text.EnumerateRunes().Count();
        if (count > MaxDescriptionLength)
            return (null, Error("SKILL-DESCRIPTION-LENGTH",
                $"The description would be {count} characters; the standard allows {MaxDescriptionLength}. Shorten the summary or the appliesTo entries. It is refused, not cut."));
        return (text, null);
    }

    /// <summary>
    /// The standard file for a skill version: <c>name</c> is the id, <c>description</c> comes from <see cref="Describe"/>,
    /// <c>metadata.version</c> is the Host's version, and there is no <c>allowed-tools</c>. <paramref name="bodyPrefix"/> goes between
    /// the frontmatter and the instructions (the workbench's check-in). The text is read back and checked before it is returned.
    /// </summary>
    public static StandardFormat Format(string id, string version, string summary, IReadOnlyList<string> appliesTo, string instructions, string? bodyPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            return new StandardFormat(null, [Error("SKILL-BODY", "The instructions are empty.")]);
        var (description, refusal) = Describe(summary, appliesTo);
        if (refusal is not null || description is null)
            return new StandardFormat(null, [refusal ?? Error("SKILL-DESCRIPTION", "No description could be made.")]);

        var text = "---\n"
            + "name: " + id + "\n"
            + "description: " + JsonSerializer.Serialize(description) + "\n"
            + "metadata:\n"
            + "  version: " + JsonSerializer.Serialize(version) + "\n"
            + "---\n\n"
            + (bodyPrefix ?? "")
            + instructions.TrimEnd() + "\n";

        var read = Parse(text);
        var errors = read.Diagnostics.Where(d => d.IsError).Concat(Check(read, id, saving: true)).ToList();
        return errors.Count == 0 ? new StandardFormat(text, []) : new StandardFormat(null, errors);
    }

    /// <summary>
    /// A suggested name that can be saved: the name lowercased, accents dropped, letters with no decomposition mapped (ß to ss and so on),
    /// every other run of characters turned into one hyphen, and the result cut to 64 characters. Null when nothing is left.
    /// The suggestion is for the caller to accept or not; nothing is renamed.
    /// </summary>
    public static string? SuggestSaveName(string name)
    {
        var lowered = name.ToLowerInvariant()
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("œ", "oe", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("đ", "d", StringComparison.Ordinal)
            .Replace("ł", "l", StringComparison.Ordinal);
        var builder = new StringBuilder();
        foreach (var rune in lowered.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
                continue;
            if (rune.Value < 128 && char.IsAsciiLetterOrDigit((char)rune.Value))
                builder.Append(char.ToLowerInvariant((char)rune.Value));
            else if (builder.Length == 0 || builder[^1] != '-')
                builder.Append('-');
        }
        var suggestion = builder.ToString().Trim('-');
        if (suggestion.Length > MaxNameLength)
            suggestion = suggestion[..MaxNameLength].TrimEnd('-');
        return suggestion.Length == 0 ? null : suggestion;
    }

    /// <summary>Separates the frontmatter from the body (<see cref="AgentSkill.SplitFrontmatter"/>).</summary>
    internal static SplitText SplitFrontmatter(string text) => AgentSkill.SplitFrontmatter(text);

    private static bool IsSaveable(Rune rune) =>
        (rune.Value >= 'a' && rune.Value <= 'z') || (rune.Value >= '0' && rune.Value <= '9') || rune.Value == '-';

    private static StandardDiagnostic Error(string code, string message, string? key = null, string? suggestion = null) =>
        AgentSkill.Error(code, message, key, suggestion);
}
