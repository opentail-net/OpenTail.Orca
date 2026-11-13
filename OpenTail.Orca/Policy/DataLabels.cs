using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>Where one label's data may go without review (policy <c>DataFlow</c>).</summary>
public sealed class FlowRule
{
    /// <summary>Destinations: tool names, <c>module.*</c> or <c>*</c>, allowed whatever the arguments.</summary>
    public List<string> Allow { get; init; } = [];

    /// <summary>Destinations allowed only when their arguments match a condition, in the policy rules' language:
    /// <c>{ "Tool": "mail.send", "When": { "arg": "/to", "all": { "domainIn": ["ourcompany.com"] } } }</c>. A condition that is unknown
    /// (a missing argument, an address that does not parse) does not allow: it errs toward caution, as rules do.</summary>
    public List<FlowDestination> Destinations { get; init; } = [];

    /// <summary>What happens at any other outbound tool: <c>Queue</c> (a person decides, the default) or <c>Deny</c>.</summary>
    public ToolMode Otherwise { get; init; } = ToolMode.Queue;

    /// <summary>When the outbound call's arguments contain none of the distinctive values the session's labelled reads returned, do not
    /// queue it (value matching). Opt-in: re-encoded data (base64, a paraphrase) is not caught, so this only relaxes a Queue, never a Deny.</summary>
    public bool RelaxWithoutMatch { get; init; }
}

public sealed class FlowDestination
{
    public string Tool { get; init; } = "";
    public System.Text.Json.JsonElement When { get; init; }
}

/// <summary>A label a session holds and the tools it came from.</summary>
public sealed record HeldLabel(string Label, IReadOnlyList<string> Sources);

/// <summary>What a flow rule says about one outbound call. <c>Relaxed</c>: the rule allows relaxing and no value matched.</summary>
public sealed record FlowOutcome(string Label, ToolMode Mode, bool RelaxWithoutMatch = false);

/// <summary>Where session labels live: per session, and per caller for the label window (so a new session cannot launder them).</summary>
public interface ILabelStore
{
    void Add(string scope, string label, string source, DateTimeOffset until);
    IReadOnlyList<(string Label, string Source)> Get(string scope, DateTimeOffset now);
    int Purge(DateTimeOffset now);
}

public sealed class InMemoryLabelStore : ILabelStore
{
    private readonly ConcurrentDictionary<(string Scope, string Label, string Source), DateTimeOffset> _rows = new();

    public void Add(string scope, string label, string source, DateTimeOffset until) =>
        _rows.AddOrUpdate((scope, label, source), until, (_, old) => until > old ? until : old);

    public IReadOnlyList<(string Label, string Source)> Get(string scope, DateTimeOffset now) =>
        _rows.Where(kv => kv.Key.Scope == scope && kv.Value > now).Select(kv => (kv.Key.Label, kv.Key.Source)).ToList();

    public int Purge(DateTimeOffset now)
    {
        var n = 0;
        foreach (var k in _rows.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            if (_rows.TryRemove(k, out _)) n++;
        return n;
    }
}

/// <summary>
/// The other half of taint (data-flow design): label data where it enters, carry the labels on the session, check them where data
/// leaves. Coarse on purpose: "this session has read finance data", never a claim to follow values through the model. Labels only
/// accumulate; configuration only adds them.
/// </summary>
public sealed class DataLabels(ILabelStore store, PolicyConfig config, TimeProvider clock)
{
    // Conditions compiled once; the policy was validated (and so compiled once already) before the Host was built.
    private readonly Dictionary<(string Label, int Index), Condition> _conditions = config.DataFlow
        .SelectMany(kv => kv.Value.Destinations.Select((d, i) => (Key: (kv.Key, i), d)))
        .ToDictionary(x => x.Key, x => ConditionCompiler.Compile(x.d.When, $"DataFlow '{x.Key.Key}', destination {x.Key.i + 1}"));

    /// <summary>The labels the Host ships (owner's decision, 2026-10-18); operators may define more.</summary>
    public static readonly IReadOnlyList<string> Fixed = ["finance", "personal", "hr", "customer", "secret"];

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string label) => NamePattern.IsMatch(label);

    public LimitMode Mode => config.DataFlowMode;

    /// <summary>The labels a tool's results carry: its module's, plus any its policy entry adds.</summary>
    public IReadOnlyList<string> Of(ToolDescriptor tool) =>
        tool.Labels.Concat(config.Tools.TryGetValue(tool.Name, out var p) ? p.Labels : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The session (or, without one, the caller) gains the tool's labels, for the session's life and the caller's label window.</summary>
    public void Gain(string? sessionId, string subject, ToolDescriptor tool)
    {
        var now = clock.GetUtcNow();
        foreach (var label in Of(tool))
        {
            if (sessionId is not null) store.Add("s:" + sessionId, label, tool.Name, now + config.SessionIdleTtl);
            if (config.TaintWindow > TimeSpan.Zero || sessionId is null)
                store.Add("u:" + subject, label, tool.Name, now + (config.TaintWindow > TimeSpan.Zero ? config.TaintWindow : config.SessionIdleTtl));
        }
    }

    /// <summary>Labels carried with work (a resume item, a delegated item, a thread note) are gained by whoever reads it, with the tools
    /// they originally came from, the same way as <see cref="Gain"/>: waiting or passing work on does not clean a session.</summary>
    public void GainCarried(string? sessionId, string subject, IEnumerable<CarriedLabel> carried)
    {
        var now = clock.GetUtcNow();
        foreach (var c in carried)
        {
            if (sessionId is not null) store.Add("s:" + sessionId, c.Label, c.Source, now + config.SessionIdleTtl);
            if (config.TaintWindow > TimeSpan.Zero || sessionId is null)
                store.Add("u:" + subject, c.Label, c.Source, now + (config.TaintWindow > TimeSpan.Zero ? config.TaintWindow : config.SessionIdleTtl));
        }
    }

    /// <summary>Every label held by the session and by the caller (within the window), with the tools it came from.</summary>
    public IReadOnlyList<HeldLabel> Held(string? sessionId, string subject)
    {
        var now = clock.GetUtcNow();
        var rows = store.Get("u:" + subject, now).Concat(sessionId is null ? [] : store.Get("s:" + sessionId, now));
        return rows.GroupBy(r => r.Label, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new HeldLabel(g.Key, g.Select(r => r.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())).ToList();
    }

    /// <summary>Is this an exit? Every ExternalEffect tool (the design's proposal; which writes also count is still open).</summary>
    public static bool IsExit(ToolDescriptor tool) => tool.Risk == ToolRisk.ExternalEffect;

    /// <summary>The flow rules an outbound call breaks, given the labels held. Labels without a rule are not checked.</summary>
    public IReadOnlyList<FlowOutcome> Check(ToolDescriptor tool, IEnumerable<string> labels, string argumentsJson = "{}")
    {
        if (!IsExit(tool)) return [];
        var broken = new List<FlowOutcome>();
        System.Text.Json.JsonElement? args = null;
        foreach (var label in labels)
        {
            if (!config.DataFlow.TryGetValue(label, out var rule) || rule.Allow.Any(a => Matches(a, tool.Name))) continue;
            var allowed = false;
            for (var i = 0; i < rule.Destinations.Count && !allowed; i++)
            {
                if (!Matches(rule.Destinations[i].Tool, tool.Name)) continue;
                args ??= Parse(argumentsJson);
                try { allowed = _conditions[(label, i)].Eval(new EvalContext(args.Value, clock.GetUtcNow(), null)) == Tri.True; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { allowed = false; }
            }
            if (!allowed) broken.Add(new FlowOutcome(label, rule.Otherwise, rule.RelaxWithoutMatch));
        }
        return broken;
    }

    private static System.Text.Json.JsonElement Parse(string json)
    {
        try { using var doc = System.Text.Json.JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return default; }
    }

    private static bool Matches(string entry, string tool) =>
        entry == "*" || entry == tool || entry.EndsWith(".*", StringComparison.Ordinal) && tool.StartsWith(entry[..^1], StringComparison.Ordinal);

    public int Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _fingerprints.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) _fingerprints.TryRemove(key, out _);
        return store.Purge(now);
    }

    // ---- value matching: salted fingerprints of distinctive values, per session and caller, in memory, never the values ----

    private readonly byte[] _salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (HashSet<string> Prints, DateTimeOffset Until)> _fingerprints = new(StringComparer.Ordinal);
    private const int MaxPrintsPerScope = 5000;

    /// <summary>Remembers fingerprints of the distinctive values a labelled tool returned (ids, account numbers, addresses, longer text).</summary>
    public void RememberValues(string? sessionId, string subject, ToolDescriptor tool, string resultJson)
    {
        if (Of(tool).Count == 0) return;
        var prints = Distinctive(resultJson).Select(Print).ToList();
        if (prints.Count == 0) return;
        var until = clock.GetUtcNow() + (config.TaintWindow > TimeSpan.Zero ? config.TaintWindow : config.SessionIdleTtl);
        foreach (var scope in sessionId is null ? ["u:" + subject] : new[] { "s:" + sessionId, "u:" + subject })
            _fingerprints.AddOrUpdate(scope,
                _ => (prints.Take(MaxPrintsPerScope).ToHashSet(StringComparer.Ordinal), until),
                (_, old) => { lock (old.Prints) foreach (var p in prints) if (old.Prints.Count < MaxPrintsPerScope) old.Prints.Add(p); return (old.Prints, until > old.Until ? until : old.Until); });
    }

    /// <summary>Do the arguments contain any value a labelled read returned to this session or caller?</summary>
    public bool ArgumentsMatch(string? sessionId, string subject, string argumentsJson)
    {
        var now = clock.GetUtcNow();
        var held = (sessionId is null ? ["u:" + subject] : new[] { "s:" + sessionId, "u:" + subject })
            .Select(s => _fingerprints.TryGetValue(s, out var f) && f.Until > now ? f.Prints : null).OfType<HashSet<string>>().ToList();
        if (held.Count == 0) return false;
        foreach (var candidate in Distinctive(argumentsJson, splitText: true).Select(Print))
            foreach (var set in held)
                lock (set) if (set.Contains(candidate)) return true;
        return false;
    }

    private string Print(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(_salt, System.Text.Encoding.UTF8.GetBytes(value)))[..24];

    /// <summary>Values distinctive enough to be evidence: strings of 8+ characters and numbers of 6+ digits, normalised; in arguments
    /// also the words of longer text, so a copied id inside a mail body is found.</summary>
    internal static IEnumerable<string> Distinctive(string json, bool splitText = false)
    {
        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(json); }
        catch (System.Text.Json.JsonException) { yield break; }
        using (doc)
        {
            var stack = new Stack<System.Text.Json.JsonElement>([doc.RootElement]);
            var count = 0;
            while (stack.Count > 0 && count < 20_000)
            {
                var e = stack.Pop();
                switch (e.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object: foreach (var p in e.EnumerateObject()) stack.Push(p.Value); break;
                    case System.Text.Json.JsonValueKind.Array: foreach (var i in e.EnumerateArray()) stack.Push(i); break;
                    case System.Text.Json.JsonValueKind.Number when e.GetRawText().Count(char.IsDigit) >= 6: count++; yield return e.GetRawText(); break;
                    case System.Text.Json.JsonValueKind.String:
                        var s = e.GetString()!.Trim().ToLowerInvariant();
                        if (s.Length >= 8 && s.Length <= 200) { count++; yield return s; }
                        if (splitText || s.Length > 200)
                            foreach (var w in s.Split([' ', '\n', '\r', '\t', ',', ';', ':', '(', ')', '"', '\'', '<', '>'], StringSplitOptions.RemoveEmptyEntries))
                                if (w.Trim('.').Length >= 8) { count++; yield return w.Trim('.'); }
                        break;
                }
            }
        }
    }

    internal static void Validate(PolicyConfig config, IReadOnlySet<string> knownTools, List<string> errors)
    {
        foreach (var (tool, p) in config.Tools)
            foreach (var l in p.Labels.Where(l => !IsValidName(l)))
                errors.Add($"Tool '{tool}': label '{l}' must be lower-case letters, digits and '-' (up to 32), starting with a letter.");
        foreach (var (label, rule) in config.DataFlow)
        {
            if (!IsValidName(label)) errors.Add($"DataFlow: label '{label}' must be lower-case letters, digits and '-' (up to 32), starting with a letter.");
            if (rule.Otherwise is not (ToolMode.Queue or ToolMode.Deny)) errors.Add($"DataFlow '{label}': Otherwise must be Queue or Deny.");
            foreach (var a in rule.Allow.Where(a => a != "*" && !a.EndsWith(".*", StringComparison.Ordinal) && !knownTools.Contains(a)))
                errors.Add($"DataFlow '{label}' allows unknown tool '{a}'.");
            for (var i = 0; i < rule.Destinations.Count; i++)
            {
                var d = rule.Destinations[i];
                if (d.Tool != "*" && !d.Tool.EndsWith(".*", StringComparison.Ordinal) && !knownTools.Contains(d.Tool))
                    errors.Add($"DataFlow '{label}', destination {i + 1}: unknown tool '{d.Tool}'.");
                if (d.When.ValueKind != System.Text.Json.JsonValueKind.Object)
                    errors.Add($"DataFlow '{label}', destination {i + 1}: When is required (a condition on the arguments).");
                else
                    try { ConditionCompiler.Compile(d.When, $"DataFlow '{label}', destination {i + 1}"); }
                    catch (RuleException ex) { errors.Add(ex.Message); }
            }
        }
    }
}
