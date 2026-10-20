namespace OpenTail.Host.Abstractions;

public sealed record ToolRegistration(ToolDescriptor Descriptor, AIFunction Function, IPreviewableTool? Preview = null);

/// <summary>Where a module declares its tools.</summary>
public interface IToolRegistry
{
    void Add(ToolDescriptor descriptor, AIFunction function, IPreviewableTool? preview = null);
}

/// <summary>
/// A unit of capability (email, files, an imported API). Declares tools and their risk; never reads policy.
/// The assembly that contains a module must say which contract it was built against:
/// <c>[assembly: HostContractVersion(HostContract.Version)]</c>. The constant is compiled into the module, so the Host sees the
/// version the module was really built with, not its own.
/// </summary>
public interface IHostModule
{
    string Id { get; }

    /// <summary>What the module is and what it needs. Shown to operators; never grants anything (policy and operator config do).</summary>
    ModuleManifest Manifest => ModuleManifest.Minimal(Id);

    void Register(IToolRegistry registry);
}

/// <summary>The contract version a module assembly was compiled against. Required on every assembly that contains modules or factories.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class HostContractVersionAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}
