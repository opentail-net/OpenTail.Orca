using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The skill-selection extension (batch 13). A partner names the skill it wants by listing this extension in its message's extensions, and
/// giving the skill id as the message's metadata value under the extension's URI. The card declares the extension. The extension's
/// specification is not yet published at its URI, so the metadata key is this Host's choice until it is; the owner confirms it before a
/// partner is onboarded (docs/design/a2a.md, section 4).
/// </summary>
internal static class A2ASkillSelection
{
    public const string Uri = "https://opentail.net/a2a/extensions/skill-selection/v1";

    /// <summary>Whether a profile may use a skill: it lists <c>a2a.submit</c>, and the skill's <see cref="A2aSkill.Profiles"/>, when set, names
    /// it. The card and the request handler both ask this, so they cannot disagree.</summary>
    public static bool MayUse(OpenTail.Orca.OpenTailHost host, A2aSkill skill, string? profile)
    {
        if (profile is null || !host.Policy.Config.Agents.TryGetValue(profile, out var agent) || !agent.Tools.Contains(A2ASubmitModule.Tool, StringComparer.Ordinal))
            return false;
        return skill.Profiles is null || skill.Profiles.Contains(profile, StringComparer.Ordinal);
    }
}
