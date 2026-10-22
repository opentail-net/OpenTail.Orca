using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTail.Connectivity.Queues;
using OpenTail.Host;

namespace OpenTail.Host.Admin;

/// <summary>A work queue whose jobs the Jobs page shows and can stop.</summary>
/// <param name="Name">The queue's name in <paramref name="Queue"/>.</param>
public sealed record AdminWorkQueue(string Name, IWorkQueue Queue);

/// <summary>
/// The Jobs page: everything waiting or running, whatever holds it, with one set of verbs. A job is a work item an agent owes, a
/// continuation waiting to resume, or a job in a registered <see cref="IWorkQueue"/>. Admin role only; every change is audited
/// with who made it. Goals are shown (operators may read them), bodies and inputs never.
/// </summary>
internal static class AdminJobs
{
    private const int GoalPreview = 160;

    public static void Map(RouteGroupBuilder admin, OpenTailHost host, AdminOptions options)
    {
        admin.MapGet("/jobs", async (CancellationToken ct) =>
        {
            var pending = new JsonArray();
            var dead = new JsonArray();
            var notes = new JsonArray();
            var work = host.Work;
            foreach (var i in work.Store.Query(WorkStatus.Open).Concat(work.Store.Query(WorkStatus.Claimed)).OrderBy(i => i.CreatedAt))
                pending.Add(WorkRow(i));
            foreach (var c in work.Store.Parked().OrderBy(c => c.CreatedAt))
                pending.Add(new JsonObject
                {
                    ["kind"] = "resume", ["id"] = c.Id, ["state"] = "waiting on " + string.Join(", ", c.WaitingOn) + (c.WakeOnAny ? " (any)" : ""),
                    ["what"] = Preview(c.Note), ["for"] = c.Owner, ["source"] = c.Owner, ["created"] = c.CreatedAt, ["until"] = c.Until,
                    ["attempts"] = null, ["actions"] = new JsonArray("cancel"),
                });
            foreach (var i in work.Store.Query(WorkStatus.Dead).Concat(work.Store.Query(WorkStatus.Expired)).OrderBy(i => i.UpdatedAt))
                dead.Add(WorkRow(i));

            foreach (var q in options.WorkQueues)
            {
                try
                {
                    foreach (var j in await q.Queue.PendingAsync(q.Name, 200, ct))
                        pending.Add(new JsonObject
                        {
                            ["kind"] = "queue", ["queue"] = q.Name, ["id"] = j.Id, ["state"] = j.State.ToString().ToLowerInvariant(),
                            ["what"] = $"{j.BodyBytes} bytes" + (j.Key is null ? "" : $", key {j.Key}"), ["for"] = j.Tenant, ["source"] = j.Source,
                            ["created"] = j.EnqueuedAt, ["until"] = j.Until, ["attempts"] = j.Attempts, ["lastError"] = j.LastError,
                            ["actions"] = Actions(j.State == PendingState.Delayed ? ["cancel", "run-now"] : ["cancel"], j.Source),
                        });
                    foreach (var d in await q.Queue.DeadLettersAsync(q.Name, 200, ct))
                        dead.Add(new JsonObject
                        {
                            ["kind"] = "queue", ["queue"] = q.Name, ["id"] = d.Id, ["state"] = "dead: " + d.Reason,
                            ["what"] = $"{System.Text.Encoding.UTF8.GetByteCount(d.Body)} bytes" + (d.Key is null ? "" : $", key {d.Key}"),
                            ["for"] = d.Tenant, ["source"] = d.Source, ["created"] = d.EnqueuedAt, ["until"] = d.DeadAt, ["attempts"] = d.Attempts,
                            ["lastError"] = d.LastError, ["actions"] = Actions(["redrive", "delete"], d.Source),
                        });
                    if (!q.Queue.Capabilities.ListsRunningJobs)
                        notes.Add($"Queue {q.Name} cannot list jobs a worker is running; they show again if they come back.");
                    if (!q.Queue.Capabilities.Cancellation)
                        notes.Add($"Queue {q.Name} cannot cancel jobs (for RabbitMQ, give it a shared cancelled-jobs store).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add($"Queue {q.Name} could not be read: {ex.Message}");
                }
            }

            return Results.Text(new JsonObject { ["pending"] = pending, ["dead"] = dead, ["notes"] = notes }.ToJsonString(), "application/json");
        });

        admin.MapPost("/jobs/act", async (HttpContext ctx, CancellationToken ct) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            var kind = body?["kind"]?.GetValue<string>();
            var id = body?["id"]?.GetValue<string>();
            var action = body?["action"]?.GetValue<string>();
            if (kind is null || id is null || action is null) return Results.BadRequest(new { error = "kind, id and action are required." });
            var by = reviewer.Subject;
            try
            {
                var done = (kind, action) switch
                {
                    ("work", "cancel") or ("work", "delete") => await host.Work.CancelAsync(id, by, ct) is not null,
                    ("work", "redrive") => await host.Work.RedriveAsync(id, by, ct: ct) is not null,
                    ("work", "stop-source") => await StopSourceAsync(host, options, Source(host.Work.Store.Get(id)), by, ct),
                    ("resume", "cancel") => await host.Work.CancelContinuationAsync(id, by, ct) is not null,
                    ("queue", _) => await QueueActAsync(host, options, body?["queue"]?.GetValue<string>(), id, action, by, ct),
                    _ => throw new ArgumentException($"'{action}' is not something a {kind} job can do."),
                };
                return done
                    ? Results.Json(new { ok = true })
                    : Results.Json(new { error = "Nothing changed: the job is gone, already finished, or in a state that does not allow this." },
                        statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (NotSupportedException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });
    }

    private static JsonObject WorkRow(WorkItem i)
    {
        var source = Source(i);
        var actions = i.Status switch
        {
            WorkStatus.Open or WorkStatus.Claimed => new[] { "cancel" },
            _ => ["redrive", "delete"],
        };
        return new JsonObject
        {
            ["kind"] = "work", ["id"] = i.Id,
            ["state"] = i.Status switch
            {
                WorkStatus.Open => "waiting", WorkStatus.Claimed => "running (" + i.ClaimedBy + ")",
                WorkStatus.Dead => "dead: " + (i.Summary ?? "too many failed runs"), _ => i.Status.ToString().ToLowerInvariant(),
            },
            ["what"] = Preview(i.Goal), ["for"] = i.For, ["source"] = source, ["created"] = i.CreatedAt,
            ["until"] = i.Status == WorkStatus.Claimed ? i.LeaseUntil : i.ExpiresAt, ["attempts"] = i.Runs,
            ["actions"] = Actions(actions, source),
        };
    }

    /// <summary>What created a work item, in the same <c>kind:id</c> form queue jobs use.</summary>
    private static string? Source(WorkItem? i) => i switch
    {
        null => null,
        { ParentId: { } parent } => "work:" + parent,
        { ContinuationId: { } continuation } => "continuation:" + continuation,
        _ => i.From,
    };

    /// <summary>The row's actions, plus stop-source when the Host can stop what created it.</summary>
    private static JsonArray Actions(IEnumerable<string> actions, string? source)
    {
        var list = new JsonArray();
        foreach (var a in actions) list.Add(a);
        if (source is not null && (source.StartsWith("work:", StringComparison.Ordinal) || source.StartsWith("continuation:", StringComparison.Ordinal)))
            list.Add("stop-source");
        return list;
    }

    /// <summary>Stops what keeps creating a job, where the Host owns it: the parent work item, or a parked continuation. Anything
    /// else (a person, a schedule or rule elsewhere) has to be turned off where it is configured.</summary>
    private static async Task<bool> StopSourceAsync(OpenTailHost host, AdminOptions options, string? source, string by, CancellationToken ct)
    {
        if (source?.StartsWith("work:", StringComparison.Ordinal) == true)
            return await host.Work.CancelAsync(source["work:".Length..], by, ct) is not null;
        if (source?.StartsWith("continuation:", StringComparison.Ordinal) == true)
            return await host.Work.CancelContinuationAsync(source["continuation:".Length..], by, ct) is not null;
        throw new NotSupportedException(source is null
            ? "This job has no recorded source."
            : $"The Host cannot stop '{source}': turn it off where it is configured.");
    }

    private static async Task<bool> QueueActAsync(OpenTailHost host, AdminOptions options, string? queue, string id, string action, string by, CancellationToken ct)
    {
        var q = options.WorkQueues.FirstOrDefault(x => x.Name == queue) ?? throw new ArgumentException($"No registered queue named '{queue}'.");
        bool done;
        string? source = null;
        switch (action)
        {
            case "cancel": done = await q.Queue.CancelAsync(q.Name, id, $"cancelled by {by} on the jobs page", ct); break;
            case "run-now": done = await q.Queue.RunNowAsync(q.Name, id, ct); break;
            case "redrive": done = await q.Queue.RedriveAsync(q.Name, id, ct); break;
            case "delete": done = await q.Queue.DeleteDeadLetterAsync(q.Name, id, ct); break;
            case "stop-source":
                source = (await q.Queue.PendingAsync(q.Name, 1000, ct)).FirstOrDefault(j => j.Id == id)?.Source
                         ?? (await q.Queue.DeadLettersAsync(q.Name, 1000, ct)).FirstOrDefault(d => d.Id == id)?.Source;
                done = await StopSourceAsync(host, options, source, by, ct);
                break;
            default: throw new ArgumentException($"'{action}' is not something a queue job can do.");
        }

        if (done)
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "job." + action.Replace('-', '_'), null, null, by,
                $"{q.Name}/{id}{(source is null ? "" : ": source " + source)}"), ct);
        return done;
    }

    private static string Preview(string text) => text.Length <= GoalPreview ? text : text[..GoalPreview] + "…";
}
