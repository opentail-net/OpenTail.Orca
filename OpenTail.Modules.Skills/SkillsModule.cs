using System.Text.Json;

namespace OpenTail.Modules.Skills;

public sealed class SkillsModuleOptions
{
    public string Id { get; init; } = "skills";
    public ModuleManifest? Manifest { get; init; }

    /// <summary>The store folder: one folder per skill, one per version, and labels.json.</summary>
    public required string Store { get; init; }

    /// <summary>The label <c>skills.find</c> and <c>skills.get</c> serve. <c>production</c> normally; <c>staging</c> for a test deployment.</summary>
    public string Label { get; init; } = "production";

    /// <summary>A folder holding the live policies skills refer to (by file name), so <c>skills.get</c> can warn when one changed since the
    /// skill was tested against it. Null: no drift check.</summary>
    public string? PolicyFolder { get; init; }

    /// <summary>Register <c>skills.publish</c>. Off for a deployment that only serves skills published elsewhere.</summary>
    public bool AllowPublish { get; init; } = true;
}

/// <summary>
/// <c>skills.find</c> and <c>skills.get</c> (Read) serve published skill versions from the Host's own store, never content fetched from
/// anywhere else: skill text is the business's trusted procedure, so it must not be possible to get arbitrary text into it.
/// <c>skills.publish</c> (ExternalEffect, previewed) adds a version and moves a label to it, which is also how a rollback is done.
/// Skills make the agent more or less inclined to call a tool; they never change what policy allows.
/// </summary>
public sealed class SkillsModule(SkillsModuleOptions options) : IHostModule
{
    private readonly SkillStore _store = new(options.Store);

    public string Id => options.Id;

    public ModuleManifest Manifest => options.Manifest ?? ModuleManifest.Minimal(Id);

    public void Register(IToolRegistry registry)
    {
        var find = $"{Id}.find";
        var get = $"{Id}.get";
        registry.Add(new ToolDescriptor(find, "List the business's published skills that may apply to a situation: id, version and summary.", ToolRisk.Read, Id),
            AIFunctionFactory.Create(Find, find, "List the business's published skills that may apply to a situation: id, version and summary."));
        registry.Add(new ToolDescriptor(get, "Read one published skill: its instructions, the policies it reads and its version. Follow it.", ToolRisk.Read, Id)
            { AuditResult = new ResultAudit("skill_served", "served") },
            AIFunctionFactory.Create(Get, get, "Read one published skill: its instructions, the policies it reads and its version. Follow it."));
        if (!options.AllowPublish) return;
        var publish = $"{Id}.publish";
        const string publishDescription = "Propose publishing a skill version: move the production or staging label to it (adding the version if new). " +
            "A person reviews the change before it takes effect. Rolling back is publishing an earlier version.";
        registry.Add(new ToolDescriptor(publish, publishDescription, ToolRisk.ExternalEffect, Id) { IdempotentByKey = true, Administers = true },
            AIFunctionFactory.Create(Publish, publish, publishDescription), new PublishPreview(_store));
    }

    private object Find([Description("The situation, in a few words (\"an invoice arrived by email\"). Omit to list every skill.")] string? situation = null)
    {
        var words = (situation ?? "").Split((char[])[' ', ',', '.', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2).ToList();
        var found = new List<(SkillVersion Skill, int Score)>();
        foreach (var (id, labels) in _store.ReadLabels())
        {
            if (!labels.TryGetValue(options.Label, out var version) || _store.Get(id, version) is not { } s) continue;
            var text = s.Summary + " " + string.Join(" ", s.AppliesTo) + " " + s.Id.Replace('-', ' ');
            found.Add((s, words.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))));
        }
        return new
        {
            label = options.Label,
            skills = found.OrderByDescending(f => f.Score).ThenBy(f => f.Skill.Id, StringComparer.Ordinal)
                .Select(f => new { id = f.Skill.Id, version = f.Skill.Version, summary = f.Skill.Summary, appliesTo = f.Skill.AppliesTo }),
            next = "Call skills.get with the id of the skill that matches, and follow its instructions.",
        };
    }

    private object Get([Description("The skill id from skills.find.")] string id)
    {
        var version = SkillStore.IsId(id) ? _store.LabelledVersion(id, options.Label) : null;
        var s = version is null ? null : _store.Get(id, version)
            ?? throw new InvalidOperationException($"The store lists {id}@{version} but its files are missing.");
        if (s is null) throw new ArgumentException($"No published skill '{id}' (label {options.Label}).");
        var warnings = new List<string>();
        foreach (var p in s.Policies)
        {
            if (options.PolicyFolder is null || p.Hash is null) continue;
            var file = Path.Combine(options.PolicyFolder, Path.GetFileName(p.Ref));
            if (!File.Exists(file)) { warnings.Add($"Policy {p.Ref} was not found where this deployment keeps policies."); continue; }
            if (SkillText.Hash(File.ReadAllText(file)) != p.Hash)
                warnings.Add($"This skill was tested against an older version of {p.Ref}. Follow the policy as it is now, and tell the user the skill may be out of date.");
        }
        return new
        {
            served = $"{s.Id}@{s.Version}",
            id = s.Id,
            version = s.Version,
            label = options.Label,
            summary = s.Summary,
            appliesTo = s.AppliesTo,
            tools = s.Tools,
            policies = s.Policies.Select(p => p.Ref),
            policyWarnings = warnings,
            instructions = s.Instructions,
        };
    }

    private object Publish(
        [Description("The skill id.")] string skillId,
        [Description("The version to publish (a whole number).")] string version,
        [Description("Why: what changed and what the scenarios showed. The reviewer reads this first.")] string reason,
        [Description("production or staging. Default production.")] string label = "production",
        [Description("skill.json of a new version. Omit when the version is already in the store (a rollback).")] string? skillJson = null,
        [Description("SKILL.md of a new version. Omit when the version is already in the store.")] string? instructions = null,
        [Description("The workbench's scenario report for this version, in Markdown, for the reviewer.")] string? report = null,
        [Description("The version the label points at now, or \"none\". The publish is refused if the label has moved since.")] string? expectedFrom = null)
    {
        // The preview already refused a missing expectedFrom; check again here, at the moment it runs.
        var from = _store.LabelledVersion(skillId, label);
        _store.Publish(skillId, version, label, skillJson, instructions, expectedFrom ?? throw new ArgumentException("expectedFrom is required."));
        return new { published = $"{skillId}@{version}", label, previous = from };
    }

    /// <summary>The skill change request: what moves, the diff against the version it replaces, the reason and the scenario report.</summary>
    private sealed class PublishPreview(SkillStore store) : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var r = doc.RootElement;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = S("skillId") ?? throw new ArgumentException("skillId is required.");
            var version = S("version") ?? throw new ArgumentException("version is required.");
            var label = S("label") ?? "production";
            var reason = S("reason");
            if (!SkillStore.IsId(id)) throw new ArgumentException($"'{id}' is not a skill id.");
            if (!SkillStore.IsVersion(version)) throw new ArgumentException($"'{version}' is not a version number.");
            if (!SkillStore.Labels.Contains(label)) throw new ArgumentException($"Label must be one of {string.Join(", ", SkillStore.Labels)}.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required: the reviewer reads it first.");
            var expectedFrom = S("expectedFrom") ?? throw new ArgumentException(
                $"expectedFrom is required: the version the '{label}' label points at now, or \"{SkillStore.NoVersion}\" if none.");

            var (json, md) = (S("skillJson"), S("instructions"));
            var existing = store.Get(id, version);
            SkillVersion target;
            if (json is not null || md is not null)
            {
                if (json is null || md is null) throw new ArgumentException("A new version needs both skillJson and instructions.");
                target = SkillStore.Parse(json, md, id, version);
                if (existing is not null && SkillText.Normalise(existing.Instructions) != SkillText.Normalise(md))
                    throw new ArgumentException($"{id} version {version} is already published with different content; use a new version number.");
            }
            else target = existing ?? throw new ArgumentException($"{id} has no published version {version}; send skillJson and instructions to add it.");

            var fromVersion = store.LabelledVersion(id, label);
            if ((fromVersion ?? SkillStore.NoVersion) != expectedFrom)
                throw new ArgumentException($"The '{label}' label of {id} is at {fromVersion ?? SkillStore.NoVersion}, not {expectedFrom}: someone published since. " +
                                            "Check what changed and propose again.");
            var from = fromVersion is null ? null : store.Get(id, fromVersion);
            var (reportText, reportCheck) = CheckReport(S("report"), target);
            var kind = fromVersion == version ? "no change" : from is null ? "first publication"
                : int.Parse(version, System.Globalization.CultureInfo.InvariantCulture) < int.Parse(fromVersion!, System.Globalization.CultureInfo.InvariantCulture) ? "rollback" : "update";
            var summary = $"Skill {id}: move '{label}' from {(fromVersion is null ? "nothing" : "version " + fromVersion)} to version {version} ({kind}" +
                (existing is null ? ", new version" : "") + $"). Reason: {reason}";
            var diff = JsonSerializer.Serialize(new
            {
                skill = id,
                label,
                from = fromVersion,
                to = version,
                change = kind,
                newVersion = existing is null,
                reason,
                instructionsDiff = SkillText.Diff(from?.Instructions ?? "", target.Instructions).Where(l => !l.StartsWith("  ", StringComparison.Ordinal) || from is null).Take(400),
                policies = target.Policies.Select(p => new { p.Ref, testedHash = p.Hash }),
                policiesChanged = from is null ? Array.Empty<string>() : target.Policies.Where(p => from.Policies.All(o => o.Ref != p.Ref || o.Hash != p.Hash)).Select(p => p.Ref).ToArray(),
                reportCheck,
                report = reportText,
            });
            return Task.FromResult(new PreviewResult(summary, diff));
        }

        /// <summary>A report must not describe other content than the one proposed: a binding line for this skill must match its id,
        /// version and content hash, or the proposal is refused. A report without one is shown, labelled as unchecked.</summary>
        private static (string Text, string Check) CheckReport(string? report, SkillVersion target)
        {
            if (report is null) return ("(no scenario report attached)", "No report attached.");
            var text = report.Length > 20_000 ? report[..20_000] + "\n(cut at 20,000 characters)" : report;
            var mine = SkillText.Bindings(report).Where(b => b.Id == target.Id).ToList();
            if (mine.Count == 0)
                return (text, "Not bound: the report names no content for this skill. It was supplied by the requester and the Host did not check it.");
            var expected = SkillText.ContentHash(target);
            if (mine.Any(b => b.Version != target.Version || b.Hash != expected))
                throw new ArgumentException($"The report describes other content than {target.Id} version {target.Version} as proposed " +
                                            $"(expected {SkillText.BindingLine(target)}). Attach the report of this version's runs.");
            return (text, $"Bound: the report states {target.Id}@{target.Version} with the proposed content's hash. Supplied by the requester; " +
                          "the Host checked which content it describes, not its numbers.");
        }
    }
}
