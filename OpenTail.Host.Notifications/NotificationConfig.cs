using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTail.Host.Notifications;

/// <summary>
/// Who hears about what. Secrets never live in this file: a channel names the environment variable that holds its secret or its
/// (secret) webhook URL. Example:
/// <code>
/// {
///   "PublicBaseUrl": "https://approvals.example",
///   "Channels": [
///     { "Name": "finance-slack", "Type": "slack",   "UrlEnv": "SLACK_FINANCE_WEBHOOK" },
///     { "Name": "audit-hook",    "Type": "webhook", "Url": "https://hooks.example/opentail", "SecretEnv": "OPENTAIL_HOOK_SECRET" }
///   ],
///   "Routes": [
///     { "On": ["queued", "reminder"], "Tools": ["files.*"], "MinRisk": "Write", "To": ["finance-slack"] },
///     { "On": ["decided"], "To": ["audit-hook"] }
///   ],
///   "Reminder": { "After": "15m", "Every": "1h", "Max": 3 }
/// }
/// </code>
/// </summary>
public sealed class NotificationConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(), new DurationConverter() },
    };

    /// <summary>Where reviewers sign in. Review links are this plus <c>/#&lt;actionId&gt;</c>. Falls back to the HTTP server's own public address.</summary>
    public string? PublicBaseUrl { get; init; }

    public List<ChannelConfig> Channels { get; init; } = [];
    public List<RouteConfig> Routes { get; init; } = [];
    public ReminderConfig? Reminder { get; init; }

    /// <summary>Only actions queued within this window are reconciled after a crash or an enabling of notifications (so turning them on
    /// does not announce everything that has been waiting for weeks).</summary>
    public TimeSpan ReconcileWindow { get; init; } = TimeSpan.FromHours(6);

    public static NotificationConfig Parse(string json) =>
        JsonSerializer.Deserialize<NotificationConfig>(json, JsonOptions) ?? throw new InvalidOperationException("The notifications file is empty.");

    public static NotificationConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Everything wrong with this configuration. <paramref name="env"/> resolves environment variables (injectable for tests).</summary>
    public IReadOnlyList<string> Validate(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in Channels)
        {
            if (!Regex.IsMatch(c.Name ?? "", "^[A-Za-z0-9._-]{1,64}$")) errors.Add($"Channel name '{c.Name}' must be 1-64 letters, digits, '.', '_' or '-'.");
            if (!names.Add(c.Name ?? "")) errors.Add($"Duplicate channel '{c.Name}'.");

            if (c.Type == ChannelType.TeamsBot)
            {
                if (c.Url is not null || c.UrlEnv is not null || c.SecretEnv is not null) errors.Add($"Channel '{c.Name}': the Teams bot takes no Url, UrlEnv or SecretEnv (it uses the HTTP host's Teams settings).");
                if (c.Target is not ("requester" or null) && !(c.Target ?? "").StartsWith("conversation:", StringComparison.Ordinal))
                    errors.Add($"Channel '{c.Name}': Target must be requester or conversation:<key>.");
                continue;
            }
            if (c.Target is not null) errors.Add($"Channel '{c.Name}': only a teams-bot channel takes a Target.");
            var url = c.Url ?? (c.UrlEnv is { } ue ? env(ue) : null);
            if (c.UrlEnv is not null && c.Url is not null) errors.Add($"Channel '{c.Name}': give Url or UrlEnv, not both.");
            if (url is null) errors.Add($"Channel '{c.Name}': " + (c.UrlEnv is not null ? $"the environment variable {c.UrlEnv} is not set." : "needs a Url or a UrlEnv."));
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) errors.Add($"Channel '{c.Name}': the URL is not valid.");
            else if (u.Scheme != "https" && !(u.Scheme == "http" && u.IsLoopback)) errors.Add($"Channel '{c.Name}': the URL must be https (plain http only for loopback).");

            switch (c.Type)
            {
                case ChannelType.Webhook:
                    if (c.SecretEnv is null) errors.Add($"Channel '{c.Name}': a webhook needs SecretEnv (the name of the environment variable holding the signing secret).");
                    else if (env(c.SecretEnv) is not { Length: >= 16 }) errors.Add($"Channel '{c.Name}': the environment variable {c.SecretEnv} must hold a secret of at least 16 characters.");
                    break;
                case ChannelType.Slack:
                    if (c.SecretEnv is not null) errors.Add($"Channel '{c.Name}': a Slack channel does not use SecretEnv (the webhook URL is the secret; use UrlEnv).");
                    break;
                case ChannelType.Teams:
                    if (c.SecretEnv is not null) errors.Add($"Channel '{c.Name}': a Teams channel does not use SecretEnv (the workflow URL is the secret; use UrlEnv).");
                    break;
            }
        }

        foreach (var (r, i) in Routes.Select((r, i) => (r, i)))
        {
            var at = $"Route {i + 1}";
            if (r.To.Count == 0) errors.Add($"{at}: needs at least one channel in To.");
            foreach (var to in r.To.Where(t => !names.Contains(t))) errors.Add($"{at}: unknown channel '{to}'.");
            if (r.On.Count == 0) errors.Add($"{at}: needs at least one event in On.");
            foreach (var on in r.On.Where(o => !NotificationEvents.Known.Contains(o))) errors.Add($"{at}: unknown event '{on}'. Known: {string.Join(", ", NotificationEvents.Known.Order())}.");
            if (r.On.Contains(NotificationEvents.WorkReady))
                foreach (var to in r.To.Where(t => Channels.Any(c => c.Name == t && c.Type != ChannelType.Webhook)))
                    errors.Add($"{at}: '{NotificationEvents.WorkReady}' goes to a webhook channel (an agent platform), not '{to}'.");
            if (r.On.Contains(NotificationEvents.AuditEntry))
                foreach (var to in r.To.Where(t => Channels.Any(c => c.Name == t && c.Type != ChannelType.Webhook)))
                    errors.Add($"{at}: '{NotificationEvents.AuditEntry}' goes to a webhook channel (a log store), not '{to}': every entry would be a chat message.");
            if (r.On.Contains(NotificationEvents.AuditCheckpoint))
                foreach (var to in r.To.Where(t => Channels.Any(c => c.Name == t && c.Type == ChannelType.TeamsBot)))
                    errors.Add($"{at}: the Teams bot channel '{to}' posts about actions only; send {NotificationEvents.AuditCheckpoint} to a webhook, Slack or Teams channel.");
            foreach (var t in r.Tools.Where(t => t.Length == 0 || t.Length > 100)) errors.Add($"{at}: a tool pattern is empty or too long ('{t}').");
        }

        if (Reminder is { } rem)
        {
            if (rem.After <= TimeSpan.Zero || rem.Every <= TimeSpan.Zero) errors.Add("Reminder: After and Every must be positive.");
            if (rem.Max is < 1 or > 20) errors.Add("Reminder: Max must be between 1 and 20.");
            if (!Routes.Any(r => r.On.Contains("reminder"))) errors.Add("Reminder is configured but no route listens for 'reminder'.");
        }
        if (PublicBaseUrl is not null && !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _)) errors.Add("PublicBaseUrl is not a valid URL.");
        if (ReconcileWindow <= TimeSpan.Zero) errors.Add("ReconcileWindow must be positive.");
        return errors;
    }
}

public enum ChannelType
{
    Webhook = 0,
    Slack = 1,

    /// <summary>Microsoft Teams, through a Workflows webhook ("Post to a channel when a webhook request is received"): an Adaptive Card with
    /// an Open button to the review page. Nothing can be approved from it, so Teams never calls the Host.</summary>
    Teams = 2,

    /// <summary>The OpenTail Teams bot (HTTP host with a <c>Teams</c> section): cards with Approve, Approve with changes and Reject in the
    /// chat of the person whose AI asked (<c>"Target": "requester"</c>) or in one conversation (<c>"Target": "conversation:&lt;key&gt;"</c>).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("teams-bot")]
    TeamsBot = 3,
}

public sealed class ChannelConfig
{
    public string Name { get; init; } = "";
    public ChannelType Type { get; init; }

    /// <summary>The destination, if it is not secret (a generic webhook).</summary>
    public string? Url { get; init; }

    /// <summary>The name of an environment variable holding the destination (a Slack webhook URL is itself the secret).</summary>
    public string? UrlEnv { get; init; }

    /// <summary>Webhook only: the name of the environment variable holding the HMAC signing secret (16+ characters).</summary>
    public string? SecretEnv { get; init; }

    /// <summary>For <c>teams-bot</c>: <c>requester</c> (default: the chat of the person whose AI asked) or <c>conversation:&lt;key&gt;</c>.</summary>
    public string? Target { get; init; }
}

public sealed class RouteConfig
{
    public List<string> On { get; init; } = [];

    /// <summary>Tool name patterns (<c>*</c> matches anything). Empty: every tool.</summary>
    public List<string> Tools { get; init; } = [];

    /// <summary>For <c>work_ready</c>: who the work is for (<c>profile:clerk</c>, <c>subject:key:bot</c>, <c>*</c>). Empty: all.</summary>
    public List<string> Work { get; init; } = [];

    /// <summary>Only actions at or above this risk. Default: all.</summary>
    public ToolRisk MinRisk { get; init; } = ToolRisk.Read;

    public List<string> To { get; init; } = [];

    /// <summary>Include the one-line preview summary ("Send an email to ...") in the message. Off by default: previews can contain
    /// customer data, and chat tools keep history.</summary>
    public bool IncludePreviewSummary { get; init; }
}

public sealed class ReminderConfig
{
    public TimeSpan After { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan Every { get; init; } = TimeSpan.FromHours(1);
    public int Max { get; init; } = 3;
}

/// <summary>Event names a route can listen for.</summary>
public static class NotificationEvents
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "queued", "approved", "rejected", "changes_requested", "succeeded", "failed", "expired", "cancelled", "denied",
        "reminder",
        "decided", // rejected, changes_requested, succeeded, failed, expired: the outcomes an agent or an audit hook cares about
        AuditCheckpoint, // the audit chain's newest entry and hash, hourly and at shutdown, so a copy exists outside the database
        AuditEntry, // every audit entry, as written, to a log store (webhook only): a copy outside the Host within the scheduler's period
        WorkReady, // a work item is waiting for an agent: ids only, to wake the agent platform for that profile (webhook only)
    };

    /// <summary>Not about an action: a work item is ready (work id, who it is for, its kind; never the goal or note).</summary>
    public const string WorkReady = "work_ready";

    /// <summary>Not about an action: each audit entry with its chain fields, for the organisation's log store (audit D8).</summary>
    public const string AuditEntry = "audit_entry";

    /// <summary>Not about an action: <c>Detail</c> carries <c>&lt;seq&gt;:&lt;hash&gt;</c> (see <c>--verify-audit --checkpoint</c>).</summary>
    public const string AuditCheckpoint = "audit_checkpoint";

    public static string NameOf(ActionEventKind k) => k switch
    {
        ActionEventKind.Queued => "queued",
        ActionEventKind.Approved => "approved",
        ActionEventKind.Rejected => "rejected",
        ActionEventKind.ChangesRequested => "changes_requested",
        ActionEventKind.Succeeded => "succeeded",
        ActionEventKind.Failed => "failed",
        ActionEventKind.Expired => "expired",
        ActionEventKind.Cancelled => "cancelled",
        _ => "denied",
    };

    public static bool IsDecided(string name) => name is "rejected" or "changes_requested" or "succeeded" or "failed" or "expired";
}
