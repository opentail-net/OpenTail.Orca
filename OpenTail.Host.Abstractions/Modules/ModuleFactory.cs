using System.Text.Json;

namespace OpenTail.Host.Abstractions;

/// <summary>
/// Builds a module from operator configuration, so a deployment can enable it by naming it in <c>modules.json</c> instead of code.
/// One factory can make several instances (two OpenAPI imports, two GitHub organisations), told apart by <see cref="ModuleContext.Id"/>.
/// Register it with <c>[assembly: HostModuleFactory(typeof(MyFactory))]</c>.
/// </summary>
public interface IHostModuleFactory
{
    /// <summary>The <c>Type</c> operators write in <c>modules.json</c>, such as <c>"github-issues"</c>. Lower case, stable.</summary>
    string Type { get; }

    /// <summary>The manifest for an instance with this id and config, without credentials or network (used to check the modules file, and
    /// by <c>--list-modules</c> and <c>--adopt-policy</c>). May depend on config (an API base URL decides the host the module talks to).</summary>
    ModuleManifest Describe(string id, JsonElement config);

    /// <summary>As <see cref="Describe(string, JsonElement)"/>, knowing the directory the modules file is in, for factories whose config names
    /// a file that decides the manifest (an integration profile). The Host calls this one; the default ignores the directory.</summary>
    ModuleManifest Describe(string id, JsonElement config, string baseDirectory) => Describe(id, config);

    /// <summary>Creates the module. Must not fetch credentials or call the network: do that when a tool runs, so describing and
    /// explaining work without secrets.</summary>
    ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default);
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class HostModuleFactoryAttribute(Type factoryType) : Attribute
{
    public Type FactoryType { get; } = factoryType;
}

/// <summary>What the Host gives a module it builds from configuration.</summary>
public sealed class ModuleContext
{
    /// <summary>The instance id from configuration; also the required prefix of every tool name (<c>id.</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The module's own <c>Config</c> object from <c>modules.json</c> (an empty object when absent).</summary>
    public required JsonElement Config { get; init; }

    /// <summary>Credentials mapped to this module. Names not mapped under this module are refused.</summary>
    public required ICredentialProvider Credentials { get; init; }

    /// <summary>HTTP clients limited to the hosts the operator allowed for this module.</summary>
    public required IModuleHttpClientFactory Http { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Where the module can report something the operator should see (written to stderr).</summary>
    public Action<string> Warn { get; init; } = _ => { };

    /// <summary>Directory the module config was read from, for resolving relative paths (a spec file, a root folder).</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Reads <see cref="Config"/> as a typed options object (property names are case-insensitive, unknown names are refused).</summary>
    public T Bind<T>() where T : new()
    {
        if (Config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new T();
        try { return Config.Deserialize<T>(BindOptions) ?? new T(); }
        catch (JsonException ex) { throw new ArgumentException($"Module '{Id}': invalid Config: {ex.Message}"); }
    }

    private static readonly JsonSerializerOptions BindOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new DurationConverter() },
    };
}
