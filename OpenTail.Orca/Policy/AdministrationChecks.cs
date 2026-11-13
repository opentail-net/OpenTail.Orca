namespace OpenTail.Orca;

/// <summary>
/// Load-time rules for administration tools (<see cref="ToolDescriptor.Administers"/>, or <see cref="ToolPolicy.Administers"/> set by the
/// operator). An administration tool is always a proposal that people decide, and never sits in one profile next to business tools with
/// effects on a live Host. Breaking a rule stops startup, naming the tool or profile.
/// </summary>
internal static class AdministrationChecks
{
    /// <summary>Requestable tools must exist and never be administration tools; <c>access.request</c>, if enabled, is decided by people only,
    /// never by the requester.</summary>
    public static void CheckRequestable(ToolCatalog catalog, PolicyConfig policy, List<string> errors)
    {
        foreach (var (name, profile) in policy.Agents)
            foreach (var tool in profile.Requestable)
            {
                if (!catalog.TryGet(tool, out var reg)) errors.Add($"Agent profile '{name}' may request unknown tool '{tool}'.");
                else if (reg.Descriptor.Administers || policy.Tools.TryGetValue(tool, out var p) && p.Administers)
                    errors.Add($"Agent profile '{name}' may request administration tool '{tool}': administration tools are never granted temporarily.");
            }
        if (policy.Tools.TryGetValue(AccessGrants.RequestTool, out var ar) && ar.Mode != ToolMode.Deny
            && (ar.Mode != ToolMode.Queue || ar.Reviewer != ReviewerKind.Human || ar.SelfApproval != SelfApproval.Forbid || ar.AllowEdits || ar.TrustRamp is { AfterCleanApprovals: > 0 }
                || (ar.Rules ?? []).Any(r => r.Mode == ToolMode.Execute)))
            errors.Add($"'{AccessGrants.RequestTool}' must be Mode Queue, Reviewer Human, SelfApproval Forbid and AllowEdits false, with no Execute rule or trust ramp: a person grants access, never the requester.");
        // Inspection proposes, people approve: a redrive is always a person's decision.
        foreach (var remedy in WorkModule.RemedyTools)
            if (policy.Tools.TryGetValue(remedy, out var rd) && rd.Mode != ToolMode.Deny
                && (rd.Mode != ToolMode.Queue || rd.Reviewer != ReviewerKind.Human || rd.SelfApproval != SelfApproval.Forbid || rd.TrustRamp is { AfterCleanApprovals: > 0 }
                    || (rd.Rules ?? []).Any(r => r.Mode == ToolMode.Execute)))
                errors.Add($"'{remedy}' must be Mode Queue, Reviewer Human and SelfApproval Forbid, with no Execute rule or trust ramp: an inspector proposes a remedy, a person decides.");
    }

    public static void Check(ToolCatalog catalog, PolicyConfig policy, bool sandboxed, List<string> errors, List<string> notes)
    {
        bool IsAdmin(ToolDescriptor d) => d.Administers || policy.Tools.TryGetValue(d.Name, out var p) && p.Administers;
        var tools = catalog.All.Select(t => t.Descriptor).ToList();

        foreach (var d in tools.Where(IsAdmin))
        {
            // No entry, or Deny: the tool is hidden from everyone, which is always allowed.
            if (!policy.Tools.TryGetValue(d.Name, out var p) || p.Mode == ToolMode.Deny) continue;
            var at = $"Administration tool '{d.Name}'";
            if (p.Mode != ToolMode.Queue) errors.Add($"{at} must be Mode Queue: it is a proposal that people decide.");
            if (p.Reviewer != ReviewerKind.Human) errors.Add($"{at} needs Reviewer Human: no AI reviewer decides it.");
            if (string.IsNullOrWhiteSpace(p.ReviewerGroup)) errors.Add($"{at} needs a ReviewerGroup: the approvers for administration.");
            if (p.SelfApproval != SelfApproval.Forbid) errors.Add($"{at} needs SelfApproval Forbid.");
            if (p.AllowEdits) errors.Add($"{at} needs AllowEdits false: a reviewer approves or rejects what was proposed.");
            if (p.TrustRamp is { AfterCleanApprovals: > 0 }) errors.Add($"{at} cannot have a trust ramp: it is never promoted to Execute.");
            foreach (var r in p.Rules ?? [])
            {
                if (r.Mode == ToolMode.Execute) errors.Add($"{at}, rule '{r.Name}': an Execute rule is not allowed.");
                if (r.Reviewer is { } rk && rk != ReviewerKind.Human) errors.Add($"{at}, rule '{r.Name}': Reviewer must be Human.");
            }
        }

        foreach (var (name, profile) in policy.Agents)
        {
            var admin = tools.Where(d => IsAdmin(d) && AgentProfiles.Grants(profile, d, administers: true)).Select(d => d.Name).Order(StringComparer.Ordinal).ToList();
            if (admin.Count == 0) continue;
            var business = tools.Where(d => !IsAdmin(d) && d.Risk > ToolRisk.Read && AgentProfiles.Grants(profile, d)).Select(d => d.Name).Order(StringComparer.Ordinal).ToList();
            if (business.Count > 0 && !sandboxed)
                errors.Add($"Agent profile '{name}' holds administration tool(s) ({string.Join(", ", admin)}) and business tools with effects " +
                           $"({string.Join(", ", business)}). Split them into two profiles; only a sandbox or shadow Host may combine them.");
            notes.Add($"Agent profile '{name}' holds administration tool(s): {string.Join(", ", admin)}.");
        }
    }
}
