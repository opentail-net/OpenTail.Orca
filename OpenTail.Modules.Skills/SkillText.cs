using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTail.Modules.Skills;

/// <summary>
/// Hashing for policies and skills: SHA-256 over normalised text (line endings, runs of spaces and tabs, trailing space, blank lines,
/// Unicode NFC), written <c>sha256:&lt;hex&gt;</c>. A cosmetic edit does not change the hash; a change of wording does. The skills
/// workbench uses the same function, so a hash recorded there is checked here.
/// </summary>
public static partial class SkillText
{
    public static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(text))));

    /// <summary>The hash of what a skill version says: its metadata without the recorded policy hashes, and its instructions. The workbench
    /// records it for every run and writes it into its reports' binding line, so <c>skills.publish</c> can check that a report describes
    /// the content proposed.</summary>
    public static string ContentHash(SkillVersion s) => Hash(string.Join("\n\n", [
        s.Id, s.Version, s.Summary, string.Join("\n", s.AppliesTo), string.Join("\n", s.Tools), string.Join("\n", s.Policies.Select(p => p.Ref)), s.Owner,
        s.Instructions]));

    /// <summary>The line a report carries to say which content it describes: <c>skill-report: invoice-check@5 sha256:…</c>.</summary>
    public static string BindingLine(SkillVersion s) => $"skill-report: {s.Id}@{s.Version} {ContentHash(s)}";

    /// <summary>The v2 prefix. Values written under v1 (<c>sha256:</c>) stay valid and are never rewritten (content-hashing plan, design item 1).</summary>
    public const string HashV2Prefix = "sha256v2:";

    /// <summary>The v2 canonical text (design item 2): NFC, line endings to LF, and the lines that are empty or hold only white space removed at the
    /// start and at the end (which removes a final newline). Indentation, inner spacing, trailing spaces and blank lines inside the text are content.</summary>
    public static string CanonicalV2(string text)
    {
        var lines = text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var first = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (first < 0) return "";
        var last = lines.FindLastIndex(l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", lines.GetRange(first, last - first + 1));
    }

    /// <summary>The v2 hash of one text: <c>sha256v2:</c> and the lowercase hex of SHA-256 over the UTF-8 bytes of its canonical text.</summary>
    public static string HashV2(string text) => HashV2Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalV2(text))));

    /// <summary>
    /// The v2 identity encoding of a version (design item 3): the schema tag, then each field in a fixed order. Every value is canonicalised on its own,
    /// then written as its UTF-8 byte length, a colon and its bytes, so no value can run into the next. A list is its item count and a semicolon, then
    /// its items the same way. An optional value has a presence marker: <c>0</c> when absent, <c>1</c> and its encoding when present. The owner is
    /// absent when <c>skill.json</c> has no <c>owner</c> key, which the store keeps apart from an empty owner. The preserved frontmatter and the
    /// resources are not stored yet, so they are absent or empty; they are in the list now, so the list does not change when they are stored
    /// (O-H2 and design item 10, recommended defaults, taken provisionally).
    /// </summary>
    public static string IdentityEncodingV2(SkillVersion s)
    {
        var meta = JsonNode.Parse(s.MetadataJson) as JsonObject;
        var sb = new StringBuilder("skill-identity/2\n");
        Value(sb, s.Id);
        Value(sb, s.Version);
        Value(sb, s.Summary);
        List(sb, s.AppliesTo);
        List(sb, s.Tools);
        // Policy references, not their recorded hashes: as in v1, a recorded hash is not content.
        List(sb, s.Policies.Select(p => p.Ref));
        if (meta?["owner"] is not null) { sb.Append('1'); Value(sb, s.Owner); }
        else sb.Append('0');
        Value(sb, s.Instructions);
        sb.Append('0');      // license (preserved frontmatter, not stored yet)
        sb.Append('0');      // compatibility
        sb.Append('0');      // metadata map
        sb.Append('0');      // allowed-tools
        List(sb, []);        // resources (design item 10): none yet
        return sb.ToString();
    }

    /// <summary>The v2 identity of a version: <c>sha256v2:</c> and the lowercase hex of SHA-256 over <see cref="IdentityEncodingV2"/>.</summary>
    public static string ContentHashV2(SkillVersion s) => HashV2Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityEncodingV2(s))));

    /// <summary>The v2 binding line, for reports written under v2. Not written by the Host yet (the switch is O-H5).</summary>
    public static string BindingLineV2(SkillVersion s) => $"skill-report: {s.Id}@{s.Version} {ContentHashV2(s)}";

    /// <summary>The identity to compare a recorded value with: the algorithm its prefix names (a report or a policy hash written before the switch is
    /// checked under v1, so it still verifies).</summary>
    public static string ContentHashLike(SkillVersion s, string recorded) => recorded.StartsWith(HashV2Prefix, StringComparison.Ordinal) ? ContentHashV2(s) : ContentHash(s);

    /// <summary>A policy text's hash under the algorithm its recorded value names.</summary>
    public static string HashLike(string text, string recorded) => recorded.StartsWith(HashV2Prefix, StringComparison.Ordinal) ? HashV2(text) : Hash(text);

    /// <summary>Whether republishing changes nothing (content-hashing plan, design item 6): the instructions by their v2 canonical text, and the
    /// metadata by its parsed JSON value, so formatting is not content and string values are compared exactly.</summary>
    public static bool SameContent(string storedInstructions, string storedMetadataJson, string instructions, string metadataJson) =>
        CanonicalV2(storedInstructions) == CanonicalV2(instructions) && JsonNode.DeepEquals(JsonNode.Parse(storedMetadataJson), JsonNode.Parse(metadataJson));

    private static void Value(StringBuilder sb, string value)
    {
        var c = CanonicalV2(value);
        sb.Append(Encoding.UTF8.GetByteCount(c)).Append(':').Append(c);
    }

    private static void List(StringBuilder sb, IEnumerable<string> items)
    {
        var values = items.ToList();
        sb.Append(values.Count).Append(';');
        foreach (var v in values) Value(sb, v);
    }

    /// <summary>The bindings a report carries (skill id, version, content hash), from its first 40 lines.</summary>
    public static IReadOnlyList<(string Id, string Version, string Hash)> Bindings(string report) =>
        report.Split('\n').Take(40).Select(l => BindingPattern().Match(l)).Where(m => m.Success)
            .Select(m => (m.Groups["id"].Value, m.Groups["v"].Value, m.Groups["h"].Value)).ToList();

    [System.Text.RegularExpressions.GeneratedRegex(@"skill-report:\s*(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)@(?<v>[1-9][0-9]{0,8})\s+(?<h>sha256(?:v2)?:[0-9a-f]{64})")]
    private static partial System.Text.RegularExpressions.Regex BindingPattern();

    public static string Normalise(string text)
    {
        var lines = text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim());
        var sb = new StringBuilder();
        var blank = true;
        foreach (var line in lines)
        {
            if (line.Length == 0) { blank = true; continue; }
            if (sb.Length > 0) sb.Append(blank ? "\n\n" : "\n");
            sb.Append(line);
            blank = false;
        }
        return sb.ToString();
    }

    /// <summary>A line diff (longest common subsequence), as "  same", "- removed", "+ added" lines. For skill files, which are short.</summary>
    public static IReadOnlyList<string> Diff(string before, string after, int maxLines = 2000)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        if (a.Length > maxLines || b.Length > maxLines) return ["(too long to diff: " + a.Length + " and " + b.Length + " lines)"];
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var diff = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { diff.Add("  " + a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) diff.Add("- " + a[x++]);
            else diff.Add("+ " + b[y++]);
        }
        while (x < a.Length) diff.Add("- " + a[x++]);
        while (y < b.Length) diff.Add("+ " + b[y++]);
        return diff;
    }

    [GeneratedRegex("[ \t]+")]
    private static partial Regex Spaces();
}
