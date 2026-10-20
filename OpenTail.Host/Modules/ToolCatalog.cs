using System.Reflection;

namespace OpenTail.Host;

/// <summary>How the Host treats one loaded module.</summary>
public sealed record ModuleLoadOptions
{
    /// <summary>Read tools without a policy entry follow <c>DefaultReadMode</c>. True for modules added in code (the operator wrote
    /// that code); false by default for modules enabled from configuration, whose risk labels the operator has not checked.</summary>
    public bool AllowDefaultRead { get; init; } = true;

    /// <summary>Every tool name must start with <c>&lt;module id&gt;.</c> (always true for modules from configuration, so a suggested
    /// policy or a policy fragment for one module can never name another module's tools).</summary>
    public bool RequireToolPrefix { get; init; }

    /// <summary>The module was built from <c>modules.json</c> by a factory of this type.</summary>
    public string? FactoryType { get; init; }

    /// <summary>The module's code runs in its own process (<c>"Isolation": { "Mode": "Process" }</c>).</summary>
    public bool Isolated { get; init; }

    /// <summary>Data labels the operator puts on every Read tool of this module instance (<c>"Labels": ["finance"]</c> in modules.json):
    /// what this database or folder holds. Added to the module's own labels, never replacing them.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public static ModuleLoadOptions Code { get; } = new();
}

/// <summary>A module the Host loaded, as operators see it.</summary>
public sealed record LoadedModule(
    string Id,
    ModuleManifest Manifest,
    string Assembly,
    string Version,
    int ContractVersion,
    ModuleLoadOptions Options,
    IReadOnlyList<string> Tools);

/// <summary>All tools from all loaded modules, sorted by name (deterministic tools/list).</summary>
public sealed class ToolCatalog : IToolRegistry
{
    private readonly SortedDictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, LoadedModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedModule> _moduleOfTool = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ToolRegistration> All => _tools.Values;

    public IReadOnlyCollection<LoadedModule> Modules => _modules.Values;

    public bool TryGet(string name, out ToolRegistration registration) => _tools.TryGetValue(name, out registration!);

    /// <summary>The module that registered a tool (null for tools added directly, as tests do).</summary>
    public LoadedModule? ModuleOf(string toolName) => _moduleOfTool.GetValueOrDefault(toolName);

    public void Add(ToolDescriptor descriptor, AIFunction function, IPreviewableTool? preview = null)
    {
        if (!string.Equals(descriptor.Name, function.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Descriptor name '{descriptor.Name}' does not match function name '{function.Name}'.");
        if (BuiltInTools.Names.Contains(descriptor.Name))
            throw new InvalidOperationException($"Tool name '{descriptor.Name}' is reserved by the Host.");
        if (!_tools.TryAdd(descriptor.Name, new ToolRegistration(descriptor, function, preview)))
            throw new InvalidOperationException($"Duplicate tool name '{descriptor.Name}'.");
    }

    internal void AddModule(IHostModule module, ModuleLoadOptions? options = null)
    {
        options ??= ModuleLoadOptions.Code;
        var id = module.Id;
        if (string.IsNullOrWhiteSpace(id) || id.Contains('.') || id.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"Module id '{id}' is not valid: no dots, no spaces.");
        if (_modules.ContainsKey(id))
            throw new InvalidOperationException($"Two modules share the id '{id}'.");

        var assembly = module.GetType().Assembly;
        var origin = module as IModuleOrigin;
        var contract = origin is null ? ContractOf(assembly, id) : CheckContract(origin.ContractVersion, id, origin.AssemblyName);

        var manifest = module.Manifest;
        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal))
            throw new InvalidOperationException($"Module '{id}' returns a manifest for '{manifest.Id}'.");
        if (manifest.Format > ModuleManifest.CurrentFormat)
            throw new InvalidOperationException($"Module '{id}' uses manifest format {manifest.Format}; this Host reads up to {ModuleManifest.CurrentFormat}.");

        var scoped = new ScopedRegistry(this, id, options.RequireToolPrefix, options.Labels);
        module.Register(scoped);

        var loaded = new LoadedModule(id, manifest, origin?.AssemblyName ?? assembly.GetName().Name ?? "?", origin?.AssemblyVersion ?? VersionOf(assembly),
            contract, options, scoped.Names);
        _modules[id] = loaded;
        foreach (var t in scoped.Names) _moduleOfTool[t] = loaded;
    }

    /// <summary>The contract a module assembly was compiled against, from <see cref="HostContractVersionAttribute"/>.</summary>
    internal static int ContractOf(Assembly assembly, string moduleId)
    {
        var attr = assembly.GetCustomAttribute<HostContractVersionAttribute>()
            ?? throw new InvalidOperationException(
                $"Module '{moduleId}' ({assembly.GetName().Name}) does not say which Host contract it was built against. " +
                "Add [assembly: HostContractVersion(HostContract.Version)] to the module's project.");
        return CheckContract(attr.Version, moduleId, assembly.GetName().Name);
    }

    private static int CheckContract(int version, string moduleId, string? assemblyName)
    {
        if (version < HostContract.MinSupportedVersion || version > HostContract.Version)
            throw new InvalidOperationException(
                $"Module '{moduleId}' ({assemblyName}) was built against Host contract {version}; this Host supports " +
                $"{HostContract.MinSupportedVersion} to {HostContract.Version}. Use a version of the module built for this Host.");
        return version;
    }

    internal static string VersionOf(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>What a module sees while registering: its tools must carry its own id (and, when required, its own name prefix).</summary>
    private sealed class ScopedRegistry(ToolCatalog catalog, string moduleId, bool requirePrefix, IReadOnlyList<string> labels) : IToolRegistry
    {
        public List<string> Names { get; } = [];

        public void Add(ToolDescriptor descriptor, AIFunction function, IPreviewableTool? preview = null)
        {
            if (!string.Equals(descriptor.ModuleId, moduleId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Module '{moduleId}' registered '{descriptor.Name}' under module id '{descriptor.ModuleId}'.");
            if (requirePrefix && !descriptor.Name.StartsWith(moduleId + ".", StringComparison.Ordinal))
                throw new InvalidOperationException($"Module '{moduleId}' registered '{descriptor.Name}'; tools of a configured module must be named '{moduleId}.<name>'.");
            // The instance's labels go on what it returns (Read tools); a write carries no data out of this module.
            if (labels.Count > 0 && descriptor.Risk == ToolRisk.Read)
                descriptor = descriptor with { Labels = [.. descriptor.Labels.Union(labels, StringComparer.Ordinal)] };
            catalog.Add(descriptor, function, preview);
            Names.Add(descriptor.Name);
        }
    }
}
