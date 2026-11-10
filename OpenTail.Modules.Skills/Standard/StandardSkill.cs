using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OpenTail.Modules.Skills.Standard;

/// <summary>A finding about a standard skill file. An error refuses a read or a save; a warning is reported and does not refuse.</summary>
public sealed record StandardDiagnostic(string Code, string Message, bool IsError, string? Key = null, string? Suggestion = null);

/// <summary>A standard skill file as read: its frontmatter fields, its body and what was found in it.</summary>
public sealed record StandardSkillFile(
    string? Name,
    string? Description,
    string? Compatibility,
    string? License,
    IReadOnlyDictionary<string, string> Metadata,
    string? AllowedTools,
    string Body,
    IReadOnlyList<StandardDiagnostic> Diagnostics);

/// <summary>The result of formatting a skill version: the file text, or the errors that refused it (then <see cref="Text"/> is null).</summary>
public sealed record StandardFormat(string? Text, IReadOnlyList<StandardDiagnostic> Errors);

/// <summary>
/// The Agent Skills form of a skill's <c>SKILL.md</c>: read it, check it, describe it, and write it. Reading accepts any Unicode
/// letter in a name, as the reference validator does; saving accepts only a-z, 0-9 and hyphens, and says what to use instead.
/// Nothing here loads, runs or grants anything: skills are text, and the standard's <c>allowed-tools</c> is preserved, never acted on.
/// </summary>
public static class StandardSkill
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxCompatibilityLength = 500;

    private static readonly HashSet<string> StandardKeys = new(StringComparer.Ordinal)
    {
        "name", "description", "license", "compatibility", "metadata", "allowed-tools",
    };

    /// <summary>
    /// Reads a standard skill file. Nothing is thrown for a bad file: problems come back as diagnostics, so a caller can list them all.
    /// <paramref name="ignoreKeys"/> names unknown top-level keys the caller accepts; each must be present in the file, and each is
    /// reported as a warning rather than an error.
    /// </summary>
    public static StandardSkillFile Parse(string text, IReadOnlyCollection<string>? ignoreKeys = null)
    {
        var split = SplitFrontmatter(text);
        var diagnostics = new List<StandardDiagnostic>();
        if (!split.HasFrontmatter)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", "The file must start with a frontmatter block: a line of ---, then name and description, then a line of ---."));
            return Empty(split.Body, diagnostics);
        }
        if (!split.Closed)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", "The frontmatter is not closed by a line of ---."));
            return Empty(split.Body, diagnostics);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        try
        {
            ReadFrontmatter(split.Yaml, diagnostics, fields, metadata, seen, unknown);
        }
        catch (YamlException ex)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", $"The frontmatter is not valid YAML (line {ex.Start.Line}): {ex.Message}"));
        }

        var ignore = new HashSet<string>(ignoreKeys ?? [], StringComparer.Ordinal);
        foreach (var key in ignore)
        {
            if (StandardKeys.Contains(key))
                diagnostics.Add(Error("SKILL-IGNORE-STANDARD", $"'{key}' is a standard field and cannot be ignored.", key));
            else if (!seen.Contains(key))
                diagnostics.Add(Error("SKILL-IGNORE-ABSENT", $"'{key}' is listed as ignored, but the frontmatter has no such key.", key));
        }
        foreach (var key in unknown.Distinct(StringComparer.Ordinal))
        {
            if (ignore.Contains(key))
                diagnostics.Add(new StandardDiagnostic("SKILL-KEY-IGNORED", $"Unknown frontmatter key '{key}' is ignored, as the caller asked.", false, key));
            else
                diagnostics.Add(Error("SKILL-UNKNOWN-KEY", $"Unknown frontmatter key '{key}'. Remove it, or list it as ignored.", key));
        }

        return new StandardSkillFile(
            Name: Field(fields, "name"),
            Description: Field(fields, "description"),
            Compatibility: Field(fields, "compatibility"),
            License: Field(fields, "license"),
            Metadata: metadata,
            AllowedTools: Field(fields, "allowed-tools"),
            Body: split.Body,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// The standard's rules for a file's fields. <paramref name="directoryName"/> null skips the directory rule.
    /// <paramref name="saving"/> adds the rule that a saved name uses only a-z, 0-9 and hyphens, with a suggestion when one exists.
    /// Lengths count code points, as the reference validator does.
    /// </summary>
    public static IReadOnlyList<StandardDiagnostic> Check(StandardSkillFile file, string? directoryName, bool saving)
    {
        var found = new List<StandardDiagnostic>();
        var name = file.Name;
        if (string.IsNullOrEmpty(name))
        {
            found.Add(Error("SKILL-NAME-MISSING", "The frontmatter needs a name."));
        }
        else
        {
            var runes = name.EnumerateRunes().ToList();
            if (runes.Count > MaxNameLength)
                found.Add(Error("SKILL-NAME", $"The name is {runes.Count} characters; the standard allows {MaxNameLength}."));
            if (name != name.ToLowerInvariant())
                found.Add(Error("SKILL-NAME", "The name must be lowercase."));
            if (name.StartsWith('-') || name.EndsWith('-'))
                found.Add(Error("SKILL-NAME", "The name must not start or end with a hyphen."));
            if (name.Contains("--", StringComparison.Ordinal))
                found.Add(Error("SKILL-NAME", "The name must not contain two hyphens in a row."));
            if (runes.Any(r => r.Value != '-' && !Rune.IsLetterOrDigit(r)))
                found.Add(Error("SKILL-NAME", "The name may contain only letters, digits and hyphens."));
            if (saving && runes.Any(r => !IsSaveable(r)))
                found.Add(Error("SKILL-NAME-ASCII", "A name saved to a skill uses only a-z, 0-9 and hyphens.", suggestion: SuggestSaveName(name)));
            if (directoryName is not null && directoryName.Normalize(NormalizationForm.FormKC) != name)
                found.Add(Error("SKILL-NAME-DIRECTORY", $"The name '{name}' does not match the directory name '{directoryName}'."));
        }

        if (string.IsNullOrWhiteSpace(file.Description))
        {
            found.Add(Error("SKILL-DESCRIPTION", "The frontmatter needs a non-empty description."));
        }
        else if (file.Description.EnumerateRunes().Count() > MaxDescriptionLength)
        {
            var count = file.Description.EnumerateRunes().Count();
            found.Add(Error("SKILL-DESCRIPTION-LENGTH", $"The description is {count} characters; the standard allows {MaxDescriptionLength}. It is refused, not cut."));
        }

        if (file.Compatibility is { } compatibility)
        {
            if (string.IsNullOrWhiteSpace(compatibility))
                found.Add(Error("SKILL-COMPATIBILITY", "compatibility, when given, must not be empty."));
            else if (compatibility.EnumerateRunes().Count() > MaxCompatibilityLength)
                found.Add(Error("SKILL-COMPATIBILITY", $"compatibility is longer than the standard's {MaxCompatibilityLength} characters."));
        }
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

    internal sealed record SplitText(bool HasFrontmatter, bool Closed, string Yaml, string Body);

    /// <summary>
    /// Separates the frontmatter from the body. The delimiters are lines of their own. A leading BOM is removed from the frontmatter
    /// reading only. A file without frontmatter comes back whole, as the body, exactly as it was given.
    /// </summary>
    internal static SplitText SplitFrontmatter(string text)
    {
        var stripped = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        var lines = stripped.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0].TrimEnd() != "---")
            return new SplitText(false, false, "", text);
        var close = Array.FindIndex(lines, 1, line => line.TrimEnd() == "---");
        if (close < 0)
            return new SplitText(true, false, "", text);
        var yaml = string.Join('\n', lines[1..close]);
        var rest = lines[(close + 1)..];
        var start = 0;
        while (start < rest.Length && rest[start].Trim().Length == 0)
            start++;
        return new SplitText(true, true, yaml, string.Join('\n', rest[start..]));
    }

    /// <summary>
    /// Reads the frontmatter's events. Anchors, aliases, tags, multiple documents, duplicate keys and non-string values are errors;
    /// <c>metadata</c> must be a mapping of strings. Nothing is deserialised into objects.
    /// </summary>
    private static void ReadFrontmatter(string yaml, List<StandardDiagnostic> diagnostics, Dictionary<string, string> fields,
        Dictionary<string, string> metadata, HashSet<string> seen, List<string> unknown)
    {
        using var reader = new StringReader(yaml);
        var parser = new Parser(reader);
        Next(parser); // stream start
        var document = Next(parser);
        if (document is StreamEnd)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", "The frontmatter is empty."));
            return;
        }
        var root = Next(parser);
        if (root is not MappingStart mapping)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", "The frontmatter must be a mapping of fields, such as name: value."));
            return;
        }
        if (HasAnchorOrTag(mapping))
            diagnostics.Add(Error("SKILL-FRONTMATTER", "Anchors and tags are not allowed in the frontmatter."));

        while (true)
        {
            var keyEvent = Next(parser);
            if (keyEvent is MappingEnd)
                break;
            if (keyEvent is not Scalar keyScalar || HasAnchorOrTag(keyScalar))
            {
                diagnostics.Add(Error("SKILL-FRONTMATTER", "Every frontmatter key must be a plain name."));
                SkipNode(parser, keyEvent);
                SkipNode(parser, Next(parser));
                continue;
            }

            var key = keyScalar.Value;
            var valueEvent = Next(parser);
            if (!seen.Add(key))
                diagnostics.Add(Error("SKILL-FRONTMATTER", $"The key '{key}' appears more than once.", key));
            if (valueEvent is AnchorAlias)
                diagnostics.Add(Error("SKILL-FRONTMATTER", $"'{key}' uses an alias, which is not allowed in the frontmatter.", key));

            if (key == "metadata")
            {
                ReadMetadata(parser, valueEvent, diagnostics, metadata);
            }
            else if (StandardKeys.Contains(key))
            {
                if (valueEvent is Scalar scalar && !HasAnchorOrTag(scalar))
                    fields[key] = scalar.Value.Trim();
                else
                {
                    diagnostics.Add(Error("SKILL-FRONTMATTER", $"'{key}' must be a single string.", key));
                    SkipNode(parser, valueEvent);
                }
            }
            else
            {
                unknown.Add(key);
                // The value is dropped, or ignored when the caller acknowledges the key. Anchors and tags are refused here too.
                var clean = SkipNode(parser, valueEvent);
                if (!clean && valueEvent is not AnchorAlias)
                    diagnostics.Add(Error("SKILL-FRONTMATTER", $"'{key}' uses an anchor or a tag, which are not allowed in the frontmatter.", key));
            }
        }

        while (parser.MoveNext())
        {
            if (parser.Current is DocumentStart)
                diagnostics.Add(Error("SKILL-FRONTMATTER", "The frontmatter must be a single YAML document."));
        }
    }

    private static void ReadMetadata(Parser parser, ParsingEvent valueEvent, List<StandardDiagnostic> diagnostics, Dictionary<string, string> metadata)
    {
        if (valueEvent is not MappingStart metadataStart)
        {
            diagnostics.Add(Error("SKILL-FRONTMATTER", "'metadata' must be a mapping of string keys to string values.", "metadata"));
            SkipNode(parser, valueEvent);
            return;
        }
        if (HasAnchorOrTag(metadataStart))
            diagnostics.Add(Error("SKILL-FRONTMATTER", "'metadata' uses an anchor or a tag, which are not allowed in the frontmatter.", "metadata"));
        while (true)
        {
            var keyEvent = Next(parser);
            if (keyEvent is MappingEnd)
                return;
            var entryValue = Next(parser);
            if (keyEvent is not Scalar key || entryValue is not Scalar value || HasAnchorOrTag(key) || HasAnchorOrTag(value))
            {
                diagnostics.Add(Error("SKILL-FRONTMATTER", "'metadata' must map string keys to string values, with no nesting.", "metadata"));
                SkipNode(parser, keyEvent);
                SkipNode(parser, entryValue);
                continue;
            }
            if (!metadata.TryAdd(key.Value, value.Value.Trim()))
                diagnostics.Add(Error("SKILL-FRONTMATTER", $"'metadata' repeats the key '{key.Value}'.", "metadata"));
        }
    }

    /// <summary>Skips a node. Returns false if the node, or anything inside it, has an anchor or a tag, or is an alias.</summary>
    private static bool SkipNode(Parser parser, ParsingEvent start)
    {
        var clean = !IsAnchorOrTag(start);
        if (start is not (SequenceStart or MappingStart))
            return clean;
        var depth = 1;
        while (depth > 0)
        {
            var next = Next(parser);
            clean &= !IsAnchorOrTag(next);
            if (next is SequenceStart or MappingStart)
                depth++;
            else if (next is SequenceEnd or MappingEnd)
                depth--;
        }
        return clean;
    }

    private static bool IsAnchorOrTag(ParsingEvent e) => e is AnchorAlias || (e is NodeEvent node && HasAnchorOrTag(node));

    private static ParsingEvent Next(Parser parser)
    {
        if (!parser.MoveNext())
            throw new InvalidOperationException("The YAML event stream ended early.");
        return parser.Current!;
    }

    private static bool HasAnchorOrTag(NodeEvent node) => !node.Anchor.IsEmpty || !node.Tag.IsEmpty;

    private static bool IsSaveable(Rune rune) =>
        (rune.Value >= 'a' && rune.Value <= 'z') || (rune.Value >= '0' && rune.Value <= '9') || rune.Value == '-';

    private static string? Field(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out var value) ? value : null;

    private static StandardSkillFile Empty(string body, List<StandardDiagnostic> diagnostics) =>
        new(null, null, null, null, new Dictionary<string, string>(StringComparer.Ordinal), null, body, diagnostics);

    private static StandardDiagnostic Error(string code, string message, string? key = null, string? suggestion = null) =>
        new(code, message, true, key, suggestion);
}
