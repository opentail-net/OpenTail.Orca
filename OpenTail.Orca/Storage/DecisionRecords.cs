using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTail.Orca;

/// <summary>One review as a decision record keeps it: who, with what authority, what they decided and why. No edited arguments.</summary>
public sealed record DecisionReview(string Reviewer, string? Subject, IReadOnlyList<string> Groups, string Decision, string? Reason, DateTimeOffset At,
    string? AiModel, string? AiPromptVersion, string? AiInputHash = null);

/// <summary>
/// What is kept of an action after retention deletes it (audit D3): how it was decided and what came of it, never its arguments or
/// result. Bound into the audit chain: each purge writes one audit entry carrying the digest of the records it kept.
/// </summary>
public sealed record DecisionRecord
{
    public required string ActionId { get; init; }
    public required string Tool { get; init; }
    public required string Module { get; init; }
    public string? ModuleVersion { get; init; }
    public required string Requester { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required string Status { get; init; }
    public required string PolicyMode { get; init; }
    public string? PolicyRule { get; init; }
    public string? PolicyHash { get; init; }
    public IReadOnlyList<DecisionStep> DecisionTrace { get; init; } = [];
    public IReadOnlyList<DecisionReview> Reviews { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorKind { get; init; }
    public required string ArgumentsHash { get; init; }
    public ResultStats? ResultStats { get; init; }
    public int Attempts { get; init; }
    public bool Tainted { get; init; }
    public IReadOnlyList<string> SessionLabels { get; init; } = [];
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>How it was finally resolved (approved, rejected, expired, cancelled, or refused at its claim), with the policy and binding it
    /// held. Kept with the record so the answer survives the purge of the action (see <see cref="ActionRecord.Resolution"/>).</summary>
    public ActionResolution? Resolution { get; init; }

    /// <summary>The purge that kept it: the audit entry with this batch's digest.</summary>
    public string? Batch { get; init; }

    private static readonly JsonSerializerOptions Canonical = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static DecisionRecord From(ActionRecord r, string batch) => new()
    {
        ActionId = r.Id,
        Tool = r.ToolName,
        Module = r.ModuleId,
        ModuleVersion = r.ModuleVersion,
        Requester = r.Requester.Id,
        Profile = r.Profile,
        SessionId = r.SessionId,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Status = r.Status.ToString(),
        PolicyMode = r.PolicyMode.ToString(),
        PolicyRule = r.PolicyRule,
        PolicyHash = r.PolicyHash,
        DecisionTrace = r.DecisionTrace,
        Reviews = [.. r.Reviews.Select(v => new DecisionReview(v.Reviewer, v.Subject, v.Groups, v.Decision.ToString(), v.Reason, v.At, v.AiModel, v.AiPromptVersion, v.AiInputHash))],
        ErrorCode = r.Error?.Code,
        ErrorKind = r.Error is { } e ? FailureKinds.Name(e.EffectiveKind) : null,
        ArgumentsHash = r.ArgumentsHash,
        ResultStats = r.ResultStats,
        Attempts = r.Attempts,
        Tainted = r.Tainted,
        SessionLabels = r.SessionLabels,
        RunAt = r.RunAt,
        Resolution = r.Resolution,
        Batch = batch,
    };

    /// <summary>The record's canonical JSON (what is stored, and what its hash covers).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Canonical);

    public static DecisionRecord Parse(string json) => JsonSerializer.Deserialize<DecisionRecord>(json, Canonical)!;

    public static string HashOf(string json) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    /// <summary>The digest of a purge batch: SHA-256 over the records' hashes in action-id order, one per line.</summary>
    public static string Digest(IEnumerable<string> hashesInOrder) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashesInOrder))));
}
