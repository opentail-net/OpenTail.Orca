using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTail.Host;

/// <summary>Operator configuration. Loaded from a declarative file; never derived from tool arguments or model output.</summary>
public sealed class PolicyConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(), new DurationConverter() },
    };

    /// <summary>Mode for Read-risk tools that have no explicit entry. Everything else without an entry is denied.</summary>
    public ToolMode DefaultReadMode { get; init; } = ToolMode.Deny;

    /// <summary>Treat calls that carry no session handle as tainted (recommended).</summary>
    public bool SessionlessTainted { get; init; } = true;

    public static readonly TimeSpan DefaultTaintWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultSessionIdleTtl = TimeSpan.FromDays(14);

    /// <summary>How long, after a caller read untrusted content, <i>every</i> session of that caller counts as tainted
    /// (so a new session cannot launder it). Zero: taint is per session only (v1 behavior).</summary>
    public TimeSpan TaintWindow { get; init; } = DefaultTaintWindow;

    /// <summary>Finished actions (and so the argument values stored in them) are deleted after this many days. Zero keeps them forever.
    /// The audit trail, which holds no argument values, is always kept.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Decision records (what is kept of a purged action: how it was decided, never its arguments) are deleted after this many
    /// days. Zero keeps them forever (the default). Set it to the customer's obligation: at least six months for high-risk uses under
    /// the EU AI Act, often years in finance. Audit entries themselves are always kept.</summary>
    public int AuditRetentionDays { get; init; }

    /// <summary>Sessions idle for longer than this are deleted.</summary>
    public TimeSpan SessionIdleTtl { get; init; } = DefaultSessionIdleTtl;

    /// <summary>Cap on actions a single requester may have waiting for a reviewer. Zero means unlimited.</summary>
    public int MaxPendingPerRequester { get; init; }

    /// <summary>Which sign-in subjects are the same human, for self-approval: <c>{ "alice": ["admin:alice", "key:alices-agent"] }</c>.</summary>
    public Dictionary<string, List<string>> People { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, ToolPolicy> Tools { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Agent profiles by name: the tools each agent identity may call (see <see cref="AgentProfile"/>). An API key names one
    /// with <c>Profile</c>; a caller without a profile keeps every tool policy allows.</summary>
    public Dictionary<string, AgentProfile> Agents { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Adaptive limits: which signals move an identity up the ladder, report only or enforced (see <see cref="LimitsConfig"/>).</summary>
    public LimitsConfig Limits { get; init; } = new();

    /// <summary>Where labelled data may go without review, per label (see <see cref="FlowRule"/>). Checked at outbound tools.</summary>
    public Dictionary<string, FlowRule> DataFlow { get; init; } = new(StringComparer.Ordinal);

    /// <summary><c>ReportOnly</c> (default): a broken flow rule is audited as what would happen. <c>Enforce</c>: it queues or denies. <c>Off</c>.</summary>
    public LimitMode DataFlowMode { get; init; } = LimitMode.ReportOnly;

    /// <summary>Capacity limits by tool name or <c>module.*</c>, across all callers (see <see cref="CapacityLimit"/>).</summary>
    public Dictionary<string, CapacityLimit> Capacity { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Markers of decoy records (honeytokens): a fake customer id, invoice number or address that no real task needs, planted in
    /// your systems. See <see cref="LimitsConfig.Decoys"/>. At least 8 characters each, so they never match ordinary text.</summary>
    public List<string> Decoys { get; init; } = [];

    /// <summary>When people are around (see <see cref="ToolPolicy.DelayUntilWorkingHours"/>). Required when a tool opts in.</summary>
    public WorkingHours? WorkingHours { get; init; }

    /// <summary>Work items and continuations: whether the work tools are offered, leases, runs, time limits and note sizes
    /// (see <see cref="WorkConfig"/>).</summary>
    public WorkConfig Work { get; init; } = new();

    /// <summary>What is kept of what an AI reviewer saw (audit D6): <c>Hash</c> (default) keeps its SHA-256 on the review; <c>Full</c>
    /// also keeps the input itself, with the action, purged with it.</summary>
    public AiInputKeep AiReviewInput { get; init; } = AiInputKeep.Hash;

    /// <summary>A call that runs longer than this becomes an MCP task for a client that opted in to the Tasks extension, and keeps
    /// running in the background (docs/design/mcp-tasks.md, step 3). Other clients wait as before. Default 20 seconds (provisional).</summary>
    public TimeSpan TaskInlineLimit { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Automatic retries and paused routes (see <see cref="FailuresConfig"/>).</summary>
    public FailuresConfig Failures { get; init; } = new();

    /// <summary>Policy fragments to add, relative to this file: a directory (every <c>*.json</c> in it, in name order) or a file.
    /// A fragment may only hold <c>Tools</c>, and only for the module its file is named after (<c>policy.d/gh.json</c> holds <c>gh.*</c>).
    /// <c>--adopt-policy</c> writes fragments; the operator's own file is never rewritten.</summary>
    public List<string> Include { get; init; } = [];

    /// <summary>Fragment files that were merged, for display.</summary>
    [JsonIgnore] public IReadOnlyList<string> IncludedFiles { get; private init; } = [];

    public static PolicyConfig Deny { get; } = new();

    public static PolicyConfig Parse(string json) => Parse(json, baseDirectory: null);

    /// <summary>A stored policy version (<see cref="ToCanonicalJson"/>): already merged, so its <c>Include</c> list is not followed.</summary>
    public static PolicyConfig ParseStored(string canonicalJson) =>
        JsonSerializer.Deserialize<PolicyConfig>(canonicalJson, JsonOptions) ?? throw new InvalidOperationException("Empty policy version.");

    /// <summary>The whole effective policy (all files merged) as canonical JSON: object keys sorted at every level, so the same policy
    /// always gives the same text, whatever order its files listed things in. What <see cref="Hash"/> is computed over and what
    /// policy versions store.</summary>
    public string ToCanonicalJson()
    {
        var node = JsonSerializer.SerializeToNode(this, JsonOptions);
        return Sorted(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

        static System.Text.Json.Nodes.JsonNode? Sorted(System.Text.Json.Nodes.JsonNode? n) => n switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(Sorted)]),
            _ => n?.DeepClone(),
        };
    }

    /// <summary>SHA-256 of <see cref="ToCanonicalJson"/>: <c>sha256:</c> and 64 lower-case hex digits. Stamped on every action decided
    /// under this policy.</summary>
    public string Hash() => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ToCanonicalJson())));

    private static PolicyConfig Parse(string json, string? baseDirectory)
    {
        var cfg = JsonSerializer.Deserialize<PolicyConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Policy file is empty.");
        if (cfg.Include.Count > 0 && baseDirectory is null)
            throw new InvalidOperationException("Include only works when the policy is loaded from a file.");
        var tools = new Dictionary<string, ToolPolicy>(cfg.Tools, StringComparer.Ordinal);
        var included = new List<string>();
        foreach (var file in cfg.Include.SelectMany(i => ExpandInclude(baseDirectory!, i)))
        {
            foreach (var (name, policy) in ReadFragment(file))
                if (!tools.TryAdd(name, policy))
                    throw new InvalidOperationException($"{file}: tool '{name}' already has a policy entry elsewhere; keep one.");
            included.Add(file);
        }
        return new PolicyConfig
        {
            DefaultReadMode = cfg.DefaultReadMode,
            SessionlessTainted = cfg.SessionlessTainted,
            MaxPendingPerRequester = cfg.MaxPendingPerRequester,
            TaintWindow = cfg.TaintWindow,
            SessionIdleTtl = cfg.SessionIdleTtl,
            RetentionDays = cfg.RetentionDays,
            AuditRetentionDays = cfg.AuditRetentionDays,
            People = new Dictionary<string, List<string>>(cfg.People, StringComparer.Ordinal),
            Tools = tools,
            Agents = new Dictionary<string, AgentProfile>(cfg.Agents, StringComparer.Ordinal),
            Limits = cfg.Limits,
            DataFlow = new Dictionary<string, FlowRule>(cfg.DataFlow, StringComparer.Ordinal),
            DataFlowMode = cfg.DataFlowMode,
            Capacity = new Dictionary<string, CapacityLimit>(cfg.Capacity, StringComparer.Ordinal),
            Decoys = [.. cfg.Decoys],
            WorkingHours = cfg.WorkingHours,
            Work = cfg.Work,
            Failures = cfg.Failures,
            AiReviewInput = cfg.AiReviewInput,
            TaskInlineLimit = cfg.TaskInlineLimit,
            Include = cfg.Include,
            IncludedFiles = included,
        };
    }

    private sealed class Fragment
    {
        public Dictionary<string, ToolPolicy> Tools { get; init; } = new(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExpandInclude(string baseDirectory, string include)
    {
        var path = Path.GetFullPath(Path.Combine(baseDirectory, include));
        if (Directory.Exists(path)) return Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal);
        if (File.Exists(path)) return [path];
        // A fragment directory that does not exist yet simply has no fragments (nothing adopted so far); a named file must exist.
        if (include.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Included policy file {path} does not exist.");
        return [];
    }

    /// <summary>A fragment: only <c>Tools</c>, only for the module its file is named after.</summary>
    public static Dictionary<string, ToolPolicy> ReadFragment(string file) => ParseFragment(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), file);

    public static Dictionary<string, ToolPolicy> ParseFragment(string json, string moduleId, string where)
    {
        Fragment fragment;
        try { fragment = JsonSerializer.Deserialize<Fragment>(json, JsonOptions) ?? new Fragment(); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{where}: a policy fragment may only contain \"Tools\" ({ex.Message})");
        }
        foreach (var name in fragment.Tools.Keys.Where(n => !n.StartsWith(moduleId + ".", StringComparison.Ordinal)))
            throw new InvalidOperationException($"{where}: names tool '{name}', but a fragment for module '{moduleId}' may only name '{moduleId}.*' tools.");
        return new Dictionary<string, ToolPolicy>(fragment.Tools, StringComparer.Ordinal);
    }

    private static void ValidateRules(string tool, ToolPolicy policy, IReadOnlyList<PolicyRule> rules, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            var at = $"Tool '{tool}', rule '{r.Name}'";
            if (string.IsNullOrWhiteSpace(r.Name)) { errors.Add($"Tool '{tool}': every rule needs a Name."); continue; }
            if (!names.Add(r.Name)) errors.Add($"{at}: duplicate rule name.");
            if (r.Mode == ToolMode.Execute && policy.Mode != ToolMode.Queue)
                errors.Add($"{at}: an Execute rule only makes sense when the tool's own Mode is Queue (a rule can relax a queue, it cannot create access).");
            if (r.Budget is { } b)
            {
                if (r.Mode != ToolMode.Execute) errors.Add($"{at}: a Budget belongs on an Execute rule.");
                if (b.Per <= TimeSpan.Zero || b.MaxCount <= 0) errors.Add($"{at}: Budget needs a positive Per and MaxCount.");
            }
            if (r.IgnoreArgs is not null && r.Mode != ToolMode.Execute) errors.Add($"{at}: IgnoreArgs only applies to Execute rules.");
            if (r.DelayFor is { } d)
            {
                if (r.Mode != ToolMode.Execute) errors.Add($"{at}: DelayFor only applies to Execute rules.");
                if (r.Budget is not null) errors.Add($"{at}: DelayFor and Budget cannot be combined.");
                if (d <= TimeSpan.Zero || d > TimeSpan.FromDays(7)) errors.Add($"{at}: DelayFor must be positive and at most 7 days.");
            }
            if (r.RequiredApprovals is < 1 or > 5) errors.Add($"{at}: RequiredApprovals must be between 1 and 5.");
            if (r.ExpireAfter is { } e && e <= TimeSpan.Zero) errors.Add($"{at}: ExpireAfter must be positive.");
            if (r.Mode != ToolMode.Queue && (r.Reviewer is not null || r.ReviewerGroup is not null || r.RequiredApprovals is not null || r.ExpireAfter is not null))
                errors.Add($"{at}: Reviewer, ReviewerGroup, RequiredApprovals and ExpireAfter only apply to Queue rules.");
            try { RuleCompiler.Compile(tool, r); }
            catch (RuleException ex) { errors.Add(ex.Message); }
        }
    }

    public static PolicyConfig Load(string path) => Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    /// <summary>Fail-fast validation: entries naming unknown tools are almost certainly typos that would silently deny.</summary>
    /// <param name="retiredModules">Modules switched off: profile entries naming them are tolerated (the builder warns).</param>
    public IReadOnlyList<string> Validate(IEnumerable<string> knownTools, IEnumerable<string>? retiredModules = null)
    {
        var known = knownTools.ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var name in Tools.Keys.Where(k => !known.Contains(k)))
            errors.Add($"Policy names unknown tool '{name}'.");
        foreach (var (name, p) in Tools)
        {
            if (p.ExpireAfter is { } e && e <= TimeSpan.Zero)
                errors.Add($"Tool '{name}': ExpireAfter must be positive.");
            if (p.DelayFor is { } df && (df <= TimeSpan.Zero || df > TimeSpan.FromDays(7)))
                errors.Add($"Tool '{name}': DelayFor must be positive and at most 7 days.");
            if (p.EffectiveDelay is not null && p.Mode != ToolMode.Execute && p.Rules is not { Count: > 0 })
                errors.Add($"Tool '{name}': a delay (DelayFor or Delay) only applies to Mode Execute.");
            if (p.RateLimit is { PerHour: < 0 } or { PerDay: < 0 })
                errors.Add($"Tool '{name}': rate limits cannot be negative.");
            if (p.TrustRamp is { AfterCleanApprovals: < 0 })
                errors.Add($"Tool '{name}': TrustRamp.AfterCleanApprovals cannot be negative.");
            if (p.TrustRamp is { AfterCleanApprovals: > 0 } && p.Mode != ToolMode.Queue)
                errors.Add($"Tool '{name}': a trust ramp only makes sense for Mode=Queue.");
            if (p.AiMaxRisk == ToolRisk.Destructive)
                errors.Add($"Tool '{name}': AiMaxRisk cannot be Destructive; an AI reviewer never decides destructive actions (use Read, Write or ExternalEffect).");
            if (p.AiMaxRejections < 0) errors.Add($"Tool '{name}': AiMaxRejections cannot be negative.");
            if (p.AiBudget is { } ab && (ab.Per <= TimeSpan.Zero || ab.MaxCount <= 0))
                errors.Add($"Tool '{name}': AiBudget needs a positive Per and MaxCount.");
        }
        var seenSubjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (person, subjects) in People)
            foreach (var s in subjects)
            {
                if (string.IsNullOrWhiteSpace(s) || !s.Contains(':', StringComparison.Ordinal) && s != Subjects.Local)
                    errors.Add($"People '{person}': '{s}' is not a namespaced subject (like admin:alice, key:name, jwt:issuer|sub).");
                else if (seenSubjects.TryGetValue(PeopleMap.CanonicalSubject(s), out var other) && other != person)
                    errors.Add($"People: '{s}' is listed under both '{other}' and '{person}'.");
                else seenSubjects[PeopleMap.CanonicalSubject(s)] = person;
            }
        foreach (var (name, p) in Tools)
        {
            if (p.RequiredApprovals > 1 && p.TrustRamp is { AfterCleanApprovals: > 0 })
                errors.Add($"Tool '{name}': a trust ramp makes no sense with several required approvers.");
            if (p.RequiredApprovals is < 1 or > 5) errors.Add($"Tool '{name}': RequiredApprovals must be between 1 and 5.");
            if (p.Rules is { Count: > 0 } rules) ValidateRules(name, p, rules, errors);
        }
        AgentProfiles.Validate(Agents, known, (retiredModules ?? []).ToHashSet(StringComparer.Ordinal), errors);
        foreach (var (name, profile) in Agents)
            foreach (var target in profile.MayDelegateTo.Where(t => !Agents.ContainsKey(t)))
                errors.Add($"Agent profile '{name}': MayDelegateTo names '{target}', which is not an agent profile.");
        if (Work.MaxRuns < 1) errors.Add("Work.MaxRuns must be at least 1.");
        if (Failures.AutoRetries is < 0 or > 10) errors.Add("Failures.AutoRetries must be between 0 and 10.");
        if (Failures.CircuitThreshold < 2) errors.Add("Failures.CircuitThreshold must be at least 2.");
        if (Failures.ReleasePerSweep < 1) errors.Add("Failures.ReleasePerSweep must be at least 1.");
        if (Work.Lease < TimeSpan.FromSeconds(30)) errors.Add("Work.Lease must be at least 30 seconds.");
        if (Work.DefaultTtl <= TimeSpan.Zero) errors.Add("Work.DefaultTtl must be positive.");
        if (Work.MaxDelegationDepth < 1) errors.Add("Work.MaxDelegationDepth must be at least 1.");
        foreach (var r in Work.Runners)
        {
            if (!(r.For.StartsWith("profile:", StringComparison.Ordinal) || r.For.StartsWith("subject:", StringComparison.Ordinal)))
                errors.Add($"Work.Runners: For '{r.For}' must be profile:<name> or subject:<id>.");
            if (string.IsNullOrWhiteSpace(r.Command)) errors.Add($"Work.Runners: the runner for '{r.For}' needs a Command.");
            if (r.MaxConcurrent < 1) errors.Add($"Work.Runners: MaxConcurrent for '{r.For}' must be at least 1.");
        }
        DataLabels.Validate(this, known, errors);
        if (WorkingHours is { } wh) errors.AddRange(wh.Errors());
        foreach (var name in Tools.Where(t => t.Value.DelayUntilWorkingHours).Select(t => t.Key))
        {
            if (WorkingHours is null) errors.Add($"Tool '{name}': DelayUntilWorkingHours needs the policy's WorkingHours.");
            if (Tools[name].EffectiveDelay is null) errors.Add($"Tool '{name}': DelayUntilWorkingHours needs a delay (DelayFor or Delay).");
        }
        foreach (var d in Decoys.Where(d => d is null || d.Trim().Length < 8))
            errors.Add($"Decoy '{d}' is too short: use at least 8 characters, so it never matches ordinary text.");
        foreach (var (key, c) in Capacity)
        {
            if (key.EndsWith(".*", StringComparison.Ordinal) ? !known.Any(t => t.StartsWith(key[..^1], StringComparison.Ordinal)) : !known.Contains(key))
                errors.Add($"Capacity names '{key}', which matches no loaded tool.");
            if (c.PerMinute <= 0) errors.Add($"Capacity '{key}': PerMinute must be positive.");
            if (c.Burst is <= 0) errors.Add($"Capacity '{key}': Burst must be positive.");
            if (c.MaxDelay < TimeSpan.Zero || c.MaxDelay > TimeSpan.FromSeconds(30)) errors.Add($"Capacity '{key}': MaxDelay must be between 0 and 30 seconds.");
        }
        if (MaxPendingPerRequester < 0) errors.Add("MaxPendingPerRequester cannot be negative.");
        if (TaintWindow < TimeSpan.Zero) errors.Add("TaintWindow cannot be negative.");
        if (SessionIdleTtl <= TimeSpan.Zero) errors.Add("SessionIdleTtl must be positive.");
        if (RetentionDays < 0) errors.Add("RetentionDays cannot be negative.");
        if (AuditRetentionDays < 0) errors.Add("AuditRetentionDays cannot be negative.");
        if (AuditRetentionDays > 0 && (RetentionDays == 0 || AuditRetentionDays < RetentionDays))
            errors.Add("AuditRetentionDays must be zero (keep) or at least RetentionDays: the record of a decision never goes before the action it records.");
        return errors;
    }
}

/// <summary>How much of an AI reviewer's input the Host keeps.</summary>
public enum AiInputKeep { Hash, Full }
