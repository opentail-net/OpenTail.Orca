using OpenTail.Connectivity.Calendar.Ics;
using OpenTail.Connectivity.Files;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Calendar.Ics.IcsCalendarModuleFactory))]

namespace OpenTail.Modules.Calendar.Ics;

/// <summary>
/// <c>"Type": "calendar-ics"</c> in modules.json: read-only events from an ICS feed.
/// <code>
/// "Network": { "Hosts": [ "calendar.corp.example" ] },
/// "Config": { "Url": "https://calendar.corp.example/team.ics", "Name": "team" }
/// </code>
/// A feed URL that carries a secret token goes in the <c>url</c> credential (Kind Secret) instead of <c>Config.Url</c>.
/// </summary>
public sealed class IcsCalendarModuleFactory : IHostModuleFactory
{
    public string Type => "calendar-ics";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "Calendar (ICS feed)",
        Publisher = "OpenTail",
        Description = "List events from an ICS feed. Read only.",
        Credentials = [new CredentialRequirement("url", CredentialKind.Secret, "Feed URL, when it contains a secret token") { Optional = true }],
        Notes =
        [
            "Fetched through the Host's guarded HTTP client: list the feed's host under Network.Hosts.",
            "Event titles and descriptions are written by other people: reading them taints the caller.",
            "Read only: no tool creates, changes or answers events.",
        ],
        SuggestedPolicyJson = $$"""{ "Tools": { "{{id}}.list_calendars": { "Mode": "Execute" }, "{{id}}.list_events": { "Mode": "Execute" } } }""",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        var secretUrl = context.Credentials.Names.Contains("url");
        if (!secretUrl && !Uri.TryCreate(c.Url, UriKind.Absolute, out _)) throw new ArgumentException("Config.Url (absolute) is required, or map the 'url' credential.");

        var http = context.Http.Create();
        var name = string.IsNullOrWhiteSpace(c.Name) ? context.Id : c.Name;
        var feeds = new PerSecret<IcsCalendarAdapter>(url => new IcsCalendarAdapter(url, name, context.Id, http, c.CacheTtl));
        var credentials = context.Credentials;
        var calendar = new DeferredCalendar(async token =>
            feeds.Get(secretUrl ? (await credentials.GetAsync("url", token)).Secret : c.Url!));

        IHostModule module = new CalendarModule(calendar, new CalendarModuleOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            ReadOnly = true,
            MaxEvents = c.MaxEvents,
            MaxBodyChars = c.MaxBodyChars,
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string? Url { get; init; }
        public string? Name { get; init; }
        public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
        public int MaxEvents { get; init; } = 100;
        public int MaxBodyChars { get; init; } = 4000;
    }
}
