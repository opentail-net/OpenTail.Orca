using System.Reflection;

namespace OpenTail.Orca.Abstractions.Tests;

/// <summary>
/// The module contract (what a module author compiles against) is frozen at <see cref="HostContract.Version"/> 1. This test compares its
/// public surface with <c>contract-v1.txt</c>. If it fails, either undo the change, or make it on purpose: adding members is allowed when
/// existing modules keep working (update the snapshot); removing or changing one means a new contract version, with
/// <see cref="HostContract.MinSupportedVersion"/> deciding which old modules still load. See PACKAGING.md.
/// </summary>
public class ContractFreezeTests
{
    private static readonly Type[] Contract =
    [
        typeof(IHostModule), typeof(IToolRegistry), typeof(ToolRegistration), typeof(ToolDescriptor), typeof(ToolRisk),
        typeof(IPreviewableTool), typeof(PreviewResult), typeof(IHostModuleFactory), typeof(HostModuleFactoryAttribute),
        typeof(HostContractVersionAttribute), typeof(ModuleContext), typeof(ModuleManifest), typeof(CredentialRequirement),
        typeof(CredentialKind), typeof(Credential), typeof(ICredentialProvider), typeof(IModuleHttpClientFactory),
        typeof(ToolTransientException), typeof(ToolFailureException), typeof(ToolFailureKind), typeof(HostContract), typeof(RootedPath), typeof(NetworkRules),
    ];

    internal static IEnumerable<string> Surface()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in Contract.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            yield return $"type {t.FullName} : {(t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")}";
            if (t.IsEnum)
            {
                foreach (var n in Enum.GetNames(t)) yield return $"  {n} = {Convert.ToInt64(Enum.Parse(t, n))}";
                continue;
            }
            var members = new List<string>();
            foreach (var m in t.GetMembers(flags))
            {
                switch (m)
                {
                    case ConstructorInfo c: members.Add($"  ctor({Params(c)})"); break;
                    case MethodInfo mi when !mi.IsSpecialName && mi.DeclaringType == t && !IsRecordPlumbing(mi):
                        members.Add($"  {(mi.IsStatic ? "static " : "")}{Name(mi.ReturnType)} {mi.Name}({Params(mi)})"); break;
                    case PropertyInfo p when p.Name != "EqualityContract":
                        members.Add($"  {(p.GetMethod?.IsStatic == true ? "static " : "")}{Name(p.PropertyType)} {p.Name} {{{(p.GetMethod?.IsPublic == true ? " get;" : "")}{(p.SetMethod?.IsPublic == true ? " set;" : "")} }}"); break;
                    case FieldInfo f: members.Add($"  {(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{Name(f.FieldType)} {f.Name}{(f.IsLiteral ? " = " + f.GetRawConstantValue() : "")}"); break;
                }
            }
            foreach (var line in members.Order(StringComparer.Ordinal)) yield return line;
        }
    }

    private static bool IsRecordPlumbing(MethodInfo m) =>
        m.Name is "ToString" or "GetHashCode" or "Equals" or "PrintMembers" or "<Clone>$" or "Deconstruct";

    private static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType) + (p.IsOptional ? "?" : "")));

    private static string Name(Type t) => t.IsGenericType
        ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>"
        : t.Name;

    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenTail.Orca.Abstractions.Tests.csproj"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("test project not found"), "contract-v1.txt");
    }

    [Fact]
    public void The_module_contract_matches_the_frozen_v1_snapshot()
    {
        var actual = string.Join("\n", Surface()) + "\n";
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("OPENTAIL_UPDATE_CONTRACT") == "1") File.WriteAllText(path, actual);
        var expected = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        if (expected == actual) return;

        var added = actual.Split('\n').Except(expected.Split('\n')).ToList();
        var removed = expected.Split('\n').Except(actual.Split('\n')).ToList();
        Assert.Fail("The module contract changed.\n" +
                    (removed.Count > 0 ? "Removed or changed (breaks modules: needs a new contract version):\n  " + string.Join("\n  ", removed) + "\n" : "") +
                    (added.Count > 0 ? "Added:\n  " + string.Join("\n  ", added) + "\n" : "") +
                    "If this is deliberate, rerun with OPENTAIL_UPDATE_CONTRACT=1 and explain the change in PACKAGING.md.");
    }

    [Fact]
    public void Contract_version_is_one_and_old_modules_down_to_the_minimum_still_load()
    {
        Assert.Equal(1, HostContract.Version);
        Assert.InRange(HostContract.MinSupportedVersion, 1, HostContract.Version);
    }
}
