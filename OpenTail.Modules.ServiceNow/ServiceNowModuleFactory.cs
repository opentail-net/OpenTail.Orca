using System.Text.Json;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.ServiceNow.ServiceNowModuleFactory))]

namespace OpenTail.Modules.ServiceNow;

/// <summary>
/// <c>"Type": "servicenow"</c> in modules.json.
/// <code>
/// { "Type": "servicenow", "Id": "sn",
///   "Network": { "Hosts": [ "acme.service-now.com" ] },
///   "Credentials": { "servicenow": { "OAuth2": { "TokenUrl": "https://acme.service-now.com/oauth_token.do", "ClientIdEnv": "SN_ID",
///                                                "ClientSecretEnv": "SN_SECRET" }, "Host": "acme.service-now.com" } },
///   "Config": { "InstanceUrl": "https://acme.service-now.com", "Tables": [ "incident" ], "AssignmentGroups": [ "Service Desk" ] } }
/// </code>
/// Use an integration account whose roles allow exactly what the tools do on these tables (itil on incident is typical). Basic auth
/// (<c>"Kind": "Basic", "UserName": ...</c>) works too.
/// </summary>
public sealed class ServiceNowModuleFactory : IHostModuleFactory
{
    public const string CredentialName = "servicenow";

    public string Type => "servicenow";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        string? instance = null;
        IReadOnlyList<string> tables = ["incident"];
        if (config.ValueKind == JsonValueKind.Object)
            foreach (var p in config.EnumerateObject())
            {
                if (p.Name.Equals("InstanceUrl", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) instance = p.Value.GetString();
                if (p.Name.Equals("Tables", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    tables = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            }
        return ManifestFor(id, instance, tables);
    }

    internal static ModuleManifest ManifestFor(string id, string? instanceUrl, IReadOnlyList<string> tables)
    {
        var host = Uri.TryCreate(instanceUrl, UriKind.Absolute, out var u)
            ? (u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort ? u.IdnHost : u.GetLeftPart(UriPartial.Authority))
            : "*.service-now.com";
        var incident = tables.Contains("incident");
        return new ModuleManifest
        {
            Id = id,
            DisplayName = "ServiceNow",
            Publisher = "OpenTail",
            Description = $"Search and read {string.Join(", ", tables)} records and their journal; comment, create, update" + (incident ? " and resolve incidents." : "."),
            NetworkHosts = [host],
            Credentials = [new CredentialRequirement(CredentialName, CredentialKind.Bearer, "an integration account (OAuth client credentials, or Basic) with roles for these tables only")],
            Notes =
            [
                "Ticket text, comments and work notes are written by callers and other people. Every read taints the caller.",
                "Every write is ExternalEffect (resolving is Destructive), so a tainted caller is always held for a reviewer.",
                "Only the configured tables and writable fields can be touched; search takes filters, never a raw encoded query.",
                "Creates carry a correlation id so a retry after a crash does not create a second record; an interrupted comment waits for a reviewer.",
            ],
            SuggestedPolicyJson = SuggestedPolicy(id, incident),
        };
    }

    private static string SuggestedPolicy(string id, bool incident)
    {
        var tools = new System.Text.Json.Nodes.JsonObject();
        foreach (var read in new[] { "search_records", "get_record", "list_journal" }) tools[$"{id}.{read}"] = new System.Text.Json.Nodes.JsonObject { ["Mode"] = "Execute" };
        foreach (var write in incident ? new[] { "add_comment", "create_record", "update_record", "resolve_incident" } : ["add_comment", "create_record", "update_record"])
            tools[$"{id}.{write}"] = new System.Text.Json.Nodes.JsonObject { ["Mode"] = "Queue", ["ExpireAfter"] = "24:00:00" };
        return new System.Text.Json.Nodes.JsonObject { ["Tools"] = tools }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.InstanceUrl)) throw new ArgumentException("Config.InstanceUrl is required, such as https://acme.service-now.com.");
        var defaults = new ServiceNowOptions { InstanceUrl = c.InstanceUrl, HttpClient = null! };
        var tables = c.Tables ?? ["incident"];
        IHostModule module = new ServiceNowModule(new ServiceNowOptions
        {
            Id = context.Id,
            InstanceUrl = c.InstanceUrl,
            Tables = tables,
            Fields = c.Fields ?? defaults.Fields,
            WritableFields = c.WritableFields ?? defaults.WritableFields,
            AssignmentGroups = c.AssignmentGroups,
            AllowCustomerComments = c.AllowCustomerComments,
            MaxBodyChars = c.MaxBodyChars,
            MaxResults = c.MaxResults,
            MaxWriteChars = c.MaxWriteChars,
            HttpClient = context.Http.Create(CredentialName),
            Manifest = ManifestFor(context.Id, c.InstanceUrl, tables),
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string? InstanceUrl { get; init; }
        public List<string>? Tables { get; init; }
        public List<string>? Fields { get; init; }
        public List<string>? WritableFields { get; init; }
        public List<string>? AssignmentGroups { get; init; }
        public bool AllowCustomerComments { get; init; } = true;
        public int MaxBodyChars { get; init; } = 4000;
        public int MaxResults { get; init; } = 20;
        public int MaxWriteChars { get; init; } = 8000;
    }
}
