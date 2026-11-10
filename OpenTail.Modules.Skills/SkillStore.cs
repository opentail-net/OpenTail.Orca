using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTail.Modules.Skills;

public sealed record SkillPolicy(string Ref, string? Hash);

/// <summary>One published version of a skill, as stored: <c>&lt;store&gt;/&lt;id&gt;/&lt;version&gt;/skill.json</c> and <c>SKILL.md</c>.</summary>
public sealed record SkillVersion(
    string Id, string Version, string Summary, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Tools,
    IReadOnlyList<SkillPolicy> Policies, string Owner, string Instructions, string MetadataJson);

/// <summary>
/// The Host's own skill store: every version a skill ever had, kept and never changed, and labels (<c>production</c>, <c>staging</c>)
/// that point at one version each in <c>labels.json</c>. Publishing moves a label; rolling back moves it back. Only
/// <c>skills.publish</c>, an approved action, writes here.
/// </summary>
public sealed partial class SkillStore(string root)
{
    public static readonly IReadOnlyList<string> Labels = ["production", "staging"];

    /// <summary>The skill.json format this Host reads and writes.</summary>
    public const int SupportedFormat = 1;

    /// <summary>Serialises publishes within one process. Across processes, <see cref="TakeStoreLock"/> does it.</summary>
    private static readonly object WriteLock = new();

    public string Root { get; } = root;

    public static bool IsId(string id) => IdPattern().IsMatch(id);
    public static bool IsVersion(string v) => VersionPattern().IsMatch(v);

    /// <summary>label → version, per skill id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadLabels()
    {
        var path = Path.Combine(Root, "labels.json");
        if (!File.Exists(path)) return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var node = JsonNode.Parse(File.ReadAllText(path))?["skills"] as JsonObject ?? [];
        return node.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)(kv.Value as JsonObject ?? []).ToDictionary(l => l.Key, l => l.Value!.ToString()),
            StringComparer.Ordinal);
    }

    public string? LabelledVersion(string id, string label) =>
        ReadLabels().TryGetValue(id, out var l) && l.TryGetValue(label, out var v) ? v : null;

    public SkillVersion? Get(string id, string version)
    {
        if (!IsId(id) || !IsVersion(version)) return null;
        var dir = Path.Combine(Root, id, version);
        var meta = Path.Combine(dir, "skill.json");
        var md = Path.Combine(dir, "SKILL.md");
        return File.Exists(meta) && File.Exists(md) ? Parse(File.ReadAllText(meta), File.ReadAllText(md), id, version) : null;
    }

    /// <summary>Checks a version's files: the metadata must parse and name this id and version. Throws ArgumentException.</summary>
    public static SkillVersion Parse(string metadataJson, string instructions, string id, string version)
    {
        JsonObject o;
        try { o = JsonNode.Parse(metadataJson) as JsonObject ?? throw new ArgumentException("skill.json must be a JSON object."); }
        catch (JsonException ex) { throw new ArgumentException("skill.json is not valid JSON: " + ex.Message); }
        string Text(string key) => o[key] is JsonValue v && v.ToString() is { Length: > 0 } s ? s : throw new ArgumentException($"skill.json needs '{key}'.");
        IReadOnlyList<string> List(string key) => o[key] is JsonArray a ? [.. a.Select(n => n?.ToString() ?? "")] : [];
        if (Text("id") != id) throw new ArgumentException($"skill.json names '{Text("id")}', not '{id}'.");
        if (Text("version") != version) throw new ArgumentException($"skill.json is version {Text("version")}, not {version}.");
        // The format is checked, not ignored: a file in a format this Host does not read is refused, at publish and at read.
        if (!(o["format"] is JsonValue f && f.TryGetValue(out int format) && format == SupportedFormat))
            throw new ArgumentException($"skill.json must say \"format\": {SupportedFormat}. This Host reads format {SupportedFormat} only.");
        if (string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("The instructions (SKILL.md) are empty.");
        var policies = o["policies"] is JsonArray p
            ? p.Select(n => new SkillPolicy(n?["ref"]?.ToString() ?? throw new ArgumentException("A policy needs 'ref'."), n?["hash"]?.ToString())).ToList()
            : [];
        return new SkillVersion(id, version, Text("summary"), List("appliesTo"), List("tools"), policies,
            o["owner"]?.ToString() ?? "", instructions, metadataJson);
    }

    /// <summary>The value of <c>expectedFrom</c> when a label points at no version yet.</summary>
    public const string NoVersion = "none";

    /// <summary>Writes a new version (its files never change after) and moves the label. Writing the same content again is a no-op;
    /// different content for an existing version is refused. With <paramref name="expectedFrom"/>, the label moves only if it still
    /// points there (<see cref="NoVersion"/>: nowhere), so an approval never applies to a label that moved after review.</summary>
    public void Publish(string id, string version, string label, string? metadataJson, string? instructions, string? expectedFrom = null)
    {
        if (!IsId(id) || !IsVersion(version) || !Labels.Contains(label)) throw new ArgumentException("Invalid skill id, version or label.");
        lock (WriteLock)
        {
            // Held from the expectedFrom check to the label write, so another process cannot move the label in between.
            using var held = TakeStoreLock();
            if (expectedFrom is not null && (LabelledVersion(id, label) ?? NoVersion) != expectedFrom)
                throw new InvalidOperationException($"The '{label}' label of {id} moved since this was reviewed (it was expected at {expectedFrom}, " +
                                                    $"it is at {LabelledVersion(id, label) ?? NoVersion}). Nothing was published: resubmit.");
            var existing = Get(id, version);
            if (metadataJson is not null || instructions is not null)
            {
                if (metadataJson is null || instructions is null) throw new ArgumentException("A new version needs both skillJson and instructions.");
                Parse(metadataJson, instructions, id, version);
                if (existing is not null)
                {
                    if (SkillText.Normalise(existing.Instructions) != SkillText.Normalise(instructions) ||
                        SkillText.Normalise(existing.MetadataJson) != SkillText.Normalise(metadataJson))
                        throw new ArgumentException($"{id} version {version} is already published with different content; use a new version number.");
                }
                else
                {
                    var dir = Path.Combine(Root, id, version);
                    var tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(tmp);
                    File.WriteAllText(Path.Combine(tmp, "skill.json"), metadataJson);
                    File.WriteAllText(Path.Combine(tmp, "SKILL.md"), instructions);
                    Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                    Directory.Move(tmp, dir);
                }
            }
            else if (existing is null) throw new ArgumentException($"{id} has no published version {version}; send skillJson and instructions to add it.");

            var labelsPath = Path.Combine(Root, "labels.json");
            var labels = File.Exists(labelsPath) ? JsonNode.Parse(File.ReadAllText(labelsPath)) as JsonObject ?? [] : new JsonObject { ["format"] = 1 };
            var skills = labels["skills"] as JsonObject ?? (JsonObject)(labels["skills"] = new JsonObject());
            var forSkill = skills[id] as JsonObject ?? (JsonObject)(skills[id] = new JsonObject());
            forSkill[label] = version;
            var tmpLabels = labelsPath + ".tmp";
            File.WriteAllText(tmpLabels, labels.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmpLabels, labelsPath, overwrite: true);
        }
    }

    /// <summary>
    /// Opens the store's lock file exclusively, for the length of a publish. A second Host process that publishes to the same folder while
    /// this one holds it is refused, rather than checking expectedFrom and then writing over the other's change. Nothing is written by a refused
    /// publish. The lock is advisory on Unix and mandatory on Windows; either way it is only held by Host processes that take it.
    /// </summary>
    private FileStream TakeStoreLock()
    {
        Directory.CreateDirectory(Root);
        try
        {
            return new FileStream(Path.Combine(Root, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The skill store is busy: another publish holds it, or its folder cannot be locked. " +
                                                "Nothing was published: resubmit.", ex);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[1-9][0-9]{0,8}$")]
    private static partial Regex VersionPattern();
}
