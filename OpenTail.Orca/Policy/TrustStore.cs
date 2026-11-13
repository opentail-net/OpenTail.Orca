using System.Collections.Concurrent;

namespace OpenTail.Orca;

/// <summary>Tools an operator promoted from Queue to Execute. Durable in a real deployment so every process on the
/// database agrees; a Deny/Queue rule or the taint override always beats a promotion.</summary>
public interface ITrustStore
{
    bool IsPromoted(string tool);
    void Promote(string tool, string by, DateTimeOffset at);
    bool Demote(string tool);
    IReadOnlyList<string> ListPromoted();
}

public sealed class InMemoryTrustStore : ITrustStore
{
    private readonly ConcurrentDictionary<string, string> _promoted = new(StringComparer.Ordinal);
    public bool IsPromoted(string tool) => _promoted.ContainsKey(tool);
    public void Promote(string tool, string by, DateTimeOffset at) => _promoted[tool] = by;
    public bool Demote(string tool) => _promoted.TryRemove(tool, out _);
    public IReadOnlyList<string> ListPromoted() => _promoted.Keys.Order(StringComparer.Ordinal).ToList();
}
