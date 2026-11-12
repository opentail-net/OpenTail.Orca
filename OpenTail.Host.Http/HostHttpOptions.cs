using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Http;

/// <summary>
/// OAuth 2.1 resource-server sign-in. Tokens come from your identity provider (Entra ID, Keycloak, Auth0, ...); this server only
/// validates them. The audience must be this resource, and tokens are never forwarded anywhere.
/// </summary>
public sealed class JwtSignInOptions
{
    /// <summary>The issuer to discover signing keys from (OIDC or RFC 8414 metadata), e.g. <c>https://login.example/realms/team</c>.</summary>
    public string? Authority { get; init; }

    /// <summary>What goes in the protected-resource metadata as the authorization server. Defaults to <see cref="Authority"/>.</summary>
    public string? AuthorizationServer { get; init; }

    /// <summary>The audience (<c>aud</c>) tokens must carry: this server, as registered at the identity provider.</summary>
    public required string Audience { get; init; }

    /// <summary>The scope a token needs to call tools. It becomes the <c>mcp:tools</c> role inside the Host.</summary>
    public string RequiredScope { get; init; } = Roles.Tools;

    /// <summary>The scope (or app role) that makes a token a reviewer. Never grant it to agent applications.</summary>
    public string ReviewScope { get; init; } = Roles.Review;

    /// <summary>The scope (or app role) that makes a token an operator (promote tools). Never grant it to agent applications.</summary>
    public string AdminScope { get; init; } = Roles.Admin;

    /// <summary>Client id of the review page's own app registration (a single-page application). When set with <see cref="Authority"/>,
    /// reviewers sign in to the page with this identity provider instead of typing a token.</summary>
    public string? PageClientId { get; init; }

    /// <summary>What the page asks the identity provider for. Default: <c>openid</c> plus <c>&lt;Audience&gt;/&lt;ReviewScope&gt;</c>.</summary>
    public string? PageScope { get; init; }

    /// <summary>Claim holding app roles / groups. Values starting with <c>group:</c> become reviewer groups.</summary>
    public string RoleClaim { get; init; } = "roles";

    /// <summary>Last-word access to the bearer options (tests, custom key material).</summary>
    public Action<JwtBearerOptions>? Configure { get; init; }
}

/// <summary>A hashed API key for a machine agent. Generate one with <see cref="ApiKeys.Generate"/>. <paramref name="Profile"/> names the
/// agent profile (policy <c>Agents</c>) that limits which tools the key may call; a key without one gets a startup warning.</summary>
/// <param name="Partner">For an <c>a2a:call</c> key: the <see cref="A2aPartner.Id"/> of the partner it belongs to. Several keys may name one partner
/// (rotation). Other keys leave it out.</param>
public sealed record ApiKeyEntry(string Name, string Hash, IReadOnlyList<string>? Scopes = null, string? Profile = null, string? Partner = null);

/// <summary>A partner: an outside agent or organisation that calls the Host over A2A. <paramref name="Id"/> is a UUID the operator
/// generates once (<c>--new-partner</c>) and never changes; <paramref name="DisplayName"/> is for people and may change.</summary>
public sealed record A2aPartner(string Id, string DisplayName);

/// <summary>The <c>A2A</c> section of the HTTP config. Phase 0: it names the partners and turns on startup checks; no A2A endpoint is
/// served yet. When <see cref="Enabled"/> is false nothing here is checked and behaviour is unchanged.</summary>
public sealed record A2aOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<A2aPartner> Partners { get; init; } = [];
}

public sealed class HostHttpOptions
{
    /// <summary>Addresses to listen on. Anything other than loopback needs sign-in, and https (or <see cref="TrustProxy"/>).</summary>
    public IReadOnlyList<string> Urls { get; init; } = ["http://127.0.0.1:0"];

    /// <summary>The address users and clients reach this server at (scheme, host, port). Used for the Host allow-list,
    /// protected-resource metadata and the links in notifications.</summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>A reverse proxy in front terminates TLS and sets <c>X-Forwarded-*</c>. Only set this when the server cannot be
    /// reached except through that proxy.</summary>
    public bool TrustProxy { get; init; }

    public JwtSignInOptions? Jwt { get; init; }

    /// <summary>The Teams bot's settings (from the HTTP config file). The runner builds <see cref="TeamsBot"/> from them.</summary>
    public Teams.TeamsBotOptions? Teams { get; init; }

    /// <summary>The Teams bot, which shares the Host's database and its notification channel; its messaging endpoint is mapped when set.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Teams.TeamsBot? TeamsBot { get; set; }
    public IReadOnlyList<ApiKeyEntry> ApiKeys { get; init; } = [];

    /// <summary>The A2A section (phase 0). Null or disabled: no A2A check runs and nothing changes.</summary>
    public A2aOptions? A2A { get; init; }

    /// <summary>Reviewer tokens for the approval page and API (in addition to JWT reviewers). They can never call tools.</summary>
    public AdminOptions? Admin { get; init; }

    /// <summary>Browser origins allowed to call this server. Default: none (MCP clients are not browsers); the approval page's own
    /// origin is always allowed.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>Requests per minute per signed-in subject (per address when not signed in). Zero turns the limit off.</summary>
    public int RateLimitPerMinute { get; init; } = 120;

    public int MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Keep metrics and recent spans in memory for the review page's Telemetry view. On by default; it never leaves the process.</summary>
    public bool RecordTelemetry { get; init; } = true;
    public string McpPath { get; init; } = "/mcp";

    internal bool HasSignIn => Jwt is not null || ApiKeys.Count > 0;

    internal static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Problems between these options and the policy: an API key naming a profile the policy does not define (an error), and
    /// keys without a profile (warnings: such a key may call every tool policy allows; a later release refuses them).</summary>
    public (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) CheckProfiles(PolicyConfig policy)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var k in ApiKeys)
        {
            if (k.Profile is null)
                warnings.Add($"API key '{k.Name}' has no agent profile, so it may call every tool the policy allows. Give it a \"Profile\" " +
                             "naming an entry of the policy's \"Agents\"; keys without one will be refused at startup in a later release.");
            else if (!policy.Agents.ContainsKey(k.Profile))
                errors.Add($"API key '{k.Name}' names agent profile '{k.Profile}', which the policy's \"Agents\" does not define.");
        }
        return (errors, warnings);
    }

    /// <summary>Startup checks for the A2A section, run only when it is enabled (a2a.md section 2 and the build order). The durability
    /// level is the one the Host's store resolved (<see cref="SqliteActionStore.Synchronous"/>), not the environment text. Null means the
    /// store is not SQLite, whose durability cannot be read, so it is refused.</summary>
    public IReadOnlyList<string> CheckA2A(PolicyConfig policy, SqliteSynchronous? synchronous)
    {
        var errors = new List<string>();
        if (A2A is not { Enabled: true }) return errors;

        if (policy.DataFlowMode != LimitMode.Enforce)
            errors.Add($"A2A is enabled, so the policy's \"DataFlowMode\" must be \"Enforce\" (it is {policy.DataFlowMode}).");

        if (synchronous is null)
            errors.Add("A2A is enabled, but the Host's store is not SQLite, so its durability cannot be checked. A2A needs the SQLite store.");
        else if (synchronous != SqliteSynchronous.Full)
            errors.Add($"A2A is enabled, so the database must run with OPENTAIL_DB_SYNCHRONOUS=full (it resolves to {synchronous.Value.ToString().ToLowerInvariant()}). " +
                       "A task id a partner was given must survive a power cut.");

        var partners = new HashSet<Guid>();
        foreach (var p in A2A.Partners)
        {
            if (!Guid.TryParseExact(p.Id ?? "", "D", out var id)) errors.Add($"A2A partner '{p.DisplayName}': Id '{p.Id}' is not a UUID (make one with --new-partner).");
            else if (!partners.Add(id)) errors.Add($"A2A partner Id '{p.Id}' is listed more than once.");
            if (string.IsNullOrWhiteSpace(p.DisplayName)) errors.Add($"A2A partner '{p.Id}' needs a DisplayName.");
        }

        foreach (var k in ApiKeys.Where(k => k.Scopes?.Contains(Roles.A2aCall, StringComparer.Ordinal) == true))
        {
            if (k.Scopes!.Any(s => s != Roles.A2aCall))
                errors.Add($"API key '{k.Name}' carries a2a:call with other scopes. A partner's key carries a2a:call alone, so it cannot also call tools.");
            if (k.Profile is null)
                errors.Add($"API key '{k.Name}' carries a2a:call and needs a \"Profile\" naming an entry of the policy's \"Agents\".");
            if (!(Guid.TryParseExact(k.Partner ?? "", "D", out var partnerId) && partners.Contains(partnerId)))
                errors.Add($"API key '{k.Name}' carries a2a:call and must name a partner from the A2A \"Partners\" list (\"Partner\": \"<id>\").");
        }
        return errors;
    }

    /// <summary>Everything wrong with these options, so a bad deployment refuses to start instead of starting open.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Urls.Count == 0) errors.Add("At least one URL is required.");
        foreach (var u in Urls)
            if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                errors.Add($"'{u}' is not an http(s) URL.");

        var allLoopback = Urls.Count > 0 && Urls.All(IsLoopback);
        if (!allLoopback)
        {
            if (!HasSignIn)
                errors.Add("Listening beyond loopback needs sign-in: configure Jwt or at least one API key.");
            if (!TrustProxy && Urls.Any(u => !IsLoopback(u) && u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                errors.Add("Listening beyond loopback over plain http is refused. Use https, or set TrustProxy when a reverse proxy terminates TLS.");
            if (PublicBaseUrl is null || !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
                errors.Add("PublicBaseUrl is required (the address clients reach this server at).");
        }
        else if (!HasSignIn && PublicBaseUrl is not null && !IsLoopback(PublicBaseUrl))
        {
            // Open on loopback is fine for a developer machine. A public address in front of loopback means a tunnel or a proxy
            // forwards to it, and then loopback no longer means "only this machine".
            errors.Add("PublicBaseUrl is not a loopback address, so a tunnel or proxy reaches this server: configure Jwt or at least one API key.");
        }

        if (Jwt is { } j)
        {
            if (string.IsNullOrWhiteSpace(j.Audience)) errors.Add("Jwt.Audience is required.");
            if (j.Authority is null && j.Configure is null) errors.Add("Jwt needs an Authority (or custom key material through Configure).");
            if (j.Authority is not null && !Uri.TryCreate(j.Authority, UriKind.Absolute, out _)) errors.Add("Jwt.Authority must be an absolute URL.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in ApiKeys)
        {
            if (!Regex.IsMatch(k.Name ?? "", "^[A-Za-z0-9._-]{1,64}$")) errors.Add($"API key name '{k.Name}' must be 1-64 letters, digits, '.', '_' or '-'.");
            if (!names.Add(k.Name ?? "")) errors.Add($"Duplicate API key name '{k.Name}'.");
            if (!Regex.IsMatch(k.Hash ?? "", "^sha256:[0-9a-fA-F]{64}$")) errors.Add($"API key '{k.Name}': Hash must be 'sha256:' and 64 hex digits (see --new-api-key).");
            if (k.Scopes is { } scopes && scopes.Any(s => s is Roles.Review or Roles.Admin || s.StartsWith(Roles.GroupPrefix, StringComparison.Ordinal)))
                errors.Add($"API key '{k.Name}': an API key can never carry a reviewer or admin role.");
            if (k.Profile is not null && !AgentProfiles.IsValidName(k.Profile))
                errors.Add($"API key '{k.Name}': Profile '{k.Profile}' must be 1-64 letters, digits, '.', '_' or '-'.");
        }

        if (Admin is { } a)
        {
            if (a.Tokens.Any(t => t.Key.Length < 16 || string.IsNullOrWhiteSpace(t.Value)))
                errors.Add("Admin tokens must be at least 16 characters and each needs a reviewer name.");
            if (a.TokenRoles is { } tr && tr.Values.Any(r => r.Contains(Roles.Tools)))
                errors.Add("A reviewer token cannot also call tools.");
        }
        if (RateLimitPerMinute < 0) errors.Add("RateLimitPerMinute cannot be negative.");
        if (MaxBodyBytes < 1024) errors.Add("MaxBodyBytes is unreasonably small.");
        if (!McpPath.StartsWith('/')) errors.Add("McpPath must start with '/'.");
        return errors;
    }
}

/// <summary>Creating and checking API keys. The key is shown once; only its hash is stored.</summary>
public static class ApiKeys
{
    public const string Prefix = "otk_";

    /// <summary>A new random key (32 random bytes) and the hash to put in configuration.</summary>
    public static (string Key, string Hash) Generate()
    {
        var key = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (key, Hash(key));
    }

    public static string Hash(string key) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    internal static byte[] HashBytes(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    internal static byte[] ParseHash(string hash) => Convert.FromHexString(hash["sha256:".Length..]);
}
