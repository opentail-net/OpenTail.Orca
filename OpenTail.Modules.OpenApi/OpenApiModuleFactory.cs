[assembly: HostModuleFactory(typeof(OpenTail.Modules.OpenApi.OpenApiModuleFactory))]

namespace OpenTail.Modules.OpenApi;

/// <summary>
/// <c>"Type": "openapi"</c> in modules.json; the entry's Id is the tool prefix. Config:
/// <code>{ "Spec": "crm.json", "Operations": [ "getCustomer", "listOrders" ], "ServerUrl": "https://api.crm.example/v2" }</code>
/// <c>Operations</c> is required: an imported API can have hundreds of operations, and none are exposed unless named. The API token is the
/// credential <c>api</c> (optional), attached by the Host only to requests for its bound host. The hosts the module may reach are whatever
/// the operator lists under Network.Hosts; the spec cannot add any.
/// </summary>
public sealed class OpenApiModuleFactory : IHostModuleFactory
{
    public const string CredentialName = "api";

    public string Type => "openapi";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => Describe(id, config, Directory.GetCurrentDirectory());

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config, string baseDirectory) =>
        ProfileReference(config) is { } reference ? ManifestFor(id, OpenApiProfile.Open(reference, baseDirectory)) : PlainManifest(id);

    private static ModuleManifest PlainManifest(string id) => new()
    {
        Id = id,
        DisplayName = "OpenAPI import",
        Publisher = "OpenTail",
        Description = "One tool per named operation of an OpenAPI document. GET/HEAD are Read, POST/PUT/PATCH ExternalEffect, DELETE Destructive.",
        Credentials = [new CredentialRequirement(CredentialName, CredentialKind.Bearer, "token for the API") { Optional = true }],
        Notes =
        [
            "Responses are third-party content and taint the caller unless ResponsesAreUntrusted is false.",
            "Only operations listed in Config.Operations become tools.",
        ],
    };

    public async ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        if (ProfileReference(context.Config) is { } reference) return await CreateFromProfileAsync(context, reference, ct);
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.Spec)) throw new ArgumentException("Config.Spec (path to the OpenAPI document) is required.");
        if (c.Operations is not { Count: > 0 })
            throw new ArgumentException("Config.Operations is required: name the operations to expose (generated names, without the prefix).");
        var specPath = Path.IsPathRooted(c.Spec) ? c.Spec : Path.GetFullPath(Path.Combine(context.BaseDirectory, c.Spec));

        var http = context.Http.Create(context.Credentials.Names.Contains(CredentialName) ? CredentialName : null);
        var module = await OpenApiModule.FromFileAsync(specPath, new OpenApiModuleOptions
        {
            Namespace = context.Id,
            ServerUrlOverride = c.ServerUrl,
            AllowInsecureHttp = c.AllowInsecureHttp,
            IncludeOperations = c.Operations.ToHashSet(StringComparer.Ordinal),
            IncludeDeprecated = c.IncludeDeprecated,
            MaxResponseBytes = c.MaxResponseBytes,
            RiskOverrides = c.RiskOverrides,
            ResponsesAreUntrusted = c.ResponsesAreUntrusted,
            HttpClient = http,
            Manifest = Describe(context.Id, context.Config),
        }, ct);

        var missing = c.Operations.Except(module.OperationNames, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Config.Operations names operations the spec does not have: {string.Join(", ", missing)} (available: {string.Join(", ", module.AllOperationNames.Take(30))}).");
        return module;
    }

    // ---------------------------------------------------------------------------------------------------------- profiles

    /// <summary><c>Config.Profile</c>: a profile file (relative to the modules file) or <c>package:Assembly/name</c>; null for a plain import.</summary>
    private static string? ProfileReference(System.Text.Json.JsonElement config)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        foreach (var p in config.EnumerateObject())
            if (p.Name.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                var v = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException("Config.Profile must name a profile file or package:Assembly/name.");
                return v;
            }
        return null;
    }

    internal static ModuleManifest ManifestFor(string id, OpenApiProfile p)
    {
        var tools = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, t) in p.Tools)
            tools[$"{id}.{name}"] = t.Risk == ToolRisk.Read
                ? new System.Text.Json.Nodes.JsonObject { ["Mode"] = "Execute" }
                : new System.Text.Json.Nodes.JsonObject { ["Mode"] = "Queue", ["ExpireAfter"] = "24:00:00" };
        var risks = p.Tools.GroupBy(t => t.Value.Risk).Select(g => $"{g.Count()} {g.Key}");
        return new ModuleManifest
        {
            Id = id,
            DisplayName = p.DisplayName ?? p.Name,
            Publisher = p.Publisher,
            Description = p.Description ?? $"Integration profile {p.Name}" + (p.Version is null ? "" : $" {p.Version}") + ".",
            NetworkHosts = p.Hosts,
            Credentials = (p.Credential is { } c
                    ? new[] { new CredentialRequirement(c.Name, c.Kind, c.Description ?? $"credential for {p.DisplayName ?? p.Name}") { Optional = c.Optional } }
                    : [])
                .Concat(p.Pseudonymises
                    ? [new CredentialRequirement(OpenApiProfile.PseudonymKeyCredential, CredentialKind.Secret, "key for stable author pseudonyms across restarts") { Optional = true }]
                    : Array.Empty<CredentialRequirement>())
                .ToList(),
            Notes = p.Notes.Append($"Integration profile {p.Name}{(p.Version is null ? "" : " " + p.Version)} ({p.Source}): {string.Join(", ", risks)} tool(s); only the operations it names exist.")
                .Concat(p.Pseudonymises ? ["Authors are shown as pseudonyms; map pseudonym-key to keep them stable across restarts."] : Array.Empty<string>())
                .Append(p.Untrusted ? "Responses are outside content and taint the caller." : "Responses are not marked as outside content.").ToList(),
            SuggestedPolicyJson = new System.Text.Json.Nodes.JsonObject { ["Tools"] = tools }.ToJsonString(),
        };
    }

    private static ValueTask<IHostModule> CreateFromProfileAsync(ModuleContext context, string reference, CancellationToken ct)
    {
        var profile = OpenApiProfile.Open(reference, context.BaseDirectory);
        string? serverUrl = null;
        var allowHttp = false;
        var maxBytes = 64 * 1024;
        var settings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in context.Config.EnumerateObject())
        {
            var key = profile.Config.Keys.FirstOrDefault(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (p.Name.Equals("Profile", StringComparison.OrdinalIgnoreCase)) continue;
            else if (p.Name.Equals("ServerUrl", StringComparison.OrdinalIgnoreCase)) serverUrl = p.Value.GetString();
            else if (p.Name.Equals("AllowInsecureHttp", StringComparison.OrdinalIgnoreCase)) allowHttp = p.Value.GetBoolean();
            else if (p.Name.Equals("MaxResponseBytes", StringComparison.OrdinalIgnoreCase)) maxBytes = p.Value.GetInt32();
            else if (key is not null)
                settings[key] = profile.Config[key].Type == "string" && p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? [p.Value.GetString()!]
                    : p.Value.ValueKind == System.Text.Json.JsonValueKind.Array && p.Value.EnumerateArray().All(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                        ? p.Value.EnumerateArray().Select(x => x.GetString()!).ToList()
                        : throw new ArgumentException($"Config.{p.Name} must be {profile.Config[key].Type}.");
            else throw new ArgumentException($"Config.{p.Name} is not a setting of profile {profile.Name} (it has: Profile, ServerUrl, AllowInsecureHttp, MaxResponseBytes" +
                (profile.Config.Count > 0 ? ", " + string.Join(", ", profile.Config.Keys) : "") + ").");
        }
        foreach (var (k, s) in profile.Config.Where(kv => kv.Value.Required && !settings.ContainsKey(kv.Key)))
            throw new ArgumentException($"Profile {profile.Name} needs Config.{k}" + (s.Description is null ? "." : $": {s.Description}."));

        var server = serverUrl ?? profile.Server;
        if (server is not null)
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var u)) throw new ArgumentException("The server URL is not absolute.");
            var host = u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort ? u.IdnHost : u.GetLeftPart(UriPartial.Authority);
            if (!profile.Hosts.Any(h => NetworkRules.Covers(h, host)))
                throw new ArgumentException($"The server {u.GetLeftPart(UriPartial.Authority)} is not one of profile {profile.Name}'s hosts ({string.Join(", ", profile.Hosts)}).");
        }

        var credential = profile.Credential is { } c && context.Credentials.Names.Contains(c.Name) ? c.Name : null;
        string spec;
        try { spec = profile.ReadRelative(profile.Spec); }
        catch (IOException ex) { throw new ArgumentException($"Profile {profile.Name}: cannot read its spec {profile.Spec}: {ex.Message}"); }
        var module = OpenApiModule.Create(spec, new OpenApiModuleOptions
        {
            Namespace = context.Id,
            ServerUrlOverride = serverUrl,
            AllowInsecureHttp = allowHttp,
            MaxResponseBytes = maxBytes,
            HttpClient = context.Http.Create(credential),
            Manifest = ManifestFor(context.Id, profile),
            Profile = profile,
            ProfileSettings = settings,
            PseudonymKey = context.Credentials.Names.Contains(OpenApiProfile.PseudonymKeyCredential)
                ? async token => (await context.Credentials.GetAsync(OpenApiProfile.PseudonymKeyCredential, token)).Secret
                : null,
        });
        // Conformance findings are warnings: shown at startup, by --list-modules and by --lint.
        foreach (var finding in module.ProfileFindings) context.Warn($"profile {profile.Name}: {finding}");
        return ValueTask.FromResult<IHostModule>(module);
    }

    private sealed class Config
    {
        public string Spec { get; init; } = "";
        public List<string>? Operations { get; init; }
        public string? ServerUrl { get; init; }
        public bool AllowInsecureHttp { get; init; }
        public bool IncludeDeprecated { get; init; }
        public int MaxResponseBytes { get; init; } = 64 * 1024;
        public Dictionary<string, ToolRisk>? RiskOverrides { get; init; }
        public bool ResponsesAreUntrusted { get; init; } = true;
    }
}
