using System.Reflection;
using Microsoft.Extensions.DependencyModel;

namespace OpenTail.Orca;

/// <summary><see cref="ModuleLoadMode.Describe"/> builds modules without checking that credentials exist (for --explain, --list-modules,
/// --adopt-policy); <see cref="ModuleLoadMode.Run"/> also refuses to start when a mapped credential source is missing.</summary>
public enum ModuleLoadMode { Describe, Run }

/// <summary>A module enabled in <c>modules.json</c>, built and ready to add to a host.</summary>
public sealed record ConfiguredModule(ModuleEntry Entry, IHostModule Module, ModuleLoadOptions Options, IReadOnlyList<string> AllowedHosts,
    IReadOnlyCollection<string> Credentials);

public sealed record ModuleLoadResult(
    IReadOnlyList<ConfiguredModule> Modules,
    IReadOnlyList<string> Retired,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, IHostModuleFactory> Factories);

/// <summary>
/// Turns <c>modules.json</c> into modules. Factories are found through <see cref="HostModuleFactoryAttribute"/> on assemblies the deployment
/// <b>references</b> (its deps.json lists them): config selects among what was compiled in, it never loads code from a folder. That keeps
/// the decision about which code runs with whoever builds the deployment.
/// </summary>
public static class ModuleLoader
{
    public const string DefaultFactoryPrefix = "OpenTail.Modules.";

    public static OpenTailHostBuilder AddModules(this OpenTailHostBuilder builder, ModuleLoadResult loaded)
    {
        foreach (var m in loaded.Modules) builder.AddModule(m.Module, m.Options);
        foreach (var id in loaded.Retired) builder.RetireModule(id);
        return builder;
    }

    /// <summary>Factories in referenced assemblies whose names start with <c>OpenTail.Modules.</c> or one of <paramref name="prefixes"/>,
    /// plus <paramref name="extra"/> (tests, or a deployment that wants to name one in code).</summary>
    public static IReadOnlyDictionary<string, IHostModuleFactory> DiscoverFactories(IEnumerable<string>? prefixes = null,
        IEnumerable<IHostModuleFactory>? extra = null, Action<string>? warn = null)
    {
        var allPrefixes = new[] { DefaultFactoryPrefix }.Concat((prefixes ?? []).Select(p => p.TrimEnd('*'))).Where(p => p.Length > 0).ToList();
        bool Wanted(string? name) => name is not null && allPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (Wanted(a.GetName().Name)) assemblies.TryAdd(a.GetName().Name!, a);

        // Referenced but never touched yet: not loaded, so ask the dependency manifest what the app was built with.
        var deps = DependencyContext.Default;
        if (deps is null) warn?.Invoke("No dependency manifest (deps.json) found; only module assemblies already loaded are searched.");
        else
            foreach (var lib in deps.RuntimeLibraries)
            foreach (var name in lib.GetDefaultAssemblyNames(deps))
            {
                if (!Wanted(name.Name) || assemblies.ContainsKey(name.Name!)) continue;
                try { assemblies[name.Name!] = Assembly.Load(name); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    warn?.Invoke($"Could not load {name.Name}: {ex.Message}");
                }
            }

        var factories = new Dictionary<string, IHostModuleFactory>(StringComparer.Ordinal);
        void Add(IHostModuleFactory f, string where)
        {
            if (string.IsNullOrWhiteSpace(f.Type)) throw new InvalidOperationException($"A module factory in {where} has no Type.");
            if (!factories.TryAdd(f.Type, f))
                throw new InvalidOperationException($"Two module factories claim the type '{f.Type}' ({factories[f.Type].GetType().Assembly.GetName().Name} and {where}).");
        }

        foreach (var a in assemblies.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
            foreach (var attr in a.GetCustomAttributes<HostModuleFactoryAttribute>())
            {
                if (!typeof(IHostModuleFactory).IsAssignableFrom(attr.FactoryType) || attr.FactoryType.GetConstructor(Type.EmptyTypes) is null)
                    throw new InvalidOperationException($"{a.GetName().Name}: {attr.FactoryType.Name} must implement IHostModuleFactory and have a public parameterless constructor.");
                ToolCatalog.ContractOf(a, attr.FactoryType.Name);
                Add((IHostModuleFactory)Activator.CreateInstance(attr.FactoryType)!, a.GetName().Name!);
            }
        foreach (var f in extra ?? []) Add(f, f.GetType().Assembly.GetName().Name ?? "code");
        return factories;
    }

    public static async Task<ModuleLoadResult> LoadAsync(ModulesConfig config, ModuleLoadMode mode,
        IEnumerable<IHostModuleFactory>? extraFactories = null, TimeProvider? clock = null, Action<string>? warn = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        void Warn(string m) { warnings.Add(m); warn?.Invoke(m); }

        var factories = DiscoverFactories(config.FactoryAssemblies, extraFactories, Warn);
        var modules = new List<ConfiguredModule>();
        var retired = new List<string>();

        foreach (var entry in config.Modules)
        {
            if (!entry.Enabled) { retired.Add(entry.Id); continue; }
            if (!factories.TryGetValue(entry.Type, out var factory))
                throw new InvalidOperationException(
                    $"Module '{entry.Id}' needs type '{entry.Type}', which no referenced package provides" +
                    (factories.Count == 0 ? " (none were found" : $" (found: {string.Join(", ", factories.Keys.Order())}") +
                    "). Add a reference to the package to the deployment project. A trimmed or single-file publish can also hide it.");

            var moduleConfig = entry.Config.ValueKind == System.Text.Json.JsonValueKind.Undefined ? EmptyObject() : entry.Config;
            var manifest = factory.Describe(entry.Id, moduleConfig, config.BaseDirectory);
            foreach (var need in manifest.Credentials.Where(c => !c.Optional && !entry.Credentials.ContainsKey(c.Name)))
                throw new InvalidOperationException($"Module '{entry.Id}' needs credential '{need.Name}' ({need.Description}); map it under Credentials.");
            foreach (var name in entry.Credentials.Keys.Where(n => manifest.Credentials.All(c => c.Name != n)))
                Warn($"Module '{entry.Id}': credential '{name}' is mapped but the module does not ask for it.");

            var hosts = entry.Network.Hosts;
            if (manifest.NetworkHosts.Count > 0)
            {
                if (hosts.Count == 0)
                    throw new InvalidOperationException($"Module '{entry.Id}' talks to {string.Join(", ", manifest.NetworkHosts)}; list the ones you allow under Network.Hosts.");
                foreach (var h in hosts.Where(h => !manifest.NetworkHosts.Any(m => NetworkRules.Covers(m, h))))
                    throw new InvalidOperationException($"Module '{entry.Id}': Network.Hosts entry '{h}' is outside what the module declares ({string.Join(", ", manifest.NetworkHosts)}).");
            }

            var credentials = new ModuleCredentials(entry.Id, entry.Credentials, config.BaseDirectory, clock);
            if (mode == ModuleLoadMode.Run && credentials.CheckPresent() is { Count: > 0 } missing)
                throw new InvalidOperationException($"Module '{entry.Id}': " + string.Join("; ", missing) + ".");

            var http = new GuardedHttpClientFactory(entry.Id, hosts, entry.Network, credentials);
            var context = new ModuleContext
            {
                Id = entry.Id,
                Config = moduleConfig,
                Credentials = credentials,
                Http = http,
                Clock = clock ?? TimeProvider.System,
                Warn = m => Warn($"Module '{entry.Id}': {m}"),
                BaseDirectory = config.BaseDirectory,
            };

            IHostModule module;
            // Describing (--list-modules, --explain, --adopt-policy) builds even an isolated module in process: it reads no credential
            // and calls no network, so starting a worker for it would only be slower.
            if (entry.IsIsolated && mode == ModuleLoadMode.Run)
                module = await IsolatedModule.StartAsync(entry, moduleConfig, config.BaseDirectory, config.FactoryAssemblies, credentials, http, Warn, ct);
            else
            try { module = await factory.CreateAsync(context, ct); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException)
            {
                throw new InvalidOperationException($"Module '{entry.Id}' ({entry.Type}) could not be created: {Redaction.Scrub(ex.Message)}");
            }
            if (!string.Equals(module.Id, entry.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"Factory '{entry.Type}' built a module with id '{module.Id}' for entry '{entry.Id}'.");

            modules.Add(new ConfiguredModule(entry, module,
                new ModuleLoadOptions { AllowDefaultRead = entry.AllowDefaultRead, RequireToolPrefix = true, FactoryType = entry.Type, Isolated = entry.IsIsolated, Labels = entry.Labels },
                hosts, credentials.Names));
        }

        // One line, not one per type: a deployment that references every package (the container image) would otherwise open each
        // start with a page of these.
        var idle = factories.Keys.Where(t => config.Modules.All(m => m.Type != t)).Order(StringComparer.Ordinal).ToList();
        if (idle.Count > 0)
            Warn($"Module types available but not enabled in the modules file: {string.Join(", ", idle.Select(t => $"'{t}' ({factories[t].GetType().Assembly.GetName().Name})"))}.");

        return new ModuleLoadResult(modules, retired, warnings, factories);
    }

    private static System.Text.Json.JsonElement EmptyObject()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
