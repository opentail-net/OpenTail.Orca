using System.Text.Json;

namespace OpenTail.Orca;

/// <summary>
/// What the Host decided for one call. <see cref="Policy"/> is the effective per-action policy (rules already applied, rules
/// themselves stripped) and is what gets snapshotted into the action. <see cref="Admissions"/> are limits that must be
/// checked atomically when an <c>Execute</c> action is created; if one refuses, <see cref="FallbackPolicy"/> applies and the call queues.
/// </summary>
public sealed record PolicyDecision(ToolMode Mode, ToolPolicy Policy, string Rule)
{
    /// <summary>The conditional rule that decided, if one did.</summary>
    public string? RuleName { get; init; }

    public IReadOnlyList<Admission> Admissions { get; init; } = [];
    public ToolPolicy? FallbackPolicy { get; init; }

    /// <summary>For operators: how each rule evaluated. Only filled when asked for.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    /// <summary>The policy checks that decided, in order (base policy, rules, trust, taint), for the action's decision trace.
    /// Always filled.</summary>
    public IReadOnlyList<DecisionStep> Steps { get; init; } = [];
}

internal sealed class CompiledRule(PolicyRule rule, Condition when, HashSet<string> referenced)
{
    public PolicyRule Rule { get; } = rule;
    public Condition When { get; } = when;
    public HashSet<string> Referenced { get; } = referenced;
    public HashSet<string> Ignored { get; } = (rule.IgnoreArgs ?? []).ToHashSet(StringComparer.Ordinal);
}

internal static class RuleCompiler
{
    public static CompiledRule Compile(string tool, PolicyRule rule)
    {
        if (rule.When.ValueKind == JsonValueKind.Undefined) throw new RuleException($"Tool '{tool}', rule '{rule.Name}': 'When' is required.");
        try
        {
            var when = ConditionCompiler.Compile(rule.When, "When");
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            when.CollectArgs(referenced);
            return new CompiledRule(rule, when, referenced);
        }
        catch (RuleException ex)
        {
            throw new RuleException($"Tool '{tool}', rule '{rule.Name}': {ex.Message}");
        }
    }
}

/// <summary>Turns descriptor + config + session taint (+ the call's arguments) into a decision. Combining rules can only tighten, except
/// where the operator wrote an explicit <c>Execute</c> rule.</summary>
public sealed class PolicyResolver
{
    private readonly PolicyConfig _config;
    private readonly ITrustStore _trust;
    private readonly Dictionary<string, List<CompiledRule>> _rules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noDefaultRead;

    /// <param name="noDefaultReadModules">Modules whose Read tools do not follow <c>DefaultReadMode</c> (modules enabled from
    /// configuration, unless the operator opted in): an unlisted tool of theirs is denied like any other.</param>
    public PolicyResolver(PolicyConfig config, ITrustStore? trust = null, IEnumerable<string>? noDefaultReadModules = null)
    {
        _config = config;
        _noDefaultRead = (noDefaultReadModules ?? []).ToHashSet(StringComparer.Ordinal);
        People = new PeopleMap(config.People);
        _trust = trust ?? new InMemoryTrustStore();
        foreach (var (tool, policy) in config.Tools)
            if (policy.Rules is { Count: > 0 } rules)
                _rules[tool] = rules.Select(r => RuleCompiler.Compile(tool, r)).ToList();
    }

    public PolicyConfig Config => _config;

    private string? _hash;

    /// <summary>The hash of the whole effective policy (<see cref="PolicyConfig.Hash"/>), computed once; policy does not change while
    /// a Host runs.</summary>
    public string PolicyHash => _hash ??= _config.Hash();

    /// <summary>Who is the same person, for self-approval.</summary>
    public PeopleMap People { get; }

    public bool IsPromoted(string tool) => _trust.IsPromoted(tool);
    internal void Promote(string tool, string by, DateTimeOffset at) => _trust.Promote(tool, by, at);
    internal bool Demote(string tool) => _trust.Demote(tool);

    /// <summary>Why a tool would be handled the way it is, for operators. Not exposed to the model.</summary>
    public ExplainResult Explain(ToolCatalog catalog, string toolName, bool tainted)
    {
        if (!catalog.TryGet(toolName, out var reg))
            return new ExplainResult(toolName, false, null, ToolMode.Deny, ToolMode.Deny, "unknown-tool", tainted, false, false,
                ["No loaded module declares this tool."]);

        var configured = Resolve(reg.Descriptor, tainted: false);
        var effective = Resolve(reg.Descriptor, tainted);
        var notes = new List<string>();
        if (effective.Rule.Contains("no-policy"))
            notes.Add(reg.Descriptor.Risk == ToolRisk.Read
                ? $"No policy entry. Module '{reg.Descriptor.ModuleId}' was enabled from configuration without AllowDefaultRead, so even its Read tools need an entry."
                : "No policy entry and the tool is not Read-risk, so it is denied.");
        if (effective.Rule.Contains("default-read")) notes.Add($"No policy entry; Read tools follow DefaultReadMode={_config.DefaultReadMode}.");
        if (effective.Rule.Contains("trust")) notes.Add("Promoted to Execute by an operator after clean approvals.");
        if (effective.Rule.Contains("taint")) notes.Add("The caller's session has read untrusted content (or is sessionless), so a high-impact tool is forced to Queue.");
        if (reg.Descriptor.ReadsUntrusted) notes.Add("Calling this tool taints the session.");
        if (reg.Descriptor.IsHighImpact && reg.Preview is null) notes.Add("High-impact tool without a preview: reviewers will be warned.");
        if (effective.Policy.RateLimit is { } rl) notes.Add($"Rate limit: {rl.PerHour}/hour, {rl.PerDay}/day (0 = none).");
        if (_rules.TryGetValue(toolName, out var rules))
            notes.Add($"{rules.Count} conditional rule(s) refine this per call: {string.Join(", ", rules.Select(r => $"{r.Rule.Name} ({r.Rule.Mode})"))}. The mode shown here is the base; use simulate with arguments to see a rule apply.");
        return new ExplainResult(toolName, true, reg.Descriptor.Risk, configured.Policy.Mode, effective.Mode, effective.Rule,
            tainted, IsAdvertised(reg.Descriptor), IsPromoted(toolName), notes);
    }

    /// <summary>What would happen to this call: the decision and how each rule evaluated. Executes nothing, stores nothing.</summary>
    public PolicyDecision Simulate(ToolCatalog catalog, string toolName, string argumentsJson, bool tainted, DateTimeOffset? at = null, string? requesterId = null)
    {
        if (!catalog.TryGet(toolName, out var reg))
            return new PolicyDecision(ToolMode.Deny, ToolPolicy.Denied, "unknown-tool") { Trace = ["No loaded module declares this tool."] };
        return Resolve(reg.Descriptor, tainted, argumentsJson, at, requesterId, trace: true);
    }

    /// <param name="argumentsJson">When given, conditional rules are evaluated against it. Without it only the base policy applies
    /// (used for advertising tools and for the static explanation).</param>
    public PolicyDecision Resolve(ToolDescriptor tool, bool tainted, string? argumentsJson = null, DateTimeOffset? now = null,
        string? requesterId = null, bool trace = false)
    {
        ToolPolicy policy;
        string rule;
        if (_config.Tools.TryGetValue(tool.Name, out var explicitPolicy))
        {
            policy = explicitPolicy;
            rule = "tool-policy";
        }
        else if (tool.Risk == ToolRisk.Read && !_noDefaultRead.Contains(tool.ModuleId))
        {
            policy = new ToolPolicy { Mode = _config.DefaultReadMode };
            rule = "default-read";
        }
        else
        {
            policy = ToolPolicy.Denied;
            rule = "no-policy:deny";
        }

        var plain = policy with { Rules = null };
        var lines = trace ? new List<string>() : null;
        var steps = new List<DecisionStep> { new("policy", policy.Mode.ToString(), rule) };
        var mode = policy.Mode;
        var effective = plain;
        string? winner = null;
        var tightened = false;
        IReadOnlyList<Admission> admissions = [];

        if (argumentsJson is not null && mode != ToolMode.Deny && _rules.TryGetValue(tool.Name, out var rules))
        {
            var root = ParseArguments(argumentsJson);
            var ctx = new EvalContext(root, now ?? DateTimeOffset.UtcNow, lines);
            var matched = new List<CompiledRule>();
            foreach (var r in rules)
            {
                lines?.Add($"rule '{r.Rule.Name}' ({r.Rule.Mode}):");
                var tri = Evaluate(r, ctx);
                // Deny and Queue rules apply when true OR unknown (anything we cannot read errs toward caution).
                // An Execute rule needs a definite true AND every argument in the call accounted for.
                var applies = r.Rule.Mode == ToolMode.Execute ? tri == Tri.True && Covers(r, root, lines) : tri != Tri.False;
                lines?.Add($"  => {tri}{(applies ? ", applies" : ", does not apply")}");
                if (applies) matched.Add(r);
            }

            if (matched.FirstOrDefault(m => m.Rule.Mode == ToolMode.Deny) is { } deny)
            {
                steps.Add(new("rule", "Deny", "rule " + deny.Rule.Name, Changed: true));
                return new PolicyDecision(ToolMode.Deny, plain, rule + "+rule:" + deny.Rule.Name) { RuleName = deny.Rule.Name, Trace = lines ?? [], Steps = steps };
            }

            var queueRules = matched.Where(m => m.Rule.Mode == ToolMode.Queue).ToList();
            if (queueRules.Count > 0)
            {
                var before = mode;
                mode = ToolPolicy.Stricter(mode, ToolMode.Queue);
                tightened = true;
                winner = queueRules[0].Rule.Name;
                effective = Merge(effective, queueRules.Select(q => q.Rule));
                rule += "+rule:" + winner;
                steps.Add(new("rule", "Queue", "rule " + string.Join(", ", queueRules.Select(q => q.Rule.Name)), Changed: mode != before));
            }
            else if (mode == ToolMode.Queue && matched.FirstOrDefault(m => m.Rule.Mode == ToolMode.Execute) is { } exec)
            {
                mode = ToolMode.Execute;
                winner = exec.Rule.Name;
                rule += "+rule:" + winner + ":execute";
                steps.Add(new("rule", "Execute", "rule " + exec.Rule.Name + (exec.Rule.Budget is null ? "" : ", within its budget"), Changed: true));
                if (exec.Rule.Budget is { } b) admissions = [BudgetAdmission(tool.Name, requesterId, b)];
                if (exec.Rule.DelayFor is { } delay) effective = effective with { DelayFor = delay, Delay = false };
            }
        }

        // A promotion never beats a Queue/Deny rule that matched, and an explicit Execute rule already decided.
        if (mode == ToolMode.Queue && !tightened && winner is null && !IsAdministration(tool) && IsPromoted(tool.Name))
        {
            mode = ToolMode.Execute;
            rule += "+trust:execute";
            steps.Add(new("trust", "Execute", "promoted after clean approvals", Changed: true));
            if (policy.TrustRamp?.DelayFor is { } trustDelay) effective = effective with { DelayFor = trustDelay, Delay = false };
        }

        // Absolute: a caller that read untrusted content never gets a high-impact tool run without a reviewer.
        // There is deliberately no rule-level opt-out (an opt-in would be an injection target).
        if (tainted && tool.IsHighImpact && mode == ToolMode.Execute)
        {
            mode = ToolMode.Queue;
            admissions = [];
            rule += "+taint:queue";
            lines?.Add("taint: the caller read untrusted content, so this high-impact tool is held for a reviewer.");
            steps.Add(new("taint", "Queue", "the caller read untrusted content; the tool is high-impact", Changed: true));
        }
        else if (tainted)
            steps.Add(new("taint", "no change", tool.IsHighImpact ? "tainted; already held or denied" : "tainted; the tool is not high-impact"));
        else
            steps.Add(new("taint", "clean"));

        return new PolicyDecision(mode, effective, rule)
        {
            RuleName = winner,
            Admissions = admissions,
            FallbackPolicy = plain,
            Trace = lines ?? [],
            Steps = steps,
        };
    }

    /// <summary>Is the tool ever usable? Used to decide what tools/list advertises (never varies by caller).</summary>
    public bool IsAdvertised(ToolDescriptor tool) => Resolve(tool, tainted: false).Mode != ToolMode.Deny;

    /// <summary>Advertised to a caller with this agent profile: policy does not deny it and the profile admits it.</summary>
    public bool IsAdvertised(ToolDescriptor tool, string? profile, bool localOperator = false) =>
        Admits(tool, profile, localOperator) && IsAdvertised(tool);

    /// <summary>Does the agent profile allow this tool? No profile allows every tool except administration tools, which only the local
    /// operator (stdio, or a trusted in-process caller) reaches without a profile naming them. A profile name the configuration does not
    /// define allows nothing: a credential pointing at a missing profile fails closed.</summary>
    public bool Admits(ToolDescriptor tool, string? profile, bool localOperator = false)
    {
        // Asking for temporary access is open to a profile that has something it may ask for.
        if (tool.Name == AccessGrants.RequestTool)
            return profile is null ? localOperator : _config.Agents.TryGetValue(profile, out var asker) && asker.Requestable.Count > 0;
        var administers = IsAdministration(tool);
        if (profile is null) return !administers || localOperator;
        return _config.Agents.TryGetValue(profile, out var p) && AgentProfiles.Grants(p, tool, administers);
    }

    /// <summary>An administration tool: marked by its module, or by the operator in its policy entry (which can only add the mark).</summary>
    public bool IsAdministration(ToolDescriptor tool) =>
        tool.Administers || _config.Tools.TryGetValue(tool.Name, out var p) && p.Administers;

    private static JsonElement ParseArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default; // Undefined: every test on it is unknown
        }
    }

    private static Tri Evaluate(CompiledRule r, EvalContext ctx)
    {
        try { return r.When.Eval(ctx); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Tri.Unknown; }
    }

    /// <summary>Every top-level argument present must be looked at by the rule (or named in IgnoreArgs). Otherwise a model could add
    /// a field the rule never inspected (a Bcc, say) and ride the rule.</summary>
    private static bool Covers(CompiledRule r, JsonElement root, List<string>? lines)
    {
        if (root.ValueKind != JsonValueKind.Object) { lines?.Add("  coverage: the arguments are not a JSON object"); return false; }
        var uncovered = root.EnumerateObject().Select(p => p.Name).Where(n => !r.Referenced.Contains(n) && !r.Ignored.Contains(n)).ToList();
        if (uncovered.Count > 0) lines?.Add($"  coverage: the rule does not account for argument(s) {string.Join(", ", uncovered)}");
        return uncovered.Count == 0;
    }

    private static ToolPolicy Merge(ToolPolicy basePolicy, IEnumerable<PolicyRule> queueRules)
    {
        var merged = basePolicy;
        foreach (var q in queueRules)
        {
            if (q.Reviewer is { } rv && merged.Reviewer == basePolicy.Reviewer) merged = merged with { Reviewer = rv };
            if (q.ReviewerGroup is { } g && merged.ReviewerGroup is null) merged = merged with { ReviewerGroup = g };
            if (q.RequiredApprovals is { } n) merged = merged with { RequiredApprovals = Math.Max(merged.RequiredApprovals, n) };
            if (q.ExpireAfter is { } e) merged = merged with { ExpireAfter = merged.ExpireAfter is { } cur ? (e < cur ? e : cur) : e };
        }
        return merged;
    }

    private static Admission BudgetAdmission(string tool, string? requesterId, RuleBudget b) => new(
        new ActionQuery
        {
            ToolName = tool,
            RequesterId = b.Scope == BudgetScope.Requester ? requesterId : null,
            AnyOfStatus = [ActionStatus.Succeeded, ActionStatus.Executing],
        },
        b.Per, b.MaxCount);
}
