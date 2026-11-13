using System.Xml.Linq;

namespace OpenTail.Orca.Abstractions.Tests;

/// <summary>Enforces the dependency rule from docs/done/MCP-HOST-PLAN.md section 4.</summary>
public class ArchitectureTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenTail.Orca.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OpenTail.Orca.slnx not found above " + AppContext.BaseDirectory);
    }

    private static (string[] Projects, string[] Packages) Refs(string csproj)
    {
        var doc = XDocument.Load(csproj);
        string[] Get(string element, string attr) =>
            doc.Descendants(element).Select(e => (string?)e.Attribute(attr)).Where(v => v is not null).Select(v => v!).ToArray();
        var projects = Get("ProjectReference", "Include")
            .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
            .ToArray();
        return (projects, Get("PackageReference", "Include"));
    }

    [Fact]
    public void Host_abstractions_references_nothing_but_extensions_ai_abstractions()
    {
        var (projects, packages) = Refs(Path.Combine(RepoDir(), "OpenTail.Orca.Abstractions", "OpenTail.Orca.Abstractions.csproj"));
        Assert.Empty(projects);
        Assert.Equal(["Microsoft.Extensions.AI.Abstractions"], packages);
    }

    [Fact]
    public void Modules_reference_only_host_abstractions_among_opentail_projects()
    {
        var root = RepoDir();
        var modules = Directory.GetDirectories(root, "OpenTail.Modules.*")
            .Select(d => Path.Combine(d, Path.GetFileName(d) + ".csproj"))
            .Where(File.Exists)
            .Where(p => !p.EndsWith(".Tests.csproj", StringComparison.Ordinal))
            .ToList();

        foreach (var csproj in modules)
        {
            var (projects, _) = Refs(csproj);
            var self = Path.GetFileNameWithoutExtension(csproj);
            // Connectivity.* are leaf connector projects (none reference Core), so modules may wrap them. Provider packages
            // (Email.Imap, Calendar.Ics, Microsoft365, Sql.Oracle) may also reference the provider-neutral tool modules, which reference no module.
            // OpenTail.AgentSkills is a leaf library (no project references; it reads and checks skill directories, see docs/design/agent-skills-library.md).
            var illegal = projects.Where(p => p != "OpenTail.Orca.Abstractions" && p != "OpenTail.AgentSkills" && !p.StartsWith("OpenTail.Connectivity.", StringComparison.Ordinal)
                && !(ToolModules.Contains(p) && !ToolModules.Contains(self))).ToList();
            Assert.True(illegal.Count == 0,
                $"{Path.GetFileName(csproj)} references {string.Join(", ", illegal)}; modules may reference only OpenTail.Orca.Abstractions, OpenTail.AgentSkills, OpenTail.Connectivity.* " +
                $"and the provider-neutral modules {string.Join(", ", ToolModules)}.");
        }
    }

    /// <summary>Modules that hold tools over an adapter interface and are completed by a provider package.</summary>
    private static readonly string[] ToolModules = ["OpenTail.Modules.Email", "OpenTail.Modules.Calendar", "OpenTail.Modules.Sql"];

    [Fact]
    public void Profile_packages_hold_no_code_and_reference_only_the_openapi_module()
    {
        var root = RepoDir();
        var profiles = Directory.GetDirectories(root, "OpenTail.Profiles.*").ToList();
        Assert.NotEmpty(profiles);
        foreach (var dir in profiles)
        {
            var csproj = Path.Combine(dir, Path.GetFileName(dir) + ".csproj");
            var (projects, packages) = Refs(csproj);
            Assert.Equal(["OpenTail.Modules.OpenApi"], projects);
            Assert.Empty(packages);
            var code = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
            Assert.True(code.Count == 0, $"{Path.GetFileName(dir)} contains code ({string.Join(", ", code.Select(Path.GetFileName))}); a profile package holds only the profile and its spec.");
        }
    }

    [Fact]
    public void The_only_opentail_packages_the_host_takes_are_connectivity_libraries()
    {
        // The Connectivity libraries live in their own repository (opentail-connectivity), which holds nothing of OpenTail Core, so
        // a package from there cannot pull in the heavy stack. Anything else named OpenTail.* would come from outside this repository.
        var csprojs = Directory.GetFiles(RepoDir(), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(csprojs);
        foreach (var csproj in csprojs)
        {
            var (_, packages) = Refs(csproj);
            var foreign = packages.Where(p => p.StartsWith("OpenTail.", StringComparison.Ordinal) && !p.StartsWith("OpenTail.Connectivity.", StringComparison.Ordinal)).ToList();
            Assert.True(foreign.Count == 0, $"{Path.GetFileName(csproj)} takes {string.Join(", ", foreign)} as a package; only OpenTail.Connectivity.* come from outside.");
        }
    }

    [Fact]
    public void Host_does_not_reference_core()
    {
        var csproj = Path.Combine(RepoDir(), "OpenTail.Orca", "OpenTail.Orca.csproj");
        if (!File.Exists(csproj)) return; // Host does not exist until Phase 2.
        var (projects, _) = Refs(csproj);
        Assert.DoesNotContain("OpenTail.Core", projects);
    }

    [Fact]
    public void Layering_of_the_host_packages_is_one_way()
    {
        var root = RepoDir();
        string[] ProjectsOf(string name) => Refs(Path.Combine(root, name, name + ".csproj")).Projects;

        Assert.Equal(["OpenTail.Orca.Abstractions"], ProjectsOf("OpenTail.Orca"));
        Assert.Equal(["OpenTail.Orca"], ProjectsOf("OpenTail.Orca.Admin"));
        Assert.Equal(["OpenTail.Orca.Abstractions"], ProjectsOf("OpenTail.Orca.Notifications"));
        Assert.Equal(["OpenTail.Orca", "OpenTail.Orca.Admin", "OpenTail.Orca.Notifications"], ProjectsOf("OpenTail.Orca.Http").Order().ToArray());
        // The A2A front door sits above the HTTP host: it references it, and nothing below references it (decision D2).
        Assert.Equal(["OpenTail.Orca.Http"], ProjectsOf("OpenTail.Orca.A2A"));
    }

    [Fact]
    public void Nothing_but_the_server_and_the_tests_references_the_a2a_front_door()
    {
        var root = RepoDir();
        foreach (var csproj in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (name is "OpenTail.Orca.A2A" or "OpenTail.Orca.Server" || name.Contains("Tests", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("OpenTail.Orca.A2A", Refs(csproj).Projects);
        }
    }

    [Fact]
    public void No_host_package_references_core_or_a_module()
    {
        var root = RepoDir();
        foreach (var name in new[] { "OpenTail.Orca.Abstractions", "OpenTail.Orca", "OpenTail.Orca.Admin", "OpenTail.Orca.Notifications", "OpenTail.Orca.Http", "OpenTail.Orca.A2A" })
        {
            var (projects, _) = Refs(Path.Combine(root, name, name + ".csproj"));
            Assert.DoesNotContain(projects, p => p == "OpenTail.Core" || p.StartsWith("OpenTail.Modules.", StringComparison.Ordinal) || p.StartsWith("OpenTail.Core.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_host_package_has_the_shared_version_prefix_applied_by_name()
    {
        // Directory.Build.props versions OpenTail.Orca*, OpenTail.Modules.* and OpenTail.Profiles.* together (host-v tags). The
        // Connectivity libraries have their own versions, in their own repository.
        var props = File.ReadAllText(Path.Combine(RepoDir(), "Directory.Build.props"));
        Assert.Contains("MinVerTagPrefix>host-v<", props);
        foreach (var prefix in new[] { "OpenTail.Orca", "OpenTail.Modules.", "OpenTail.Profiles." })
            Assert.Contains($"StartsWith('{prefix}')", props);
    }
}
