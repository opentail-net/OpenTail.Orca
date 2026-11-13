using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace OpenTail.Host.Http.Teams;

/// <summary>
/// The OpenTail Teams bot: cards with Approve, Approve with changes and Reject in Teams. Needs an Azure Bot / Entra app registration for
/// the bot (single tenant), the Teams app installed for the reviewers, and this Host reachable over public HTTPS at
/// <see cref="MessagingPath"/>. Only the Entra users listed in <see cref="Reviewers"/> can decide anything from Teams.
/// </summary>
public sealed class TeamsBotOptions
{
    /// <summary>The bot's Microsoft App ID (the app registration's client id).</summary>
    public required string AppId { get; init; }

    /// <summary>Name of the environment variable holding the app registration's client secret.</summary>
    public string? AppPasswordEnv { get; init; }

    /// <summary>The company's Entra tenant: activities from any other tenant are refused, and the bot's token is issued here.</summary>
    public required string TenantId { get; init; }

    /// <summary>The highest risk that may be decided from Teams; anything above shows only "Open" (decide on the review page).</summary>
    public ToolRisk ChatMaxRisk { get; init; } = ToolRisk.ExternalEffect;

    /// <summary>Whether a partner's reply (<c>a2a.reply</c>) may be approved from Teams. When false, its card has no Approve or Approve with
    /// changes, an approve click for it is refused with a note that partner replies are approved on the review page, and Reject and request
    /// changes still work from chat. Other tools are not affected.</summary>
    public bool ApprovePartnerRepliesInChat { get; init; } = true;

    /// <summary>More waiting actions than this for one chat or channel are shown as one digest card.</summary>
    public int DigestThreshold { get; init; } = 3;

    /// <summary>Who may decide from Teams, by Entra object id: <c>{ "&lt;object id&gt;": { "Name": "rita", "Groups": [ "finance" ] } }</c>.
    /// Their subject is <c>teams:&lt;object id&gt;</c>; list it under the policy's <c>People</c> with the person's AI agent so that agent's
    /// actions arrive in their chat (and self-approval rules know it is the same person).</summary>
    public Dictionary<string, TeamsReviewer> Reviewers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where Teams posts activities (the bot's messaging endpoint is <c>&lt;public address&gt;&lt;MessagingPath&gt;</c>).</summary>
    public string MessagingPath { get; init; } = "/api/teams/messages";

    public string OpenIdMetadataUrl { get; init; } = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    public string? TokenEndpoint { get; init; }
    public string ConnectorScope { get; init; } = "https://api.botframework.com/.default";

    /// <summary>Tests and sovereign clouds: replaces the Bot Framework signing keys (no metadata is fetched when set).</summary>
    [JsonIgnore] public Action<TokenValidationParameters>? ConfigureValidation { get; init; }

    [JsonIgnore] public HttpMessageHandler? Handler { get; init; }

    [JsonIgnore] public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    public static string SubjectOf(string objectId) => "teams:" + objectId.ToLowerInvariant();

    /// <summary>One line for the startup output: how the bot is set up. It names no secret (the secret is only ever read from the environment).</summary>
    public string Describe() => $"Teams bot: app {AppId}, tenant {TenantId}, ChatMaxRisk {ChatMaxRisk}, ApprovePartnerRepliesInChat {(ApprovePartnerRepliesInChat ? "true" : "false")}, {Reviewers.Count} reviewer(s).";
}

public sealed class TeamsReviewer
{
    public string Name { get; init; } = "";
    public List<string> Groups { get; init; } = [];
}
