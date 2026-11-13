namespace OpenTail.Orca;

/// <summary>
/// A tool's rate limit as store admissions: one per window that is set. The store checks them in the same transaction that claims or
/// creates the action, so calls that arrive together cannot all count the same slot. A refusal names its window and stores the action
/// failed (rate_limited, retryable) instead of running it, which is how a reviewer finds it later and retries it once the window has passed.
/// </summary>
internal sealed record RateGate(IReadOnlyList<Admission> Admissions, IReadOnlyList<string> Windows)
{
    public static RateGate For(string tool, RateLimit? limit)
    {
        // Successful and running calls count against the limit, as they always have.
        var filter = new ActionQuery { ToolName = tool, AnyOfStatus = [ActionStatus.Succeeded, ActionStatus.Executing] };
        var admissions = new List<Admission>();
        var windows = new List<string>();
        if (limit is { PerHour: > 0 })
        {
            admissions.Add(new Admission(filter, TimeSpan.FromHours(1), limit.PerHour));
            windows.Add($"{limit.PerHour}/hour");
        }
        if (limit is { PerDay: > 0 })
        {
            admissions.Add(new Admission(filter, TimeSpan.FromDays(1), limit.PerDay));
            windows.Add($"{limit.PerDay}/day");
        }
        return new RateGate(admissions, windows);
    }

    /// <summary>The record a refused action is stored as: failed with rate_limited, retryable, and no longer leased.</summary>
    public ActionRecord Refuse(ActionRecord record, int index, DateTimeOffset now)
    {
        var error = new ActionError("rate_limited", $"Limit of {Windows[index]} reached for this tool. A reviewer can retry later.", true);
        return record with { Status = ActionStatus.Failed, Error = error with { Kind = error.EffectiveKind }, LeaseUntil = null, UpdatedAt = now };
    }
}
