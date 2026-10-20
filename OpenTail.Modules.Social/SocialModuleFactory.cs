using System.Text.Json;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Social.SocialModuleFactory))]

namespace OpenTail.Modules.Social;

/// <summary>
/// <c>"Type": "social"</c> in modules.json: read-only Reddit listening and a local sentiment score.
/// <code>
/// { "Type": "social", "Id": "social",
///   "Network": { "Hosts": [ "oauth.reddit.com" ] },
///   "Credentials": {
///     "reddit": { "OAuth2": { "TokenUrl": "https://www.reddit.com/api/v1/access_token", "ClientIdEnv": "REDDIT_ID", "ClientSecretEnv": "REDDIT_SECRET",
///                             "ClientAuthentication": "Basic" }, "Host": "oauth.reddit.com" },
///     "pseudonym-key": { "Env": "SOCIAL_PSEUDONYM_KEY", "Kind": "Secret" } },
///   "Config": { "Reddit": { "Subreddits": [ "dotnet", "csharp" ] } } }
/// </code>
/// YouTube and X are integration profiles (<c>opentail/profiles/youtube</c>, <c>opentail/profiles/x</c>), enabled with <c>"Type": "openapi"</c>.
/// </summary>
public sealed class SocialModuleFactory : IHostModuleFactory
{
    public string Type => "social";

    private const string DefaultReddit = "https://oauth.reddit.com";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var baseUrl = TryBind(config)?.Reddit?.ApiBaseUrl ?? DefaultReddit;
        return new ModuleManifest
        {
            Id = id,
            DisplayName = "Social listening (Reddit)",
            Publisher = "OpenTail",
            Description = "Search and read public Reddit posts and threads; score sentiment locally. Read only.",
            NetworkHosts = [Host(baseUrl)],
            Credentials =
            [
                new CredentialRequirement("reddit", CredentialKind.Bearer, "Reddit app-only OAuth (client credentials, ClientAuthentication Basic)"),
                new CredentialRequirement("pseudonym-key", CredentialKind.Secret, "Key for stable author pseudonyms across restarts") { Optional = true },
            ],
            Notes =
            [
                "Everything returned is written by strangers: every network tool taints the caller, so any later send or comment waits for a reviewer.",
                "Authors (and @mentions, u/names in text) are replaced by pseudonyms unless PseudonymiseAuthors is false. Without pseudonym-key they change on restart.",
                "No posting, replying, voting or messaging. Results are kept in the action store until the Host's RetentionDays removes them: set it.",
                "YouTube and X are integration profiles now (opentail/profiles); see PROFILES.md.",
            ],
            SuggestedPolicyJson = $$"""
                { "Tools": {
                    "{{id}}.search":     { "Mode": "Execute" },
                    "{{id}}.get_post":   { "Mode": "Execute" },
                    "{{id}}.get_thread": { "Mode": "Execute" },
                    "{{id}}.sentiment":  { "Mode": "Execute" }
                } }
                """,
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        if (context.Config.ValueKind == JsonValueKind.Object)
            foreach (var p in context.Config.EnumerateObject())
                if (p.Name.Equals("YouTube", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("X", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Config.{p.Name}: YouTube and X are integration profiles now. Enable opentail/profiles/{p.Name.ToLowerInvariant()} with \"Type\": \"openapi\" (see PROFILES.md).");
        var c = context.Bind<Config>();
        if (c.Reddit is null) throw new ArgumentException("Config.Reddit is required (it may be empty: { }).");
        var credentials = context.Credentials;
        IHostModule module = new SocialModule(new SocialOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            Reddit = new RedditOptions { HttpClient = context.Http.Create("reddit"), ApiBaseUrl = c.Reddit.ApiBaseUrl ?? DefaultReddit, Subreddits = c.Reddit.Subreddits },
            PseudonymiseAuthors = c.PseudonymiseAuthors,
            PseudonymKey = credentials.Names.Contains("pseudonym-key") ? async token => (await credentials.GetAsync("pseudonym-key", token)).Secret : null,
            MaxResults = c.MaxResults,
            MaxTextChars = c.MaxTextChars,
        });
        return ValueTask.FromResult(module);
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort ? u.IdnHost : u.GetLeftPart(UriPartial.Authority)) : url;

    private static Config? TryBind(JsonElement config)
    {
        try { return config.ValueKind == JsonValueKind.Object ? config.Deserialize<Config>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null; }
        catch (JsonException) { return null; }
    }

    private sealed class RedditConfig
    {
        public string? ApiBaseUrl { get; init; }
        public List<string>? Subreddits { get; init; }
    }

    private sealed class Config
    {
        public RedditConfig? Reddit { get; init; }
        public bool PseudonymiseAuthors { get; init; } = true;
        public int MaxResults { get; init; } = 25;
        public int MaxTextChars { get; init; } = 2000;
    }
}
