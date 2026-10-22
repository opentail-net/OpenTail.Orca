using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTail.Connectivity.Queues;
using OpenTail.Host;
using QueueCircuit = OpenTail.Connectivity.Queues.CircuitState;

namespace OpenTail.Host.Admin;

/// <summary>A work queue whose jobs the Jobs page shows and can stop.</summary>
/// <param name="Name">The queue's name in <paramref name="Queue"/>.</param>
/// <param name="Health">When it counts as needing attention; it also feeds <c>/healthz</c> as <c>queue:{Name}</c> (degraded, never unhealthy).</param>
public sealed record AdminWorkQueue(string Name, IWorkQueue Queue, WorkQueueHealthThresholds? Health = null);

/// <summary>
/// The Jobs page: everything waiting or running, whatever holds it, with one set of verbs. A job is a work item an agent owes, a
/// continuation waiting to resume, or a job in a registered <see cref="IWorkQueue"/>. Shown per queue: its health, and each
/// resource (concurrency key) with how many run against its limit and whether its circuit is open. Admin role only; every change is
/// audited with who made it. Goals are shown (operators may read them), bodies and inputs never.
/// </summary>
internal static class AdminJobs
{
    private const int GoalPreview = 160;

    public static void Map(RouteGroupBuilder admin, OpenTailHost host, AdminOptions options)
    {
        var health = StartHealthChecks(admin, host, options);

        admin.MapGet("/jobs", async (CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var pending = new JsonArray();
            var dead = new JsonArray();
            var notes = new JsonArray();
            var resources = new JsonArray();
            var queues = new JsonArray();
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
                    {
                        var stuck = j is { State: PendingState.Running, RunningSince: { } since, MaxRunTime: { } max } && now - since > max;
                        pending.Add(new JsonObject
                        {
                            ["kind"] = "queue", ["queue"] = q.Name, ["id"] = j.Id, ["state"] = j.State.ToString().ToLowerInvariant(),
                            ["what"] = $"{j.BodyBytes} bytes" + (j.Key is null ? "" : $", key {j.Key}"), ["for"] = j.Tenant, ["source"] = j.Source,
                            ["resource"] = j.ConcurrencyKey, ["created"] = j.EnqueuedAt, ["until"] = j.Until, ["attempts"] = j.Attempts,
                            ["lastError"] = j.LastError, ["runningSince"] = j.RunningSince, ["maxRunSeconds"] = j.MaxRunTime?.TotalSeconds,
                            ["stuck"] = stuck ? true : null,
                            ["actions"] = Actions(j.State == PendingState.Delayed ? ["cancel", "run-now"] : ["cancel"], j.Source),
                        });
                    }

                    foreach (var d in await q.Queue.DeadLettersAsync(q.Name, 200, ct))
                        dead.Add(new JsonObject
                        {
                            ["kind"] = "queue", ["queue"] = q.Name, ["id"] = d.Id, ["state"] = "dead: " + d.Reason,
                            ["what"] = $"{System.Text.Encoding.UTF8.GetByteCount(d.Body)} bytes" + (d.Key is null ? "" : $", key {d.Key}"),
                            ["for"] = d.Tenant, ["source"] = d.Source, ["resource"] = d.ConcurrencyKey, ["created"] = d.EnqueuedAt, ["until"] = d.DeadAt,
                            ["attempts"] = d.Attempts, ["lastError"] = d.LastError, ["actions"] = Actions(["redrive", "delete"], d.Source),
                        });
                    foreach (var k in await q.Queue.ConcurrencyKeysAsync(q.Name, ct))
                        resources.Add(new JsonObject
                        {
                            ["queue"] = q.Name, ["key"] = k.ConcurrencyKey, ["running"] = k.Running, ["limit"] = k.Limit, ["waiting"] = k.Waiting,
                            ["circuit"] = k.Circuit switch { QueueCircuit.Open => "open", QueueCircuit.HalfOpen => "testing", _ => "closed" },
                            ["openUntil"] = k.OpenUntil, ["failures"] = k.Failures,
                        });
                    var h = await WorkQueueHealth.CheckAsync(q.Queue, q.Name, q.Health, ct: ct);
                    health[q.Name] = h.Status;
                    var reasons = new JsonArray();
                    foreach (var r in h.Reasons) reasons.Add(r);
                    queues.Add(new JsonObject { ["name"] = q.Name, ["status"] = h.Status == QueueHealthStatus.Healthy ? "healthy" : "needs attention", ["reasons"] = reasons });
                    if (!q.Queue.Capabilities.ListsRunningJobs)
                        notes.Add($"Queue {q.Name} cannot list jobs a worker is running, or count jobs waiting per resource.");
                    if (!q.Queue.Capabilities.Cancellation)
                        notes.Add($"Queue {q.Name} cannot cancel jobs (for RabbitMQ, give it a shared queue control store).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add($"Queue {q.Name} could not be read: {ex.Message}");
                }
            }

            return Results.Text(new JsonObject
            {
                ["queues"] = queues, ["resources"] = resources, ["pending"] = pending, ["dead"] = dead, ["notes"] = notes,
            }.ToJsonString(), "application/json");
        });

        admin.MapPost("/jobs/act", async (HttpContext ctx, CancellationToken ct) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            if (await BodyAsync(ctx, ct) is not { } body) return Results.BadRequest(new { error = "The body is not JSON." });
            var kind = Text(body, "kind");
            var id = Text(body, "id");
            var action = Text(body, "action");
            if (kind is null || id is null || action is null) return Results.BadRequest(new { error = "kind, id and action are required." });
            var by = reviewer.Subject;
            try
            {
                var done = (kind, action) switch
                {
                    ("work", "cancel") or ("work", "delete") => await host.Work.CancelAsync(id, by, ct) is not null,
                    ("work", "redrive") => await host.Work.RedriveAsync(id, by, ct: ct) is not null,
                    ("work", "stop-source") => await StopSourceAsync(host, Source(host.Work.Store.Get(id)), by, ct),
                    ("resume", "cancel") => await host.Work.CancelContinuationAsync(id, by, ct) is not null,
                    ("queue", _) => await QueueActAsync(host, options, Text(body, "queue"), id, action, by, ct),
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

        // After an outage: bring back the dead jobs that match. Queue jobs are released through their resource's limit and circuit,
        // so they trickle back instead of stampeding a resource that has only just recovered.
        admin.MapPost("/jobs/redrive-matching", async (HttpContext ctx, CancellationToken ct) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            if (await BodyAsync(ctx, ct) is not { } body) return Results.BadRequest(new { error = "The body is not JSON." });
            var dryRun = body["dryRun"]?.GetValue<bool>() ?? true;
            DateTimeOffset? since = Text(body, "deadSince") is { Length: > 0 } s && DateTimeOffset.TryParse(s, out var parsed) ? parsed : null;
            var filter = new DeadLetterFilter(Blank(Text(body, "resource")), Blank(Text(body, "reasonContains")), Blank(Text(body, "source")), since);
            var target = Text(body, "queue");
            int count;
            if (target is null or "work")
            {
                var matching = host.Work.Store.Query(WorkStatus.Dead).Concat(host.Work.Store.Query(WorkStatus.Expired))
                    .Where(i => (filter.ReasonContains is null || i.Summary?.Contains(filter.ReasonContains, StringComparison.OrdinalIgnoreCase) == true)
                                && (filter.Source is null || Source(i) == filter.Source)
                                && (filter.DeadSince is null || i.UpdatedAt >= filter.DeadSince)
                                && filter.ConcurrencyKey is null)
                    .ToList();
                count = matching.Count;
                if (!dryRun)
                {
                    count = 0;
                    foreach (var i in matching)
                        if (await host.Work.RedriveAsync(i.Id, reviewer.Subject, ct: ct) is not null) count++;
                }
            }
            else
            {
                var q = options.WorkQueues.FirstOrDefault(x => x.Name == target);
                if (q is null) return Results.BadRequest(new { error = $"No registered queue named '{target}'." });
                count = await WorkQueueOperations.RedriveMatchingAsync(q.Queue, q.Name, filter, dryRun, ct);
                if (!dryRun)
                    await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "job.redrive_matching", null, null, reviewer.Subject,
                        $"{q.Name}: {count} redriven (resource {filter.ConcurrencyKey ?? "any"}, reason {filter.ReasonContains ?? "any"}, source {filter.Source ?? "any"}, since {filter.DeadSince?.ToString("O") ?? "any"})"), ct);
            }

            return Results.Json(new { count, dryRun });
        });
    }

    /// <summary>Feeds each queue's health into <c>/healthz</c> as <c>queue:{name}</c>. The probe must be cheap, so it reads a value
    /// refreshed once a minute (and whenever the Jobs page is read).</summary>
    private static ConcurrentDictionary<string, QueueHealthStatus> StartHealthChecks(RouteGroupBuilder admin, OpenTailHost host, AdminOptions options)
    {
        var latest = new ConcurrentDictionary<string, QueueHealthStatus>(StringComparer.Ordinal);
        if (options.WorkQueues.Count == 0)
            return latest;
        foreach (var q in options.WorkQueues)
            host.Health.Add("queue:" + q.Name, () => latest.GetValueOrDefault(q.Name) == QueueHealthStatus.Degraded ? HealthState.Degraded : HealthState.Ok);
        var timer = new Timer(_ =>
        {
            foreach (var q in options.WorkQueues)
            {
                try { latest[q.Name] = WorkQueueHealth.CheckAsync(q.Queue, q.Name, q.Health).GetAwaiter().GetResult().Status; }
                catch (Exception) { latest[q.Name] = QueueHealthStatus.Degraded; } // a queue that cannot be read needs a person too
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        ((IEndpointRouteBuilder)admin).ServiceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(() => timer.Dispose());
        return latest;
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
            ["runningSince"] = i.Status == WorkStatus.Claimed ? i.UpdatedAt : null,
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
    private static async Task<bool> StopSourceAsync(OpenTailHost host, string? source, string by, CancellationToken ct)
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
                done = await StopSourceAsync(host, source, by, ct);
                break;
            default: throw new ArgumentException($"'{action}' is not something a queue job can do.");
        }

        if (done)
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "job." + action.Replace('-', '_'), null, null, by,
                $"{q.Name}/{id}{(source is null ? "" : ": source " + source)}"), ct);
        return done;
    }

    private static async Task<JsonNode?> BodyAsync(HttpContext ctx, CancellationToken ct)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Text(JsonNode body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Preview(string text) => text.Length <= GoalPreview ? text : text[..GoalPreview] + "…";
}
