using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>Who a review's subject says decided. Classed by subject only, never by the display name.</summary>
public enum ReviewClass { Person, Ai, Operator, Unclassed, OtherSubject }

/// <summary>Where a stop falls against its action's scheduled time: strictly before it is an undo-window stop, at or after it is late.</summary>
public enum StopKind { UndoWindow, Late }

/// <summary>Clean human decisions for one skill version. <c>Days</c> is the length of the requested window.</summary>
public sealed record MeasuredVersion(string Skill, string Version, int Clean, double Days, double? CleanPerThirtyDays, double? MonthsTo20);

/// <summary>Delayed actions whose undo window completed in the stop-rate period. Only these form the denominator.</summary>
public sealed record StopCohort(int Size, int UndoWindowStops, int LateStops, int Held, int UnreadableDetails);

/// <summary>
/// Step 0 of the attribution plan: a read-only account of how people decided actions after a served skill version, under the rules the
/// plan proposes. It changes nothing, generates no rollback suggestion, and leaves <see cref="SkillOutcomes"/> as it is.
/// </summary>
public sealed record SkillMeasurementReport(
    DateTimeOffset DecidedFrom, DateTimeOffset DecidedTo, DateTimeOffset ScheduledFrom,
    IReadOnlyList<MeasuredVersion> Versions,
    int Ambiguous, int Unattributed, int AiDecisions, int AwaitingApprovals,
    int OperatorCancelled, int UndoWindowStopped, int SystemElapsed, int Unclassed, int OtherSubject,
    int UnreadableServes, StopCohort Stops,
    IReadOnlyList<string> Rules, IReadOnlyList<string> Limitations);

public static class SkillMeasurement
{
    private static readonly Regex ScheduledDetail = new(@"^runs at (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})Z unless stopped$", RegexOptions.CultureInvariant);

    private enum Attribution { None, One, Many }

    /// <summary>The subject of a review, classed. A review with no subject cannot be classed and is never treated as a person's.</summary>
    public static ReviewClass ClassOf(ActionReview review) =>
        review.Subject is null ? ReviewClass.Unclassed
        : Subjects.IsAi(review.Subject) ? ReviewClass.Ai
        : review.Subject.StartsWith("operator:", StringComparison.Ordinal) ? ReviewClass.Operator
        : review.Subject == Subjects.Local || review.Subject.StartsWith("admin:", StringComparison.Ordinal)
            || review.Subject.StartsWith("jwt:", StringComparison.Ordinal) || review.Subject.StartsWith("teams:", StringComparison.Ordinal) ? ReviewClass.Person
        : ReviewClass.OtherSubject;

    /// <summary>A stop strictly before the scheduled time is an undo-window stop; at it, or after it, a late stop.</summary>
    public static StopKind ClassifyStop(DateTimeOffset stoppedAt, DateTimeOffset scheduledAt) =>
        stoppedAt < scheduledAt ? StopKind.UndoWindow : StopKind.Late;

    /// <summary>The scheduled time in a <c>delayed</c> audit entry's detail, which the queue writes as <c>runs at {time:u} unless stopped</c>.
    /// The time is to the second, in UTC. Null when the detail is not in that form.</summary>
    public static DateTimeOffset? ScheduledTime(string? detail)
    {
        if (detail is null || ScheduledDetail.Match(detail) is not { Success: true } m) return null;
        return DateTimeOffset.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public static async Task<SkillMeasurementReport> MeasureAsync(OpenTailHost host, IAuditReader audit, DateTimeOffset decidedFrom, DateTimeOffset scheduledFrom,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var serveTools = host.Catalog.All.Where(t => t.Descriptor.AuditResult?.Event == "skill_served")
            .ToDictionary(t => t.Descriptor.Name, t => t.Descriptor.AuditResult!.Field, StringComparer.Ordinal);
        var trail = audit.Read();
        var byAction = trail.Where(e => e.ActionId is not null).ToLookup(e => e.ActionId!, StringComparer.Ordinal);

        // Every action updated since the window opened is a candidate. A decision moves the update time, so this is a superset of the decisions
        // made in the window; the effective review's own time is checked against the window below (the SQLite test proves the update).
        var candidates = await host.Store.QueryAsync(new ActionQuery { UpdatedSince = decidedFrom, Limit = int.MaxValue }, ct);

        var sessionServes = new Dictionary<string, List<(DateTimeOffset At, string? Label)>>(StringComparer.Ordinal);
        var unreadable = 0;
        async Task<List<(DateTimeOffset At, string? Label)>> ServesOf(string session)
        {
            if (sessionServes.TryGetValue(session, out var known)) return known;
            var serves = new List<(DateTimeOffset At, string? Label)>();
            foreach (var a in await host.Store.QueryAsync(new ActionQuery { SessionId = session, Limit = int.MaxValue }, ct))
            {
                if (!serveTools.TryGetValue(a.ToolName, out var field) || a.Status != ActionStatus.Succeeded) continue;
                var label = Field(a.ResultJson, field);
                var readable = label?.Contains('@') == true;
                if (!readable) unreadable++;
                serves.Add((a.CreatedAt, readable ? label : null));
            }
            sessionServes[session] = serves;
            return serves;
        }

        var versions = new Dictionary<(string Skill, string Version), int>();
        int ambiguous = 0, unattributed = 0, ai = 0, awaiting = 0, operatorCancelled = 0, stopped = 0, system = 0, unclassed = 0, other = 0;

        foreach (var a in candidates)
        {
            if (serveTools.ContainsKey(a.ToolName)) continue;
            var entries = byAction[a.Id].ToList();
            var decisions = a.Reviews.Where(r => r.Decision is ReviewDecision.Approve or ReviewDecision.Reject or ReviewDecision.RequestChanges).ToList();

            // A stop during the undo window is never a human judgement (O-A3). It is counted on its own, by the time it happened.
            if (entries.FirstOrDefault(e => e.Event == "stopped") is { } stop)
            {
                if (InWindow(stop.At, decidedFrom, now)) stopped++;
                continue;
            }

            var person = decisions.LastOrDefault(r => ClassOf(r) == ReviewClass.Person);
            var aiDecision = decisions.LastOrDefault(r => ClassOf(r) == ReviewClass.Ai);
            var effective = person ?? aiDecision;
            if (effective is not null)
            {
                // An approval that did not complete the required approvals leaves the action pending: it is not a decision yet.
                if (effective.Decision == ReviewDecision.Approve && a.Status == ActionStatus.Pending)
                {
                    if (InWindow(effective.At, decidedFrom, now)) awaiting++;
                    continue;
                }
                if (!InWindow(effective.At, decidedFrom, now)) continue;
                if (person is null)
                {
                    ai++;
                    continue;
                }
                var (kind, label) = await AttributionOf(a, ServesOf);
                switch (kind)
                {
                    case Attribution.None: unattributed++; break;
                    case Attribution.Many: ambiguous++; break;
                    default:
                        var split = label!.LastIndexOf('@');
                        var key = (label[..split], label[(split + 1)..]);
                        versions[key] = versions.GetValueOrDefault(key) + 1;
                        break;
                }
                continue;
            }

            // No person or AI decision. The review or the audit says why: an operator cancellation, an elapsed undo window (audited as
            // delay_elapsed, a system review with no subject), or a review with no subject or another subject.
            if (decisions.LastOrDefault(r => ClassOf(r) == ReviewClass.Operator) is { } cancel)
            {
                if (InWindow(cancel.At, decidedFrom, now)) operatorCancelled++;
            }
            else if (entries.FirstOrDefault(e => e.Event == "delay_elapsed") is { } elapsed)
            {
                if (InWindow(elapsed.At, decidedFrom, now)) system++;
            }
            else if (decisions.LastOrDefault(r => ClassOf(r) == ReviewClass.Unclassed) is { } no)
            {
                if (InWindow(no.At, decidedFrom, now)) unclassed++;
            }
            else if (decisions.LastOrDefault(r => ClassOf(r) == ReviewClass.OtherSubject) is { } foreign)
            {
                if (InWindow(foreign.At, decidedFrom, now)) other++;
            }
        }

        var days = (now - decidedFrom).TotalDays;
        var measured = versions.OrderBy(kv => kv.Key.Skill, StringComparer.Ordinal)
            .ThenBy(kv => int.TryParse(kv.Key.Version, out var n) ? n : int.MaxValue)
            .Select(kv =>
            {
                var perThirty = days > 0 ? kv.Value / days * 30 : (double?)null;
                var months = perThirty is > 0 ? 20 / perThirty : null;
                return new MeasuredVersion(kv.Key.Skill, kv.Key.Version, kv.Value, days, perThirty, months);
            }).ToList();

        return new SkillMeasurementReport(decidedFrom, now, scheduledFrom, measured,
            ambiguous, unattributed, ai, awaiting, operatorCancelled, stopped, system, unclassed, other, unreadable,
            Cohort(trail, scheduledFrom, now), Rules, Limitations);
    }

    /// <summary>Which version a decision is attributed to: the one candidate served before it in its session, several (ambiguous), or none
    /// (no session, or no successful serve before the action was created).</summary>
    private static async Task<(Attribution Kind, string? Label)> AttributionOf(ActionRecord a, Func<string, Task<List<(DateTimeOffset At, string? Label)>>> servesOf)
    {
        if (a.SessionId is null) return (Attribution.None, null);
        var before = (await servesOf(a.SessionId)).Where(s => s.At <= a.CreatedAt && s.Label is not null)
            .Select(s => s.Label!).Distinct(StringComparer.Ordinal).ToList();
        return before.Count switch
        {
            0 => (Attribution.None, null),
            1 => (Attribution.One, before[0]),
            _ => (Attribution.Many, null),
        };
    }

    private static StopCohort Cohort(IReadOnlyList<AuditEntry> trail, DateTimeOffset from, DateTimeOffset now)
    {
        var held = trail.Where(e => e.Event == "delay_held" && e.ActionId is not null).Select(e => e.ActionId!).ToHashSet(StringComparer.Ordinal);
        var firstStop = trail.Where(e => e.Event == "stopped" && e.ActionId is not null)
            .GroupBy(e => e.ActionId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(e => e.At), StringComparer.Ordinal);
        var size = 0; var undo = 0; var late = 0; var heldCount = 0; var unreadable = 0;
        foreach (var delayed in trail.Where(e => e.Event == "delayed"))
        {
            if (delayed.ActionId is null || ScheduledTime(delayed.Detail) is not { } scheduled)
            {
                unreadable++;
                continue;
            }
            // Only completed windows count: the scheduled time has passed, and it falls in the stop-rate period.
            if (scheduled < from || scheduled > now) continue;
            size++;
            if (held.Contains(delayed.ActionId)) heldCount++;
            if (firstStop.TryGetValue(delayed.ActionId, out var stoppedAt))
            {
                if (ClassifyStop(stoppedAt, scheduled) == StopKind.UndoWindow) undo++;
                else late++;
            }
        }
        return new StopCohort(size, undo, late, heldCount, unreadable);
    }

    private static bool InWindow(DateTimeOffset at, DateTimeOffset from, DateTimeOffset to) => at >= from && at <= to;

    private static string? Field(string? json, string field)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static readonly IReadOnlyList<string> Rules =
    [
        "Decisions are counted by the time of their effective review (the completing approval, the rejection, or the request for changes), within the decided window.",
        "An action counts for a skill version only when exactly one distinct skill version was served in its session before it was created. Several candidates are ambiguous and counted for no version.",
        "No session, or no successful serve before the action: unattributed.",
        "Only reviews by people count in the per-version rates. Reviews by AI reviewers are counted separately. Reviews with no subject are unclassed and excluded.",
        "Operator cancellations, elapsed undo windows and undo-window stops are excluded from the rates and counted separately.",
        "A partial approval (more approvals still needed) is not a decision until the action leaves Pending.",
    ];

    private static readonly IReadOnlyList<string> Limitations =
    [
        "Attribution is by session. A session that served a version is not proof that the version caused the decision.",
        "20 clean decisions per version is a minimum for the evidence to mean anything; it is not proof of causation or of statistical reliability.",
        "The projection in months assumes the observed rate stays broadly stable.",
        "A stop's scheduled time is read to the second from the audit detail. A stop in the same second as the scheduled time counts as late.",
    ];
}
