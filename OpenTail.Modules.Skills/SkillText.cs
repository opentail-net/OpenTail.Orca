using System.Security.Cryptography;
using System.Text;
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

    /// <summary>The bindings a report carries (skill id, version, content hash), from its first 40 lines.</summary>
    public static IReadOnlyList<(string Id, string Version, string Hash)> Bindings(string report) =>
        report.Split('\n').Take(40).Select(l => BindingPattern().Match(l)).Where(m => m.Success)
            .Select(m => (m.Groups["id"].Value, m.Groups["v"].Value, m.Groups["h"].Value)).ToList();

    [System.Text.RegularExpressions.GeneratedRegex(@"skill-report:\s*(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)@(?<v>[1-9][0-9]{0,8})\s+(?<h>sha256:[0-9a-f]{64})")]
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
