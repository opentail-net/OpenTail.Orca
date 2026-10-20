using OpenTail.Host;

namespace OpenTail.Host.Admin;

/// <summary>Shared handling of <c>--modules &lt;file&gt;</c> for both runners and the policy commands.</summary>
public static class ModulesCli
{
    /// <summary>Loads <c>--modules</c> (if given) into the builder. Returns what was loaded, or null when there is no modules file.</summary>
    public static async Task<ModuleLoadResult?> ApplyAsync(OpenTailHostBuilder builder, string[] args, ModuleLoadMode mode, TextWriter? warnings = null,
        CancellationToken ct = default)
    {
        var path = ValueOf(args, "--modules");
        if (path is null) return null;
        var loaded = await ModuleLoader.LoadAsync(ModulesConfig.Load(path), mode, warn: m => warnings?.WriteLine("warning: " + m), ct: ct);
        builder.AddModules(loaded);
        return loaded;
    }

    /// <summary>One line per module for the startup log: what runs, at which version, reaching which hosts with which credentials.</summary>
    public static IEnumerable<string> Describe(OpenTailHost host)
    {
        foreach (var m in host.Catalog.Modules)
        {
            var risks = m.Tools.Select(t => host.Catalog.TryGet(t, out var r) ? r.Descriptor.Risk.ToString() : "?")
                .GroupBy(r => r).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
            var line = $"module {m.Id} ({(m.Options.FactoryType ?? "code")}, {m.Assembly} {m.Version}, contract {m.ContractVersion}): {m.Tools.Count} tool(s) [{string.Join(", ", risks)}]";
            if (m.Manifest.NetworkHosts.Count > 0) line += $"; declares hosts {string.Join(", ", m.Manifest.NetworkHosts)}";
            if (m.Manifest.Credentials.Count > 0) line += $"; credentials {string.Join(", ", m.Manifest.Credentials.Select(c => c.Name))}";
            if (!m.Options.AllowDefaultRead) line += "; Read tools need explicit policy entries";
            if (m.Options.Isolated) line += "; runs in its own process";
            yield return line;
        }
        foreach (var id in host.RetiredModules) yield return $"module {id}: switched off (pending actions expire, promotions cleared)";
    }

    internal static string? ValueOf(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
