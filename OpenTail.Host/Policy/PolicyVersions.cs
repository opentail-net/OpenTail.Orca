using System.Reflection;

namespace OpenTail.Host;

/// <summary>One stored policy: its hash, its canonical JSON, and when this database first and last saw it loaded.</summary>
public sealed record PolicyVersion(string Hash, string Json, DateTimeOffset FirstLoaded, DateTimeOffset LastLoaded);

/// <summary>Every policy a Host has run under, kept by hash (audit D2), so "what did the policy say on that day" is a lookup.</summary>
public interface IPolicyVersionStore
{
    /// <summary>Stores the version if it is new; otherwise moves its last-loaded time. Returns true when it was new.</summary>
    bool Record(string hash, string json, DateTimeOffset at);

    PolicyVersion? Get(string hash);

    /// <summary>All versions, oldest first by first-loaded time.</summary>
    IReadOnlyList<PolicyVersion> List();
}

public sealed class InMemoryPolicyVersionStore : IPolicyVersionStore
{
    private readonly Dictionary<string, PolicyVersion> _versions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool Record(string hash, string json, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_versions.TryGetValue(hash, out var v)) { _versions[hash] = v with { LastLoaded = at }; return false; }
            _versions[hash] = new PolicyVersion(hash, json, at, at);
            return true;
        }
    }

    public PolicyVersion? Get(string hash) { lock (_gate) return _versions.GetValueOrDefault(hash); }

    public IReadOnlyList<PolicyVersion> List() { lock (_gate) return [.. _versions.Values.OrderBy(v => v.FirstLoaded)]; }
}

/// <summary>Records the policy a Host starts with: the version is stored and a <c>policy_loaded</c> audit entry names its hash, the
/// Host's version, the instance and the operator who started it (when known).</summary>
public static class PolicyLoad
{
    /// <summary>The Host's informational version (from its assembly), for audit entries.</summary>
    public static string HostVersion { get; } =
        typeof(PolicyLoad).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>This process: machine name and process id. Distinguishes Host instances sharing one database.</summary>
    public static string Instance { get; } = $"{Environment.MachineName}/{Environment.ProcessId}";

    public static async Task RecordAsync(PolicyResolver policy, IPolicyVersionStore versions, IAuditSink audit, DateTimeOffset at,
        string? startedBy, CancellationToken ct = default)
    {
        // The version this database ran under most recently: if it is another one, the policy changed since then, and what changed in
        // profiles and people is audited (audit D4). A restart under the same policy audits nothing new.
        var latest = versions.List().MaxBy(v => v.LastLoaded);
        var previous = latest is not null && latest.Hash != policy.PolicyHash ? latest : null;
        var isNew = versions.Record(policy.PolicyHash, policy.Config.ToCanonicalJson(), at);
        await audit.AppendAsync(new AuditEntry(at, "policy_loaded", null, null, startedBy ?? "system",
            $"{policy.PolicyHash}{(isNew ? " (new version)" : "")}; host {HostVersion}; instance {Instance}"), ct);
        if (previous is not null)
            await AccessChanges.RecordPolicyChangesAsync(PolicyConfig.ParseStored(previous.Json), policy.Config, audit, at, startedBy, ct);
    }

    /// <summary>A line diff of two policy versions' canonical JSON: lines only in the first start with "-", only in the second with
    /// "+". Small policies, so a plain longest-common-subsequence is enough.</summary>
    public static IReadOnlyList<string> Diff(string fromJson, string toJson)
    {
        var a = fromJson.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var b = toJson.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var lines = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) lines.Add("- " + a[x++]);
            else lines.Add("+ " + b[y++]);
        }
        while (x < a.Length) lines.Add("- " + a[x++]);
        while (y < b.Length) lines.Add("+ " + b[y++]);
        return lines;
    }
}
