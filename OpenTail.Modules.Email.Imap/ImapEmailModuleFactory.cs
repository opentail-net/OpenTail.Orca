using OpenTail.Connectivity.Inbox.Imap;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Email.Imap.ImapEmailModuleFactory))]

namespace OpenTail.Modules.Email.Imap;

/// <summary>
/// <c>"Type": "email-imap"</c> in modules.json.
/// <code>
/// "Config": { "UserName": "agent@corp.example",
///             "Imap": { "Host": "imap.corp.example" },
///             "Smtp": { "Host": "smtp.corp.example", "Port": 587 },
///             "AllowedRecipientDomains": [ "corp.example" ] },
/// "Credentials": { "password": { "Env": "MAIL_PASSWORD", "Kind": "Secret" } }
/// </code>
/// The password (use an app password) is read when a tool runs. <c>smtp-password</c> overrides it for sending.
/// </summary>
public sealed class ImapEmailModuleFactory : IHostModuleFactory
{
    public string Type => "email-imap";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config)
    {
        var c = TryBind(config);
        var send = c?.AllowSend ?? true;
        return new()
        {
            Id = id,
            DisplayName = "Email (IMAP/SMTP)",
            Publisher = "OpenTail",
            Description = send ? "List and read recent mail over IMAP; send plain-text mail over SMTP." : "List and read recent mail over IMAP (sending off).",
            Credentials =
            [
                new CredentialRequirement("password", CredentialKind.Secret, "Mailbox password or app password"),
                new CredentialRequirement("smtp-password", CredentialKind.Secret, "SMTP password, when it differs") { Optional = true },
            ],
            Notes =
            [
                $"Connects to {c?.Imap?.Host ?? "the IMAP host"}" + (send ? $" and {c?.Smtp?.Host ?? "the SMTP host"}" : "") +
                    " directly: mail traffic does not go through the Host's HTTP guard.",
                "Message text is written by anyone who can email this address: reading it taints the caller.",
                send ? "send is ExternalEffect: a tainted caller is always held for review. Set AllowedRecipientDomains to limit recipients." : "Sending is off (AllowSend false).",
            ],
            SuggestedPolicyJson = send
                ? $$"""{ "Tools": { "{{id}}.list_recent": { "Mode": "Execute" }, "{{id}}.read": { "Mode": "Execute" }, "{{id}}.send": { "Mode": "Queue", "ExpireAfter": "24:00:00" } } }"""
                : $$"""{ "Tools": { "{{id}}.list_recent": { "Mode": "Execute" }, "{{id}}.read": { "Mode": "Execute" } } }""",
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.UserName)) throw new ArgumentException("Config.UserName is required.");
        if (string.IsNullOrWhiteSpace(c.Imap?.Host)) throw new ArgumentException("Config.Imap.Host is required.");
        if (c.AllowSend && string.IsNullOrWhiteSpace(c.Smtp?.Host)) throw new ArgumentException("Config.Smtp.Host is required when AllowSend is true.");

        var credentials = context.Credentials;
        var tenant = context.Id;
        async ValueTask<string> Secret(string name, CancellationToken token) => (await credentials.GetAsync(name, token)).Secret;
        ImapSettings Imap(string password) => ImapSettings.Create(c.Imap!.Host!, c.Imap.Port, c.Imap.UseSsl, c.UserName!, password);

        var inbox = new DeferredInbox(async token => new ImapInboxAdapter(Imap(await Secret("password", token)), tenant));
        IEmailSender sender = c.AllowSend
            ? new DeferredSender(async token =>
            {
                var password = await Secret("password", token);
                var smtpPassword = credentials.Names.Contains("smtp-password") ? await Secret("smtp-password", token) : password;
                var smtp = SmtpSettings.Create(c.Smtp!.Host!, c.Smtp.Port, c.Smtp.UseSsl, c.Smtp.UserName ?? c.UserName!, smtpPassword);
                return new ImapEmailSender(smtp, Imap(password));
            })
            : new NoSender();

        IHostModule module = new EmailModule(inbox, sender, new EmailModuleOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            AllowSend = c.AllowSend,
            Folder = c.Folder,
            MaxListMessages = c.MaxListMessages,
            ReadSearchWindow = c.ReadSearchWindow,
            Lookback = TimeSpan.FromDays(c.LookbackDays),
            MaxBodyChars = c.MaxBodyChars,
            MaxRecipients = c.MaxRecipients,
            AllowedRecipientDomains = c.AllowedRecipientDomains,
        });
        return ValueTask.FromResult(module);
    }

    private static Config? TryBind(System.Text.Json.JsonElement config)
    {
        try
        {
            return config.ValueKind == System.Text.Json.JsonValueKind.Object
                ? System.Text.Json.JsonSerializer.Deserialize<Config>(config, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private sealed class Server
    {
        public string? Host { get; init; }
        public int? Port { get; init; }
        public bool? UseSsl { get; init; }
        public string? UserName { get; init; }
    }

    private sealed class Config
    {
        public string? UserName { get; init; }
        public Server? Imap { get; init; }
        public Server? Smtp { get; init; }
        public bool AllowSend { get; init; } = true;
        public string Folder { get; init; } = "INBOX";
        public int MaxListMessages { get; init; } = 25;
        public int ReadSearchWindow { get; init; } = 200;

        /// <summary>Without it the IMAP adapter searches only the last 30 days, and older mail is invisible to list and read.</summary>
        public int LookbackDays { get; init; } = 365;
        public int MaxBodyChars { get; init; } = 20_000;
        public int MaxRecipients { get; init; } = 10;
        public List<string> AllowedRecipientDomains { get; init; } = [];
    }
}
