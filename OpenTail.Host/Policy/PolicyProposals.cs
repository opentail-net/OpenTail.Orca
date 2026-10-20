using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Host;

public enum ProposalStatus { Proposed, Approved, Rejected, Withdrawn, Applied }

/// <summary>
/// A proposed policy (plan v7, A3): a whole candidate policy, validated and replayed when it was proposed. The Host never applies it
/// itself: an approved proposal is downloaded and deployed like any policy file, and the first start under it marks it applied.
/// </summary>
public sealed record PolicyProposal(
    string Id, DateTimeOffset CreatedAt, string Proposer, string BaseHash, string CandidateJson, string CandidateHash, string? Note,
    string ReplayJson, IReadOnlyList<string> Warnings, ProposalStatus Status, string? DecidedBy = null, DateTimeOffset? DecidedAt = null,
    string? Reason = null);

public interface IPolicyProposalStore
{
    void Add(PolicyProposal proposal);
    PolicyProposal? Get(string id);

    /// <summary>Newest first.</summary>
    IReadOnlyList<PolicyProposal> List();

    /// <summary>Replaces the proposal only if it still has <paramref name="expected"/> status; false when someone decided it first.</summary>
    bool Update(PolicyProposal proposal, ProposalStatus expected);
}

public sealed class InMemoryPolicyProposalStore : IPolicyProposalStore
{
    private readonly Dictionary<string, PolicyProposal> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(PolicyProposal proposal) { lock (_gate) _items.Add(proposal.Id, proposal); }
    public PolicyProposal? Get(string id) { lock (_gate) return _items.GetValueOrDefault(id); }
    public IReadOnlyList<PolicyProposal> List() { lock (_gate) return [.. _items.Values.OrderByDescending(p => p.CreatedAt)]; }

    public bool Update(PolicyProposal proposal, ProposalStatus expected)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(proposal.Id, out var now) || now.Status != expected) return false;
            _items[proposal.Id] = proposal;
            return true;
        }
    }
}

/// <summary>
/// Proposing, deciding and recognising policy changes. Proposing runs the start-up checks against the loaded modules and replays the
/// candidate against stored actions; approving needs an admin who is not the proposer (nor the same person under <c>People</c>) and a
/// proposal made against the policy that is running. Every step is audited.
/// </summary>
public sealed class PolicyProposals(OpenTailHost host, IPolicyProposalStore store, TimeProvider clock)
{
    public const int MaxPolicyBytes = 512 * 1024;

    public IReadOnlyList<PolicyProposal> List() => store.List();
    public PolicyProposal? Get(string id) => store.Get(id);

    /// <summary>True when the policy changed since the proposal was made: approving it would undo whatever changed in between.</summary>
    public bool IsStale(PolicyProposal p) => p.Status == ProposalStatus.Proposed && p.BaseHash != host.Policy.PolicyHash;

    /// <summary>The running policy as one deployable file: merged, without <c>Include</c>. Where an edit starts from.</summary>
    public string CurrentPolicyJson() => Deployable(host.Policy.Config.ToCanonicalJson());

    /// <exception cref="ArgumentException">The candidate is not valid JSON, is not a whole policy, or fails a start-up check.</exception>
    public async Task<PolicyProposal> ProposeAsync(string policyJson, string proposer, string? note, CancellationToken ct = default)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(policyJson) > MaxPolicyBytes) throw new ArgumentException("The policy is too large.");
        if (note is { Length: > 1000 }) throw new ArgumentException("The note is limited to 1000 characters.");
        PolicyConfig candidate;
        try { candidate = PolicyConfig.Parse(policyJson); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new ArgumentException("Not a valid policy: " + ex.Message);
        }
        if (candidate.Include.Count > 0) throw new ArgumentException("A proposal is a whole policy: Include is not allowed (start from the current policy, which is already merged).");

        var warnings = new List<string>();
        var errors = OpenTailHostBuilder.CheckPolicy(host.Catalog, candidate, host.RetiredModules, host.IsSandbox, warnings);
        if (errors.Count > 0) throw new ArgumentException("The policy would not start: " + string.Join(" ", errors));
        var hash = candidate.Hash();
        if (hash == host.Policy.PolicyHash) throw new ArgumentException("The proposal is the policy that is already running.");

        var resolver = new PolicyResolver(candidate, host.TrustStore,
            host.Catalog.Modules.Where(m => !m.Options.AllowDefaultRead).Select(m => m.Id));
        var replay = await PolicyReplay.RunAsync(host.Store, host.Catalog, resolver, since: null, ct);
        var replayJson = new JsonObject
        {
            ["examined"] = replay.Examined,
            ["unchanged"] = replay.Unchanged,
            ["changed"] = replay.Changes.Count,
            ["summary"] = new JsonArray([.. replay.Summary().Select(s => (JsonNode)s.Trim())]),
            ["examples"] = new JsonArray([.. replay.Changes.Take(20).Select(c => (JsonNode)new JsonObject
            {
                ["actionId"] = c.ActionId, ["tool"] = c.Tool, ["was"] = c.Was.ToString(), ["wouldBe"] = c.WouldBe.ToString(), ["at"] = c.At,
            })]),
        }.ToJsonString();

        var now = clock.GetUtcNow();
        var p = new PolicyProposal("prop_" + Guid.NewGuid().ToString("N")[..16], now, proposer, host.Policy.PolicyHash,
            candidate.ToCanonicalJson(), hash, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), replayJson, warnings, ProposalStatus.Proposed);
        store.Add(p);
        await host.Audit.AppendAsync(new AuditEntry(now, "policy_proposed", null, null, proposer,
            $"{p.Id}: {hash} replacing {p.BaseHash}; {replay.Changes.Count} of {replay.Examined} stored actions would have been handled differently"), ct);
        return p;
    }

    /// <exception cref="KeyNotFoundException">No such proposal.</exception>
    /// <exception cref="UnauthorizedAccessException">The approver proposed it (or is the same person).</exception>
    /// <exception cref="InvalidOperationException">Already decided, or stale.</exception>
    public async Task<PolicyProposal> ApproveAsync(string id, string approver, CancellationToken ct = default)
    {
        var p = store.Get(id) ?? throw new KeyNotFoundException(id);
        if (p.Status != ProposalStatus.Proposed) throw new InvalidOperationException($"The proposal is {p.Status.ToString().ToLowerInvariant()}.");
        if (host.Policy.People.Same(p.Proposer, approver))
            throw new UnauthorizedAccessException("A policy change needs a second admin: you proposed this one.");
        if (IsStale(p)) throw new InvalidOperationException("The running policy changed since this was proposed; approving it would undo that. Propose again from the current policy.");
        return await DecideAsync(p, ProposalStatus.Approved, approver, null, "policy_proposal_approved", ct);
    }

    public async Task<PolicyProposal> RejectAsync(string id, string by, string reason, CancellationToken ct = default)
    {
        var p = store.Get(id) ?? throw new KeyNotFoundException(id);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new ArgumentException("A reason (up to 1000 characters) is required.");
        if (p.Status is not (ProposalStatus.Proposed or ProposalStatus.Approved))
            throw new InvalidOperationException($"The proposal is {p.Status.ToString().ToLowerInvariant()}.");
        return await DecideAsync(p, ProposalStatus.Rejected, by, reason.Trim(), "policy_proposal_rejected", ct);
    }

    public async Task<PolicyProposal> WithdrawAsync(string id, string by, CancellationToken ct = default)
    {
        var p = store.Get(id) ?? throw new KeyNotFoundException(id);
        if (!host.Policy.People.Same(p.Proposer, by)) throw new UnauthorizedAccessException("Only the proposer can withdraw a proposal; others reject it.");
        if (p.Status != ProposalStatus.Proposed) throw new InvalidOperationException($"The proposal is {p.Status.ToString().ToLowerInvariant()}.");
        return await DecideAsync(p, ProposalStatus.Withdrawn, by, null, "policy_proposal_withdrawn", ct);
    }

    /// <summary>At start: an approved proposal whose policy is now running is marked applied.</summary>
    public async Task MarkAppliedAsync(CancellationToken ct = default)
    {
        foreach (var p in store.List().Where(p => p.Status == ProposalStatus.Approved && p.CandidateHash == host.Policy.PolicyHash))
            await DecideAsync(p, ProposalStatus.Applied, "system", null, "policy_proposal_applied", ct, keepDecider: true);
    }

    /// <summary>The candidate as a file to deploy: parses to exactly <see cref="PolicyProposal.CandidateHash"/>.</summary>
    public static string Deployable(string canonicalJson)
    {
        var node = JsonNode.Parse(canonicalJson)!.AsObject();
        node.Remove("Include");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<PolicyProposal> DecideAsync(PolicyProposal p, ProposalStatus to, string by, string? reason, string auditEvent,
        CancellationToken ct, bool keepDecider = false)
    {
        var now = clock.GetUtcNow();
        var next = keepDecider ? p with { Status = to } : p with { Status = to, DecidedBy = by, DecidedAt = now, Reason = reason };
        if (!store.Update(next, p.Status)) throw new InvalidOperationException("Someone else decided this proposal first.");
        await host.Audit.AppendAsync(new AuditEntry(now, auditEvent, null, null, by, $"{p.Id}: {p.CandidateHash}" + (reason is null ? "" : $"; {reason}")), ct);
        return next;
    }
}
