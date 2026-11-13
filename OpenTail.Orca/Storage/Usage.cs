namespace OpenTail.Orca;

/// <summary>
/// One day of one identity's use of one tool: how its actions ended and how much the successful ones touched. No argument values, no
/// results, no entity keys (only how many distinct ones). Kept for <see cref="Usage.Retention"/>, well past <c>RetentionDays</c>, because
/// baselines for adaptive limits need history that exists before the feature does.
/// </summary>
public sealed record UsageRow(
    DateOnly Day, string Subject, string? Profile, string Tool, string Module,
    long Calls, long Succeeded, long Failed, long Denied, long Rejected, long Other,
    long Records, long Recipients, long Bytes, long Entities);

/// <summary>A store that keeps daily usage rollups, written in the same transaction as the action reaching its end.</summary>
public interface IUsageStore
{
    /// <summary>The deployment's salt for entity-key hashes: random, created once, never leaves the store.</summary>
    byte[] EntitySalt { get; }

    /// <summary>Counts distinct entity hashes for a day, identity and tool (a hash seen before that day adds nothing).</summary>
    void RecordEntities(DateOnly day, string subject, string tool, IReadOnlyCollection<string> hashes);

    IReadOnlyList<UsageRow> ReadUsage(DateOnly from, DateOnly to);

    /// <summary>Deletes rollups older than <see cref="Usage.Retention"/> and entity hashes older than <see cref="Usage.EntityRetention"/>.</summary>
    int PurgeUsage(DateTimeOffset now);
}

public static class Usage
{
    /// <summary>Daily rollups are kept for 13 months (owner's decision, 2026-10-18): long enough to see yearly patterns.</summary>
    public const int RetentionMonths = 13;

    public static DateOnly Retention(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddMonths(-RetentionMonths);

    /// <summary>Entity hashes are kept only long enough for weekly distinct counts; the daily count stays in the rollup.</summary>
    public static readonly TimeSpan EntityRetention = TimeSpan.FromDays(8);

    /// <summary>Statuses an action ends in, for the rollup. A Failed action can be retried later; each attempt's end is counted.</summary>
    internal static bool IsEnd(ActionStatus s) => s is ActionStatus.Succeeded or ActionStatus.Failed or ActionStatus.Denied
        or ActionStatus.Rejected or ActionStatus.ChangesRequested or ActionStatus.Expired or ActionStatus.Cancelled;
}
