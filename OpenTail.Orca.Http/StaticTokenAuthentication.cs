using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTail.Orca.Admin;

namespace OpenTail.Orca.Http;

/// <summary>Claim carrying the namespaced subject (see <see cref="Subjects"/>) of the authenticated caller.</summary>
public static class HostClaims
{
    public const string Subject = "opentail:subject";

    /// <summary>Claim carrying the agent profile bound to the credential (see <see cref="CallerIdentity.Profile"/>).</summary>
    public const string Profile = "opentail:profile";

    /// <summary>Claim carrying the A2A partner id of an <c>a2a:call</c> key (see <see cref="A2aPartner"/>). Absent for every other credential.</summary>
    public const string Partner = "opentail:partner";

    public static string? SubjectOf(ClaimsPrincipal? p) => p?.FindFirst(Subject)?.Value;

    public static bool HasRole(ClaimsPrincipal? p, string role) =>
        p?.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == role) == true;

    public static CallerIdentity CallerOf(ClaimsPrincipal? p)
    {
        var subject = SubjectOf(p);
        if (subject is null) return CallerIdentity.Local; // not reachable for an authenticated MCP call; stdio has no principal
        return new CallerIdentity(subject, p!.Identity?.Name ?? subject, subject.StartsWith("key:", StringComparison.Ordinal) ? CallerKind.Agent : CallerKind.User)
        {
            Roles = p.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal),
            Profile = p.FindFirst(Profile)?.Value,
        };
    }

    public static ReviewerIdentity ReviewerOf(ClaimsPrincipal p)
    {
        var subject = SubjectOf(p) ?? "unknown";
        var roles = p.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        return new ReviewerIdentity(subject, p.Identity?.Name ?? subject, roles.Contains(Roles.Admin),
            roles.Where(r => r.StartsWith(Roles.GroupPrefix, StringComparison.Ordinal)).Select(r => r[Roles.GroupPrefix.Length..]).ToList());
    }
}

/// <summary>One credential the server knows: a subject, a display name, and the roles it carries.</summary>
internal sealed record StaticCredential(byte[] Hash, string Subject, string Name, IReadOnlyList<string> Roles, string? Profile = null, string? Partner = null);

internal sealed class StaticTokenTable(IReadOnlyList<StaticCredential> credentials)
{
    /// <summary>Constant-time over the whole table: the time taken does not depend on which credential matched.</summary>
    public StaticCredential? Find(string token)
    {
        var hash = ApiKeys.HashBytes(token);
        StaticCredential? found = null;
        foreach (var c in credentials)
            if (CryptographicOperations.FixedTimeEquals(c.Hash, hash)) found = c;
        return found;
    }

    public static StaticTokenTable Build(HostHttpOptions options)
    {
        var list = new List<StaticCredential>();
        foreach (var k in options.ApiKeys)
        {
            // An API key is a tool-calling credential and nothing else; the validation already refused review/admin scopes.
            var roles = (k.Scopes is { Count: > 0 } ? k.Scopes : [Roles.Tools]).ToList();
            list.Add(new StaticCredential(ApiKeys.ParseHash(k.Hash), Subjects.Key(k.Name), k.Name, roles, k.Profile, k.Partner));
        }
        if (options.Admin is { } a)
            foreach (var (token, name) in a.Tokens)
            {
                // Reviewer tokens never carry mcp:tools: a reviewer credential cannot be used to make requests.
                var roles = a.TokenRoles is { } tr && tr.TryGetValue(token, out var set)
                    ? set.Where(r => r != Roles.Tools).ToList()
                    : [Roles.Review, Roles.Admin];
                list.Add(new StaticCredential(ApiKeys.HashBytes(token), Subjects.Admin(name), name, roles));
            }
        foreach (var ai in options.AiReviewers)
            // An AI reviewer (D-30): its own identity and the review role, and nothing else. No mcp:tools, so it cannot call a tool; no admin role, so
            // it cannot change policy, budgets, credentials or levels.
            list.Add(new StaticCredential(ApiKeys.ParseHash(ai.Hash), Subjects.Ai(ai.Name), ai.Name, [Roles.Review]));
        return new StaticTokenTable(list);
    }
}

internal sealed class StaticTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, StaticTokenTable table)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Static";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = BearerToken(Request);
        if (token is null) return Task.FromResult(AuthenticateResult.NoResult());
        var credential = table.Find(token);
        if (credential is null) return Task.FromResult(AuthenticateResult.Fail("Unknown credential."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.Subject),
            new(ClaimTypes.Name, credential.Name),
            new(HostClaims.Subject, credential.Subject),
        };
        claims.AddRange(credential.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (credential.Profile is { } profile) claims.Add(new Claim(HostClaims.Profile, profile));
        if (credential.Partner is { } partner) claims.Add(new Claim(HostClaims.Partner, partner));
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"opentail-orca\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    internal static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var t = header["Bearer ".Length..].Trim();
            return t.Length > 0 ? t : null;
        }
        var key = request.Headers["X-API-Key"].ToString();
        return key.Length > 0 ? key : null;
    }
}
