using Microsoft.IdentityModel.JsonWebTokens;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Http;

public sealed class RunningHostHttp(WebApplication app, IReadOnlyList<Uri> addresses) : IAsyncDisposable
{
    public IReadOnlyList<Uri> Addresses { get; } = addresses;
    public Uri BaseUri => Addresses[0];
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

/// <summary>
/// The Host over Streamable HTTP, for a team. Security stance: every request is signed in (OAuth 2.1 resource server via JWT, or a
/// hashed API key); tool credentials and reviewer credentials are separate things (a key can never review, a reviewer token can
/// never call tools); browser origins are refused unless allowed; the Host header must be ours; bodies and request rates are
/// capped; the server refuses to start open beyond loopback or in plain http beyond loopback without a trusted proxy.
/// </summary>
public static class HostHttp
{
    private const string HostScheme = "Host";
    private const string JwtScheme = "Jwt";
    private const string McpPolicy = "mcp";
    private const string ReviewPolicy = "review";

    public static async Task<RunningHostHttp> StartAsync(OpenTailHost host, HostHttpOptions options, CancellationToken ct = default)
    {
        var errors = options.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid HTTP options: " + string.Join(" ", errors));
        if (!host.CallerResolverConfigured)
            throw new InvalidOperationException("The Host was built without HTTP sign-in: call builder.UseHttpSignIn(), or every caller would share one identity.");
        var (profileErrors, _) = options.CheckProfiles(host.Policy.Config);
        if (profileErrors.Count > 0) throw new ArgumentException("Invalid HTTP options: " + string.Join(" ", profileErrors));
        // Phase 0: A2A checks (nothing runs unless the A2A section is enabled). The durability level is the store's resolved one.
        var a2aErrors = options.CheckA2A(host.Policy.Config, (host.Store as SqliteActionStore)?.Synchronous);
        if (a2aErrors.Count > 0) throw new ArgumentException("Invalid HTTP options: " + string.Join(" ", a2aErrors));
        // Keys live in this config, not in the database: audit what changed since the last start (audit D4).
        await AccessChanges.RecordKeysAsync(host.AccessSnapshots, host.Audit,
            options.ApiKeys.Select(k => KeyAccess.Of(k.Name, k.Hash, k.Profile)), DateTimeOffset.UtcNow, null, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls([.. options.Urls]);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.MaxBodyBytes;
        });

        var table = StaticTokenTable.Build(options);
        builder.Services.AddSingleton(table);
        ConfigureAuthentication(builder, options);
        ConfigureAuthorization(builder, options);
        ConfigureRateLimit(builder, options);

        var serverOptions = host.CreateServerOptions();
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = serverOptions.ServerInfo;
                o.ServerInstructions = serverOptions.ServerInstructions;
                o.Capabilities = serverOptions.Capabilities;
                o.Handlers = serverOptions.Handlers;
            })
            .WithHttpTransport(t => t.Stateless = true); // our sessions are our own handles; the protocol carries none

        builder.Services.AddHostedService(_ => new MaintenanceService(host));

        var app = builder.Build();
        var allowedHosts = AllowedHostNames(options);
        var allowedOrigins = AllowedOriginsSet(options);
        var loopbackOnly = options.Urls.All(HostHttpOptions.IsLoopback);

        if (options.TrustProxy)
        {
            var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost };
            fh.KnownIPNetworks.Clear(); // the operator asserted that only the proxy can reach us
            fh.KnownProxies.Clear();
            app.UseForwardedHeaders(fh);
        }

        app.Use(async (ctx, next) =>
        {
            // Health probes (Docker, an orchestrator) call the container by its own address; the answer carries nothing.
            var probe = HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.Equals("/healthz", StringComparison.Ordinal);
            if (!probe && !allowedHosts.Contains(ctx.Request.Host.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !OriginAllowed(origin, allowedOrigins, loopbackOnly))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            AdminEndpoints.SecurityHeaders(ctx);
            await next();
        });

        app.UseAuthentication();
        if (options.RateLimitPerMinute > 0) app.UseRateLimiter();
        app.UseAuthorization();

        // Healthy and degraded answer 200 (a restart would not fix a dead webhook); unhealthy (the maintenance sweep has stopped) answers
        // 503 so an orchestrator restarts the Host. Check names and states only: the probe is anonymous.
        app.MapGet("/healthz", () =>
        {
            var report = host.Health.Report();
            return Results.Json(new { status = report.State.ToString().ToLowerInvariant(), checks = report.Checks },
                statusCode: report.State == HealthState.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        }).AllowAnonymous();
        app.MapMcp(options.McpPath).RequireAuthorization(McpPolicy);

        if (options.Teams is not null && options.TeamsBot is null)
            throw new InvalidOperationException("Teams is configured but no Teams bot was built: start through HttpDeploymentRunner, or set HostHttpOptions.TeamsBot.");
        if (options.TeamsBot is { } teamsBot)
        {
            teamsBot.Attach(host);
            teamsBot.Map(app);
        }

        if (options.Admin is not null || options.Jwt is not null)
        {
            var admin = options.Admin ?? new AdminOptions();
            admin.RecordTelemetry &= options.RecordTelemetry;
            if (options.Jwt is { PageClientId: { Length: > 0 } clientId, Authority: { Length: > 0 } authority } j && admin.SignIn is null)
                admin.SignIn = new PageSignIn(authority, clientId, j.PageScope ?? $"openid {j.Audience.TrimEnd('/')}/{j.ReviewScope}");
            AdminEndpoints.Map(app, host, admin, api =>
            {
                api.RequireAuthorization(ReviewPolicy);
                api.AddEndpointFilter(async (c, nextFilter) =>
                {
                    // The reviewer the server authenticated: nothing in the request can change it.
                    c.HttpContext.Items[AdminEndpoints.ReviewerItem] = HostClaims.ReviewerOf(c.HttpContext.User);
                    return await nextFilter(c);
                });
            });
        }

        await app.StartAsync(ct);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a)).ToList();
        return new RunningHostHttp(app, addresses);
    }

    /// <summary>Wire the sign-in a host needs into an existing builder is intentionally not offered: one way to start, fewer ways to start open.</summary>
    private static void ConfigureAuthentication(WebApplicationBuilder builder, HostHttpOptions options)
    {
        var jwt = options.Jwt;
        var auth = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = HostScheme;
            o.DefaultChallengeScheme = jwt is not null ? McpAuthenticationDefaults.AuthenticationScheme : StaticTokenHandler.SchemeName;
        });

        auth.AddPolicyScheme(HostScheme, HostScheme, o =>
            o.ForwardDefaultSelector = ctx =>
                jwt is not null && StaticTokenHandler.BearerToken(ctx.Request) is { } t && LooksLikeJwt(t) ? JwtScheme : StaticTokenHandler.SchemeName);

        auth.AddScheme<AuthenticationSchemeOptions, StaticTokenHandler>(StaticTokenHandler.SchemeName, _ => { });

        if (jwt is null) return;

        auth.AddJwtBearer(JwtScheme, o =>
        {
            o.Authority = jwt.Authority;
            o.Audience = jwt.Audience;
            o.RequireHttpsMetadata = jwt.Authority is null || jwt.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            o.MapInboundClaims = false;
            o.TokenValidationParameters.ValidateAudience = true;
            o.TokenValidationParameters.ValidAudience = jwt.Audience;
            o.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            o.Events = new JwtBearerEvents { OnTokenValidated = ctx => { Enrich(ctx, jwt); return Task.CompletedTask; } };
            jwt.Configure?.Invoke(o);
        });

        // Publishes protected-resource metadata (RFC 9728) and the 401 challenge that points clients at it.
        var baseUrl = (options.PublicBaseUrl ?? options.Urls[0]).TrimEnd('/');
        auth.AddMcp(o => o.ResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = baseUrl + options.McpPath,
            AuthorizationServers = [jwt.AuthorizationServer ?? jwt.Authority ?? baseUrl],
            ScopesSupported = [jwt.RequiredScope],
            ResourceName = "OpenTail Host",
        });
    }

    private static void ConfigureAuthorization(WebApplicationBuilder builder, HostHttpOptions options) =>
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(McpPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Tools))
            .AddPolicy(ReviewPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Review));

    private static void ConfigureRateLimit(WebApplicationBuilder builder, HostHttpOptions options)
    {
        if (options.RateLimitPerMinute <= 0) return;
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var key = HostClaims.SubjectOf(ctx.User) ?? ("ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
    }

    /// <summary>Turns a validated token into a Host principal: the namespaced subject, and roles from scopes and app roles.</summary>
    private static void Enrich(TokenValidatedContext ctx, JwtSignInOptions jwt)
    {
        var p = ctx.Principal;
        var sub = p?.FindFirst("sub")?.Value;
        var iss = (ctx.SecurityToken as JsonWebToken)?.Issuer ?? p?.FindFirst("iss")?.Value;
        if (p is null || string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(iss))
        {
            ctx.Fail("The token has no subject or issuer.");
            return;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in p.FindAll("scope")) foreach (var s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) values.Add(s);
        foreach (var c in p.FindAll("scp")) foreach (var s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) values.Add(s);
        foreach (var c in p.FindAll(jwt.RoleClaim)) values.Add(c.Value);

        var identity = new ClaimsIdentity(authenticationType: JwtScheme, nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        identity.AddClaim(new Claim(HostClaims.Subject, Subjects.Jwt(iss, sub)));
        identity.AddClaim(new Claim(ClaimTypes.Name, p.FindFirst("name")?.Value ?? p.FindFirst("preferred_username")?.Value ?? sub));
        if (values.Contains(jwt.RequiredScope)) identity.AddClaim(new Claim(ClaimTypes.Role, Roles.Tools));
        if (values.Contains(jwt.ReviewScope)) identity.AddClaim(new Claim(ClaimTypes.Role, Roles.Review));
        if (values.Contains(jwt.AdminScope)) identity.AddClaim(new Claim(ClaimTypes.Role, Roles.Admin));
        foreach (var v in values.Where(v => v.StartsWith(Roles.GroupPrefix, StringComparison.Ordinal)))
            identity.AddClaim(new Claim(ClaimTypes.Role, v));
        p.AddIdentity(identity);
    }

    private static bool LooksLikeJwt(string token) => token.StartsWith("eyJ", StringComparison.Ordinal) && token.Count(c => c == '.') == 2;

    private static HashSet<string> AllowedHostNames(HostHttpOptions o)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (o.PublicBaseUrl is not null && Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var pub)) set.Add(pub.Host);
        foreach (var u in o.Urls)
            if (HostHttpOptions.IsLoopback(u)) { set.Add("127.0.0.1"); set.Add("localhost"); set.Add("[::1]"); set.Add("::1"); }
        return set;
    }

    private static HashSet<string> AllowedOriginsSet(HostHttpOptions o)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in o.AllowedOrigins) set.Add(a.TrimEnd('/'));
        if (o.PublicBaseUrl is not null && Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var pub))
            set.Add(pub.GetLeftPart(UriPartial.Authority));
        return set;
    }

    private static bool OriginAllowed(string origin, HashSet<string> allowed, bool loopbackOnly)
    {
        if (allowed.Contains(origin.TrimEnd('/'))) return true;
        // The approval page served from a loopback address talks to itself.
        return loopbackOnly && Uri.TryCreate(origin, UriKind.Absolute, out var u) && HostHttpOptions.IsLoopback(u.GetLeftPart(UriPartial.Authority));
    }
}

/// <summary>Recovers interrupted work and expires overdue actions while the server runs.</summary>
internal sealed class MaintenanceService(OpenTailHost host) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await host.RunMaintenanceOnceAsync(stoppingToken); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await host.RunMaintenanceOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* next tick tries again */ }
            }
        }
        catch (OperationCanceledException) { }
    }
}
