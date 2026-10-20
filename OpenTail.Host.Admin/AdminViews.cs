using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTail.Host;

namespace OpenTail.Host.Admin;

/// <summary>
/// Read-only administration views (plan v7, A1): agent profiles as the loaded catalog resolves them, people, keys (name, hash
/// prefix, profile; never the key), policy versions and their differences, health and usage. Admin role only: a reviewer gets 403.
/// Nothing here carries argument values or results.
/// </summary>
internal static class AdminViews
{
    // One per process: the listeners are process-wide anyway, and several hosts in one process share the same source and meter.
    private static readonly Lazy<HostTelemetryRecorder> SharedRecorder = new(() => new HostTelemetryRecorder());

    public static void Map(RouteGroupBuilder api, OpenTailHost host, AdminOptions options)
    {
        var recorder = options.Telemetry ?? (options.RecordTelemetry ? SharedRecorder.Value : null);
        api.MapGet("/me", (HttpContext ctx) => ctx.Items[AdminEndpoints.ReviewerItem] is ReviewerIdentity r
            ? Results.Json(new { subject = r.Subject, name = r.Name, isAdmin = r.IsAdmin, groups = r.Groups })
            : Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized));

        var admin = api.MapGroup("/admin");
        admin.AddEndpointFilter(async (c, next) =>
            c.HttpContext.Items[AdminEndpoints.ReviewerItem] is ReviewerIdentity { IsAdmin: true }
                ? await next(c)
                : Results.Json(new { error = "This view needs the admin role." }, statusCode: StatusCodes.Status403Forbidden));

        admin.MapGet("/overview", () => Results.Text(Overview(host).ToJsonString(), "application/json"));

        admin.MapGet("/policy-versions", () =>
        {
            var arr = new JsonArray();
            foreach (var v in host.PolicyVersions.List())
                arr.Add(new JsonObject
                {
                    ["hash"] = v.Hash, ["firstLoaded"] = v.FirstLoaded, ["lastLoaded"] = v.LastLoaded,
                    ["running"] = v.Hash == host.Policy.PolicyHash,
                });
            return Results.Text(new JsonObject { ["versions"] = arr }.ToJsonString(), "application/json");
        });

        admin.MapGet("/policy-versions/diff", (string? from, string? to) =>
        {
            to ??= host.Policy.PolicyHash;
            if (string.IsNullOrEmpty(from)) return Results.BadRequest(new { error = "from is required (a policy hash)." });
            var a = from == host.Policy.PolicyHash ? host.Policy.Config.ToCanonicalJson() : host.PolicyVersions.Get(from)?.Json;
            var b = to == host.Policy.PolicyHash ? host.Policy.Config.ToCanonicalJson() : host.PolicyVersions.Get(to)?.Json;
            if (a is null || b is null) return Results.NotFound(new { error = "No such policy version." });
            return Results.Text(new JsonObject { ["from"] = from, ["to"] = to, ["changes"] = PolicyDiff.Compare(a, b) }.ToJsonString(), "application/json");
        });

        admin.MapGet("/telemetry/metrics", () => recorder is null
            ? Results.Json(new { enabled = false })
            : Results.Json(new { enabled = true, since = recorder.Started, droppedSeries = recorder.DroppedSeries, series = recorder.Metrics() }));

        admin.MapGet("/telemetry/traces", (int? limit) =>
        {
            if (recorder is null) return Results.Json(new { enabled = false });
            // Newest traces first; each trace's spans in start order, so the page can draw a waterfall.
            var traces = recorder.Spans(Math.Clamp(limit ?? 300, 1, recorder.MaxSpans))
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    traceId = g.Key,
                    start = g.Min(s => s.Start),
                    durationMs = (g.Max(s => s.Start.AddMilliseconds(s.DurationMs)) - g.Min(s => s.Start)).TotalMilliseconds,
                    root = g.OrderBy(s => s.Start).First().Name,
                    actionId = g.Select(s => s.Tags.GetValueOrDefault("opentail.action_id")).FirstOrDefault(a => a is not null),
                    error = g.Any(s => s.Status == "Error"),
                    spans = g.OrderBy(s => s.Start).ToList(),
                })
                .OrderByDescending(t => t.start).ToList();
            return Results.Json(new { enabled = true, traces });
        });

        admin.MapGet("/audit", (int? limit) =>
        {
            if (host.Audit is not IAuditReader reader) return Results.Json(new { entries = Array.Empty<object>() });
            var entries = reader.ReadRecent(Math.Clamp(limit ?? 200, 1, 2_000)).Reverse()
                .Select(e => new { at = e.At, @event = e.Event, actionId = e.ActionId, tool = e.ToolName, actor = e.Actor, detail = e.Detail });
            return Results.Json(new { entries });
        });

        // Policy proposals (A3). The Host never applies one: an approved proposal is downloaded and deployed like any policy file.
        admin.MapGet("/policy/current", () => Results.Text(host.Proposals.CurrentPolicyJson(), "application/json"));

        admin.MapGet("/proposals", () =>
            Results.Text(new JsonArray([.. host.Proposals.List().Select(p => (JsonNode)ProposalJson(host, p, full: false))]).ToJsonString(), "application/json"));

        admin.MapGet("/proposals/{id}", (string id) => host.Proposals.Get(id) is { } p
            ? Results.Text(ProposalJson(host, p, full: true).ToJsonString(), "application/json")
            : Results.NotFound(new { error = "No such proposal." }));

        admin.MapGet("/proposals/{id}/policy.json", (string id, HttpContext ctx) =>
        {
            if (host.Proposals.Get(id) is not { } p) return Results.NotFound(new { error = "No such proposal." });
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"policy-{p.Id}.json\"";
            return Results.Text(PolicyProposals.Deployable(p.CandidateJson), "application/json");
        });

        admin.MapPost("/proposals", async (HttpContext ctx) =>
        {
            var who = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            JsonObject body;
            try
            {
                if (ctx.Request.ContentLength > PolicyProposals.MaxPolicyBytes + 8192) return Results.BadRequest(new { error = "Request too large." });
                body = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? throw new System.Text.Json.JsonException();
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Body must be a JSON object with policy and note." }); }
            var policy = body["policy"] switch
            {
                JsonObject o => o.ToJsonString(),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (policy is null) return Results.BadRequest(new { error = "policy is required: the whole candidate policy." });
            try
            {
                var p = await host.Proposals.ProposeAsync(policy, who.Subject, body["note"] is JsonValue nv && nv.TryGetValue<string>(out var note) ? note : null);
                return Results.Text(ProposalJson(host, p, full: true).ToJsonString(), "application/json");
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapPost("/proposals/{id}/{decision}", async (string id, string decision, HttpContext ctx) =>
        {
            var who = (ReviewerIdentity)ctx.Items[AdminEndpoints.ReviewerItem]!;
            string? reason = null;
            if (ctx.Request.ContentLength is > 0)
            {
                if (ctx.Request.ContentLength > 8192) return Results.BadRequest(new { error = "Request too large." });
                try { reason = (await JsonNode.ParseAsync(ctx.Request.Body))?["reason"] is JsonValue rv && rv.TryGetValue<string>(out var r) ? r : null; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return Results.BadRequest(new { error = "Body must be JSON." }); }
            }
            try
            {
                var p = decision switch
                {
                    "approve" => await host.Proposals.ApproveAsync(id, who.Subject),
                    "reject" => await host.Proposals.RejectAsync(id, who.Subject, reason ?? ""),
                    "withdraw" => await host.Proposals.WithdrawAsync(id, who.Subject),
                    _ => null,
                };
                return p is null
                    ? Results.BadRequest(new { error = "decision must be approve, reject or withdraw." })
                    : Results.Text(ProposalJson(host, p, full: true).ToJsonString(), "application/json");
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "No such proposal." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        admin.MapGet("/usage", (int? days) =>
        {
            if (host.Store is not IUsageStore usage) return Results.Json(new { rows = Array.Empty<object>(), note = "This store keeps no usage rollups." });
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = usage.ReadUsage(today.AddDays(-(Math.Clamp(days ?? 7, 1, 400) - 1)), today);
            return Results.Json(new { rows });
        });
    }

    private static JsonObject Overview(OpenTailHost host)
    {
        var config = host.Policy.Config;
        var agents = new JsonArray();
        foreach (var (name, profile) in config.Agents.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var tools = new JsonArray();
            foreach (var t in host.Catalog.All.Where(t => host.Policy.Admits(t.Descriptor, name)).OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal))
                tools.Add(new JsonObject
                {
                    ["name"] = t.Descriptor.Name,
                    ["risk"] = t.Descriptor.Risk.ToString(),
                    ["mode"] = host.Policy.Resolve(t.Descriptor, tainted: false).Mode.ToString(),
                    ["modeAfterOutsideContent"] = host.Policy.Resolve(t.Descriptor, tainted: true).Mode.ToString(),
                });
            agents.Add(new JsonObject
            {
                ["name"] = name,
                ["entries"] = Strings(profile.Tools),
                ["requestable"] = Strings(profile.Requestable),
                ["mayDelegateTo"] = Strings(profile.MayDelegateTo),
                ["tools"] = tools,
            });
        }

        var people = new JsonArray();
        foreach (var (person, ids) in config.People.OrderBy(p => p.Key, StringComparer.Ordinal))
            people.Add(new JsonObject { ["person"] = person, ["identities"] = Strings(ids) });

        var keys = new JsonArray();
        foreach (var k in AccessChanges.CurrentKeys(host.AccessSnapshots))
            keys.Add(new JsonObject { ["name"] = k.Name, ["hashPrefix"] = k.HashPrefix, ["profile"] = k.Profile });

        var health = host.Health.Report();
        var running = host.PolicyVersions.Get(host.Policy.PolicyHash);
        return new JsonObject
        {
            ["host"] = host.Name,
            ["policy"] = new JsonObject { ["hash"] = host.Policy.PolicyHash, ["loadedAt"] = running?.LastLoaded, ["firstLoaded"] = running?.FirstLoaded },
            ["agents"] = agents,
            ["people"] = people,
            ["keys"] = keys,
            ["health"] = new JsonObject
            {
                ["status"] = health.State.ToString().ToLowerInvariant(),
                ["checks"] = new JsonObject(health.Checks.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
                ["lastSweep"] = host.Health.LastSweep,
            },
            ["warnings"] = Strings(host.StartupWarnings),
            ["shadow"] = host.IsSandbox,
        };
    }

    private static JsonObject ProposalJson(OpenTailHost host, PolicyProposal p, bool full)
    {
        var o = new JsonObject
        {
            ["id"] = p.Id, ["createdAt"] = p.CreatedAt, ["proposer"] = p.Proposer, ["note"] = p.Note,
            ["status"] = p.Status.ToString(), ["stale"] = host.Proposals.IsStale(p),
            ["baseHash"] = p.BaseHash, ["candidateHash"] = p.CandidateHash,
            ["decidedBy"] = p.DecidedBy, ["decidedAt"] = p.DecidedAt, ["reason"] = p.Reason,
            ["replay"] = JsonNode.Parse(p.ReplayJson),
            ["warnings"] = Strings(p.Warnings),
        };
        if (full)
        {
            var baseJson = p.BaseHash == host.Policy.PolicyHash ? host.Policy.Config.ToCanonicalJson() : host.PolicyVersions.Get(p.BaseHash)?.Json;
            o["changes"] = baseJson is null ? null : PolicyDiff.Compare(PolicyProposals.Deployable(baseJson), PolicyProposals.Deployable(p.CandidateJson));
        }
        return o;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());
}

/// <summary>Differences between two canonical policy documents, section by section and, inside a section that is a map
/// (agents, people, tools, ...), entry by entry.</summary>
public static class PolicyDiff
{
    public static JsonArray Compare(string fromJson, string toJson)
    {
        var a = JsonNode.Parse(fromJson) as JsonObject ?? [];
        var b = JsonNode.Parse(toJson) as JsonObject ?? [];
        var changes = new JsonArray();
        foreach (var section in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            a.TryGetPropertyValue(section, out var x);
            b.TryGetPropertyValue(section, out var y);
            if (JsonNode.DeepEquals(x, y)) continue;
            if (x is JsonObject xo && y is JsonObject yo && IsMap(section))
            {
                foreach (var key in xo.Select(p => p.Key).Union(yo.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    xo.TryGetPropertyValue(key, out var xv);
                    yo.TryGetPropertyValue(key, out var yv);
                    if (JsonNode.DeepEquals(xv, yv)) continue;
                    changes.Add(Change(section, key, xv, yv));
                }
            }
            else changes.Add(Change(section, null, x, y));
        }
        return changes;
    }

    // Sections whose entries are named things; elsewhere the whole section is one value.
    private static bool IsMap(string section) => section is "Agents" or "People" or "Tools" or "DataFlow" or "Capacity" or "Rules";

    private static JsonObject Change(string section, string? key, JsonNode? before, JsonNode? after) => new()
    {
        ["section"] = section,
        ["key"] = key,
        ["kind"] = before is null ? "added" : after is null ? "removed" : "changed",
        ["before"] = before?.DeepClone(),
        ["after"] = after?.DeepClone(),
    };
}
