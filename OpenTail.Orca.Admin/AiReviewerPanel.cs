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
/// what it has decided, what came of those decisions, what the queue refused it, and whether it is suspended. An admin can suspend or clear it, and
/// each act records a reason. A suspension stops the reviewer's decisions at once (ActionQueue.ReviewCoreAsync). The routes are in the admin group,
/// so only an admin token reaches them; an AI reviewer and a reviewer without the admin role are refused.
/// </summary>
internal static class AiReviewerPanel
{
    private const int RecentPerReviewer = 20;
    private const int MaxReasonLength = 500;

    public static void Map(RouteGroupBuilder admin, OpenTailHost host, AdminOptions options)
    {
        admin.MapGet("/ai-reviewers", async () => Results.Text((await OverviewAsync(host, options)).ToJsonString(), "application/json"));

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

    /// <summary>The panel's data: every AI reviewer the Host knows of (configured, or seen deciding or suspended), with its record.</summary>
    internal static async Task<JsonObject> OverviewAsync(OpenTailHost host, AdminOptions options)
    {
        IReadOnlyList<AuditEntry> log = host.Audit is IAuditReader reader ? reader.Read() : [];
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var configured in options.AiReviewers.Keys) names.Add(configured);
        foreach (var e in log.Where(IsAiDecision)) names.Add(e.Actor![3..]);
        foreach (var row in host.Levels.List().Where(r => r.Subject.StartsWith("ai:", StringComparison.Ordinal))) names.Add(row.Subject[3..]);

        var reviewers = new JsonArray();
        foreach (var name in names)
        {
            var subject = Subjects.Ai(name);
            var mine = log.Where(e => IsAiDecision(e) && e.Actor == subject).OrderByDescending(e => e.At).ToList();
            var row = host.Levels.Get(subject);
            var suspended = row?.Level == IdentityLevel.Suspended;

            var recent = new JsonArray();
            foreach (var e in mine.Take(RecentPerReviewer))
            {
                var action = e.ActionId is { } id ? await host.Store.GetAsync(id) : null;
                // The review this decision was: the one of this subject closest in time to the audit entry.
                var review = action?.Reviews.Where(v => v.Subject == subject).OrderBy(v => Math.Abs((v.At - e.At).Ticks)).FirstOrDefault();
                recent.Add(new JsonObject
                {
                    ["at"] = e.At,
                    ["actionId"] = e.ActionId,
                    ["tool"] = e.ToolName,
                    ["decision"] = DecisionOf(e.Detail),
                    ["status"] = action?.Status.ToString() ?? "unknown",
                    ["reason"] = review?.Reason,
                    ["model"] = review?.AiModel,
                    ["promptVersion"] = review?.AiPromptVersion,
                    ["checks"] = review?.AiChecks is { } checks ? new JsonArray(checks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) : null,
                });
            }

            reviewers.Add(new JsonObject
            {
                ["subject"] = subject,
                ["name"] = name,
                ["configured"] = options.AiReviewers.ContainsKey(name),
                ["suspended"] = suspended,
                ["suspension"] = suspended ? new JsonObject { ["note"] = row!.Reason, ["since"] = row.Since } : null,
                ["decisions"] = new JsonObject
                {
                    ["total"] = mine.Count,
                    ["approve"] = mine.Count(e => DecisionOf(e.Detail) == "approve"),
                    ["reject"] = mine.Count(e => DecisionOf(e.Detail) == "reject"),
                    ["requestChanges"] = mine.Count(e => DecisionOf(e.Detail) == "requestChanges"),
                    ["retry"] = mine.Count(e => DecisionOf(e.Detail) == "retry"),
                },
                // Refused by the queue: outside its limits, not assigned to it, or suspended. The queue audits each one.
                ["refused"] = log.Count(e => e.Event == "review_refused" && e.Actor == subject),
                ["recent"] = recent,
            });
        }
        return new JsonObject { ["reviewers"] = reviewers };
    }

    private static bool IsAiDecision(AuditEntry e) => e.Event == "ai_review" && e.Actor is { } actor && Subjects.IsAi(actor);

    /// <summary>The decision word the audit line starts with (see AiReviewRules.AuditDetail).</summary>
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
