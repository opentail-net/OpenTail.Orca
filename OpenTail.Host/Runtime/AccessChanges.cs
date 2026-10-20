using System.Text;
using System.Text.Json;

namespace OpenTail.Host;

/// <summary>An API key as the access record sees it: its name, a prefix of its hash (never the key) and its agent profile.</summary>
public sealed record KeyAccess(string Name, string HashPrefix, string? Profile)
{
    public static KeyAccess Of(string name, string hash, string? profile) =>
        new(name, hash.Length > 19 ? hash[..19] : hash, profile);
}

/// <summary>Somewhere to remember the last state seen (the API keys a Host started with), so the next start can audit what changed.</summary>
public interface IAccessSnapshotStore
{
    string? Get(string key);
    void Set(string key, string value);
}

public sealed class InMemoryAccessSnapshotStore : IAccessSnapshotStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? Get(string key) { lock (_values) return _values.GetValueOrDefault(key); }
    public void Set(string key, string value) { lock (_values) _values[key] = value; }
}

/// <summary>
/// Audit D4: who could do what, and who changed it. API keys live in the HTTP config, so each start compares the keys with the last
/// ones seen and audits what was added, removed, re-profiled or rotated; a new policy version is compared with the previous one and
/// changes to agent profiles and people are audited. <see cref="Report"/> rebuilds access as of a date from those entries.
/// </summary>
public static class AccessChanges
{
    private const string KeysSnapshot = "access.keys";

    /// <summary>The keys as the last start recorded them: name, hash prefix and profile, never the key.</summary>
    public static IReadOnlyList<KeyAccess> CurrentKeys(IAccessSnapshotStore snapshots) =>
        snapshots.Get(KeysSnapshot) is { } json ? JsonSerializer.Deserialize<List<KeyAccess>>(json) ?? [] : [];

    public static async Task RecordKeysAsync(IAccessSnapshotStore snapshots, IAuditSink audit, IEnumerable<KeyAccess> keys, DateTimeOffset at,
        string? by, CancellationToken ct = default)
    {
        var now = keys.OrderBy(k => k.Name, StringComparer.Ordinal).ToList();
        var before = snapshots.Get(KeysSnapshot) is { } json ? JsonSerializer.Deserialize<List<KeyAccess>>(json) ?? [] : [];
        var was = before.ToDictionary(k => k.Name, StringComparer.Ordinal);
        var actor = by ?? "system";
        foreach (var k in now)
        {
            if (!was.TryGetValue(k.Name, out var old))
                await audit.AppendAsync(new AuditEntry(at, "key_added", null, null, actor, $"key:{k.Name} ({k.HashPrefix}…) profile {k.Profile ?? "none"}"), ct);
            else
            {
                if (old.HashPrefix != k.HashPrefix)
                    await audit.AppendAsync(new AuditEntry(at, "key_rotated", null, null, actor, $"key:{k.Name} ({old.HashPrefix}… -> {k.HashPrefix}…)"), ct);
                if (old.Profile != k.Profile)
                    await audit.AppendAsync(new AuditEntry(at, "key_profile_changed", null, null, actor, $"key:{k.Name}: profile {old.Profile ?? "none"} -> {k.Profile ?? "none"}"), ct);
            }
        }
        foreach (var gone in before.Where(b => now.All(k => k.Name != b.Name)))
            await audit.AppendAsync(new AuditEntry(at, "key_removed", null, null, actor, $"key:{gone.Name} ({gone.HashPrefix}…)"), ct);
        snapshots.Set(KeysSnapshot, JsonSerializer.Serialize(now));
    }

    /// <summary>Changes to agent profiles and to who counts as the same person, between two policy versions.</summary>
    public static async Task RecordPolicyChangesAsync(PolicyConfig? previous, PolicyConfig current, IAuditSink audit, DateTimeOffset at, string? by,
        CancellationToken ct = default)
    {
        if (previous is null) return;
        var actor = by ?? "system";
        foreach (var (name, profile) in current.Agents.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (!previous.Agents.TryGetValue(name, out var old))
            {
                await audit.AppendAsync(new AuditEntry(at, "profile_added", null, null, actor, $"{name}: tools {string.Join(", ", profile.Tools)}"), ct);
                continue;
            }
            var change = Describe("tools", old.Tools, profile.Tools) + Describe("requestable", old.Requestable, profile.Requestable)
                         + Describe("may delegate to", old.MayDelegateTo, profile.MayDelegateTo);
            if (change.Length > 0) await audit.AppendAsync(new AuditEntry(at, "profile_changed", null, null, actor, $"{name}:{change}"), ct);
        }
        foreach (var gone in previous.Agents.Keys.Where(k => !current.Agents.ContainsKey(k)).Order(StringComparer.Ordinal))
            await audit.AppendAsync(new AuditEntry(at, "profile_removed", null, null, actor, gone), ct);
        foreach (var person in current.People.Keys.Union(previous.People.Keys).Order(StringComparer.Ordinal))
        {
            var change = Describe("subjects", previous.People.GetValueOrDefault(person) ?? [], current.People.GetValueOrDefault(person) ?? []);
            if (change.Length > 0) await audit.AppendAsync(new AuditEntry(at, "people_changed", null, null, actor, $"{person}:{change}"), ct);
        }
    }

    private static string Describe(string what, IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var added = after.Except(before, StringComparer.Ordinal).ToList();
        var removed = before.Except(after, StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0) return "";
        var sb = new StringBuilder($" {what}");
        if (added.Count > 0) sb.Append(" +").Append(string.Join(" +", added));
        if (removed.Count > 0) sb.Append(" -").Append(string.Join(" -", removed));
        return sb.ToString();
    }

    /// <summary>Access as of a moment, rebuilt from the audit trail and the stored policy versions: every key with its profile, what
    /// that profile allowed, grants in force and identities above Normal. Lines of text for an access review.</summary>
    public static IReadOnlyList<string> Report(IReadOnlyList<AuditEntry> trail, IPolicyVersionStore versions, IReadOnlyList<AccessGrant> grants,
        DateTimeOffset at)
    {
        var upTo = trail.Where(e => e.At <= at).OrderBy(e => e.At).ToList();
        var keys = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var e in upTo)
        {
            var detail = e.Detail ?? "";
            var subject = detail.Split(' ', 2)[0].TrimEnd(':');
            switch (e.Event)
            {
                case "key_added":
                    keys[subject] = After(detail, "profile ");
                    break;
                case "key_profile_changed":
                    keys[subject] = After(detail, "-> ");
                    break;
                case "key_removed":
                    keys.Remove(subject);
                    break;
            }
        }
        var lines = new List<string> { $"Access as of {at:u}" };
        var loaded = upTo.LastOrDefault(e => e.Event == "policy_loaded");
        var hash = loaded?.Detail?.Split(' ', 2)[0].TrimEnd(';');
        PolicyConfig? policy = hash is not null && versions.Get(hash) is { } v ? PolicyConfig.ParseStored(v.Json) : null;
        lines.Add(policy is null ? "Policy in force: unknown (no policy_loaded entry before this time, or its version is not stored)." : $"Policy in force: {hash}");
        lines.Add("API keys:");
        if (keys.Count == 0) lines.Add("  none recorded");
        foreach (var (key, profile) in keys)
        {
            var allows = profile is null or "none" ? "every tool policy allows (no profile)"
                : policy?.Agents.TryGetValue(profile, out var p) == true ? "tools " + string.Join(", ", p.Tools) + (p.MayDelegateTo.Count > 0 ? "; may delegate to " + string.Join(", ", p.MayDelegateTo) : "")
                : "profile not in the policy in force";
            lines.Add($"  {key}  profile {profile ?? "none"}: {allows}");
        }
        var active = grants.Where(g => g.At <= at && g.Until > at).ToList();
        lines.Add("Temporary grants in force (from those still stored):");
        if (active.Count == 0) lines.Add("  none");
        foreach (var g in active) lines.Add($"  {g.Subject} may use {g.Tool} until {g.Until:u} ({g.Id}, by {g.GrantedBy})");
        var levels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in upTo)
        {
            if (e.Event == "level_changed" && e.Actor is { } who) levels[who] = e.Detail ?? "";
            // "key:bot: Elevated -> Normal": the subject itself holds a colon, so split at ": ".
            else if (e.Event == "level_cleared" && e.Detail?.IndexOf(": ", StringComparison.Ordinal) is > 0 and var i) levels.Remove(e.Detail[..i]);
        }
        lines.Add("Identities above Normal:");
        if (levels.Count == 0) lines.Add("  none");
        foreach (var (who, why) in levels) lines.Add($"  {who}: {why}");
        return lines;

        static string? After(string text, string marker) =>
            text.LastIndexOf(marker, StringComparison.Ordinal) is var i and >= 0 ? text[(i + marker.Length)..].Trim() : null;
    }
}
