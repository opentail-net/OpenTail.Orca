[assembly: HostModuleFactory(typeof(OpenTail.Modules.Web.WebModuleFactory))]

namespace OpenTail.Modules.Web;

/// <summary>
/// <c>"Type": "web"</c> in modules.json. The hosts it may fetch from are the entry's <c>Network.Hosts</c>, enforced by the Host's guarded
/// HTTP client (https unless an origin is listed, public addresses only, redirects re-checked). Config: <c>{ "MaxTextChars": 40000 }</c>.
/// </summary>
public sealed class WebModuleFactory : IHostModuleFactory
{
    public string Type => "web";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "Web pages",
        Publisher = "OpenTail",
        Description = "Fetch web pages from the hosts you list and return readable text.",
        Notes =
        [
            "Reaches only the hosts listed under Network.Hosts; list at least one.",
            "Page text is outside content: it taints the caller.",
            "Read only: GET requests, no credentials sent.",
        ],
        SuggestedPolicyJson = $$"""{ "Tools": { "{{id}}.fetch": { "Mode": "Execute" } } }""",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        IHostModule module = new WebModule(new WebModuleOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            AllowedHosts = ["*"],          // not used: HostsEnforcedByClient
            HostsEnforcedByClient = true,
            AllowHttp = true,              // the guarded client refuses plain http unless the operator listed an http origin
            MaxBytes = c.MaxBytes,
            MaxTextChars = c.MaxTextChars,
            HttpClient = context.Http.Create(),
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public int MaxBytes { get; init; } = 512 * 1024;
        public int MaxTextChars { get; init; } = 40_000;
    }
}
