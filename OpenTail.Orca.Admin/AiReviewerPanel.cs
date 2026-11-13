using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTail.Orca;

namespace OpenTail.Orca.Admin;

/// <summary>
/// The AI reviewers panel: the overseer's kill switch with the evidence beside it (reviewer gate plan, section 5.13). For each AI reviewer it shows
/// its decisions in total, what the queue refused it, what came of its decisions, and whether it is suspended. The list is a window (7 days by
/// default), with older decisions loaded a page at a time by cursor. An admin can suspend or clear a reviewer; each act needs a reason and is audited.
/// Every query is filtered and paged in the store, so the panel does not read the whole audit trail. The routes are in the admin group: only an admin
/// token reaches them.
/// </summary>
internal static class AiReviewerPanel
{
    /// <summary>The window a page shows before anyone asks for more: the last seven days.</summary>
    public const int DefaultDays = 7;
    private const int PageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxDays = 365;
    private const int MaxReasonLength = 500;
    private const string AiDecision = "ai_review";

    public static void Map(RouteGroupBuilder admin, OpenTailHost host, AdminOptions options)
    {
        admin.MapGet("/ai-reviewers", async (int? days) =>
        {
            if (!ValidDays(days ?? DefaultDays)) return BadDays();
            return Results.Text((await OverviewAsync(host, options, days ?? DefaultDays)).ToJsonString(), "application/json");
        });

        // Older decisions of one reviewer, a page at a time: `before` is the sequence number of the oldest one already shown.
        admin.MapGet("/ai-reviewers/{name}/decisions", async (string name, int? days, long? before, int? limit) =>
        {
            if (!AiReviewRules.IsValidReviewerName(name)) return Results.BadRequest(new { error = "Not a valid AI reviewer name." });
            if (!ValidDays(days ?? DefaultDays)) return BadDays();
            if (before is < 1) return Results.BadRequest(new { error = "before is a sequence number from an earlier page." });
            if (host.Audit is not IAuditQuery query) return Results.Json(new { error = "This audit trail cannot be queried." }, statusCode: StatusCodes.Status501NotImplemented);

            var subject = Subjects.Ai(name);
            var size = Math.Clamp(limit ?? PageSize, 1, MaxPageSize);
            var rows = query.Page(new AuditQuery
            {
                Event = AiDecision, Actor = subject, Since = SinceFor(days ?? DefaultDays), Before = before, Limit = size + 1,
            });
            var items = new JsonArray();
            foreach (var row in rows.Take(size)) items.Add(await ItemAsync(host, subject, row));
            return Results.Text(new JsonObject { ["items"] = items, ["hasMore"] = rows.Count > size }.ToJsonString(), "application/json");
        });

        admin.MapPost("/ai-reviewers/{name}/suspend", async (string name, HttpContext ctx) =>
        {
            var who = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            if (!AiReviewRules.IsValidReviewerName(name)) return Results.BadRequest(new { error = "Not a valid AI reviewer name." });
            if (await ReasonOf(ctx) is not { } reason)
                return Results.BadRequest(new { error = $"A reason is required, at most {MaxReasonLength} characters. Say why, so the record shows it." });

            var subject = Subjects.Ai(name);
            if (!await host.Levels.SuspendAsync(subject, who.Name, reason))
                return Results.Json(new { error = $"{subject} is already suspended." }, statusCode: StatusCodes.Status409Conflict);
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "ai_reviewer_suspended", null, null, who.Subject, $"{subject}: {reason}"));
            return Results.Json(new { suspended = true, subject });
        });

        admin.MapPost("/ai-reviewers/{name}/clear", async (string name, HttpContext ctx) =>
        {
            var who = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            if (!AiReviewRules.IsValidReviewerName(name)) return Results.BadRequest(new { error = "Not a valid AI reviewer name." });
            if (await ReasonOf(ctx) is not { } reason)
                return Results.BadRequest(new { error = $"A reason is required, at most {MaxReasonLength} characters. Say why, so the record shows it." });

            var subject = Subjects.Ai(name);
            if (host.Levels.Get(subject)?.Level != IdentityLevel.Suspended)
                return Results.Json(new { error = $"{subject} is not suspended." }, statusCode: StatusCodes.Status409Conflict);
            if (!await host.Levels.ClearAsync(subject, who.Name, keyRotated: false))
                return Results.Json(new { error = $"{subject} could not be cleared." }, statusCode: StatusCodes.Status409Conflict);
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "ai_reviewer_cleared", null, null, who.Subject, $"{subject}: {reason}"));
            return Results.Json(new { suspended = false, subject });
        });
    }

    /// <summary>The panel's data: every AI reviewer the Host knows of (configured, seen deciding, or suspended). Totals cover the live trail (the archive is
    /// not read here); the list and the window count cover the last <paramref name="days"/> days (0: the whole live trail).</summary>
    internal static async Task<JsonObject> OverviewAsync(OpenTailHost host, AdminOptions options, int days)
    {
        var query = host.Audit as IAuditQuery;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var configured in options.AiReviewers.Keys) names.Add(configured);
        if (query is not null)
            foreach (var actor in query.Actors(AiDecision).Where(Subjects.IsAi)) names.Add(actor[3..]);
        foreach (var row in host.Levels.List().Where(r => r.Subject.StartsWith("ai:", StringComparison.Ordinal))) names.Add(row.Subject[3..]);

        var since = SinceFor(days);
        var reviewers = new JsonArray();
        foreach (var name in names)
        {
            var subject = Subjects.Ai(name);
            var state = host.Levels.Get(subject);
            var suspended = state?.Level == IdentityLevel.Suspended;
            var reviewer = new JsonObject
            {
                ["subject"] = subject,
                ["name"] = name,
                ["configured"] = options.AiReviewers.ContainsKey(name),
                ["suspended"] = suspended,
                ["suspension"] = suspended ? new JsonObject { ["note"] = state!.Reason, ["since"] = state.Since } : null,
                ["decisions"] = Totals(query, subject),
                ["refused"] = query is null ? 0 : query.Count(new AuditQuery { Event = "review_refused", Actor = subject }),
                ["inWindow"] = query is null ? 0 : query.Count(new AuditQuery { Event = AiDecision, Actor = subject, Since = since }),
            };

            var items = new JsonArray();
            var hasMore = false;
            if (query is not null)
            {
                var rows = query.Page(new AuditQuery { Event = AiDecision, Actor = subject, Since = since, Limit = PageSize + 1 });
                hasMore = rows.Count > PageSize;
                foreach (var row in rows.Take(PageSize)) items.Add(await ItemAsync(host, subject, row));
            }
            reviewer["recent"] = items;
            reviewer["hasMore"] = hasMore;
            reviewers.Add(reviewer);
        }
        return new JsonObject
        {
            ["days"] = days,
            ["sinceUtc"] = since,
            ["reviewers"] = reviewers,
        };
    }

    /// <summary>All-time decision counts of one reviewer, by the decision word the audit line starts with (see AiReviewRules.AuditDetail).</summary>
    private static JsonObject Totals(IAuditQuery? query, string subject)
    {
        long Count(string? detailStart = null) =>
            query is null ? 0 : query.Count(new AuditQuery { Event = AiDecision, Actor = subject, DetailStartsWith = detailStart });
        return new JsonObject
        {
            ["total"] = Count(),
            ["approve"] = Count("Approve;"),
            ["reject"] = Count("Reject;"),
            ["requestChanges"] = Count("RequestChanges;"),
            ["retry"] = Count("Retry;"),
        };
    }

    /// <summary>One decision as the page shows it: when, what, what became of it, and what the reviewer said about itself.</summary>
    private static async Task<JsonObject> ItemAsync(OpenTailHost host, string subject, AuditRow row)
    {
        var e = row.Entry;
        var action = e.ActionId is { } id ? await host.Store.GetAsync(id) : null;
        // The review this decision was: the one of this subject closest in time to the audit entry.
        var review = action?.Reviews.Where(v => v.Subject == subject).OrderBy(v => Math.Abs((v.At - e.At).Ticks)).FirstOrDefault();
        return new JsonObject
        {
            ["seq"] = row.Seq,
            ["at"] = e.At,
            ["actionId"] = e.ActionId,
            ["tool"] = e.ToolName,
            ["decision"] = DecisionOf(e.Detail),
            ["status"] = action?.Status.ToString() ?? "unknown",
            ["reason"] = review?.Reason,
            ["model"] = review?.AiModel,
            ["promptVersion"] = review?.AiPromptVersion,
            ["checks"] = review?.AiChecks is { } checks ? new JsonArray(checks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) : null,
        };
    }

    private static bool ValidDays(int days) => days is 0 || days is >= 1 and <= MaxDays;

    private static IResult BadDays() => Results.BadRequest(new { error = $"days is 0 (all time) or from 1 to {MaxDays}." });

    /// <summary>The start of the window: null for all time (0 days).</summary>
    private static DateTimeOffset? SinceFor(int days) => days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days);

    private static string DecisionOf(string? detail) => detail?.Split(';')[0].Trim() switch
    {
        "Approve" => "approve",
        "Reject" => "reject",
        "RequestChanges" => "requestChanges",
        "Retry" => "retry",
        _ => "other",
    };

    /// <summary>The reason an admin gave, or null when it is missing, blank or too long: an act without a reason is refused.</summary>
    private static async Task<string?> ReasonOf(HttpContext ctx)
    {
        try
        {
            var reason = (await ctx.Request.ReadFromJsonAsync<ReasonBody>())?.Reason?.Trim();
            return string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength ? null : reason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ReasonBody(string? Reason);
}
