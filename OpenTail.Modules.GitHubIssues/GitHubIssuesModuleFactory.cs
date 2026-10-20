using System.Text.Json;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.GitHubIssues.GitHubIssuesModuleFactory))]

namespace OpenTail.Modules.GitHubIssues;

/// <summary>
/// <c>"Type": "github-issues"</c> in modules.json.
/// <code>
/// { "Type": "github-issues", "Id": "github",
///   "Network": { "Hosts": [ "api.github.com" ] },
///   "Credentials": { "github": { "Env": "GITHUB_TOKEN", "Kind": "Bearer", "Host": "api.github.com" } },
///   "Config": { "Repositories": [ "acme/support" ], "AllowedLabels": [ "bug", "question" ] } }
/// </code>
/// Use a fine-grained token limited to these repositories with "Issues: read and write" (read-only if you only want the read tools).
/// </summary>
public sealed class GitHubIssuesModuleFactory : IHostModuleFactory
{
    public const string CredentialName = "github";

    public string Type => "github-issues";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var baseUrl = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("ApiBaseUrl", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()! : "https://api.github.com";
        return ManifestFor(id, baseUrl);
    }

    internal static ModuleManifest ManifestFor(string id, string apiBaseUrl)
    {
        var host = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var u)
            ? (u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort ? u.IdnHost : u.GetLeftPart(UriPartial.Authority))
            : "api.github.com";
        return new ModuleManifest
        {
            Id = id,
            DisplayName = "GitHub Issues",
            Publisher = "OpenTail",
            Description = "Search and read issues and comments; comment on, open, change and close issues, in allow-listed repositories only.",
            NetworkHosts = [host],
            Credentials = [new CredentialRequirement(CredentialName, CredentialKind.Bearer, "a fine-grained token limited to the configured repositories (Issues: read and write)")],
            Notes =
            [
                "Issue and comment text is written by anyone who can open an issue. Every read taints the caller.",
                "Every write is ExternalEffect (closing is Destructive), so a tainted caller is always held for a reviewer.",
                "Only the configured repositories can be touched; search qualifiers that name other repositories, organisations or users are refused.",
                "Writes carry a hidden marker so a retry after a crash does not post twice.",
            ],
            SuggestedPolicyJson = $$"""
                { "Tools": {
                    "{{id}}.search_issues": { "Mode": "Execute" },
                    "{{id}}.get_issue":     { "Mode": "Execute" },
                    "{{id}}.list_comments": { "Mode": "Execute" },
                    "{{id}}.add_comment":   { "Mode": "Queue", "ExpireAfter": "24:00:00" },
                    "{{id}}.create_issue":  { "Mode": "Queue", "ExpireAfter": "24:00:00" },
                    "{{id}}.update_issue":  { "Mode": "Queue", "ExpireAfter": "24:00:00" },
                    "{{id}}.close_issue":   { "Mode": "Queue", "ExpireAfter": "24:00:00" }
                } }
                """,
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (c.Repositories is not { Count: > 0 }) throw new ArgumentException("Config.Repositories is required: list the owner/name repositories the module may use.");
        IHostModule module = new GitHubIssuesModule(new GitHubIssuesOptions
        {
            Id = context.Id,
            Repositories = c.Repositories,
            ApiBaseUrl = c.ApiBaseUrl,
            AllowedLabels = c.AllowedLabels,
            MaxBodyChars = c.MaxBodyChars,
            MaxResults = c.MaxResults,
            MaxWriteChars = c.MaxWriteChars,
            HttpClient = context.Http.Create(CredentialName),
            Manifest = ManifestFor(context.Id, c.ApiBaseUrl),
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public List<string>? Repositories { get; init; }
        public string ApiBaseUrl { get; init; } = "https://api.github.com";
        public List<string>? AllowedLabels { get; init; }
        public int MaxBodyChars { get; init; } = 4000;
        public int MaxResults { get; init; } = 20;
        public int MaxWriteChars { get; init; } = 20_000;
    }
}
