using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>
/// An agent profile: the tools one agent identity may call. Bound to the credential (an API key's <c>Profile</c>), never chosen by the
/// agent. A profile only narrows: a tool in it is still Execute, Queue or Deny by policy, a tool outside it is not advertised to
/// that caller and is refused (and audited) if called by name.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Tool names, <c>module.*</c> for every tool whose name starts with <c>module.</c>, or <c>*</c> for every tool.</summary>
    public List<string> Tools { get; init; } = [];

    /// <summary>Tools the agent may ask a person for, for one task (<c>access.request</c>): exact names, never administration tools.</summary>
    public List<string> Requestable { get; init; } = [];

    /// <summary>Agent profiles this agent may hand work to (<c>work.delegate</c>). Empty: it delegates to nobody.</summary>
    public List<string> MayDelegateTo { get; init; } = [];
}

public static class AgentProfiles
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>True when an entry of the profile names the tool, its module (<c>module.*</c>) or everything (<c>*</c>). An administration
    /// tool is granted only by its exact name: no wildcard includes it.</summary>
    public static bool Grants(AgentProfile profile, ToolDescriptor tool, bool administers = false)
    {
        foreach (var entry in profile.Tools)
        {
            if (entry == tool.Name) return true;
            if (administers) continue;
            if (entry == "*") return true;
            if (entry.EndsWith(".*", StringComparison.Ordinal) && tool.Name.StartsWith(entry[..^1], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The module an entry belongs to (<c>mail</c> for <c>mail.send</c> and <c>mail.*</c>), or null for <c>*</c>.</summary>
    internal static string? ModuleOf(string entry)
    {
        if (entry == "*") return null;
        var dot = entry.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? entry[..dot] : entry;
    }

    internal static void Validate(IReadOnlyDictionary<string, AgentProfile> agents, IReadOnlySet<string> knownTools,
        IReadOnlySet<string> retiredModules, List<string> errors)
    {
        var knownModules = knownTools.Select(ModuleOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, profile) in agents)
        {
            if (!IsValidName(name)) errors.Add($"Agent profile '{name}': the name must be 1-64 letters, digits, '.', '_' or '-'.");
            if (profile.Tools.Count == 0) errors.Add($"Agent profile '{name}' names no tools.");
            foreach (var entry in profile.Tools)
            {
                if (entry == "*") continue;
                var module = ModuleOf(entry);
                if (module is not null && retiredModules.Contains(module)) continue; // tolerated with a warning, like policy entries
                if (entry.EndsWith(".*", StringComparison.Ordinal))
                {
                    if (!knownModules.Contains(module!)) errors.Add($"Agent profile '{name}' names '{entry}', but no loaded tool's name starts with '{module}.'.");
                }
                else if (!knownTools.Contains(entry)) errors.Add($"Agent profile '{name}' names unknown tool '{entry}'.");
            }
        }
    }
}
