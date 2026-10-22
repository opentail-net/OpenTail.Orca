using System.Text.Json;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Inbox.Gmail;
using OpenTail.Modules.Calendar;
using OpenTail.Modules.Email;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Google.GmailModuleFactory))]
[assembly: HostModuleFactory(typeof(OpenTail.Modules.Google.GoogleCalendarModuleFactory))]

namespace OpenTail.Modules.Google;

/// <summary>Settings shared by both Google factories: an OAuth client and a refresh token for one account.</summary>
internal static class GoogleAuth
{
    public static readonly CredentialRequirement[] Credentials =
    [
        new("client-secret", CredentialKind.Secret, "Client secret of the Google Cloud OAuth client"),
        new("refresh-token", CredentialKind.Secret, "Refresh token the account granted to that client"),
    ];

    public const string Note =
        "Talks to oauth2.googleapis.com, gmail.googleapis.com and www.googleapis.com through the Google API client, not through the Host's HTTP guard. " +
        "The refresh token carries the scopes the account consented to: grant only what the enabled tools need (read only, or read and send).";

    public static void Require(string? clientId, string? account)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Config.ClientId is required.");
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("Config.Account (the Google account's address) is required.");
    }

    public static bool Flag(JsonElement config, string name, bool fallback) =>
        config.ValueKind == JsonValueKind.Object
        && config.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.True or JsonValueKind.False } p
            ? p.Value.GetBoolean() : fallback;

    /// <summary>One OAuth credential per (client secret, refresh token) pair, shared by everything built from it, rebuilt when either rotates.</summary>
    public static PerSecret<GmailCredentialProvider> Providers(string clientId, string account) =>
        new(pair => new GmailCredentialProvider(Settings(clientId, account, pair)));

    public static GmailSettings Settings(string clientId, string account, string pair)
    {
        var i = pair.IndexOf('\n');
        return GmailSettings.Create(clientId, pair[..i], pair[(i + 1)..], account);
    }

    public static async ValueTask<string> PairAsync(ICredentialProvider credentials, CancellationToken ct) =>
        (await credentials.GetAsync("client-secret", ct)).Secret + "\n" + (await credentials.GetAsync("refresh-token", ct)).Secret;
}

/// <summary>
/// <c>"Type": "email-gmail"</c>.
/// <code>
/// "Config": { "ClientId": "1234.apps.googleusercontent.com", "Account": "agent@corp.example", "AllowedRecipientDomains": [ "corp.example" ] },
/// "Credentials": { "client-secret": { "Env": "GOOGLE_CLIENT_SECRET", "Kind": "Secret" },
///                  "refresh-token": { "Env": "GOOGLE_REFRESH_TOKEN", "Kind": "Secret" } }
/// </code>
/// Scopes: gmail.readonly (and gmail.send unless AllowSend is false).
/// </summary>
public sealed class GmailModuleFactory : IHostModuleFactory
{
    public string Type => "email-gmail";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var send = GoogleAuth.Flag(config, "AllowSend", true);
        return new()
        {
            Id = id,
            DisplayName = "Email (Gmail)",
            Publisher = "OpenTail",
            Description = send ? "List and read recent mail in one Gmail account; send plain-text mail." : "List and read recent mail in one Gmail account (sending off).",
            Credentials = GoogleAuth.Credentials,
            Notes =
            [
                GoogleAuth.Note,
                "Message text is written by anyone who can email this account: reading it taints the caller.",
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
        GoogleAuth.Require(c.ClientId, c.Account);
        var providers = GoogleAuth.Providers(c.ClientId!, c.Account!);
        var credentials = context.Credentials;

        IHostModule module = new EmailModule(
            new DeferredInbox(async token =>
            {
                var pair = await GoogleAuth.PairAsync(credentials, token);
                return new GmailInboxAdapter(GoogleAuth.Settings(c.ClientId!, c.Account!, pair), context.Id, credentialProvider: providers.Get(pair));
            }),
            c.AllowSend
                ? new DeferredSender(async token =>
                {
                    var pair = await GoogleAuth.PairAsync(credentials, token);
                    return new GmailEmailSender(GoogleAuth.Settings(c.ClientId!, c.Account!, pair), providers.Get(pair));
                })
                : new NoSender(),
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
        public string? ClientId { get; init; }
        public string? Account { get; init; }
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
/// <c>"Type": "calendar-google"</c>: the calendars of one Google account. Same Config and credentials as <c>email-gmail</c>, plus
/// <c>ReadOnly</c> and <c>AllowedAttendeeDomains</c>. Scope: calendar.readonly (or calendar unless ReadOnly).
/// </summary>
public sealed class GoogleCalendarModuleFactory : IHostModuleFactory
{
    public string Type => "calendar-google";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var readOnly = GoogleAuth.Flag(config, "ReadOnly", false);
        return new()
        {
            Id = id,
            DisplayName = "Calendar (Google)",
            Publisher = "OpenTail",
            Description = readOnly ? "List calendars and events of one Google account. Read only." : "List calendars and events; create, delete and answer events in one Google account.",
            Credentials = GoogleAuth.Credentials,
            Notes =
            [
                GoogleAuth.Note,
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
        GoogleAuth.Require(c.ClientId, c.Account);
        var providers = GoogleAuth.Providers(c.ClientId!, c.Account!);
        var credentials = context.Credentials;

        IHostModule module = new CalendarModule(
            new DeferredCalendar(async token =>
            {
                var pair = await GoogleAuth.PairAsync(credentials, token);
                // The Host's policy decides each write (review, undo window); the adapter only needs to know writes are on at all.
                return new GmailCalendarAdapter(GoogleAuth.Settings(c.ClientId!, c.Account!, pair), providers.Get(pair),
                    c.ReadOnly ? CalendarWritePolicy.Disabled : CalendarWritePolicy.AllowAll);
            }),
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
        public string? ClientId { get; init; }
        public string? Account { get; init; }
        public bool ReadOnly { get; init; }
        public int MaxEvents { get; init; } = 100;
        public int MaxAttendees { get; init; } = 25;
        public TimeSpan MaxEventLength { get; init; } = TimeSpan.FromDays(7);
        public int MaxBodyChars { get; init; } = 4000;
        public List<string> AllowedAttendeeDomains { get; init; } = [];
    }
}
