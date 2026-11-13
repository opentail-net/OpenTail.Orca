using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTail.Orca;

/// <summary>Warnings about a policy for the modules actually loaded. Nothing here changes behaviour; it tells the operator what will happen.</summary>
public static class PolicyLint
{
    public static IReadOnlyList<string> Run(OpenTailHost host)
    {
        var warnings = new List<string>(host.StartupWarnings);
        var cfg = host.Policy.Config;
        foreach (var m in host.Catalog.Modules)
        {
            var tools = m.Tools.Select(t => host.Catalog.TryGet(t, out var r) ? r : null).Where(r => r is not null).Select(r => r!).ToList();
            var readsUntrusted = tools.Any(t => t.Descriptor.ReadsUntrusted);

            var unlisted = tools.Where(t => !cfg.Tools.ContainsKey(t.Descriptor.Name)).ToList();
            var defaultRead = unlisted.Where(t => t.Descriptor.Risk == ToolRisk.Read && m.Options.AllowDefaultRead && cfg.DefaultReadMode != ToolMode.Deny).ToList();
            var denied = unlisted.Except(defaultRead).ToList();
            if (denied.Count > 0)
                warnings.Add($"Module '{m.Id}': {denied.Count} tool(s) have no policy entry and are denied (not offered to the model): {string.Join(", ", denied.Select(t => t.Descriptor.Name))}.");
            if (defaultRead.Count > 0)
                warnings.Add($"Module '{m.Id}': {defaultRead.Count} Read tool(s) have no entry and follow DefaultReadMode={cfg.DefaultReadMode}: {string.Join(", ", defaultRead.Select(t => t.Descriptor.Name))}. The module labelled them Read itself.");

            foreach (var t in tools)
            {
                if (!cfg.Tools.TryGetValue(t.Descriptor.Name, out var p)) continue;
                var executes = p.Mode == ToolMode.Execute || (p.Rules ?? []).Any(r => r.Mode == ToolMode.Execute);
                if (!executes) continue;
                if (t.Descriptor.Risk != ToolRisk.Read && p.Mode == ToolMode.Execute)
                    warnings.Add($"Tool '{t.Descriptor.Name}' ({t.Descriptor.Risk}) runs without review for every caller (Mode Execute).");
                // Taint holds Destructive and ExternalEffect tools only; a Write tool with Execute in a module that reads outside content
                // can be driven by an injected instruction with nobody looking.
                if (t.Descriptor.Risk == ToolRisk.Write && readsUntrusted)
                    warnings.Add($"Tool '{t.Descriptor.Name}' is Write risk and can execute without review, and module '{m.Id}' returns outside content. " +
                                 "Taint does not hold Write tools, so text an outsider wrote could trigger it. Queue it, or make sure its effect stays inside the deployment.");
            }
        }
        return warnings;
    }
}

/// <summary>What <c>--adopt-policy</c> accepted from a module's suggested policy, and why it refused anything it refused.</summary>
public sealed record AdoptionResult(string ModuleId, Dictionary<string, ToolPolicy> Tools, string Json, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

/// <summary>
/// A module's suggested policy can only narrow or fill in, never widen: only <c>Tools</c> entries, only for that module's own tools as
/// actually loaded, and <c>Execute</c> on anything but a Read tool only when the operator asks for it explicitly.
/// </summary>
public static class PolicyAdoption
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions CompactOptions = new(WriteOptions) { WriteIndented = false };

    public static AdoptionResult Check(ToolCatalog catalog, string moduleId, bool allowExecute)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var module = catalog.Modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null) return new AdoptionResult(moduleId, [], "", [$"Module '{moduleId}' is not loaded."], notes);
        var json = module.Manifest.SuggestedPolicyJson;
        if (string.IsNullOrWhiteSpace(json)) return new AdoptionResult(moduleId, [], "", [$"Module '{moduleId}' does not suggest a policy."], notes);

        Dictionary<string, ToolPolicy> tools;
        try { tools = PolicyConfig.ParseFragment(json, moduleId, $"suggested policy of '{moduleId}'"); }
        catch (InvalidOperationException ex) { return new AdoptionResult(moduleId, [], "", [ex.Message], notes); }

        foreach (var (name, p) in tools)
        {
            if (!module.Tools.Contains(name)) { errors.Add($"'{name}' is not a tool this module registered."); continue; }
            catalog.TryGet(name, out var reg);
            var risk = reg.Descriptor.Risk;
            var executeRules = (p.Rules ?? []).Where(r => r.Mode == ToolMode.Execute).Select(r => r.Name).ToList();
            if (risk != ToolRisk.Read && (p.Mode == ToolMode.Execute || executeRules.Count > 0))
            {
                var what = p.Mode == ToolMode.Execute ? "Mode Execute" : $"Execute rule(s) {string.Join(", ", executeRules)}";
                if (allowExecute) notes.Add($"'{name}' ({risk}): {what} accepted because --allow-execute was given.");
                else errors.Add($"'{name}' ({risk}): {what} would run it without review. Not adopted; pass --allow-execute after reading it.");
            }
        }

        var probe = new PolicyConfig { Tools = tools };
        errors.AddRange(probe.Validate(catalog.All.Select(t => t.Descriptor.Name)));
        var text = JsonSerializer.Serialize(new { Tools = tools.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value) }, WriteOptions);
        return new AdoptionResult(moduleId, tools, text, errors, notes);
    }

    /// <summary>Line-by-line summary of what adopting would change against an existing fragment.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, ToolPolicy> before, IReadOnlyDictionary<string, ToolPolicy> after)
    {
        var lines = new List<string>();
        string Show(ToolPolicy p) => JsonSerializer.Serialize(p, CompactOptions);
        foreach (var k in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(k, out var b);
            var has = after.TryGetValue(k, out var a);
            if (had && !has) lines.Add($"- {k}: {Show(b!)}");
            else if (!had && has) lines.Add($"+ {k}: {Show(a!)}");
            else if (Show(b!) != Show(a!)) { lines.Add($"- {k}: {Show(b!)}"); lines.Add($"+ {k}: {Show(a!)}"); }
        }
        return lines;
    }
}

/// <summary>One expected decision in a policy test file (<c>--test-policy cases.json</c>).</summary>
public sealed class PolicyTestCase
{
    public string? Name { get; init; }
    public string Tool { get; init; } = "";
    public JsonElement Args { get; init; }
    public bool Tainted { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Requester { get; init; }
    public ToolMode Expect { get; init; }
}

public sealed record PolicyTestOutcome(PolicyTestCase Case, ToolMode Actual, string Rule, bool Passed);

public static class PolicyTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<PolicyTestCase> Parse(string json) =>
        JsonSerializer.Deserialize<List<PolicyTestCase>>(json, Options) ?? [];

    /// <summary>Budgets are not counted (nothing is stored); time rules use <see cref="PolicyTestCase.At"/> when given.</summary>
    public static IReadOnlyList<PolicyTestOutcome> Run(OpenTailHost host, IEnumerable<PolicyTestCase> cases) =>
        cases.Select(c =>
        {
            var args = c.Args.ValueKind == JsonValueKind.Undefined ? "{}" : c.Args.GetRawText();
            var d = host.Policy.Simulate(host.Catalog, c.Tool, args, c.Tainted, c.At, c.Requester);
            return new PolicyTestOutcome(c, d.Mode, d.Rule, d.Mode == c.Expect);
        }).ToList();
}
