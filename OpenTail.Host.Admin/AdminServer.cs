using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTail.Host;

namespace OpenTail.Host.Admin;

public sealed class AdminOptions
{
    /// <summary>Bearer token to reviewer name. Tokens must be at least 16 characters. The name is what the audit trail records.
    /// Used by the standalone (loopback) admin server; a server that signs reviewers in some other way leaves it empty.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>();

    /// <summary>Roles per token (<see cref="Roles.Review"/>, <see cref="Roles.Admin"/>, <c>group:name</c>). A token with no entry
    /// here keeps the single-operator behavior: it is a reviewer and an admin.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>>? TokenRoles { get; init; }

    public int MaxEditedArgumentsBytes { get; init; } = 64 * 1024;

    /// <summary>Sign-in to the review page with the company's identity provider (OpenID Connect, authorization code with PKCE). When set, the
    /// page shows "Sign in" instead of a token box. The Host must also accept the provider's tokens for reviewers (HTTP <c>Jwt</c> options).</summary>
    public PageSignIn? SignIn { get; set; }

    /// <summary>Keep the Host's metrics and recent spans in memory for the page's Telemetry view (plan v7, A2). On by default.</summary>
    public bool RecordTelemetry { get; set; } = true;

    /// <summary>The recorder to show; when null and <see cref="RecordTelemetry"/> is on, one shared by the process is started.</summary>
    public HostTelemetryRecorder? Telemetry { get; set; }

    /// <summary>Work queues whose jobs the Jobs view lists and can stop, beside the Host's own work items and continuations.</summary>
    public IReadOnlyList<AdminWorkQueue> WorkQueues { get; init; } = [];
}

/// <summary>Role names used by the admin surface (also the scope/role values a JWT or token carries).</summary>
/// <param name="Authority">The issuer, such as <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>.</param>
/// <param name="ClientId">The page's own app registration (a single-page application with the review page as redirect URI).</param>
/// <param name="Scope">What the page asks for, such as <c>openid api://opentail-host/opentail.review</c>.</param>
public sealed record PageSignIn(string Authority, string ClientId, string Scope);

public static class Roles
{
    /// <summary>May list actions and decide them.</summary>
    public const string Review = "opentail.review";

    /// <summary>May also promote tools to auto-execute and other operator actions.</summary>
    public const string Admin = "opentail.admin";

    /// <summary>May call tools over MCP. Never granted to a reviewer-only credential, and an API key never carries Review.</summary>
    public const string Tools = "mcp:tools";

    /// <summary>May call the A2A endpoint (phase 0: the role exists and is checked at startup; no A2A endpoint is served yet).
    /// A partner's key carries this scope alone, so it cannot also call tools.</summary>
    public const string A2aCall = "a2a:call";

    public const string GroupPrefix = "group:";
}

/// <summary>Who is reviewing. <see cref="Subject"/> is namespaced (see <see cref="Subjects"/>); the name is for display.</summary>
public sealed record ReviewerIdentity(string Subject, string Name, bool IsAdmin, IReadOnlyList<string> Groups);

public sealed class RunningAdmin(WebApplication app, Uri baseUri) : IAsyncDisposable
{
    public Uri BaseUri { get; } = baseUri;
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

/// <summary>
/// Loopback-only standalone admin surface. Defences: loopback bind enforced, Host header allow-list (DNS rebinding),
/// bearer token on every /api call (no cookies, so no CSRF), no-store, nosniff, and a nonce-based CSP on the page.
/// The endpoints themselves are <see cref="AdminEndpoints"/>, which a hosted deployment mounts behind its own sign-in.
/// </summary>
public static class AdminServer
{
    private static readonly string[] AllowedHostNames = ["127.0.0.1", "localhost", "[::1]"];

    public static async Task<RunningAdmin> StartAsync(
        OpenTailHost host, AdminOptions options, string url = "http://127.0.0.1:0", CancellationToken ct = default)
    {
        if (options.Tokens.Count == 0 || options.Tokens.Any(t => t.Key.Length < 16 || string.IsNullOrWhiteSpace(t.Value)))
            throw new ArgumentException("Admin tokens must be at least 16 characters and each needs a reviewer name.");
        if (!IsLoopbackUrl(url))
            throw new ArgumentException("The admin surface only binds to a loopback address.");

        var tokenHashes = options.Tokens.ToDictionary(t => SHA256.HashData(Encoding.UTF8.GetBytes(t.Key)), t => t.Value, new BytesComparer());
        var rolesByHash = (options.TokenRoles ?? new Dictionary<string, IReadOnlySet<string>>())
            .ToDictionary(t => SHA256.HashData(Encoding.UTF8.GetBytes(t.Key)), t => t.Value, new BytesComparer());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (!AllowedHostNames.Contains(ctx.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            AdminEndpoints.SecurityHeaders(ctx);

            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var reviewer = Authenticate(ctx, tokenHashes, rolesByHash);
                if (reviewer is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "A valid bearer token is required." });
                    return;
                }
                ctx.Items[AdminEndpoints.ReviewerItem] = reviewer;
            }
            await next();
        });

        AdminEndpoints.Map(app, host, options);

        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningAdmin(app, new Uri(address));
    }

    private static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    private static ReviewerIdentity? Authenticate(
        HttpContext ctx, Dictionary<byte[], string> tokenHashes, Dictionary<byte[], IReadOnlySet<string>> rolesByHash)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        string? found = null;
        byte[]? matched = null;
        foreach (var (candidate, name) in tokenHashes) // no early exit: the loop time does not depend on which token matched
            if (CryptographicOperations.FixedTimeEquals(candidate, hash)) { found = name; matched = candidate; }
        if (found is null || matched is null) return null;

        // No explicit roles: the single-operator behavior (reviewer and admin, no groups).
        if (!rolesByHash.TryGetValue(matched, out var roles))
            return new ReviewerIdentity(Subjects.Admin(found), found, IsAdmin: true, []);
        if (!roles.Contains(Roles.Review)) return null; // a token without the review role cannot use the admin API at all
        return new ReviewerIdentity(Subjects.Admin(found), found, roles.Contains(Roles.Admin),
            roles.Where(r => r.StartsWith(Roles.GroupPrefix, StringComparison.Ordinal)).Select(r => r[Roles.GroupPrefix.Length..]).ToList());
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }
}

/// <summary>
/// The approval page and the review API as plain endpoints, so they can run in the standalone loopback server or be mounted
/// by a hosted deployment. The caller of <see cref="Map"/> must put an authentication step in front of <c>/api</c> that sets
/// <see cref="ReviewerItem"/> to a <see cref="ReviewerIdentity"/> (and rejects everyone else): the identity a review records is
/// the one the server authenticated, never anything from the request body.
/// </summary>
public static class AdminEndpoints
{
    public const string ReviewerItem = "opentail.reviewer";

    private static readonly JsonSerializerOptions EnumsAsText = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static void SecurityHeaders(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
    }

    /// <param name="configureApi">Applied to the whole <c>/api</c> group: put sign-in and authorization here.</param>
    public static void Map(IEndpointRouteBuilder app, OpenTailHost host, AdminOptions options, Action<IEndpointConventionBuilder>? configureApi = null)
    {
        var api = app.MapGroup("/api");
        configureApi?.Invoke(api);
        AdminViews.Map(api, host, options);

        app.MapGet("/", (HttpContext ctx) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            // With sign-in, the page also talks to the identity provider (its discovery document and token endpoint), and nothing else.
            var idp = options.SignIn is { } si && Uri.TryCreate(si.Authority, UriKind.Absolute, out var au) ? " " + au.GetLeftPart(UriPartial.Authority) : "";
            ctx.Response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'{idp}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            return Results.Content(AdminPage.Html.Replace("{{nonce}}", nonce, StringComparison.Ordinal), "text/html", Encoding.UTF8);
        });

        // Public on purpose: the page needs it before anyone is signed in. It holds no secret (a PKCE client has none).
        app.MapGet("/sign-in.json", () => options.SignIn is { } si
            ? Results.Json(new { authority = si.Authority.TrimEnd('/'), clientId = si.ClientId, scope = si.Scope })
            : Results.Json(new { })).AllowAnonymous();

        api.MapGet("/actions", async (string? status, string? tool, int? limit, HttpContext ctx) =>
        {
            ActionStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ActionStatus>(status.Replace("_", "", StringComparison.Ordinal), true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                filter = parsed;
            }
            var rows = await host.Store.QueryAsync(new ActionQuery { Status = filter, ToolName = tool, Limit = Math.Clamp(limit ?? 100, 1, 500) });
            var arr = new JsonArray();
            var who = ctx.Items[ReviewerItem] as ReviewerIdentity;
            foreach (var r in rows.OrderByDescending(r => r.CreatedAt)) arr.Add(Summary(r, host, who));
            return Results.Text(arr.ToJsonString(), "application/json");
        });

        api.MapGet("/actions/{id}", async (string id, HttpContext ctx) =>
        {
            var r = await host.Store.GetAsync(id);
            if (r is null) return Results.NotFound(new { error = "No such action." });
            var viewer = ctx.Items[ReviewerItem] as ReviewerIdentity;
            // Who read an action's arguments (they may hold personal data): audit D5.
            await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "action_viewed", r.Id, r.ToolName, viewer?.Subject ?? "review page", null));
            var detail = Detail(r, host, viewer);
            await AddContextAsync(detail, r, host);
            return Results.Text(detail.ToJsonString(), "application/json");
        });

        api.MapPost("/actions/{id}/review", async (string id, HttpContext ctx) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[ReviewerItem]!;
            JsonElement body;
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                if (ms.Length > options.MaxEditedArgumentsBytes + 4096) return Results.BadRequest(new { error = "Request too large." });
                body = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Body must be JSON." }); }

            var decisionText = body.TryGetProperty("decision", out var d) ? d.GetString() : null;
            ReviewDecision? decision = decisionText switch
            {
                "approve" => ReviewDecision.Approve,
                "reject" => ReviewDecision.Reject,
                "request_changes" => ReviewDecision.RequestChanges,
                "retry" => ReviewDecision.Retry,
                _ => null,
            };
            if (decision is null) return Results.BadRequest(new { error = "decision must be approve, reject, request_changes or retry." });

            var reason = body.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
            if (reason is { Length: > 1000 }) return Results.BadRequest(new { error = "reason is limited to 1000 characters." });
            if (decision is ReviewDecision.Reject or ReviewDecision.RequestChanges && string.IsNullOrWhiteSpace(reason))
                return Results.BadRequest(new { error = "A reason is required so the AI can act on it." });

            string? edited = null;
            if (body.TryGetProperty("editedArguments", out var ea) && ea.ValueKind != JsonValueKind.Null)
            {
                if (decision is not (ReviewDecision.Approve or ReviewDecision.Retry))
                    return Results.BadRequest(new { error = "Edited arguments only apply when approving or retrying." });
                if (ea.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "editedArguments must be a JSON object." });
                edited = ea.GetRawText();
                if (Encoding.UTF8.GetByteCount(edited) > options.MaxEditedArgumentsBytes)
                    return Results.BadRequest(new { error = "editedArguments is too large." });

                // Validate the edit the same way a submission is validated, so a typo does not become a failed execution.
                var current = await host.Store.GetAsync(id);
                if (current is null) return Results.NotFound(new { error = "No such action." });
                if (host.Catalog.TryGet(current.ToolName, out var reg) && reg.Preview is not null)
                {
                    try { await reg.Preview.PreviewAsync(edited); }
                    catch (ArgumentException ex) { return Results.BadRequest(new { error = "The edited arguments are not valid: " + ex.Message }); }
                }
            }

            try
            {
                var result = await host.Queue.ReviewAsync(id,
                    new ActionReview(reviewer.Name, decision.Value, reason, DateTimeOffset.UtcNow)
                    {
                        EditedArgumentsJson = edited,
                        Subject = reviewer.Subject,
                        Groups = reviewer.Groups,
                    });
                return Results.Text(Detail(result, host, reviewer).ToJsonString(), "application/json");
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "No such action." }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // Stop a delayed action during its undo window. Anyone in its reviewer group may: stopping is always the safe direction.
        api.MapPost("/actions/{id}/stop", async (string id, HttpContext ctx) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[ReviewerItem]!;
            try
            {
                var result = await host.Queue.StopAsync(id, reviewer.Subject, reviewer.Groups);
                return Results.Text(Detail(result, host, reviewer).ToJsonString(), "application/json");
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "No such action." }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        api.MapGet("/trust/{tool}", async (string tool) =>
            Results.Json(await host.Trust.EvaluateAsync(tool), EnumsAsText));

        api.MapPost("/trust/{tool}/promote", async (string tool, HttpContext ctx) =>
        {
            var reviewer = (ReviewerIdentity)ctx.Items[ReviewerItem]!;
            // Turning a tool into auto-execute is an operator decision, not a reviewer one.
            if (!reviewer.IsAdmin)
                return Results.Json(new { error = "Promoting a tool needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            var promoted = await host.Trust.PromoteAsync(tool, reviewer.Subject);
            return promoted
                ? Results.Json(new { promoted = true })
                : Results.Json(new { promoted = false, error = "Not eligible (or already promoted)." }, statusCode: StatusCodes.Status409Conflict);
        });

        api.MapGet("/explain/{tool}", (string tool, bool? tainted) =>
            Results.Json(host.Explain(tool, tainted ?? false), EnumsAsText));

        // Read-only: what is loaded, at which version, what it says it needs. Module-authored text is data; the page writes it with textContent.
        api.MapGet("/modules", () =>
        {
            var arr = new JsonArray();
            foreach (var m in host.Catalog.Modules)
            {
                var tools = new JsonArray();
                foreach (var t in m.Tools)
                {
                    if (!host.Catalog.TryGet(t, out var reg)) continue;
                    tools.Add(new JsonObject
                    {
                        ["name"] = t,
                        ["risk"] = reg.Descriptor.Risk.ToString(),
                        ["readsUntrusted"] = reg.Descriptor.ReadsUntrusted,
                        ["mode"] = host.Policy.Resolve(reg.Descriptor, tainted: false).Mode.ToString(),
                    });
                }
                arr.Add(new JsonObject
                {
                    ["id"] = m.Id,
                    ["type"] = m.Options.FactoryType,
                    ["displayName"] = m.Manifest.DisplayName,
                    ["publisher"] = m.Manifest.Publisher,
                    ["description"] = m.Manifest.Description,
                    ["assembly"] = m.Assembly,
                    ["version"] = m.Version,
                    ["contract"] = m.ContractVersion,
                    ["networkHosts"] = new JsonArray(m.Manifest.NetworkHosts.Select(h => (JsonNode?)h).ToArray()),
                    ["credentials"] = new JsonArray(m.Manifest.Credentials.Select(c => (JsonNode?)c.Name).ToArray()),
                    ["notes"] = new JsonArray(m.Manifest.Notes.Select(n => (JsonNode?)n).ToArray()),
                    ["tools"] = tools,
                });
            }
            return Results.Text(new JsonObject { ["modules"] = arr, ["retired"] = new JsonArray(host.RetiredModules.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString(), "application/json");
        });
    }

    private static JsonObject Summary(ActionRecord r, OpenTailHost host, ReviewerIdentity? reviewer)
    {
        var o = new JsonObject
    {
        ["id"] = r.Id,
        ["tool"] = r.ToolName,
        ["module"] = r.ModuleId,
        ["moduleVersion"] = r.ModuleVersion,
        ["status"] = r.Status.ToString(),
        ["requester"] = r.Requester.Id,
        ["sessionId"] = r.SessionId,
        ["tainted"] = r.Tainted,
        ["sessionLabels"] = new JsonArray(r.SessionLabels.Select(l => (JsonNode)l).ToArray()),
        ["runAt"] = r.RunAt,
        ["previewUnavailable"] = r.PreviewUnavailable,
        ["previewSummary"] = PreviewParts(r).Summary,
        ["createdAt"] = r.CreatedAt,
        ["expiresAt"] = r.ExpiresAt,
        ["attempts"] = r.Attempts,
        ["requiredApprovals"] = r.RequiredApprovals,
        ["approvals"] = ReviewAuthority.Approvals(r, host.Policy.People),
        ["reviewerGroup"] = r.ReviewerGroup,
        ["risk"] = host.Catalog.TryGet(r.ToolName, out var reg) ? reg.Descriptor.Risk.ToString() : null,
    };
        if (reviewer is not null)
        {
            var open = r.Status == ActionStatus.Pending || (r.Status == ActionStatus.Failed && r.Error?.Retryable == true);
            var verdict = ReviewAuthority.Check(r, r.Status == ActionStatus.Failed ? ReviewDecision.Retry : ReviewDecision.Approve,
                reviewer.Subject, reviewer.Groups, host.Policy.People);
            o["canDecide"] = open && verdict.Allowed;
            if (open && !verdict.Allowed) o["cannotDecideReason"] = verdict.Reason;
        }
        return o;
    }

    private static JsonObject Detail(ActionRecord r, OpenTailHost host, ReviewerIdentity? reviewer)
    {
        var o = Summary(r, host, reviewer);
        var (_, diff) = PreviewParts(r);
        o["preview"] = diff is null ? null : JsonNode.Parse(diff);
        o["arguments"] = JsonNode.Parse(r.ArgumentsJson);
        o["effectiveArguments"] = JsonNode.Parse(r.EffectiveArgumentsJson);
        o["policyMode"] = r.PolicyMode.ToString();
        o["reviewerKind"] = r.PolicySnapshot?.Reviewer.ToString();
        o["parentActionId"] = r.ParentActionId;
        o["resultAvailable"] = r.ResultJson is not null;
        o["policyRule"] = r.PolicyRule;
        o["policyHash"] = r.PolicyHash;
        o["decisionTrace"] = new JsonArray(r.DecisionTrace.Select(t => (JsonNode)new JsonObject
            { ["check"] = t.Check, ["result"] = t.Result, ["evidence"] = t.Evidence, ["changed"] = t.Changed }).ToArray());
        if (r.Error is not null)
            o["error"] = new JsonObject { ["code"] = r.Error.Code, ["message"] = r.Error.Message, ["retryable"] = r.Error.Retryable,
                ["kind"] = FailureKinds.Name(r.Error.EffectiveKind) };
        var reviews = new JsonArray();
        foreach (var rv in r.Reviews)
            reviews.Add(new JsonObject { ["reviewer"] = rv.Reviewer, ["decision"] = rv.Decision.ToString(), ["reason"] = rv.Reason, ["at"] = rv.At, ["edited"] = rv.EditedArgumentsJson is not null });
        o["reviews"] = reviews;

        var audit = new JsonArray();
        if (host.Audit is IAuditReader reader)
            foreach (var e in reader.Read(r.Id)) audit.Add(new JsonObject { ["at"] = e.At, ["event"] = e.Event, ["actor"] = e.Actor, ["detail"] = e.Detail });
        o["audit"] = audit;
        return o;
    }

    /// <summary>
    /// What a reviewer needs besides the action itself: why it is waiting (a rule, or outside content the caller read, and which read),
    /// what kind of effect it has, what the caller did just before, and how this tool's last actions were decided.
    /// </summary>
    private static async Task AddContextAsync(JsonObject o, ActionRecord r, OpenTailHost host)
    {
        host.Catalog.TryGet(r.ToolName, out var reg);
        var risk = reg?.Descriptor.Risk;
        bool Untrusted(string tool) => host.Catalog.TryGet(tool, out var t) && t.Descriptor.ReadsUntrusted;

        // What the caller did before this one: same session when there is one, else the same requester.
        var earlier = (await host.Store.QueryAsync(r.SessionId is { } s
                ? new ActionQuery { SessionId = s, Limit = 200 }
                : new ActionQuery { RequesterId = r.Requester.Id, Limit = 200 }))
            .Where(x => x.Id != r.Id && x.CreatedAt <= r.CreatedAt).OrderByDescending(x => x.CreatedAt).ToList();
        var source = r.Tainted ? earlier.FirstOrDefault(x => x.Status == ActionStatus.Succeeded && Untrusted(x.ToolName)) : null;

        string text, kind;
        if (r.Tainted && risk is ToolRisk.ExternalEffect or ToolRisk.Destructive && source is not null)
            (kind, text) = ("taint", $"The caller read outside content first ({source.ToolName}, {source.CreatedAt:u}). Text written by others can carry instructions, so actions that reach other people are held for a person, whatever the policy says.");
        else if (r.Tainted && r.SessionId is null)
            (kind, text) = ("taint", "The call came without a session, so the Host cannot tell what the caller read before; it is treated as if it had read outside content.");
        else if (r.Tainted && risk is ToolRisk.ExternalEffect or ToolRisk.Destructive)
            (kind, text) = ("taint", "The caller is marked as having read outside content recently, so actions that reach other people are held for a person.");
        else if (r.PolicyRule is { } rule && rule.Length > 0)
            (kind, text) = ("rule", $"The policy rule '{rule}' sends this to a reviewer.");
        else
            (kind, text) = ("policy", $"The policy for {r.ToolName} asks for a reviewer.");
        var why = new JsonObject { ["kind"] = kind, ["text"] = text };
        if (source is not null)
            why["taintSource"] = new JsonObject { ["actionId"] = source.Id, ["tool"] = source.ToolName, ["at"] = source.CreatedAt, ["summary"] = PreviewParts(source).Summary };
        o["why"] = why;

        o["effect"] = risk switch
        {
            ToolRisk.ExternalEffect => "Reaches other people: something is sent, posted or created that someone outside this deployment will see.",
            ToolRisk.Destructive => "Deletes, closes or cancels something. It may not be possible to undo.",
            ToolRisk.Write => "Changes data this deployment owns.",
            ToolRisk.Read => "Only reads.",
            _ => null,
        };

        var recent = new JsonArray();
        foreach (var x in earlier.Take(10))
            recent.Add(new JsonObject
            {
                ["id"] = x.Id, ["tool"] = x.ToolName, ["status"] = x.Status.ToString(), ["at"] = x.CreatedAt,
                ["readsUntrusted"] = Untrusted(x.ToolName), ["summary"] = PreviewParts(x).Summary, ["taintSource"] = x.Id == source?.Id,
            });
        o["recent"] = recent;

        var similar = new JsonArray();
        foreach (var x in (await host.Store.QueryAsync(new ActionQuery { ToolName = r.ToolName, Limit = 50 }))
                     .Where(x => x.Id != r.Id && x.Reviews.Count > 0).OrderByDescending(x => x.UpdatedAt).Take(5))
        {
            var last = x.Reviews[^1];
            similar.Add(new JsonObject
            {
                ["id"] = x.Id, ["decision"] = last.Decision.ToString(), ["reviewer"] = last.Reviewer, ["reason"] = last.Reason, ["at"] = last.At,
                ["summary"] = PreviewParts(x).Summary,
            });
        }
        o["similar"] = similar;
    }

    private static (string? Summary, string? DiffJson) PreviewParts(ActionRecord r)
    {
        if (r.PreviewJson is null) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(r.PreviewJson);
            var summary = doc.RootElement.TryGetProperty("Summary", out var s) ? s.GetString() : null;
            var diff = doc.RootElement.TryGetProperty("DiffJson", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return (summary, diff);
        }
        catch (JsonException) { return (null, null); }
    }
}
