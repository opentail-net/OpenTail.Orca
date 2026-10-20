using System.Text.Json;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Inbox.Graph;
using OpenTail.Modules.Calendar;
using OpenTail.Modules.Email;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Microsoft365.GraphEmailModuleFactory))]
[assembly: HostModuleFactory(typeof(OpenTail.Modules.Microsoft365.GraphCalendarModuleFactory))]

namespace OpenTail.Modules.Microsoft365;

/// <summary>Settings shared by both Graph factories: an app registration (client credentials) acting on one mailbox.</summary>
internal static class Graph
{
    public static readonly CredentialRequirement Secret = new("client-secret", CredentialKind.Secret, "Client secret of the app registration");

    public const string Note =
        "Talks to login.microsoftonline.com and graph.microsoft.com through the Microsoft Graph SDK, not through the Host's HTTP guard. " +
        "Use an application access policy so the app registration can reach only this mailbox.";

    public static void Require(string? tenantId, string? clientId, string? mailbox)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Config.TenantId is required.");
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Config.ClientId is required.");
        if (string.IsNullOrWhiteSpace(mailbox)) throw new ArgumentException("Config.Mailbox is required.");
    }

    public static bool Flag(JsonElement config, string name, bool fallback) =>
        config.ValueKind == JsonValueKind.Object
        && config.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.True or JsonValueKind.False } p
            ? p.Value.GetBoolean() : fallback;
}

/// <summary>
/// <c>"Type": "email-graph"</c>.
/// <code>
/// "Config": { "TenantId": "...", "ClientId": "...", "Mailbox": "agent@corp.example", "AllowedRecipientDomains": [ "corp.example" ] },
/// "Credentials": { "client-secret": { "Env": "GRAPH_CLIENT_SECRET", "Kind": "Secret" } }
/// </code>
/// Needs Mail.Read (and Mail.Send unless AllowSend is false) as application permissions.
/// </summary>
public sealed class GraphEmailModuleFactory : IHostModuleFactory
{
    public string Type => "email-graph";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var send = Graph.Flag(config, "AllowSend", true);
        return new()
        {
            Id = id,
            DisplayName = "Email (Microsoft 365)",
            Publisher = "OpenTail",
            Description = send ? "List and read recent mail in one Microsoft 365 mailbox; send plain-text mail." : "List and read recent mail in one Microsoft 365 mailbox (sending off).",
            Credentials = [Graph.Secret],
            Notes =
            [
                Graph.Note,
                "Message text is written by anyone who can email this mailbox: reading it taints the caller.",
                send ? "send is ExternalEffect: a tainted caller is always held for review." : "Sending is off (AllowSend false).",
            ],
            SuggestedPolicyJson = send
                ? $$"""{ "Tools": { "{{id}}.list_recent": { "Mode": "Execute" }, "{{id}}.read": { "Mode": "Execute" }, "{{id}}.send": { "Mode": "Queue", "ExpireAfter": "24:00:00" } } }"""
                : $$"""{ "Tools": { "{{id}}.list_recent": { "Mode": "Execute" }, "{{id}}.read": { "Mode": "Execute" } } }""",
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        Graph.Require(c.TenantId, c.ClientId, c.Mailbox);
        GraphSettings Settings(string secret) => GraphSettings.Create(c.TenantId!, c.ClientId!, secret, c.Mailbox!);
        var inboxes = new PerSecret<GraphInboxAdapter>(s => new GraphInboxAdapter(Settings(s), context.Id));
        var senders = new PerSecret<GraphEmailSender>(s => new GraphEmailSender(Settings(s)));
        var credentials = context.Credentials;
        async ValueTask<string> Secret(CancellationToken token) => (await credentials.GetAsync("client-secret", token)).Secret;

        IHostModule module = new EmailModule(
            new DeferredInbox(async token => inboxes.Get(await Secret(token))),
            c.AllowSend ? new DeferredSender(async token => senders.Get(await Secret(token))) : new NoSender(),
            new EmailModuleOptions
            {
                Id = context.Id,
                Manifest = Describe(context.Id, context.Config),
                AllowSend = c.AllowSend,
                Folder = c.Folder,
                MaxListMessages = c.MaxListMessages,
                ReadSearchWindow = c.ReadSearchWindow,
                MaxBodyChars = c.MaxBodyChars,
                MaxRecipients = c.MaxRecipients,
                AllowedRecipientDomains = c.AllowedRecipientDomains,
            });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string? TenantId { get; init; }
        public string? ClientId { get; init; }
        public string? Mailbox { get; init; }
        public bool AllowSend { get; init; } = true;
        public string Folder { get; init; } = "INBOX";
        public int MaxListMessages { get; init; } = 25;
        public int ReadSearchWindow { get; init; } = 200;
        public int MaxBodyChars { get; init; } = 20_000;
        public int MaxRecipients { get; init; } = 10;
        public List<string> AllowedRecipientDomains { get; init; } = [];
    }
}

/// <summary>
/// <c>"Type": "calendar-graph"</c>: the calendars of one Microsoft 365 mailbox. Same Config and credential as <c>email-graph</c>, plus
/// <c>ReadOnly</c> and <c>AllowedAttendeeDomains</c>. Needs Calendars.Read (Calendars.ReadWrite unless ReadOnly) as application permissions.
/// </summary>
public sealed class GraphCalendarModuleFactory : IHostModuleFactory
{
    public string Type => "calendar-graph";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var readOnly = Graph.Flag(config, "ReadOnly", false);
        return new()
        {
            Id = id,
            DisplayName = "Calendar (Microsoft 365)",
            Publisher = "OpenTail",
            Description = readOnly ? "List calendars and events of one Microsoft 365 mailbox. Read only." : "List calendars and events; create, delete and answer events in one Microsoft 365 mailbox.",
            Credentials = [Graph.Secret],
            Notes =
            [
                Graph.Note,
                "Event titles and descriptions are written by other people: reading them taints the caller.",
                readOnly ? "Read only (ReadOnly true)." : "create_event and respond are ExternalEffect, delete_event is Destructive: a tainted caller is always held for review.",
            ],
            SuggestedPolicyJson = readOnly
                ? $$"""{ "Tools": { "{{id}}.list_calendars": { "Mode": "Execute" }, "{{id}}.list_events": { "Mode": "Execute" } } }"""
                : $$"""{ "Tools": { "{{id}}.list_calendars": { "Mode": "Execute" }, "{{id}}.list_events": { "Mode": "Execute" }, "{{id}}.create_event": { "Mode": "Queue" }, "{{id}}.respond": { "Mode": "Queue" }, "{{id}}.delete_event": { "Mode": "Queue" } } }""",
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        Graph.Require(c.TenantId, c.ClientId, c.Mailbox);
        var calendars = new PerSecret<GraphCalendarAdapter>(s => new GraphCalendarAdapter(GraphSettings.Create(c.TenantId!, c.ClientId!, s, c.Mailbox!)));
        var credentials = context.Credentials;

        IHostModule module = new CalendarModule(
            new DeferredCalendar(async token => calendars.Get((await credentials.GetAsync("client-secret", token)).Secret)),
            new CalendarModuleOptions
            {
                Id = context.Id,
                Manifest = Describe(context.Id, context.Config),
                ReadOnly = c.ReadOnly,
                MaxEvents = c.MaxEvents,
                MaxAttendees = c.MaxAttendees,
                MaxEventLength = c.MaxEventLength,
                MaxBodyChars = c.MaxBodyChars,
                AllowedAttendeeDomains = c.AllowedAttendeeDomains,
            });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string? TenantId { get; init; }
        public string? ClientId { get; init; }
        public string? Mailbox { get; init; }
        public bool ReadOnly { get; init; }
        public int MaxEvents { get; init; } = 100;
        public int MaxAttendees { get; init; } = 25;
        public TimeSpan MaxEventLength { get; init; } = TimeSpan.FromDays(7);
        public int MaxBodyChars { get; init; } = 4000;
        public List<string> AllowedAttendeeDomains { get; init; } = [];
    }
}
