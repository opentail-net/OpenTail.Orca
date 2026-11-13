using OpenTail.AgentSkills;

namespace OpenTail.Orca.Tests;

/// <summary>The library is independent of the Host: it references no OpenTail assembly, and only the platform and YamlDotNet (agent-skills-library plan, step 1).</summary>
public sealed class AgentSkillsLibraryTests
{
    [Fact]
    public void The_agent_skills_library_references_no_opentail_assembly_only_the_platform_and_yamldotnet()
    {
        var references = typeof(AgentSkill).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        Assert.DoesNotContain(references, n => n.StartsWith("OpenTail", StringComparison.Ordinal));
        Assert.All(references, n => Assert.True(n.StartsWith("System", StringComparison.Ordinal) || n == "YamlDotNet" || n == "netstandard",
            $"unexpected reference {n}"));
    }
}
