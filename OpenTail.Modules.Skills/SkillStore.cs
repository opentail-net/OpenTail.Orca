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

    /// <summary>A problem found while reading labels.json: the skill and label it concerns (both null for the whole file), and a message naming them.</summary>
    public sealed record LabelProblem(string? Skill, string? Label, string Message);

    /// <summary>What a read of labels.json found: the valid labels by skill id, and a problem for each damaged part.</summary>
    public sealed record LabelsRead(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels, IReadOnlyList<LabelProblem> Problems);

    /// <summary>Reads labels.json without throwing for its content. Each damage becomes a problem that names the file, the skill or the label, and
    /// the valid labels are kept. A file that is not JSON, whose root or <c>skills</c> is not an object, or that cannot be read, gives no labels.</summary>
    public LabelsRead ReadLabelsChecked()
    {
        var empty = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        LabelsRead WholeFile(string message) => new(empty, [new LabelProblem(null, null, message)]);
        var path = Path.Combine(Root, "labels.json");
        if (!File.Exists(path)) return new LabelsRead(empty, []);
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return WholeFile($"labels.json cannot be read, so no label is served from it: {ex.Message}");
        }
        if (node is not JsonObject root) return WholeFile("labels.json is not a JSON object, so no label is served from it.");
        if (!root.TryGetPropertyValue("skills", out var skills)) return new LabelsRead(empty, []);
        if (skills is not JsonObject skillsObject) return WholeFile("labels.json's 'skills' is not an object, so no label is served from it.");

        var labels = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var problems = new List<LabelProblem>();
        foreach (var (id, entry) in skillsObject)
        {
            if (!IsId(id))
            {
                problems.Add(new LabelProblem(id, null, $"labels.json names '{id}', which is not a skill id; it is not served."));
                continue;
            }
            if (entry is not JsonObject forSkill)
            {
                problems.Add(new LabelProblem(id, null, $"labels.json's entry for {id} is not an object; {id} is not served."));
                continue;
            }
            var valid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (label, value) in forSkill)
            {
                if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                {
                    problems.Add(new LabelProblem(id, label, $"labels.json's '{label}' label for {id} is {(value is null ? "null" : "not a version text")}; it is not served."));
                    continue;
                }
                var version = v.GetValue<string>();
                if (!IsVersion(version))
                {
                    problems.Add(new LabelProblem(id, label, $"labels.json's '{label}' label for {id} is '{version}', not a version number; it is not served."));
                    continue;
                }
                valid[label] = version;
            }
            if (valid.Count > 0) labels[id] = valid;
        }
        return new LabelsRead(labels, problems);
    }

    /// <summary>The version a label points at, or null. Refused with an <see cref="ArgumentException"/> that names the damage when labels.json,
    /// or this skill's entry, is damaged: a read tool names the problem rather than guessing. Other skills' damage does not block it.</summary>
    public string? LabelledVersion(string id, string label)
    {
        var read = ReadLabelsChecked();
        if (read.Problems.FirstOrDefault(p => p.Skill is null || p.Skill == id) is { } problem)
            throw new ArgumentException(problem.Message);
        return read.Labels.TryGetValue(id, out var l) && l.TryGetValue(label, out var v) ? v : null;
    }

    public SkillVersion? Get(string id, string version)
    {
        if (!IsId(id) || !IsVersion(version)) return null;
        var dir = Path.Combine(Root, id, version);
        var meta = Path.Combine(dir, "skill.json");
        var md = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(meta) || !File.Exists(md)) return null;
        // A file that is locked or cannot be read is refused by name, as an ArgumentException, like a damaged file.
        try { return Parse(File.ReadAllText(meta), File.ReadAllText(md), id, version); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArgumentException(ex.Message, ex); }
    }

    /// <summary>Checks a version's files: the metadata must parse and name this id and version. Throws ArgumentException.</summary>
    public static SkillVersion Parse(string metadataJson, string instructions, string id, string version)
    {
        JsonObject o;
        try { o = JsonNode.Parse(metadataJson) as JsonObject ?? throw new ArgumentException("skill.json must be a JSON object."); }
        catch (JsonException ex) { throw new ArgumentException("skill.json is not valid JSON: " + ex.Message); }
        // Each field has the type the format gives it, and a value of another type is refused, not converted: 42 is not a summary.
        string Text(string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s
            ? s : throw new ArgumentException($"skill.json needs '{key}', as a non-empty string.");
        string Optional(string key) => o[key] switch
        {
            null => "",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            _ => throw new ArgumentException($"skill.json '{key}', when given, must be a string."),
        };
        IReadOnlyList<string> List(string key) => o[key] switch
        {
            null => [],
            JsonArray a => [.. a.Select(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>()
                : throw new ArgumentException($"skill.json '{key}' must be a list of strings."))],
            _ => throw new ArgumentException($"skill.json '{key}' must be a list of strings."),
        };
        static SkillPolicy PolicyOf(JsonNode? n)
        {
            if (n is not JsonObject po) throw new ArgumentException("Each skill policy must be an object with a 'ref'.");
            if (po["ref"] is not JsonValue r || r.GetValueKind() != JsonValueKind.String || r.GetValue<string>().Length == 0)
                throw new ArgumentException("A skill policy's 'ref' must be a non-empty string.");
            var hash = po["hash"];
            if (hash is not null && !(hash is JsonValue h && h.GetValueKind() == JsonValueKind.String))
                throw new ArgumentException("A skill policy's 'hash' must be a string, or null.");
            return new SkillPolicy(r.GetValue<string>(), hash?.GetValue<string>());
        }
        if (Text("id") != id) throw new ArgumentException($"skill.json names '{Text("id")}', not '{id}'.");
        if (Text("version") != version) throw new ArgumentException($"skill.json is version {Text("version")}, not {version}.");
        // The format is checked, not ignored: a file in a format this Host does not read is refused, at publish and at read.
        if (!(o["format"] is JsonValue f && f.TryGetValue(out int format) && format == SupportedFormat))
            throw new ArgumentException($"skill.json must say \"format\": {SupportedFormat}. This Host reads format {SupportedFormat} only.");
        if (string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("The instructions (SKILL.md) are empty.");
        var policies = o["policies"] switch
        {
            null => new List<SkillPolicy>(),
            JsonArray p => p.Select(PolicyOf).ToList(),
            _ => throw new ArgumentException("skill.json 'policies' must be a list of objects."),
        };
        return new SkillVersion(id, version, Text("summary"), List("appliesTo"), List("tools"), policies,
            Optional("owner"), instructions, metadataJson);
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
            // The labels file is read once, under the lock, before anything is written. A damaged file, or a damaged entry for this skill,
            // refuses the publish with the reason and is never replaced (store plan, design item 2).
            var labels = ReadLabelsForWrite(id);
            if (expectedFrom is not null && (LabelOf(labels, id, label) ?? NoVersion) != expectedFrom)
                throw new InvalidOperationException($"The '{label}' label of {id} moved since this was reviewed (it was expected at {expectedFrom}, " +
                                                    $"it is at {LabelOf(labels, id, label) ?? NoVersion}). Nothing was published: resubmit.");
            string? created = null; // the version folder this publish moved into place: removed again if the label write does not complete
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
                    // A folder already in place is never overwritten or repaired: it is refused and named (store plan, publish table).
                    if (Directory.Exists(dir))
                        throw new ArgumentException($"{id}@{version} has a folder that is not a complete version, so nothing was published. Remove or repair it by hand first.");
                    var tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        Directory.CreateDirectory(tmp);
                        File.WriteAllText(Path.Combine(tmp, "skill.json"), metadataJson);
                        File.WriteAllText(Path.Combine(tmp, "SKILL.md"), instructions);
                        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                        Directory.Move(tmp, dir);
                    }
                    catch
                    {
                        DeleteTree(tmp);
                        throw;
                    }
                    created = dir;
                }
            }
            else if (existing is null) throw new ArgumentException($"{id} has no published version {version}; send skillJson and instructions to add it.");

            var labelsPath = Path.Combine(Root, "labels.json");
            var skills = labels["skills"] as JsonObject ?? (JsonObject)(labels["skills"] = new JsonObject());
            var forSkill = skills[id] as JsonObject ?? (JsonObject)(skills[id] = new JsonObject());
            forSkill[label] = version;
            var tmpLabels = labelsPath + ".tmp";
            try
            {
                File.WriteAllText(tmpLabels, labels.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmpLabels, labelsPath, overwrite: true);
            }
            catch
            {
                // The label did not move, so the version this publish created is removed: a new version is never left unlabelled by a handled failure (store plan, guarantee 2).
                if (created is not null) DeleteTree(created);
                throw;
            }
            finally
            {
                if (File.Exists(tmpLabels)) File.Delete(tmpLabels);
            }
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

    /// <summary>The labels file, read for a write. A file that is not JSON, or whose root or <c>skills</c> is not an object, is refused; so is a
    /// damaged entry for <paramref name="id"/> (not an object, or a label that is not text). The refusal names the problem and nothing is written.
    /// Other skills' entries are not checked, and are kept exactly as they are when the file is written back.</summary>
    private JsonObject ReadLabelsForWrite(string id)
    {
        var path = Path.Combine(Root, "labels.json");
        if (!File.Exists(path)) return new JsonObject { ["format"] = 1 };
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new ArgumentException("labels.json is not valid JSON, so nothing was published. Repair it by hand first. " + ex.Message); }
        if (node is not JsonObject root) throw new ArgumentException("labels.json is not a JSON object, so nothing was published. Repair it by hand first.");
        if (root.TryGetPropertyValue("skills", out var skills))
        {
            if (skills is not JsonObject skillsObject) throw new ArgumentException("labels.json's 'skills' is not an object, so nothing was published. Repair it by hand first.");
            if (skillsObject.TryGetPropertyValue(id, out var entry))
            {
                if (entry is not JsonObject forSkill) throw new ArgumentException($"labels.json's entry for {id} is not an object, so nothing was published. Repair it by hand first.");
                foreach (var (name, value) in forSkill)
                    if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                        throw new ArgumentException($"labels.json's '{name}' label for {id} is not a version text, so nothing was published. Repair it by hand first.");
            }
        }
        return root;
    }

    /// <summary>Removes a folder that a publish created, if it still exists. Used only for folders this publish made itself.</summary>
    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>The version a label holds in a labels file read by <see cref="ReadLabelsForWrite"/>, or null when it has none.</summary>
    private static string? LabelOf(JsonObject labels, string id, string label) =>
        labels["skills"] is JsonObject skills && skills[id] is JsonObject forSkill && forSkill[label] is JsonValue v ? v.GetValue<string>() : null;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[1-9][0-9]{0,8}$")]
    private static partial Regex VersionPattern();
}
